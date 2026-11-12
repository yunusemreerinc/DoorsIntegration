using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DoorsIntegration.Core
{
    public interface IBaselineJournalProtector
    {
        byte[] Protect(byte[] plaintext);
        byte[] Unprotect(byte[] ciphertext);
    }
    public sealed class WindowsBaselineJournalProtector : IBaselineJournalProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DoorsIntegration.BulkBaselineJournal.v2");
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
    public sealed class BulkBaselineJournalOptions
    {
        public BulkBaselineJournalOptions() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "baseline-jobs")) { }
        public BulkBaselineJournalOptions(string directoryPath, long maxJournalBytes = 64L * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("Journal directory required.");
            if (maxJournalBytes < 1024 || maxJournalBytes > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxJournalBytes));
            DirectoryPath = Path.GetFullPath(directoryPath); MaxJournalBytes = maxJournalBytes;
        }
        public string DirectoryPath { get; }
        public long MaxJournalBytes { get; }
    }
    public sealed class FileBulkBaselineJobJournal : IBulkBaselineJobJournal, IBaselineJobAuditStore
    {
        private readonly BulkBaselineJournalOptions options;
        private readonly IBaselineJournalProtector protector;
        public FileBulkBaselineJobJournal(BulkBaselineJournalOptions options, IBaselineJournalProtector protector = null)
        { this.options = options ?? throw new ArgumentNullException(nameof(options)); this.protector = protector ?? new WindowsBaselineJournalProtector(); }
        // Diagnostic peek only, not a lease or write authorization. Atomic cache may lag the audit head.
        public BulkBaselineJobRecord ReadCachedSnapshot(Guid jobId) => ReadCache(Path.Combine(options.DirectoryPath, jobId.ToString("N") + ".json"), options, jobId, protector).Job;
        public IEnumerable<Guid> EnumerateJobIds(CancellationToken token)
        {
            if (!Directory.Exists(options.DirectoryPath)) yield break;
            var found = new HashSet<Guid>();
            foreach (var path in Directory.EnumerateFileSystemEntries(options.DirectoryPath))
            {
                token.ThrowIfCancellationRequested(); string name = Path.GetFileName(path);
                if (!(name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".audit", StringComparison.Ordinal))) continue;
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out var id) && found.Add(id)) yield return id;
            }
        }
        public IEnumerable<BaselineJobAuditEntry> ReadAudit(Guid id, long throughRevision, CancellationToken token)
        {
            var replay = new ReplayState(protector);
            foreach (var file in AuditFiles(options, id))
            {
                token.ThrowIfCancellationRequested(); var envelope = ReadEvent(file, options);
                if (envelope.Projection.Revision > throughRevision) break;
                replay.Accept(envelope, id); envelope.Projection.Hash = envelope.Hash; yield return envelope.Projection;
            }
            if (replay.Job != null) replay.Job.Validate();
        }
        public IBulkBaselineJobJournalLease Acquire(Guid jobId)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("Job ID required.");
            Directory.CreateDirectory(options.DirectoryPath);
            FileStream ownership;
            try { ownership = new FileStream(Path.Combine(options.DirectoryPath, jobId.ToString("N") + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw BaselineJobGuard.Failure("Job journal is owned by another runner/process; no write dispatched."); }
            try { return new Lease(jobId, options, ownership, protector); }
            catch { ownership.Dispose(); throw; }
        }
        [DataContract]
        private sealed class Envelope
        {
            [DataMember(Order = 0, IsRequired = true)] internal int Schema;
            [DataMember(Order = 1, IsRequired = true)] internal string Checksum;
            [DataMember(Order = 2, EmitDefaultValue = false)] internal BulkBaselineJobRecord Job;
            [DataMember(Order = 3, EmitDefaultValue = false)] internal string ProtectedPayload;
            [DataMember(Order = 4, EmitDefaultValue = false)] internal string AuditHead;
        }
        [DataContract] private sealed class ModuleChange
        {
            [DataMember(Order = 0)] internal int Index;
            [DataMember(Order = 1)] internal BulkBaselineJobModule Module;
        }
        [DataContract] private sealed class Delta
        {
            [DataMember(Order = 0)] internal BulkBaselineJobRecord Initial;
            [DataMember(Order = 1)] internal long Revision;
            [DataMember(Order = 2)] internal BulkBaselineModuleStatus Status;
            [DataMember(Order = 3)] internal DateTime UpdatedUtc;
            [DataMember(Order = 4)] internal long ElapsedTicks;
            [DataMember(Order = 5)] internal bool Cancelled;
            [DataMember(Order = 6)] internal BaselineAuditAction Action;
            [DataMember(Order = 7)] internal DateTime? Started;
            [DataMember(Order = 8)] internal DateTime? Ended;
            [DataMember(Order = 9)] internal BaselineApprovedPlan Approval;
            [DataMember(Order = 10)] internal List<ModuleChange> Changes;
        }
        [DataContract] private sealed class AuditEnvelope
        {
            [DataMember(Order = 0)] internal int Schema;
            [DataMember(Order = 1)] internal BaselineJobAuditEntry Projection;
            [DataMember(Order = 2)] internal string ProtectedDelta;
            [DataMember(Order = 3)] internal string Hash;
        }
        private sealed class Lease : IBulkBaselineJobJournalLease
        {
            private readonly Guid jobId;
            private readonly string path;
            private readonly BulkBaselineJournalOptions options;
            private readonly IBaselineJournalProtector protector;
            private FileStream ownership;
            private string auditHead = "";
            private bool legacy;
            private bool faulted;
            private int auditRecords;
            private long auditBytes;
            internal Lease(Guid jobId, BulkBaselineJournalOptions options, FileStream ownership, IBaselineJournalProtector protector)
            {
                this.protector = protector;
                this.jobId = jobId; this.options = options; this.ownership = ownership;
                path = Path.Combine(options.DirectoryPath, jobId.ToString("N") + ".json");
                try
                {
                    Envelope cache = File.Exists(path) ? ReadCache(path, options, jobId, protector) : null;
                    var replay = new ReplayState(protector); bool cacheMatched = cache == null;
                    foreach (var file in AuditFiles(options, jobId))
                    {
                        auditRecords++; auditBytes += new FileInfo(file).Length;
                        var envelope = ReadEvent(file, options); replay.Accept(envelope, jobId);
                        if (cache != null && replay.Job.Revision == cache.Job.Revision)
                            cacheMatched = Checksum(replay.Job) == cache.Checksum && (cache.Schema == 1 || cache.AuditHead == envelope.Hash);
                    }
                    if (replay.Job != null)
                    { replay.Job.Validate(); if (!cacheMatched || cache != null && cache.Job.Revision > replay.Job.Revision) throw new SerializationException("Audit/cache contradiction."); Current = replay.Job; auditHead = replay.Hash; }
                    else if (cache != null)
                    { if (cache.Schema != 1) throw new SerializationException("Audit history missing."); Current = cache.Job; legacy = true; }
                }
                catch (Exception) { throw new BulkBaselineJournalException("Job journal is corrupt, incompatible or unreadable. It was not reset; no retry/write authorized."); }
            }
            public BulkBaselineJobRecord Current { get; private set; }
            public void Save(BulkBaselineJobRecord record)
            {
                if (ownership == null) throw new ObjectDisposedException(nameof(Lease));
                if (faulted) throw BaselineJobGuard.Failure("Failed journal lease must be closed and reloaded before further work.");
                if (record == null || record.JobId != jobId || record.Revision != (Current == null ? 0 : checked(Current.Revision + 1)))
                    throw BaselineJobGuard.Failure("Journal revision/identity conflict; reload instead of retrying a mutation.");
                record.Validate();
                if (Current != null && record.InputFingerprint != Current.InputFingerprint) throw BaselineJobGuard.Failure("Immutable job input changed.");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (legacy) { auditHead = Append(Current, null, "", true); legacy = false; }
                    auditHead = Append(record, Current, auditHead, false); // Commit append-only evidence before the replaceable cache.
                    var envelope = new Envelope { Schema = 2, ProtectedPayload = Protect(record, typeof(BulkBaselineJobRecord), protector), Checksum = Checksum(record), AuditHead = auditHead };
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        using (var bounded = new BoundedWriteStream(output, options.MaxJournalBytes)) Serializer(typeof(Envelope)).WriteObject(bounded, envelope);
                        output.Flush(true);
                    }
                    // Same directory/volume. No delete+move fallback: unsupported atomic replace fails closed.
                    if (!File.Exists(path)) File.Move(temporary, path);
                    else File.Replace(temporary, path, null);
                    Current = record;
                }
                catch (Exception error)
                { faulted = true; throw new BulkBaselineJournalException("Atomic journal checkpoint failed. No subsequent module was dispatched. A persisted Running intent requires reconciliation. ExceptionType=" + error.GetType().FullName + "; HResult=0x" + error.HResult.ToString("X8")); }
                finally
                {
                    // Only this lease's uniquely named, explicitly owned temporary file is removed.
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            public void Dispose() { ownership?.Dispose(); ownership = null; }
            private string Append(BulkBaselineJobRecord record, BulkBaselineJobRecord previous, string previousHash, bool imported)
            {
                string directory = AuditDirectory(options, jobId); Directory.CreateDirectory(directory);
                if (auditRecords >= 100000) throw new IOException("Audit revision limit reached.");
                var changes = new List<ModuleChange>();
                for (int index = 0; index < record.Modules.Count; index++)
                    if (previous == null || !ReferenceEquals(record.Modules[index], previous.Modules[index])) changes.Add(new ModuleChange { Index = index, Module = record.Modules[index] });
                var approval = record.LastApprovedPlan != null && record.LastApprovedPlan.ApprovalId != previous?.LastApprovedPlan?.ApprovalId ? record.LastApprovedPlan : null;
                var delta = new Delta { Initial = previous == null ? record : null, Revision = record.Revision, Status = record.Status, UpdatedUtc = record.UpdatedAtUtc,
                    ElapsedTicks = record.Elapsed.Ticks, Cancelled = record.CancellationRequested, Action = record.LastAuditAction, Started = record.ExecutionStartedAtUtc,
                    Ended = record.ExecutionEndedAtUtc, Approval = approval, Changes = previous == null ? new List<ModuleChange>() : changes };
                var entry = new BaselineJobAuditEntry { JobId = record.JobId, Revision = record.Revision, RecordedAtUtc = record.UpdatedAtUtc,
                    Action = imported ? BaselineAuditAction.LegacyImported.ToString() : record.LastAuditAction.ToString(), PreviousHash = previousHash, Status = record.Status.ToString(),
                    Context = previous == null ? record.AuditContext : null, ApprovedPlan = approval, modules = changes.Select(c => new BaselineAuditModuleState(c.Module)).ToList() };
                var envelope = new AuditEnvelope { Schema = 1, Projection = entry, ProtectedDelta = Protect(delta, typeof(Delta), protector) };
                envelope.Hash = EventHash(envelope);
                string final = Path.Combine(directory, record.Revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json");
                string temporary = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    { using (var bounded = new BoundedWriteStream(output, options.MaxJournalBytes)) Serializer(typeof(AuditEnvelope)).WriteObject(bounded, envelope); output.Flush(true); }
                    long bytes = new FileInfo(temporary).Length;
                    if (auditBytes + bytes > 4294967296L) throw new IOException("Audit byte limit reached.");
                    File.Move(temporary, final); // Create-new publication: NEVER replace/delete a committed revision.
                    auditRecords++; auditBytes += bytes;
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                return envelope.Hash;
            }
        }
        private sealed class ReplayState
        {
            private readonly IBaselineJournalProtector protector;
            internal ReplayState(IBaselineJournalProtector protector) { this.protector = protector; }
            internal BulkBaselineJobRecord Job;
            internal string Hash = "";
            internal void Accept(AuditEnvelope envelope, Guid id)
            {
                var p = envelope.Projection;
                if (p.JobId != id || p.PreviousHash != Hash || envelope.Hash != EventHash(envelope)) throw new SerializationException("Audit chain invalid.");
                var delta = (Delta)Unprotect(envelope.ProtectedDelta, typeof(Delta), protector);
                if (Job == null)
                { if (delta.Initial == null || delta.Initial.JobId != id || delta.Initial.Revision != p.Revision || p.PreviousHash != "") throw new SerializationException("Audit genesis invalid."); Job = delta.Initial; }
                else
                {
                    if (delta.Initial != null || p.Revision != Job.Revision + 1 || delta.Revision != p.Revision) throw new SerializationException("Audit delta invalid.");
                    Job = Job.Replay(delta.Revision, delta.Status, delta.UpdatedUtc, TimeSpan.FromTicks(delta.ElapsedTicks), delta.Cancelled, delta.Action, delta.Started, delta.Ended, delta.Approval,
                        delta.Changes.Select(c => new KeyValuePair<int, BulkBaselineJobModule>(c.Index, c.Module)));
                }
                Hash = envelope.Hash;
            }
        }
        private static string AuditDirectory(BulkBaselineJournalOptions options, Guid id) => Path.Combine(options.DirectoryPath, id.ToString("N") + ".audit");
        private static IEnumerable<string> AuditFiles(BulkBaselineJournalOptions options, Guid id)
        {
            string directory = AuditDirectory(options, id); if (!Directory.Exists(directory)) return Array.Empty<string>();
            var files = Directory.EnumerateFiles(directory, "*.json").Take(100001).ToArray();
            if (files.Length > 100000) throw new SerializationException("Audit limit exceeded.");
            long bytes = 0; foreach (var file in files) { bytes += new FileInfo(file).Length; if (bytes > 4294967296L) throw new SerializationException("Audit byte limit exceeded."); }
            return files.OrderBy(Path.GetFileName, StringComparer.Ordinal);
        }
        private static AuditEnvelope ReadEvent(string file, BulkBaselineJournalOptions options)
        {
            using (var input = File.OpenRead(file))
            {
                if (input.Length == 0 || input.Length > options.MaxJournalBytes) throw new SerializationException("Audit size invalid.");
                var value = (AuditEnvelope)Serializer(typeof(AuditEnvelope)).ReadObject(input);
                if (value == null || value.Schema != 1 || value.Projection == null || input.Position != input.Length ||
                    Path.GetFileNameWithoutExtension(file) != value.Projection.Revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)) throw new SerializationException("Audit frame invalid.");
                return value;
            }
        }
        private static Envelope ReadCache(string path, BulkBaselineJournalOptions options, Guid id, IBaselineJournalProtector protector)
        {
            using (var input = File.OpenRead(path))
            {
                if (input.Length == 0 || input.Length > options.MaxJournalBytes) throw new SerializationException("Cache size invalid.");
                var value = (Envelope)Serializer(typeof(Envelope)).ReadObject(input);
                if (value == null || input.Position != input.Length || value.Schema != 1 && value.Schema != 2) throw new SerializationException("Cache schema invalid.");
                if (value.Schema == 2) { if (value.Job != null || string.IsNullOrEmpty(value.AuditHead)) throw new SerializationException("Invalid protected cache."); value.Job = (BulkBaselineJobRecord)Unprotect(value.ProtectedPayload, typeof(BulkBaselineJobRecord), protector); }
                if (value.Job == null || value.Job.JobId != id || value.Checksum != Checksum(value.Job)) throw new SerializationException("Cache checksum invalid.");
                value.Job.Validate(); return value;
            }
        }
        private static string EventHash(AuditEnvelope value)
        {
            string saved = value.Hash; value.Hash = null;
            try { using (var sha = SHA256.Create()) using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                { Serializer(typeof(AuditEnvelope)).WriteObject(stream, value); stream.FlushFinalBlock(); return BitConverter.ToString(sha.Hash).Replace("-", ""); } }
            finally { value.Hash = saved; }
        }
        private static string Protect(object value, Type type, IBaselineJournalProtector protector)
        { using (var data = new MemoryStream()) { Serializer(type).WriteObject(data, value); return Convert.ToBase64String(protector.Protect(data.ToArray())); } }
        private static object Unprotect(string value, Type type, IBaselineJournalProtector protector)
        { using (var data = new MemoryStream(protector.Unprotect(Convert.FromBase64String(value)))) return Serializer(type).ReadObject(data); }
        private static DataContractJsonSerializer Serializer(Type type) => new DataContractJsonSerializer(type,
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 1000000 });
        private static string Checksum(BulkBaselineJobRecord record)
        {
            using (var sha = SHA256.Create()) using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                Serializer(typeof(BulkBaselineJobRecord)).WriteObject(stream, record); stream.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        private sealed class BoundedWriteStream : Stream
        {
            private readonly Stream output; private readonly long maximum; private long written;
            internal BoundedWriteStream(Stream output, long maximum) { this.output = output; this.maximum = maximum; }
            public override void Write(byte[] buffer, int offset, int count)
            { if (count < 0 || written > maximum - count) throw new IOException("Journal size limit exceeded."); output.Write(buffer, offset, count); written += count; }
            public override void Flush() => output.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => written;
            public override long Position { get => written; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
