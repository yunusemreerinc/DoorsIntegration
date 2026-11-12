using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Dxl
{
    public sealed class DoorsBatchAdapterOptions
    {
        public DoorsBatchAdapterOptions(DoorsVersion version = DoorsVersion.V97, DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting, TimeSpan? timeout = null)
        {
            DoorsVersions.Format(version); Timeout = timeout ?? TimeSpan.FromMinutes(2);
            if ((mode != DoorsConnectionMode.AttachExisting && mode != DoorsConnectionMode.Activate) || Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(10)) throw new ArgumentException("Invalid DOORS batch settings.");
            Version = version; Mode = mode;
        }
        public DoorsVersion Version { get; }
        public DoorsConnectionMode Mode { get; }
        public TimeSpan Timeout { get; }
    }
    public sealed class DoorsBatchChangeAdapter : IBatchChangeAdapter
    {
        private readonly IRequirementService requirements;
        private readonly DoorsBatchAdapterOptions options;
        public DoorsBatchChangeAdapter(IRequirementService requirements, DoorsBatchAdapterOptions options)
        { this.requirements = requirements ?? throw new ArgumentNullException(nameof(requirements)); this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        public async Task<BatchInspection> InspectAsync(BatchChange change, BatchOptions scope, CancellationToken token)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            try
            {
                var preview = await requirements.PreviewWriteAsync(Request(change), token).ConfigureAwait(false);
                return Inspection(preview, scope);
            }
            catch (IntegrationException ex) when (ex.Code == IntegrationErrorCode.InvalidConfiguration)
            { return new BatchInspection(false, scope.RepositoryId, "", "", "", new[] { "InvalidDOORSChange" }); }
            catch (ArgumentException) { return new BatchInspection(false, scope.RepositoryId, "", "", "", new[] { "InvalidDOORSChange" }); }
        }
        public async Task<BatchDispatchResult> ApplyAsync(BatchChange change, BatchInspection reviewed, BatchOptions scope, CancellationToken token)
        {
            if (reviewed == null || scope == null) return new BatchDispatchResult(BatchMutation.None, "MissingReviewedInspection");
            bool invokingWrite = false;
            string freshStamp = reviewed.ReviewStamp;
            try
            {
                // Ordered imports can change hierarchy state. Never silently authorize a refreshed native plan.
                // RequirementService asks the host for explicit consent to THIS fresh per-record preview.
                var fresh = await requirements.PreviewWriteAsync(Request(change), token).ConfigureAwait(false);
                freshStamp = fresh.ApprovalFingerprint;
                var current = Inspection(fresh, scope);
                if (!current.CanApply) return new BatchDispatchResult(BatchMutation.None, "NativeValidationFailed");
                if (current.RepositoryId != reviewed.RepositoryId || current.RepositoryId != scope.RepositoryId ||
                    current.Actor != reviewed.Actor || current.TargetId != reviewed.TargetId)
                    return new BatchDispatchResult(BatchMutation.None, "NativeContextChanged");
                token.ThrowIfCancellationRequested(); invokingWrite = true;
                var result = await requirements.ApplyWriteAsync(fresh, token).ConfigureAwait(false);
                return Translate(result, fresh.ApprovalFingerprint);
            }
            catch (RequirementWriteUncertainException ex)
            { return new BatchDispatchResult(BatchMutation.Unknown, "NativeWriteStillRunningOrUnverifiable", completion: LateAsync(ex, freshStamp)); }
            catch (DxlExecutionWaitException)
            { return new BatchDispatchResult(invokingWrite ? BatchMutation.Unknown : BatchMutation.None, "NativeReadStillRunning"); }
            catch (OperationCanceledException) { return new BatchDispatchResult(BatchMutation.None, "CancelledBeforeNativeDispatch"); }
            catch (IntegrationException ex)
            {
                bool definite = !invokingWrite || ex.Code == IntegrationErrorCode.RequirementWriteApprovalRequired || ex.Code == IntegrationErrorCode.RequirementWriteBlocked ||
                    ex.Code == IntegrationErrorCode.RequirementWriteAuditFailed || ex.Code == IntegrationErrorCode.DoorsOperationBusy || ex.Code == IntegrationErrorCode.InvalidConfiguration;
                return new BatchDispatchResult(definite ? BatchMutation.None : BatchMutation.Unknown, ex.Code.ToString(),
                    definite && (ex.Code == IntegrationErrorCode.DoorsOperationBusy || ex.Code == IntegrationErrorCode.ComServerBusy));
            }
            catch (ArgumentException) when (!invokingWrite) { return new BatchDispatchResult(BatchMutation.None, "InvalidDOORSChange"); }
            catch { return new BatchDispatchResult(invokingWrite ? BatchMutation.Unknown : BatchMutation.None, "NativeAdapterFailed"); }
        }
        private static async Task<BatchDispatchResult> LateAsync(RequirementWriteUncertainException exception, string stamp)
        {
            try { return Translate(await exception.Completion.ConfigureAwait(false), stamp); }
            catch { return new BatchDispatchResult(BatchMutation.Unknown, "UnverifiableNativeCompletion"); }
        }
        private static BatchInspection Inspection(RequirementWritePreview preview, BatchOptions scope)
        {
            var schema = preview.Inspection.Schema;
            return new BatchInspection(preview.CanApply && schema.DatabaseId == scope.RepositoryId, schema.DatabaseId, schema.UserName, schema.QualifiedModuleId,
                preview.ApprovalFingerprint, schema.Issues.Select(i => i.Severity + ":" + i.Code + ":" + i.AttributeName),
                preview.Inspection.BeforeValues.Select(v => new BatchBeforeValue(v.Name, v.PlainText, v.RichTextWithOle)));
        }
        private static BatchDispatchResult Translate(RequirementWriteResult result, string stamp)
        {
            string identity = result.AbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
            if (result.Succeeded && result.AbsoluteNumber > 0) return new BatchDispatchResult(BatchMutation.Applied, "AppliedAndVerified", resultingObjectId: identity, reviewStamp: stamp);
            if (result.Persistence == RequirementWritePersistence.Saved)
                return new BatchDispatchResult(BatchMutation.Applied, "SavedWithProblems", resultingObjectId: identity, reviewStamp: stamp, auditRecorded: false);
            if ((result.Outcome == RequirementWriteOutcome.Blocked || result.Outcome == RequirementWriteOutcome.ContextChanged) &&
                result.Persistence == RequirementWritePersistence.NotAttempted && result.Cleanup != RequirementWriteCleanup.LeftOpenForReview && result.Cleanup != RequirementWriteCleanup.Failed &&
                result.Steps.All(s => s.Status == RequirementWriteStepStatus.Skipped)) return new BatchDispatchResult(BatchMutation.None, result.Outcome.ToString(), reviewStamp: stamp);
            return new BatchDispatchResult(result.Outcome == RequirementWriteOutcome.PartialFailure ? BatchMutation.Partial : BatchMutation.Unknown,
                result.Outcome.ToString(), resultingObjectId: identity, reviewStamp: stamp, auditRecorded: result.AuditRecorded);
        }
        private RequirementWriteRequest Request(BatchChange change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            int? absolute = change.ObjectId == null ? (int?)null : Positive(change.ObjectId);
            int? anchor = change.AnchorId == null ? (int?)null : Positive(change.AnchorId);
            return new RequirementWriteRequest(new RequirementWriteTarget(change.Target, options.Version, options.Mode, options.Timeout),
                change.Attributes.Select(a => new RequirementAttributeChange(a.Name, a.Value,
                    a.Format == BatchValueFormat.PlainText ? RequirementWriteFormat.PlainText : RequirementWriteFormat.DoorsRichText, a.ExpectedPlainText)),
                absolute, new RequirementPosition((RequirementPositionKind)Enum.Parse(typeof(RequirementPositionKind), change.Position.ToString()), anchor), change.RequiredAttributes);
        }
        private static int Positive(string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result <= 0 || result.ToString(CultureInfo.InvariantCulture) != value) throw new ArgumentException("Absolute Number must be a canonical positive integer.");
            return result;
        }
    }
    public sealed class DoorsBatchServiceFactory
    {
        private readonly IRequirementService requirements; private readonly IBatchJournal journal; private readonly IBatchAuditSink audit;
        private readonly IBatchConfirmation confirmation; private readonly ProcessingStorageOptions storage; private readonly IProcessingClock clock;
        private readonly IEnumerable<IBatchChangeValidator> validators;
        public DoorsBatchServiceFactory(IRequirementService requirements, IBatchJournal journal, IBatchAuditSink audit, IBatchConfirmation confirmation, ProcessingStorageOptions storage, IProcessingClock clock, IEnumerable<IBatchChangeValidator> validators)
        { this.requirements = requirements; this.journal = journal; this.audit = audit; this.confirmation = confirmation; this.storage = storage; this.clock = clock; this.validators = validators; }
        public BatchService Create(DoorsVersion version, DoorsConnectionMode mode, TimeSpan? timeout = null) =>
            new BatchService(new DoorsBatchChangeAdapter(requirements, new DoorsBatchAdapterOptions(version, mode, timeout)), journal, audit, confirmation, storage, clock, validators);
    }
}
