using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public sealed class BatchPreviewRow
    {
        public BatchPreviewRow(long recordNumber, BatchChange change, BatchInspection inspection, BatchRowStatus status, string code, string priorObjectId = "")
        { RecordNumber = recordNumber; Change = change; Inspection = inspection; Status = status; Code = code; PriorObjectId = priorObjectId; }
        public long RecordNumber { get; }
        public BatchChange Change { get; }
        public BatchInspection Inspection { get; }
        public BatchRowStatus Status { get; }
        public string Code { get; }
        public string PriorObjectId { get; }
    }
    public sealed class BatchPreview : IDisposable
    {
        private FileStream lease;
        private int consumed;
        internal BatchPreview(object owner, Guid id, BatchOptions options, string path, long count, long ready, long blocked,
            DateTime expiresAtUtc, bool complete, string validatorFingerprint)
        {
            Owner = owner; JobId = id; Options = options; FilePath = path; RecordCount = count; ReadyCount = ready; BlockedCount = blocked;
            ExpiresAtUtc = expiresAtUtc; IsComplete = complete;
            ValidatorFingerprint = validatorFingerprint;
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string fileHash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) fileHash = BitConverter.ToString(sha.ComputeHash(lease)).Replace("-", "");
            Fingerprint = ProcessingGuard.Hash("BatchPreview-v1", fileHash, options.RepositoryId, options.OperationNamespace,
                options.ContinueOnError.ToString(), ProcessingGuard.Number(options.MaxAttempts), ProcessingGuard.Number(options.RetryDelay.Ticks), validatorFingerprint);
        }
        internal object Owner { get; }
        internal bool Consume() => !IsDisposed && Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
        public Guid JobId { get; }
        public BatchOptions Options { get; }
        public string FilePath { get; }
        public string Fingerprint { get; }
        public string ValidatorFingerprint { get; }
        public DateTime ExpiresAtUtc { get; }
        public long RecordCount { get; }
        public long ReadyCount { get; }
        public long BlockedCount { get; }
        public bool IsComplete { get; }
        public bool IsDisposed => Volatile.Read(ref lease) == null;
        public bool CanApply => !IsDisposed && IsComplete && RecordCount > 0 && (ReadyCount > 0 || BlockedCount == 0) && (BlockedCount == 0 || Options.ContinueOnError);
        public IEnumerable<BatchPreviewRow> ReadRows()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(BatchPreview));
            using (var reader = ProcessingJson.Reader(FilePath))
            {
                string line; long count = 0;
                while ((line = ProcessingJson.ReadLine(reader, ProcessingJson.MaxLineChars, out bool oversized, out bool terminated)) != null)
                {
                    if (IsDisposed) throw new ObjectDisposedException(nameof(BatchPreview));
                    if (oversized || !terminated || ++count > RecordCount) throw new ProcessingException("PlanCorrupt", "Preview file is incomplete or invalid.");
                    yield return ProcessingJson.Decode<BatchRowDto>(line).ToModel();
                }
                if (count != RecordCount) throw new ProcessingException("PlanCorrupt", "Preview record count changed.");
            }
        }
        public void Dispose() { Interlocked.Exchange(ref lease, null)?.Dispose(); }
    }
    public sealed class BatchRowResult
    {
        public BatchRowResult(long recordNumber, string key, BatchRowStatus status, string code, int attempts, string objectId,
            bool canRepreview, bool auditRecorded = true, string reviewedStamp = "")
        { RecordNumber = recordNumber; Key = key ?? ""; Status = status; Code = code ?? ""; Attempts = attempts; ObjectId = objectId ?? ""; CanRepreview = canRepreview; AuditRecorded = auditRecorded; ReviewedStamp = reviewedStamp; }
        public long RecordNumber { get; }
        public string Key { get; }
        public BatchRowStatus Status { get; }
        public string Code { get; }
        public int Attempts { get; }
        public string ObjectId { get; }
        public bool CanRepreview { get; }
        public bool AuditRecorded { get; }
        public string ReviewedStamp { get; }
    }
    public sealed class BatchReport
    {
        internal BatchReport(Guid jobId, string path, long records, long applied, long failed, bool stopped)
        { JobId = jobId; RowsPath = path; RecordCount = records; AppliedCount = applied; FailedCount = failed; Stopped = stopped; }
        public Guid JobId { get; }
        public string RowsPath { get; }
        public string FailuresPath => Path.Combine(Path.GetDirectoryName(RowsPath), "failures.jsonl");
        public long RecordCount { get; }
        public long AppliedCount { get; }
        public long FailedCount { get; }
        public bool Stopped { get; }
        public IEnumerable<BatchRowResult> ReadRows()
        {
            using (var reader = ProcessingJson.Reader(RowsPath))
            {
                string line; long count = 0;
                while ((line = ProcessingJson.ReadLine(reader, ProcessingJson.MaxLineChars, out bool oversized, out bool terminated)) != null)
                {
                    if (oversized || !terminated || ++count > RecordCount) throw new ProcessingException("ReportCorrupt", "Batch report is incomplete.");
                    yield return ProcessingJson.Decode<BatchResultDto>(line).ToModel();
                }
                if (count != RecordCount) throw new ProcessingException("ReportCorrupt", "Batch report count changed.");
            }
        }
    }

    [DataContract]
    public sealed class BatchJournalEntry
    {
        public BatchJournalEntry(string key, string payloadHash, BatchJournalState state, int attempts, string objectId = "", string code = "")
        { Key = key; PayloadHash = payloadHash; State = state; Attempts = attempts; ObjectId = objectId; Code = code; Validate(); }
        [DataMember(Order = 0)] public string Key { get; private set; }
        [DataMember(Order = 1)] public string PayloadHash { get; private set; }
        [DataMember(Order = 2)] public BatchJournalState State { get; private set; }
        [DataMember(Order = 3)] public int Attempts { get; private set; }
        [DataMember(Order = 4)] public string ObjectId { get; private set; }
        [DataMember(Order = 5)] public string Code { get; private set; }
        internal void Validate()
        {
            if (!IsHash(Key) || !IsHash(PayloadHash) || !Enum.IsDefined(typeof(BatchJournalState), State) || Attempts < 1 || Attempts > 5) throw new SerializationException("Invalid idempotency record.");
            ProcessingGuard.Text(ObjectId, 4096, empty: true); ProcessingGuard.Text(Code, 256, empty: true);
        }
        internal static bool IsHash(string value) => value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
    public interface IBatchJournalLease : IDisposable
    {
        BatchJournalEntry Current { get; }
        void Append(BatchJournalEntry entry);
    }
    public interface IBatchJournal
    {
        IBatchJournalLease Acquire(string key);
    }
    public sealed class FileBatchJournal : IBatchJournal
    {
        private readonly ProcessingStorageOptions options;
        public FileBatchJournal(ProcessingStorageOptions options) { this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        public IBatchJournalLease Acquire(string key)
        {
            if (!BatchJournalEntry.IsHash(key)) throw new ArgumentException("Invalid idempotency key.");
            string root = Path.Combine(options.DirectoryPath, "journal"); Directory.CreateDirectory(root);
            FileStream lease;
            try { lease = new FileStream(Path.Combine(root, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new ProcessingException("IdempotencyBusy", "Another process owns this operation key. No mutation was dispatched by this attempt.", ex); }
            try { return new Lease(key, Path.Combine(root, key + ".jsonl"), lease); }
            catch (ProcessingException) { lease.Dispose(); throw; }
            catch (Exception ex) { lease.Dispose(); throw new ProcessingException("JournalCorrupt", "Idempotency evidence could not be read; review it before further writes.", ex); }
        }
        private sealed class Lease : IBatchJournalLease
        {
            private readonly string key, path;
            private FileStream lease;
            public Lease(string key, string path, FileStream lease)
            {
                this.key = key; this.path = path; this.lease = lease;
                if (!File.Exists(path)) return;
                using (var reader = ProcessingJson.Reader(path))
                {
                    string line; int count = 0;
                    while ((line = ProcessingJson.ReadLine(reader, 32768, out bool oversized, out bool terminated)) != null)
                    {
                        if (oversized || !terminated || ++count > 32) throw new ProcessingException("JournalCorrupt", "Idempotency journal requires manual review.");
                        try { var entry = ProcessingJson.Decode<BatchJournalEntry>(line); entry.Validate(); Check(entry); Current = entry; }
                        catch (Exception ex) when (!(ex is ProcessingException)) { throw new ProcessingException("JournalCorrupt", "Idempotency journal requires manual review.", ex); }
                    }
                }
            }
            public BatchJournalEntry Current { get; private set; }
            private void Check(BatchJournalEntry entry)
            {
                if (entry.Key != key || (Current != null && entry.PayloadHash != Current.PayloadHash)) throw new ProcessingException("JournalCorrupt", "Idempotency identity changed.");
                if (Current == null) { if (entry.State != BatchJournalState.Started || entry.Attempts != 1) throw new ProcessingException("JournalCorrupt", "Invalid initial idempotency state."); return; }
                bool retry = (Current.State == BatchJournalState.Failed || Current.State == BatchJournalState.RetryableFailure) &&
                    entry.State == BatchJournalState.Started && entry.Attempts == Current.Attempts + 1;
                bool complete = (Current.State == BatchJournalState.Started || Current.State == BatchJournalState.Unknown) &&
                    entry.State != BatchJournalState.Started && entry.Attempts == Current.Attempts;
                if (!retry && !complete) throw new ProcessingException("JournalCorrupt", "Illegal idempotency transition; no replay permitted.");
            }
            public void Append(BatchJournalEntry entry)
            {
                if (lease == null) throw new ObjectDisposedException(nameof(Lease));
                entry.Validate(); Check(entry); ProcessingJson.DurableAppend(path, ProcessingJson.Encode(entry)); Current = entry;
            }
            public void Dispose() { Interlocked.Exchange(ref lease, null)?.Dispose(); }
        }
    }
    public interface IBatchAuditSink { void Record(BatchAuditEntry entry); }
    [DataContract]
    public sealed class BatchAuditEntry
    {
        public BatchAuditEntry(DateTime timestampUtc, Guid jobId, string phase, long recordNumber = 0, string key = "", string payloadHash = "", string code = "", string fingerprint = "",
            string repositoryId = "", string operationNamespace = "", string target = "", string action = "", string objectId = "", string actor = "", string targetId = "")
        {
            TimestampUtc = timestampUtc.ToString("O"); JobId = jobId.ToString("N"); Phase = phase; RecordNumber = recordNumber; Key = key; PayloadHash = payloadHash; Code = code; Fingerprint = fingerprint;
            RepositoryId = repositoryId; OperationNamespace = operationNamespace; Target = target; Action = action; ObjectId = objectId; Actor = actor; TargetId = targetId;
        }
        [DataMember] public string TimestampUtc { get; private set; }
        [DataMember] public string JobId { get; private set; }
        [DataMember] public string Phase { get; private set; }
        [DataMember] public long RecordNumber { get; private set; }
        [DataMember] public string Key { get; private set; }
        [DataMember] public string PayloadHash { get; private set; }
        [DataMember] public string Code { get; private set; }
        [DataMember] public string Fingerprint { get; private set; }
        [DataMember] public string RepositoryId { get; private set; }
        [DataMember] public string OperationNamespace { get; private set; }
        [DataMember] public string Target { get; private set; }
        [DataMember] public string Action { get; private set; }
        [DataMember] public string ObjectId { get; private set; }
        [DataMember] public string Actor { get; private set; }
        [DataMember] public string TargetId { get; private set; }
    }
    public sealed class FileBatchAuditSink : IBatchAuditSink
    {
        private readonly string path; private readonly object sync = new object();
        public FileBatchAuditSink(ProcessingStorageOptions options) { path = Path.Combine((options ?? throw new ArgumentNullException(nameof(options))).DirectoryPath, "audit.jsonl"); }
        public void Record(BatchAuditEntry entry) { lock (sync) ProcessingJson.DurableAppend(path, ProcessingJson.Encode(entry ?? throw new ArgumentNullException(nameof(entry)))); }
    }

    // Storage DTOs have no executable payload. Public input always goes through validated constructors.
    [DataContract] internal sealed class BatchAttributeDto
    {
        [DataMember] public string Name;
        [DataMember] public string Value;
        [DataMember] public BatchValueFormat Format;
        [DataMember] public string ExpectedPlainText;
    }
    [DataContract] internal sealed class BatchBeforeDto
    {
        [DataMember] public string Name;
        [DataMember] public string PlainText;
        [DataMember] public string RichText;
    }
    [DataContract] internal sealed class BatchRowDto
    {
        [DataMember] public long RecordNumber;
        [DataMember] public string Key, Target, ObjectId, AnchorId, Code, PriorObjectId;
        [DataMember] public BatchAction Action;
        [DataMember] public BatchPosition Position;
        [DataMember] public BatchAttributeDto[] Attributes;
        [DataMember] public string[] Required;
        [DataMember] public BatchRowStatus Status;
        [DataMember] public bool CanApply;
        [DataMember] public string RepositoryId, Actor, TargetId, ReviewStamp;
        [DataMember] public string[] Issues;
        [DataMember] public BatchBeforeDto[] Before;
        public static BatchRowDto From(BatchPreviewRow row)
        {
            var change = row.Change; var inspection = row.Inspection;
            return new BatchRowDto { RecordNumber = row.RecordNumber, Key = change?.Key, Target = change?.Target, ObjectId = change?.ObjectId,
                AnchorId = change?.AnchorId, Action = change?.Action ?? BatchAction.Create, Position = change?.Position ?? BatchPosition.FirstInModule,
                Attributes = change?.Attributes.Select(a => new BatchAttributeDto { Name = a.Name, Value = a.Value, Format = a.Format, ExpectedPlainText = a.ExpectedPlainText }).ToArray(),
                Required = change?.RequiredAttributes.ToArray(), Status = row.Status, Code = row.Code, PriorObjectId = row.PriorObjectId,
                CanApply = inspection?.CanApply ?? false, RepositoryId = inspection?.RepositoryId, Actor = inspection?.Actor, TargetId = inspection?.TargetId,
                ReviewStamp = inspection?.ReviewStamp, Issues = inspection?.Issues.ToArray(), Before = inspection?.BeforeValues.Select(v => new BatchBeforeDto { Name = v.Name, PlainText = v.PlainText, RichText = v.RichText }).ToArray() };
        }
        public BatchPreviewRow ToModel()
        {
            if (RecordNumber < 1 || !Enum.IsDefined(typeof(BatchRowStatus), Status)) throw new SerializationException("Invalid preview row.");
            var change = Key == null ? null : new BatchChange(Key, Action, Target,
                (Attributes ?? new BatchAttributeDto[0]).Select(a => new BatchAttribute(a.Name, a.Value, a.Format, a.ExpectedPlainText)), ObjectId, Position, AnchorId, Required);
            var inspection = ReviewStamp == null ? null : new BatchInspection(CanApply, RepositoryId, Actor, TargetId, ReviewStamp, Issues,
                Before?.Select(v => new BatchBeforeValue(v.Name, v.PlainText, v.RichText)));
            return new BatchPreviewRow(RecordNumber, change, inspection, Status, Code ?? "", PriorObjectId ?? "");
        }
    }
    [DataContract] internal sealed class BatchResultDto
    {
        [DataMember] public long RecordNumber;
        [DataMember] public string Key, Code, ObjectId, ReviewedStamp;
        [DataMember] public BatchRowStatus Status;
        [DataMember] public int Attempts;
        [DataMember] public bool CanRepreview, AuditRecorded;
        public static BatchResultDto From(BatchRowResult row) => new BatchResultDto { RecordNumber = row.RecordNumber, Key = row.Key, Code = row.Code,
            ObjectId = row.ObjectId, Status = row.Status, Attempts = row.Attempts, CanRepreview = row.CanRepreview, AuditRecorded = row.AuditRecorded, ReviewedStamp = row.ReviewedStamp };
        public BatchRowResult ToModel()
        { if (!Enum.IsDefined(typeof(BatchRowStatus), Status) || RecordNumber < 1 || Attempts < 0 || Attempts > 5) throw new SerializationException("Invalid report row."); return new BatchRowResult(RecordNumber, Key, Status, Code, Attempts, ObjectId, CanRepreview, AuditRecorded, ReviewedStamp); }
    }
}
