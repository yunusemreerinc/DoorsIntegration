using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace DoorsIntegration.Core
{
    public enum BaselineAuditAction { Checkpoint, Created, RunStarted, PlanApproved, ModuleCheckpoint, RunCompleted, Reconciled, LegacyImported }
    public enum BaselineReportGroup { Successful, Skipped, Locked, Failed, Unverifiable }
    public enum BaselineBatchOutcome { Success, PartialSuccess, Failed, Skipped, Indeterminate }
    [DataContract]
    public sealed class BaselineJobAuditContext
    {
        public BaselineJobAuditContext(string actor, string database, string environment, string clientVersion, string clientVersionEvidence)
        { Operator = Safe(actor); DatabaseIdentifier = Safe(database); EnvironmentIdentifier = Safe(environment); ClientVersion = Safe(clientVersion); ClientVersionEvidence = Safe(clientVersionEvidence); }
        [DataMember(Order = 0)] public string Operator { get; private set; }
        [DataMember(Order = 1)] public string DatabaseIdentifier { get; private set; }
        [DataMember(Order = 2)] public string EnvironmentIdentifier { get; private set; }
        [DataMember(Order = 3)] public string ClientVersion { get; private set; }
        [DataMember(Order = 4)] public string ClientVersionEvidence { get; private set; }
        private static string Safe(string value) => BaselineAuditSanitizer.Text(value ?? "NotCollected");
    }
    public interface IBaselineJobAuditContextProvider
    { BaselineJobAuditContext Capture(BulkBaselineDryRunPlan plan, BaselineMetadataSource source); }
    public sealed class BaselineJobAuditContextProvider : IBaselineJobAuditContextProvider
    {
        public BaselineJobAuditContext Capture(BulkBaselineDryRunPlan plan, BaselineMetadataSource source) => new BaselineJobAuditContext(
            Join(plan.Items.Select(i => i.Precondition?.Facts.CurrentUserId)), Join(plan.Items.Select(i => i.Precondition?.Facts.DatabaseIdentity)),
            Join(plan.Items.Select(i => i.Module.RepositoryId)) + " / " + source,
            (source == BaselineMetadataSource.Mock ? "Mock simulator; target " : "Requested profile ") + DoorsVersions.Format(plan.Request.Target),
            source == BaselineMetadataSource.Mock ? "Simulated; not a native executable version" : "RequestedProfileOnly; observed executable version NOT_COLLECTED");
        private static string Join(IEnumerable<string> values) { var found = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray(); return found.Length == 0 ? "NotCollected" : string.Join("; ", found); }
    }
    [DataContract]
    public sealed class BaselineApprovedModule
    {
        [DataMember(Order = 0)] public string StableIdentity { get; private set; }
        [DataMember(Order = 1)] public string FullPath { get; private set; }
        [DataMember(Order = 2)] public string PreviousBaseline { get; private set; }
        [DataMember(Order = 3)] public string ExpectedBaseline { get; private set; }
        [DataMember(Order = 4)] public string Suffix { get; private set; }
        [DataMember(Order = 5)] public string Annotation { get; private set; }
        [DataMember(Order = 6)] public string PreconditionFingerprint { get; private set; }
        [DataMember(Order = 7)] public string Warning { get; private set; }
        internal BaselineApprovedModule(BulkBaselineDryRunItem item)
        { StableIdentity = BaselineAuditSanitizer.Text(item.ModuleId); FullPath = BaselineAuditSanitizer.Text(item.FullPath); PreviousBaseline = BaselineAuditSanitizer.Text(item.CurrentVersion?.ToString()); ExpectedBaseline = BaselineAuditSanitizer.Text(item.ExpectedVersion?.ToString()); Suffix = BaselineAuditSanitizer.Text(item.Suffix); Annotation = BaselineAuditSanitizer.Text(item.Annotation); PreconditionFingerprint = item.Precondition?.Fingerprint; Warning = BaselineAuditSanitizer.Text(item.Warning); }
    }
    [DataContract]
    public sealed class BaselineApprovedPlan
    {
        [DataMember(Order = 0)] public Guid ApprovalId { get; private set; }
        [DataMember(Order = 1)] public Guid JobId { get; private set; }
        [DataMember(Order = 2)] public Guid PlanId { get; private set; }
        [DataMember(Order = 3)] public string Fingerprint { get; private set; }
        [DataMember(Order = 4)] public DateTime ApprovedAtUtc { get; private set; }
        [DataMember(Order = 5)] public string Operator { get; private set; }
        [DataMember(Order = 6)] public BaselineType Type { get; private set; }
        [DataMember(Order = 7)] public string Suffix { get; private set; }
        [DataMember(Order = 8)] public string Annotation { get; private set; }
        [DataMember(Order = 9)] public string Scope { get; private set; }
        [DataMember(Order = 10)] public bool SecondRealConfirmation { get; private set; }
        [DataMember(Order = 11)] private List<BaselineApprovedModule> modules;
        public IReadOnlyList<BaselineApprovedModule> Modules => modules.AsReadOnly();
        public static BaselineApprovedPlan Capture(BulkBaselineDryRunPlan plan, Guid jobId, DateTime utc, string actor, string scope, bool secondRealConfirmation = false, Guid? approvalId = null)
        {
            if (plan == null || jobId == Guid.Empty) throw new ArgumentException("Exact confirmed plan and job required."); BulkBaselineGuard.Utc(utc);
            return new BaselineApprovedPlan { ApprovalId = approvalId ?? Guid.NewGuid(), JobId = jobId, PlanId = plan.PlanId, Fingerprint = plan.Fingerprint,
                ApprovedAtUtc = utc, Operator = BaselineAuditSanitizer.Text(actor), Type = plan.Request.Baseline.Type, Suffix = BaselineAuditSanitizer.Text(plan.Request.Baseline.Suffix),
                Annotation = BaselineAuditSanitizer.Text(plan.Request.Baseline.Annotation), Scope = BaselineAuditSanitizer.Text(scope), SecondRealConfirmation = secondRealConfirmation,
                modules = plan.Items.Select(i => new BaselineApprovedModule(i)).ToList() };
        }
    }
    public interface IBaselineApprovalAuditSource
    { BaselineApprovedPlan CaptureAcceptedApproval(BaselineExecutionReview review, DateTime acceptedAtUtc); }
    [DataContract]
    public sealed class BaselineAuditModuleState
    {
        [DataMember(Order = 0)] public string StableIdentity { get; private set; }
        [DataMember(Order = 1)] public string FullPath { get; private set; }
        [DataMember(Order = 2)] public string PreviousBaseline { get; private set; }
        [DataMember(Order = 3)] public string ExpectedBaseline { get; private set; }
        [DataMember(Order = 4)] public string ActualBaseline { get; private set; }
        [DataMember(Order = 5)] public string ResultStatus { get; private set; }
        [DataMember(Order = 6)] public string ErrorMessage { get; private set; }
        [DataMember(Order = 7)] public string DurationSeconds { get; private set; }
        [DataMember(Order = 8)] public string PreconditionFingerprint { get; private set; }
        [DataMember(Order = 9)] public Guid? ApprovalId { get; private set; }
        [DataMember(Order = 10)] public Guid? OperationId { get; private set; }
        [DataMember(Order = 11)] public int Attempts { get; private set; }
        [DataMember(Order = 12)] public string Suffix { get; private set; }
        [DataMember(Order = 13)] public string Annotation { get; private set; }
        [DataMember(Order = 14)] public string Group { get; private set; }
        [DataMember(Order = 15)] public string ActualEvidence { get; private set; }
        [DataMember(Order = 16)] public DateTime? StartedAtUtc { get; private set; }
        [DataMember(Order = 17)] public DateTime? EndedAtUtc { get; private set; }
        internal BaselineAuditModuleState(BulkBaselineJobModule m)
        {
            StableIdentity = BaselineAuditSanitizer.Text(m.ModuleId); FullPath = BaselineAuditSanitizer.Text(m.CanonicalFullPath); PreviousBaseline = BaselineAuditSanitizer.Text(m.CurrentVersion?.ToString()); ExpectedBaseline = BaselineAuditSanitizer.Text(m.ExpectedVersion?.ToString());
            ActualBaseline = m.Status == BulkBaselineModuleStatus.Succeeded && m.EverSucceeded ? ExpectedBaseline : null;
            ActualEvidence = ActualBaseline == null ? "NotVerifiedAtThisRevision" : "RecordedVerifiedSuccessOrReadOnlyReconciliation; not causal ownership proof";
            ResultStatus = m.Status.ToString(); ErrorMessage = BaselineAuditSanitizer.Text(m.FailureReason); PreconditionFingerprint = m.LastPrecondition;
            ApprovalId = m.AcceptedApprovalId; OperationId = m.OperationId; Attempts = m.Attempts; Suffix = BaselineAuditSanitizer.Text(m.Suffix); Annotation = BaselineAuditSanitizer.Text(m.Annotation);
            StartedAtUtc = m.StartedAtUtc; EndedAtUtc = m.CompletedAtUtc;
            DurationSeconds = m.StartedAtUtc.HasValue && m.CompletedAtUtc.HasValue ? (m.CompletedAtUtc.Value - m.StartedAtUtc.Value).TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : null;
            Group = Classify(m).ToString();
        }
        public static BaselineReportGroup Classify(BulkBaselineJobModule m)
        {
            if (m.Status == BulkBaselineModuleStatus.Succeeded && m.EverSucceeded) return BaselineReportGroup.Successful;
            if (m.Status == BulkBaselineModuleStatus.Unknown || m.Status == BulkBaselineModuleStatus.NeedsReconciliation ||
                m.Status == BulkBaselineModuleStatus.Running || m.Status == BulkBaselineModuleStatus.CancelRequested && m.WriteMayHaveOccurred) return BaselineReportGroup.Unverifiable;
            if (m.PreflightReason == BaselinePreflightReason.OtherSessionLock || m.ObservedLockState == BaselineLockState.OwnedByOtherSession) return BaselineReportGroup.Locked;
            if (m.Status == BulkBaselineModuleStatus.Failed) return BaselineReportGroup.Failed;
            if (m.Status == BulkBaselineModuleStatus.Skipped || m.Status == BulkBaselineModuleStatus.CancelRequested && !m.WriteMayHaveOccurred) return BaselineReportGroup.Skipped;
            return BaselineReportGroup.Unverifiable;
        }
    }
    [DataContract]
    public sealed class BaselineJobAuditEntry
    {
        [DataMember(Order = 0)] public Guid JobId { get; internal set; }
        [DataMember(Order = 1)] public long Revision { get; internal set; }
        [DataMember(Order = 2)] public DateTime RecordedAtUtc { get; internal set; }
        [DataMember(Order = 3)] public string Action { get; internal set; }
        [DataMember(Order = 4)] public string PreviousHash { get; internal set; }
        [DataMember(Order = 5)] public string Hash { get; internal set; }
        [DataMember(Order = 6)] public string Status { get; internal set; }
        [DataMember(Order = 7)] public BaselineJobAuditContext Context { get; internal set; }
        [DataMember(Order = 8)] public BaselineApprovedPlan ApprovedPlan { get; internal set; }
        [DataMember(Order = 9)] internal List<BaselineAuditModuleState> modules;
        public IReadOnlyList<BaselineAuditModuleState> Modules => modules.AsReadOnly();
        public string Disclosure => BulkBaselineReport.Disclosure;
    }
    public interface IBaselineJobAuditStore
    {
        IEnumerable<Guid> EnumerateJobIds(CancellationToken cancellationToken);
        IEnumerable<BaselineJobAuditEntry> ReadAudit(Guid jobId, long throughRevision, CancellationToken cancellationToken);
    }
    public static class BaselineAuditSanitizer
    {
        private static readonly Regex Authorization = new Regex(@"(?i)\b((?:proxy-)?authorization)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n;]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Credentials = new Regex(@"(?i)\b(password|passwd|pwd|secret|token|access[_-]?token|api[_-]?key|authorization)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^;\s]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Bearer = new Regex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex UserInfo = new Regex(@"(?i)([a-z][a-z0-9+.-]*://)[^/\s@]+@", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        public static string Text(string value)
        { if (value == null) return null; try { return UserInfo.Replace(Credentials.Replace(Bearer.Replace(Authorization.Replace(value, "$1=[REDACTED]"), "Bearer [REDACTED]"), "$1=[REDACTED]"), "$1[REDACTED]@"); } catch (RegexMatchTimeoutException) { return "[REDACTED: text could not be safely processed]"; } }
    }
}
