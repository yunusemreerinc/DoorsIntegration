using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Processing
{
    public sealed class BatchService
    {
        private readonly IBatchChangeAdapter adapter;
        private readonly IBatchJournal journal;
        private readonly IBatchAuditSink audit;
        private readonly IBatchConfirmation confirmation;
        private readonly ProcessingStorageOptions storage;
        private readonly IProcessingClock clock;
        private readonly SemaphoreSlim active = new SemaphoreSlim(1, 1);
        private readonly object owner = new object();
        private readonly IBatchChangeValidator[] validators;
        public BatchService(IBatchChangeAdapter adapter, IBatchJournal journal, IBatchAuditSink audit, IBatchConfirmation confirmation,
            ProcessingStorageOptions storage, IProcessingClock clock, IEnumerable<IBatchChangeValidator> validators = null)
        {
            this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            this.audit = audit ?? throw new ArgumentNullException(nameof(audit)); this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.validators = (validators ?? Enumerable.Empty<IBatchChangeValidator>()).Take(65).ToArray();
            if (this.validators.Length > 64 || this.validators.Any(v => v == null || string.IsNullOrEmpty(v.ConfigurationFingerprint))) throw new ArgumentException("Invalid validators.");
        }
        public async Task<BatchPreview> PreviewAsync(IBatchRecordSource source, BatchOptions options, CancellationToken token = default(CancellationToken), IProgress<BatchProgress> progress = null)
        {
            if (source == null || options == null) throw new ArgumentNullException();
            if (!await active.WaitAsync(0, token).ConfigureAwait(false)) throw new ProcessingException("BatchBusy", "This batch service is already running.");
            try { return await PreviewCoreAsync(source, options, token, progress).ConfigureAwait(false); }
            finally { active.Release(); }
        }
        private async Task<BatchPreview> PreviewCoreAsync(IBatchRecordSource source, BatchOptions options, CancellationToken token, IProgress<BatchProgress> progress)
        {
            Guid id = Guid.NewGuid(); string directory = Path.Combine(storage.DirectoryPath, "plans", id.ToString("N")); Directory.CreateDirectory(directory);
            string capture = Path.Combine(directory, "captured.jsonl"), path = Path.Combine(directory, "preview.jsonl");
            var keys = new Dictionary<string, int>(StringComparer.Ordinal); var targets = new Dictionary<string, int>(StringComparer.Ordinal);
            var numbers = new HashSet<long>(); long count = 0, bytes = 0;
            // Capture once before contacting an adapter. Duplicate keys/overlapping updates block ALL matching rows.
            using (var output = NewWriter(capture))
                foreach (var record in source.Read(token))
                {
                    token.ThrowIfCancellationRequested();
                    if (record == null || ++count > options.MaxRecords || !numbers.Add(record.RecordNumber)) throw new ProcessingException("InputLimit", "Input limit or duplicate record number; preview is not executable.");
                    var change = record.Change;
                    if (change != null)
                    {
                        Count(keys, change.Key);
                        if (change.Action == BatchAction.Update) Count(targets, ProcessingGuard.Hash(change.Target, change.ObjectId));
                    }
                    var row = new BatchPreviewRow(record.RecordNumber, change, null, change == null ? BatchRowStatus.InvalidInput : BatchRowStatus.Ready, record.ErrorCode);
                    WriteBounded(output, ProcessingJson.Encode(BatchRowDto.From(row)), ref bytes, options.MaxPlanBytes);
                }
            long ready = 0, blocked = 0; bool complete = true, inspectionHalted = false;
            using (var input = ProcessingJson.Reader(capture))
            using (var output = NewWriter(path))
            {
                string line;
                while ((line = ProcessingJson.ReadLine(input, ProcessingJson.MaxLineChars, out bool oversized, out bool terminated)) != null)
                {
                    token.ThrowIfCancellationRequested(); if (oversized || !terminated) throw new ProcessingException("PlanCorrupt", "Captured input is incomplete.");
                    var row = ProcessingJson.Decode<BatchRowDto>(line).ToModel();
                    if (row.Change != null)
                    {
                        var change = row.Change;
                        if (keys[change.Key] > 1 || (change.Action == BatchAction.Update && targets[ProcessingGuard.Hash(change.Target, change.ObjectId)] > 1))
                            row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Conflict, "DuplicateKeyOrOverlappingUpdate");
                        else
                        {
                            if (inspectionHalted)
                            {
                                row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Blocked, "InspectionHalted");
                            }
                            else
                            try
                            {
                                using (var lease = journal.Acquire(options.IdempotencyKey(change)))
                                {
                                    var prior = lease.Current;
                                    if (prior != null && prior.PayloadHash != change.PayloadHash) row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Conflict, "IdempotencyPayloadConflict");
                                    else if (prior?.State == BatchJournalState.Succeeded) row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.AlreadyApplied, "HistoricalVerifiedResult", prior.ObjectId);
                                    else if (prior != null && IsHeld(prior.State)) row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Unknown, "ManualReconciliationRequired", prior.ObjectId);
                                    else if (prior != null && prior.Attempts >= options.MaxAttempts) row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Blocked, "AttemptLimit");
                                    else
                                    {
                                        var problems = Validate(change, token);
                                        var inspection = problems.Length == 0 ? await adapter.InspectAsync(change, options, token).ConfigureAwait(false) :
                                            new BatchInspection(false, options.RepositoryId, "", "", "", problems);
                                        bool allowed = inspection != null && inspection.CanApply && inspection.RepositoryId == options.RepositoryId;
                                        row = new BatchPreviewRow(row.RecordNumber, change, inspection, allowed ? BatchRowStatus.Ready : BatchRowStatus.Blocked,
                                            inspection == null ? "MissingInspection" : inspection.RepositoryId != options.RepositoryId ? "RepositoryMismatch" : allowed ? "" : "ValidationFailed");
                                    }
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (ProcessingException ex) { row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Blocked, ex.Code); }
                            catch { row = new BatchPreviewRow(row.RecordNumber, change, null, BatchRowStatus.Blocked, "InspectionFailed"); complete = false; inspectionHalted = true; }
                        }
                    }
                    if (row.Status == BatchRowStatus.Ready) ready++; else if (row.Status != BatchRowStatus.AlreadyApplied) blocked++;
                    WriteBounded(output, ProcessingJson.Encode(BatchRowDto.From(row)), ref bytes, options.MaxPlanBytes);
                    ProcessingGuard.Progress(progress, new BatchProgress(id, "Preview", row.RecordNumber, ready + blocked));
                }
            }
            var preview = new BatchPreview(owner, id, options, path, count, ready, blocked, clock.UtcNow.Add(options.PreviewLifetime), complete, ValidatorFingerprint());
            try { Audit(id, "PreviewCompleted", fingerprint: preview.Fingerprint, scope: options); return preview; }
            catch { preview.Dispose(); throw; }
        }
        public async Task<BatchReport> ApplyAsync(BatchPreview preview, CancellationToken token = default(CancellationToken), IProgress<BatchProgress> progress = null)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            if (!await active.WaitAsync(0, token).ConfigureAwait(false)) throw new ProcessingException("BatchBusy", "This batch service is already running.");
            try
            {
                if (!ReferenceEquals(preview.Owner, owner) || clock.UtcNow >= preview.ExpiresAtUtc || !preview.Consume()) throw new ProcessingException("ApprovalRequired", "A fresh unused preview from this service is required.");
                if (!preview.CanApply || preview.ValidatorFingerprint != ValidatorFingerprint()) throw new ProcessingException("BatchBlocked", "Resolve preview failures or changed validators before previewing again.");
                Audit(preview.JobId, "ApprovalRequested", fingerprint: preview.Fingerprint, scope: preview.Options);
                if (!await confirmation.ConfirmAsync(preview, token).ConfigureAwait(false))
                { Audit(preview.JobId, "ApprovalDeclined", fingerprint: preview.Fingerprint, scope: preview.Options); throw new ProcessingException("ApprovalRequired", "Batch was not explicitly approved. No row was dispatched."); }
                token.ThrowIfCancellationRequested();
                if (clock.UtcNow >= preview.ExpiresAtUtc) throw new ProcessingException("ApprovalRequired", "Batch preview expired during review.");
                Audit(preview.JobId, "ApprovalGranted", fingerprint: preview.Fingerprint, scope: preview.Options);
                return await RunAsync(preview, token, progress).ConfigureAwait(false);
            }
            finally { active.Release(); }
        }
        private async Task<BatchReport> RunAsync(BatchPreview preview, CancellationToken token, IProgress<BatchProgress> progress)
        {
            string directory = Path.Combine(storage.DirectoryPath, "reports", preview.JobId.ToString("N")); Directory.CreateDirectory(directory);
            string rowsPath = Path.Combine(directory, "rows.jsonl"); long count = 0, applied = 0, failed = 0; bool stop = false;
            try
            {
                using (var rows = NewWriter(rowsPath))
                using (var failures = NewWriter(Path.Combine(directory, "failures.jsonl")))
                {
                    foreach (var row in preview.ReadRows())
                    {
                        BatchRowResult result;
                        if (stop || token.IsCancellationRequested)
                        { stop = true; result = Result(row, BatchRowStatus.NotAttempted, "JobStopped", 0, canRepreview: row.Change != null); }
                        else if (row.Status != BatchRowStatus.Ready && row.Status != BatchRowStatus.AlreadyApplied)
                            result = Result(row, row.Status, row.Code, 0, canRepreview: row.Status == BatchRowStatus.Blocked && row.Code != "AttemptLimit");
                        else result = await RunRowAsync(preview, row, token, progress).ConfigureAwait(false);
                        count++; bool success = result.Status == BatchRowStatus.Applied || result.Status == BatchRowStatus.AlreadyApplied;
                        if (success) applied++; else failed++;
                        string line = ProcessingJson.Encode(BatchResultDto.From(result)); rows.WriteLine(line); rows.Flush();
                        if (!success) { failures.WriteLine(line); failures.Flush(); }
                        if (!success && (!preview.Options.ContinueOnError || result.Status == BatchRowStatus.Unknown || result.Status == BatchRowStatus.PartialFailure ||
                            result.Status == BatchRowStatus.AppliedWithProblems || !result.AuditRecorded || result.Code == "JournalWriteFailed" || result.Code == "NativeReadStillRunning")) stop = true;
                        if (result.Code.StartsWith("LateCompletion:", StringComparison.Ordinal)) stop = true;
                        ProcessingGuard.Progress(progress, new BatchProgress(preview.JobId, "CompletedRow", row.RecordNumber, count));
                    }
                }
                var report = new BatchReport(preview.JobId, rowsPath, count, applied, failed, stop);
                File.WriteAllText(Path.Combine(directory, "summary.json"), ProcessingJson.Encode(new Dictionary<string, string>
                { ["jobId"] = preview.JobId.ToString("N"), ["fingerprint"] = preview.Fingerprint, ["records"] = ProcessingGuard.Number(count),
                    ["appliedOrHistorical"] = ProcessingGuard.Number(applied), ["failedOrNotAttempted"] = ProcessingGuard.Number(failed), ["stopped"] = stop.ToString(),
                    ["atomic"] = "False", ["complianceAssessed"] = "False", ["disclosure"] = ProcessingDisclosure.Statement }), new UTF8Encoding(false));
                return report;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException)
            { throw new ProcessingException("ReportWriteFailed", "Batch report could not be completed. Earlier rows may have been applied; inspect the durable journal and report directory before any retry: " + directory, ex); }
        }
        private async Task<BatchRowResult> RunRowAsync(BatchPreview plan, BatchPreviewRow row, CancellationToken token, IProgress<BatchProgress> progress)
        {
            var change = row.Change; string key = plan.Options.IdempotencyKey(change);
            IBatchJournalLease lease;
            try { lease = journal.Acquire(key); }
            catch (ProcessingException ex) { return Result(row, BatchRowStatus.Blocked, ex.Code, 0, canRepreview: ex.Code == "IdempotencyBusy"); }
            using (lease)
            {
                var prior = lease.Current;
                if (prior != null && prior.PayloadHash != change.PayloadHash) return Result(row, BatchRowStatus.Conflict, "IdempotencyPayloadConflict", prior.Attempts);
                if (prior?.State == BatchJournalState.Succeeded) return Result(row, BatchRowStatus.AlreadyApplied, "HistoricalVerifiedResult", prior.Attempts, prior.ObjectId);
                if (prior != null && IsHeld(prior.State)) return Result(row, BatchRowStatus.Unknown, "ManualReconciliationRequired", prior.Attempts, prior.ObjectId);
                if (row.Status == BatchRowStatus.AlreadyApplied) return Result(row, BatchRowStatus.Conflict, "JournalChanged", 0);
                int attempts = prior?.Attempts ?? 0;
                while (attempts < plan.Options.MaxAttempts)
                {
                    if (token.IsCancellationRequested) return Result(row, BatchRowStatus.Cancelled, "CancelledBeforeDispatch", attempts, canRepreview: true);
                    try { Audit(plan.JobId, "AttemptAuthorized", row.RecordNumber, change, fingerprint: plan.Fingerprint, scope: plan.Options, inspection: row.Inspection); }
                    catch (ProcessingException) { return Result(row, BatchRowStatus.Failed, "AuditFailedBeforeDispatch", attempts, canRepreview: true, auditRecorded: false); }
                    attempts++;
                    try { lease.Append(new BatchJournalEntry(key, change.PayloadHash, BatchJournalState.Started, attempts)); }
                    catch { return Result(row, BatchRowStatus.Failed, "JournalWriteFailed", attempts); }
                    BatchDispatchResult dispatch;
                    bool adapterInvoked = false;
                    try
                    {
                        if (plan.ValidatorFingerprint != ValidatorFingerprint() || Validate(change, token).Length != 0) dispatch = new BatchDispatchResult(BatchMutation.None, "PreflightRecheckBlocked");
                        else { token.ThrowIfCancellationRequested(); adapterInvoked = true; dispatch = await adapter.ApplyAsync(change, row.Inspection, plan.Options, token).ConfigureAwait(false); }
                    }
                    catch { dispatch = new BatchDispatchResult(adapterInvoked ? BatchMutation.Unknown : BatchMutation.None, adapterInvoked ? "AdapterOutcomeUnknown" : "PreflightFailedBeforeDispatch"); }
                    if (dispatch == null) dispatch = new BatchDispatchResult(BatchMutation.Unknown, "MissingDispatchResult");
                    bool wasUncertain = dispatch.Mutation == BatchMutation.Unknown;
                    if (dispatch.Completion != null)
                    {
                        try { lease.Append(new BatchJournalEntry(key, change.PayloadHash, BatchJournalState.Unknown, attempts, code: "NativeCallStillRunning")); }
                        catch { /* Keep the lease and observe the original completion; never dispatch another row. */ }
                        ProcessingGuard.Progress(progress, new BatchProgress(plan.JobId, "WaitingForOriginalCompletion", row.RecordNumber, 0));
                        try { Audit(plan.JobId, "OutcomeUnknown", row.RecordNumber, change, scope: plan.Options, inspection: row.Inspection); } catch { /* Native operation is already in progress. */ }
                        try { dispatch = await dispatch.Completion.ConfigureAwait(false) ?? new BatchDispatchResult(BatchMutation.Unknown, "MissingLateResult"); }
                        catch { dispatch = new BatchDispatchResult(BatchMutation.Unknown, "LateOutcomeUnknown"); }
                    }
                    var state = dispatch.Mutation == BatchMutation.Applied ? (dispatch.AuditRecorded ? BatchJournalState.Succeeded : BatchJournalState.AppliedWithProblems) :
                        dispatch.Mutation == BatchMutation.Partial ? BatchJournalState.Partial : dispatch.Mutation == BatchMutation.Unknown ? BatchJournalState.Unknown :
                        dispatch.Retryable ? BatchJournalState.RetryableFailure : BatchJournalState.Failed;
                    try { lease.Append(new BatchJournalEntry(key, change.PayloadHash, state, attempts, dispatch.ResultingObjectId, dispatch.Code)); }
                    catch { return Result(row, BatchRowStatus.Unknown, "JournalWriteFailed", attempts, dispatch.ResultingObjectId); }
                    bool auditOk = dispatch.AuditRecorded;
                    try { Audit(plan.JobId, "AttemptCompleted", row.RecordNumber, change, dispatch.Code, dispatch.ReviewStamp, plan.Options, row.Inspection); }
                    catch { auditOk = false; }
                    if (dispatch.Mutation == BatchMutation.None && dispatch.Retryable && attempts < plan.Options.MaxAttempts && auditOk && !wasUncertain && !token.IsCancellationRequested)
                    {
                        var delay = TimeSpan.FromMilliseconds(Math.Min(5000, plan.Options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempts - 1)));
                        try { await clock.DelayAsync(delay, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return Result(row, BatchRowStatus.Cancelled, "CancelledBeforeRetry", attempts, canRepreview: true); }
                        continue;
                    }
                    var status = dispatch.Mutation == BatchMutation.Applied ? (auditOk ? BatchRowStatus.Applied : BatchRowStatus.AppliedWithProblems) :
                        dispatch.Mutation == BatchMutation.Partial ? BatchRowStatus.PartialFailure : dispatch.Mutation == BatchMutation.Unknown ? BatchRowStatus.Unknown :
                        token.IsCancellationRequested ? BatchRowStatus.Cancelled : BatchRowStatus.Failed;
                    return Result(row, status, wasUncertain ? "LateCompletion:" + dispatch.Code : dispatch.Code, attempts,
                        dispatch.ResultingObjectId, dispatch.Mutation == BatchMutation.None && attempts < plan.Options.MaxAttempts && !wasUncertain, auditOk, dispatch.ReviewStamp);
                }
                return Result(row, BatchRowStatus.Blocked, "AttemptLimit", attempts);
            }
        }
        public Task<BatchPreview> PreviewRetryAsync(BatchPreview original, BatchReport report, CancellationToken token = default(CancellationToken), IProgress<BatchProgress> progress = null)
        {
            if (original == null || report == null || !ReferenceEquals(original.Owner, owner) || report.JobId != original.JobId) throw new ArgumentException("Matching original plan/report required.");
            var selected = new HashSet<long>(report.ReadRows().Where(r => r.CanRepreview && r.Status != BatchRowStatus.Applied && r.Status != BatchRowStatus.AlreadyApplied &&
                r.Status != BatchRowStatus.Unknown && r.Status != BatchRowStatus.PartialFailure && r.Status != BatchRowStatus.AppliedWithProblems).Select(r => r.RecordNumber));
            return PreviewAsync(new RetrySource(original, selected), original.Options, token, progress);
        }
        private sealed class RetrySource : IBatchRecordSource
        {
            private readonly BatchPreview original; private readonly HashSet<long> selected;
            public RetrySource(BatchPreview original, HashSet<long> selected) { this.original = original; this.selected = selected; }
            public IEnumerable<BatchInputRecord> Read(CancellationToken token)
            { foreach (var row in original.ReadRows()) { token.ThrowIfCancellationRequested(); if (row.Change != null && selected.Contains(row.RecordNumber)) yield return new BatchInputRecord(row.RecordNumber, row.Change); } }
        }
        private static bool IsHeld(BatchJournalState state) => state == BatchJournalState.Started || state == BatchJournalState.Unknown || state == BatchJournalState.Partial || state == BatchJournalState.AppliedWithProblems;
        private static BatchRowResult Result(BatchPreviewRow row, BatchRowStatus status, string code, int attempts, string objectId = "", bool canRepreview = false, bool auditRecorded = true, string reviewedStamp = "") =>
            new BatchRowResult(row.RecordNumber, row.Change?.Key, status, code, attempts, objectId, canRepreview, auditRecorded, reviewedStamp);
        private void Audit(Guid id, string phase, long number = 0, BatchChange change = null, string code = "", string fingerprint = "", BatchOptions scope = null, BatchInspection inspection = null)
        {
            try { audit.Record(new BatchAuditEntry(clock.UtcNow, id, phase, number, change?.Key ?? "", change?.PayloadHash ?? "", code, fingerprint,
                scope?.RepositoryId ?? "", scope?.OperationNamespace ?? "", change?.Target ?? "", change?.Action.ToString() ?? "", change?.ObjectId ?? "", inspection?.Actor ?? "", inspection?.TargetId ?? "")); }
            catch (Exception ex) { throw new ProcessingException("AuditFailed", "Batch audit could not be written. No new mutation was authorized.", ex); }
        }
        private string ValidatorFingerprint() => ProcessingGuard.Hash(validators.Select(v => v.ConfigurationFingerprint).ToArray());
        private string[] Validate(BatchChange change, CancellationToken token) => validators.SelectMany(v => v.Validate(change, token) ?? throw new ProcessingException("ValidatorFailed", "Validator returned no result.")).Take(256).ToArray();
        private static StreamWriter NewWriter(string path) => new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false, true)) { NewLine = "\n" };
        private static void Count(Dictionary<string, int> values, string key) { values.TryGetValue(key, out int count); values[key] = count + 1; }
        private static void WriteBounded(TextWriter writer, string line, ref long bytes, long max)
        { bytes += Encoding.UTF8.GetByteCount(line) + 1; if (bytes > max) throw new ProcessingException("PlanLimit", "Captured input and preview exceeded the configured disk limit; no executable plan was produced."); writer.WriteLine(line); }
    }
    public static class BatchReportExporter
    {
        public static void ExportFailuresCsv(BatchReport report, string path)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            using (var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
            {
                output.WriteLine("record,key,status,code,attempts,objectId,canRepreview,auditRecorded");
                foreach (var row in report.ReadRows().Where(r => r.Status != BatchRowStatus.Applied && r.Status != BatchRowStatus.AlreadyApplied))
                    output.WriteLine(string.Join(",", new[] { ProcessingGuard.Number(row.RecordNumber), row.Key, row.Status.ToString(), row.Code,
                        ProcessingGuard.Number(row.Attempts), row.ObjectId, row.CanRepreview.ToString(), row.AuditRecorded.ToString() }.Select(Csv)));
            }
        }
        internal static string Csv(string value)
        {
            value = value ?? ""; string trimmed = value.TrimStart();
            if ((trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0) || (value.Length > 0 && (value[0] == '\t' || value[0] == '\r' || value[0] == '\n'))) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
