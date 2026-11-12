using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Processing
{
    public enum BatchAction { Create, Update }
    public enum BatchValueFormat { PlainText, OpaqueRichText }
    public enum BatchPosition { Keep, FirstInModule, After, FirstChild, LastChild }
    public enum BatchRowStatus { Ready, InvalidInput, Blocked, Applied, AlreadyApplied, Failed, Conflict, Unknown, PartialFailure, AppliedWithProblems, Cancelled, NotAttempted }
    public enum BatchMutation { None, Applied, Partial, Unknown }
    public enum BatchJournalState { Started, Succeeded, Failed, RetryableFailure, Partial, Unknown, AppliedWithProblems }

    public sealed class BatchAttribute
    {
        public BatchAttribute(string name, string value, BatchValueFormat format = BatchValueFormat.PlainText, string expectedPlainText = null)
        {
            Name = ProcessingGuard.Text(name, 256); Value = ProcessingGuard.Text(value, 65536, true, true);
            if (name != name.Trim() || !Enum.IsDefined(typeof(BatchValueFormat), format) ||
                (format == BatchValueFormat.OpaqueRichText ? expectedPlainText == null : expectedPlainText != null)) throw new ArgumentException("Invalid attribute format.");
            if (expectedPlainText != null) ProcessingGuard.Text(expectedPlainText, 65536, true, true);
            Format = format; ExpectedPlainText = expectedPlainText;
        }
        public string Name { get; }
        public string Value { get; }
        public BatchValueFormat Format { get; }
        public string ExpectedPlainText { get; }
    }
    public sealed class BatchChange
    {
        public BatchChange(string key, BatchAction action, string target, IEnumerable<BatchAttribute> attributes, string objectId = null,
            BatchPosition? position = null, string anchorId = null, IEnumerable<string> requiredAttributes = null)
        {
            Key = ProcessingGuard.Text(key, 256); Target = ProcessingGuard.Text(target, 4096);
            if (!Enum.IsDefined(typeof(BatchAction), action)) throw new ArgumentException("Invalid action.");
            Action = action; ObjectId = objectId;
            Position = position ?? (action == BatchAction.Create ? BatchPosition.FirstInModule : BatchPosition.Keep);
            AnchorId = anchorId;
            if (!Enum.IsDefined(typeof(BatchPosition), Position) || (action == BatchAction.Update ? string.IsNullOrWhiteSpace(objectId) : objectId != null) ||
                (action == BatchAction.Create && Position == BatchPosition.Keep) || (action == BatchAction.Update && Position != BatchPosition.Keep) ||
                ((Position == BatchPosition.Keep || Position == BatchPosition.FirstInModule) ? anchorId != null : string.IsNullOrWhiteSpace(anchorId)))
                throw new ArgumentException("Invalid object identity/position. Batch updates preserve hierarchy.");
            if (objectId != null) ProcessingGuard.Text(objectId, 256);
            if (anchorId != null) ProcessingGuard.Text(anchorId, 256);
            var values = (attributes ?? Enumerable.Empty<BatchAttribute>()).Take(33).ToArray();
            if (values.Length == 0 || values.Length > 32 || values.Any(a => a == null) || values.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != values.Length ||
                values.Sum(a => (long)a.Name.Length + a.Value.Length + (a.ExpectedPlainText?.Length ?? 0)) > 262144) throw new ArgumentException("Invalid attribute changes.");
            Attributes = new ReadOnlyCollection<BatchAttribute>(values);
            var required = (requiredAttributes ?? Enumerable.Empty<string>()).Take(33).ToArray();
            if (required.Length > 32 || required.Distinct(StringComparer.Ordinal).Count() != required.Length) throw new ArgumentException("Invalid required fields.");
            foreach (string name in required) ProcessingGuard.Text(name, 256);
            RequiredAttributes = new ReadOnlyCollection<string>(required);
            var fields = new List<string> { "BatchChange-v2", Key, Action.ToString(), Target, ObjectId ?? "", Position.ToString(), AnchorId ?? "", "attributes", ProcessingGuard.Number(values.Length) };
            foreach (var a in values.OrderBy(a => a.Name, StringComparer.Ordinal)) fields.AddRange(new[] { a.Name, a.Value, a.Format.ToString(), a.ExpectedPlainText ?? "" });
            fields.Add("required"); fields.Add(ProcessingGuard.Number(required.Length));
            fields.AddRange(required.OrderBy(n => n, StringComparer.Ordinal)); PayloadHash = ProcessingGuard.Hash(fields.ToArray());
        }
        public string Key { get; }
        public BatchAction Action { get; }
        public string Target { get; }
        public string ObjectId { get; }
        public BatchPosition Position { get; }
        public string AnchorId { get; }
        public IReadOnlyList<BatchAttribute> Attributes { get; }
        public IReadOnlyList<string> RequiredAttributes { get; }
        public string PayloadHash { get; }
    }
    public sealed class BatchInputRecord
    {
        public BatchInputRecord(long recordNumber, BatchChange change, string errorCode = "")
        { if (recordNumber < 1 || (change == null) == string.IsNullOrEmpty(errorCode)) throw new ArgumentException("Invalid input record."); RecordNumber = recordNumber; Change = change; ErrorCode = errorCode; }
        public long RecordNumber { get; }
        public BatchChange Change { get; }
        public string ErrorCode { get; }
    }
    public interface IBatchRecordSource { IEnumerable<BatchInputRecord> Read(CancellationToken cancellationToken); }
    public interface IBatchChangeValidator
    {
        string ConfigurationFingerprint { get; }
        IEnumerable<string> Validate(BatchChange change, CancellationToken cancellationToken);
    }
    public sealed class BatchOptions
    {
        public BatchOptions(string repositoryId, string operationNamespace, bool continueOnError = false, int maxAttempts = 3,
            TimeSpan? retryDelay = null, int maxRecords = 10000, long maxPlanBytes = 268435456, TimeSpan? previewLifetime = null)
        {
            RepositoryId = ProcessingGuard.Text(repositoryId, 1024); OperationNamespace = ProcessingGuard.Text(operationNamespace, 256);
            RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200); PreviewLifetime = previewLifetime ?? TimeSpan.FromMinutes(15);
            if (maxAttempts < 1 || maxAttempts > 5 || RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromSeconds(5) ||
                maxRecords < 1 || maxRecords > 100000 || maxPlanBytes < 65536 || maxPlanBytes > 1073741824 ||
                PreviewLifetime < TimeSpan.FromSeconds(1) || PreviewLifetime > TimeSpan.FromHours(24)) throw new ArgumentException("Invalid batch limits.");
            ContinueOnError = continueOnError; MaxAttempts = maxAttempts; MaxRecords = maxRecords; MaxPlanBytes = maxPlanBytes;
        }
        public string RepositoryId { get; }
        public string OperationNamespace { get; }
        public bool ContinueOnError { get; }
        public int MaxAttempts { get; }
        public TimeSpan RetryDelay { get; }
        public TimeSpan PreviewLifetime { get; }
        public int MaxRecords { get; }
        public long MaxPlanBytes { get; }
        public string IdempotencyKey(BatchChange change) => ProcessingGuard.Hash("BatchIdempotency-v1", RepositoryId, OperationNamespace, change.Key);
    }
    public sealed class BatchBeforeValue
    {
        public BatchBeforeValue(string name, string plainText, string richText) { Name = name; PlainText = plainText; RichText = richText; }
        public string Name { get; }
        public string PlainText { get; }
        public string RichText { get; }
    }
    public sealed class BatchInspection
    {
        public BatchInspection(bool canApply, string repositoryId, string actor, string targetId, string reviewStamp,
            IEnumerable<string> issues = null, IEnumerable<BatchBeforeValue> beforeValues = null)
        {
            CanApply = canApply; RepositoryId = repositoryId ?? ""; Actor = actor ?? ""; TargetId = targetId ?? ""; ReviewStamp = reviewStamp ?? "";
            Issues = new ReadOnlyCollection<string>((issues ?? Enumerable.Empty<string>()).Take(257).ToArray());
            BeforeValues = new ReadOnlyCollection<BatchBeforeValue>((beforeValues ?? Enumerable.Empty<BatchBeforeValue>()).Take(33).ToArray());
            if (Issues.Count > 256 || BeforeValues.Count > 32 || (canApply && (RepositoryId.Length == 0 || Actor.Length == 0 || TargetId.Length == 0 || ReviewStamp.Length == 0)))
                throw new ArgumentException("Invalid batch inspection.");
        }
        public bool CanApply { get; }
        public string RepositoryId { get; }
        public string Actor { get; }
        public string TargetId { get; }
        public string ReviewStamp { get; }
        public IReadOnlyList<string> Issues { get; }
        public IReadOnlyList<BatchBeforeValue> BeforeValues { get; }
    }
    public sealed class BatchDispatchResult
    {
        public BatchDispatchResult(BatchMutation mutation, string code, bool retryable = false, string resultingObjectId = "",
            string reviewStamp = "", bool auditRecorded = true, Task<BatchDispatchResult> completion = null)
        {
            if (!Enum.IsDefined(typeof(BatchMutation), mutation) || (retryable && mutation != BatchMutation.None) || (completion != null && mutation != BatchMutation.Unknown))
                throw new ArgumentException("Invalid mutation certainty.");
            Mutation = mutation; Code = code ?? ""; Retryable = retryable; ResultingObjectId = resultingObjectId ?? "";
            ReviewStamp = reviewStamp ?? ""; AuditRecorded = auditRecorded; Completion = completion;
        }
        public BatchMutation Mutation { get; }
        public string Code { get; }
        public bool Retryable { get; }
        public string ResultingObjectId { get; }
        public string ReviewStamp { get; }
        public bool AuditRecorded { get; }
        public Task<BatchDispatchResult> Completion { get; }
    }
    public interface IBatchChangeAdapter
    {
        Task<BatchInspection> InspectAsync(BatchChange change, BatchOptions options, CancellationToken token);
        // Implementations must retain native preview/approval, revalidate identity and classify mutation certainty.
        Task<BatchDispatchResult> ApplyAsync(BatchChange change, BatchInspection reviewed, BatchOptions options, CancellationToken token);
    }
    public interface IBatchConfirmation { Task<bool> ConfirmAsync(BatchPreview preview, CancellationToken token); }
    public sealed class RejectBatchConfirmation : IBatchConfirmation
    { public Task<bool> ConfirmAsync(BatchPreview preview, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(false); } }
    public sealed class BatchProgress
    {
        public BatchProgress(Guid jobId, string phase, long record, long completed) { JobId = jobId; Phase = phase; RecordNumber = record; Completed = completed; }
        public Guid JobId { get; }
        public string Phase { get; }
        public long RecordNumber { get; }
        public long Completed { get; }
    }
}
