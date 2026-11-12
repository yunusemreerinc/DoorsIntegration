using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class BaselineExecutionScriptOptions
    {
        public string ExecutePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "baseline-execute.dxl");
        public string ReconcilePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "baseline-reconcile.dxl");
    }
    public interface IBaselineExecutionCompatibility
    {
        string ClientVersion { get; }
        // Deployment evidence is NOT user approval. BOTH are required, independently.
        bool IsValidated(BaselineExecutionAuthorization authorization, BaselineExecutionScriptOptions scripts);
    }
    public sealed class UnvalidatedBaselineExecutionCompatibility : IBaselineExecutionCompatibility
    {
        public string ClientVersion => "";
        public bool IsValidated(BaselineExecutionAuthorization authorization, BaselineExecutionScriptOptions scripts) => false;
    }
    // Opt-in host configuration after target release manual review and controlled runtime validation.
    // Evidence is pinned to database/user/target and both exact deployed script hashes.
    public sealed class ReviewedBaselineExecutionCompatibility : IBaselineExecutionCompatibility
    {
        private readonly DoorsVersion target;
        private readonly string database, user, executeHash, reconcileHash;
        public ReviewedBaselineExecutionCompatibility(DoorsVersion target, string database, string user,
            string executeSha256, string reconcileSha256, string manualReviewEvidence, string runtimeValidationEvidence, string clientVersion)
        {
            DoorsVersions.Format(target); this.target = target;
            this.database = Required(database); this.user = Required(user);
            executeHash = Hash(executeSha256); reconcileHash = Hash(reconcileSha256);
            ManualReviewEvidence = Required(manualReviewEvidence); RuntimeValidationEvidence = Required(runtimeValidationEvidence);
            ClientVersion = Required(clientVersion);
            string family = DoorsVersions.Format(target);
            if (ClientVersion != family && !ClientVersion.StartsWith(family + ".", StringComparison.Ordinal)) throw new ArgumentException("Client executable version must match the reviewed target family.");
        }
        public string ManualReviewEvidence { get; }
        public string RuntimeValidationEvidence { get; }
        public string ClientVersion { get; }
        public bool IsValidated(BaselineExecutionAuthorization authorization, BaselineExecutionScriptOptions scripts)
        {
            var facts = authorization.Item.Precondition.Facts;
            return authorization.Item.Probe.Request.Target == target && facts.Source == BaselineMetadataSource.NativeDxl &&
                facts.DatabaseIdentity == database && facts.CurrentUserId == user && int.TryParse(facts.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out int epoch) && epoch > 0 &&
                FileHash(scripts.ExecutePath) == executeHash && FileHash(scripts.ReconcilePath) == reconcileHash;
        }
        private static string Required(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 ? value : throw new ArgumentException("Reviewed validation evidence/context is required.");
        private static string Hash(string value)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("SHA256 must contain 64 hexadecimal characters.");
            foreach (char c in value) if (!Uri.IsHexDigit(c)) throw new ArgumentException("Invalid SHA256.");
            return value.ToUpperInvariant();
        }
        private static string FileHash(string path)
        { using (var input = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""); }
    }
    public enum BaselineReconciliationStatus { Verified, NotFoundOrUnreadable, Conflict, Unknown }
    public sealed class DxlBaselineExecutionAdapter : IBaselineExecutionAdapter
    {
        private readonly IDxlExecutor executor;
        private readonly BaselineExecutionScriptOptions scripts;
        private readonly IBaselineExecutionCompatibility compatibility;
        private readonly IIntegrationLogger logger;
        private readonly ConcurrentDictionary<Guid, BaselineExecutionAuthorization> pendingReconciliation = new ConcurrentDictionary<Guid, BaselineExecutionAuthorization>();
        public DxlBaselineExecutionAdapter(IDxlExecutor executor, BaselineExecutionScriptOptions scripts,
            IBaselineExecutionCompatibility compatibility, IIntegrationLogger logger)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts)); this.compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public BaselineMetadataSource Source => BaselineMetadataSource.NativeDxl;
        public async Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization authorization, CancellationToken cancellationToken)
        {
            if (authorization == null) throw new ArgumentNullException(nameof(authorization)); cancellationToken.ThrowIfCancellationRequested();
            DateTime start = DateTime.UtcNow;
            var item = authorization.Item;
            // No activation/new login for writes; exact attached session context must match fresh evidence.
            bool validated;
            try
            {
                validated = executor is DxlExecutor queued && queued.UsesStaQueue && item.Precondition.Facts.Source == Source &&
                    item.Probe.Request.ConnectionMode == DoorsConnectionMode.AttachExisting && item.Status == BulkBaselineItemStatus.Ready &&
                    compatibility.IsValidated(authorization, scripts) && authorization.TryBeginWrite();
            }
            catch (Exception)
            { return Result(authorization, BaselineWriteOutcome.NotRun, false, "Deployment evidence could not be validated; COM was not opened.", start); }
            if (!validated)
                return Result(authorization, BaselineWriteOutcome.NotRun, false, "NOT_RUN: native evidence, attach-only session, release/script validation and fresh single-use approval are required.", start);
            if (!pendingReconciliation.IsEmpty)
            {
                // Task 24's durable runner may rearm ONLY the same job/module/target, after separate
                // retry approval + renewed authoritative absence + fresh preflight. Never rearm a busy COM call.
                var pending = pendingReconciliation.ToArray();
                bool exactRetry = authorization.HasFreshAuthoritativeRetryVerification && !executor.IsBusy;
                foreach (var entry in pending)
                {
                    var old = entry.Value;
                    exactRetry &= old.Review.JobId == authorization.Review.JobId && old.Item.Module.Matches(item.Module) &&
                        old.Item.ExpectedVersion.Equals(item.ExpectedVersion) && old.Item.Annotation == item.Annotation &&
                        old.Item.Precondition.Facts.DatabaseIdentity == item.Precondition.Facts.DatabaseIdentity && old.Item.Precondition.Facts.CurrentUserId == item.Precondition.Facts.CurrentUserId;
                }
                if (!exactRetry) return Result(authorization, BaselineWriteOutcome.NotRun, false, "Session is quarantined; same-target operator-approved verified retry or explicit reconciliation is required.", start);
                foreach (var entry in pending) pendingReconciliation.TryRemove(entry.Key, out var ignoredPending);
            }
            var values = Parameters(item);
            values["approval"] = authorization.Review.JobId.ToString("N"); values["mode"] = "commit";
            values["clientVersion"] = compatibility.ClientVersion;
            values["session"] = item.Precondition.Facts.SessionId;
            double expires = (authorization.ExpiresAtUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            if (expires < 1 || expires >= int.MaxValue) return Result(authorization, BaselineWriteOutcome.NotRun, false, "Native UTC expiration is not representable.", start);
            values["expires"] = ((int)expires).ToString(CultureInfo.InvariantCulture);
            DxlExecutionOperation operation;
            try
            {
                var request = new DxlExecutionRequest(scripts.ExecutePath, parameters: values,
                    timeout: item.Probe.Request.Timeout, target: item.Probe.Request.Target, connectionMode: DoorsConnectionMode.AttachExisting, maxInlineResultChars: 131072)
                { DispatchPrecondition = () => authorization.IsFreshForDispatch && compatibility.IsValidated(authorization, scripts) };
                operation = executor.Start(request);
            }
            catch (Exception)
            { return Result(authorization, BaselineWriteOutcome.NotRun, false, "Execution could not start; no retry was scheduled.", start); }
            // This task owns even a late result. Never dispose the active COM call or release its gate early.
            var completion = ParseCompletionAsync(operation, authorization, start);
            _ = completion.ContinueWith(t => { var observed = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                var parsed = await completion.ConfigureAwait(false);
                if (parsed.Outcome == BaselineWriteOutcome.NeedsReconciliation) pendingReconciliation.TryAdd(operation.CorrelationId, authorization);
                return parsed;
            }
            catch (Exception)
            {
                pendingReconciliation.TryAdd(operation.CorrelationId, authorization);
                DxlLog.Write(logger, IntegrationLogLevel.Error, "BaselineNeedsReconciliation", operation.CorrelationId, "JobId=" + authorization.Review.JobId.ToString("N") + "; AutomaticRetry=False");
                return Result(authorization, BaselineWriteOutcome.NeedsReconciliation, true,
                    "COM wait/protocol failed. Creation may have completed; drain the operation and reconcile the exact target. Never retry automatically.", start, operation.CorrelationId, completion);
            }
        }
        public async Task<BaselineReconciliationStatus> ReconcileAsync(BaselineModuleExecutionResult uncertainResult, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (uncertainResult == null || uncertainResult.Source != Source || uncertainResult.Outcome != BaselineWriteOutcome.NeedsReconciliation)
                throw new ArgumentException("A native uncertain result is required.");
            if (!uncertainResult.OperationId.HasValue || !pendingReconciliation.TryGetValue(uncertainResult.OperationId.Value, out var authorization) ||
                uncertainResult.JobId != authorization.Review.JobId || !ReferenceEquals(uncertainResult.Item, authorization.Item) || !compatibility.IsValidated(authorization, scripts))
                throw new ArgumentException("Tracked native outcome and unchanged deployment evidence are required. Inspect untracked outcomes manually.");
            // Read-only; the shared executor refuses a new call until any active COM call drains.
            var operation = executor.Start(new DxlExecutionRequest(scripts.ReconcilePath, parameters: Parameters(uncertainResult.Item),
                timeout: uncertainResult.Item.Probe.Request.Timeout, target: uncertainResult.Item.Probe.Request.Target,
                connectionMode: DoorsConnectionMode.AttachExisting));
            try
            {
                using (var result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    string prefix = "DOORS_BASELINE_RECONCILE\t1\t" + operation.CorrelationId.ToString("N") + "\t";
                    if (result.IsFile || !result.Text.StartsWith(prefix, StringComparison.Ordinal)) throw BaselineExecutionProtocol.Failure();
                    switch (result.Text.Substring(prefix.Length))
                    {
                        case "VERIFIED\n":
                            pendingReconciliation.TryRemove(uncertainResult.OperationId.Value, out var ignoredAuthorization);
                            return BaselineReconciliationStatus.Verified;
                        case "NOT_FOUND_OR_UNREADABLE\n": return BaselineReconciliationStatus.NotFoundOrUnreadable;
                        case "CONFLICT\n": return BaselineReconciliationStatus.Conflict;
                        case "UNKNOWN\n": return BaselineReconciliationStatus.Unknown;
                        default: throw BaselineExecutionProtocol.Failure();
                    }
                }
            }
            catch
            {
                _ = operation.Completion.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else { var ignored = t.Exception; } }, TaskScheduler.Default);
                throw;
            }
        }
        private async Task<BaselineModuleExecutionResult> ParseCompletionAsync(DxlExecutionOperation operation, BaselineExecutionAuthorization authorization, DateTime start)
        {
            using (var result = await operation.Completion.ConfigureAwait(false))
            {
                if (result.IsFile) throw BaselineExecutionProtocol.Failure();
                return BaselineExecutionProtocol.Parse(result.Text, operation.CorrelationId, authorization, start);
            }
        }
        internal static Dictionary<string, string> Parameters(BulkBaselineDryRunItem item) => new Dictionary<string, string>
        {
            ["path"] = item.Module.CanonicalFullPath, ["moduleId"] = item.Module.ModuleId,
            ["database"] = item.Precondition.Facts.DatabaseIdentity, ["user"] = item.Precondition.Facts.CurrentUserId,
            ["type"] = item.Probe.Type.ToString(), ["suffix"] = item.Probe.Suffix, ["annotation"] = item.Probe.Annotation,
            ["major"] = item.ExpectedVersion.Major.ToString(CultureInfo.InvariantCulture), ["minor"] = item.ExpectedVersion.Minor.ToString(CultureInfo.InvariantCulture),
            ["expectedVersion"] = VersionString(item.ExpectedVersion), ["currentVersion"] = VersionString(item.CurrentVersion)
        };
        private static string VersionString(BaselineVersion version) => version == null ? "" : version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture) + (version.Suffix.Length == 0 ? "" : "(" + version.Suffix + ")");
        private static BaselineModuleExecutionResult Result(BaselineExecutionAuthorization authorization, BaselineWriteOutcome outcome, bool attempted,
            string reason, DateTime start, Guid? operationId = null, Task<BaselineModuleExecutionResult> late = null) => new BaselineModuleExecutionResult(
                authorization.Review.JobId, authorization.Item, outcome, BaselineMetadataSource.NativeDxl, attempted, reason, start, DateTime.UtcNow, operationId, late);
    }
}
