using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace DoorsIntegration.Core
{
    [DataContract]
    public sealed class BaselineVerificationAuditEntry
    {
        [DataMember] public Guid JobId { get; internal set; }
        [DataMember] public DateTime RecordedAtUtc { get; internal set; }
        [DataMember] public string Phase { get; internal set; }
        [DataMember] public BaselineMetadataSource Source { get; internal set; }
        [DataMember] public string Environment { get; internal set; }
        [DataMember] public string SetIdentity { get; internal set; }
        [DataMember] public string Status { get; internal set; }
        [DataMember] public string EvidenceFingerprint { get; internal set; }
    }
    public sealed class FileBaselineVerificationAuditSink : IBaselineVerificationAuditSink
    {
        private readonly BulkBaselineJournalOptions options; private readonly IBaselineJournalProtector protector;
        [DataContract] private sealed class Frame
        { [DataMember] public long Sequence; [DataMember] public string PreviousHash; [DataMember] public string Payload; [DataMember] public string Hash; }
        public FileBaselineVerificationAuditSink(BulkBaselineJournalOptions options, IBaselineJournalProtector protector = null)
        { this.options = options ?? throw new ArgumentNullException(nameof(options)); this.protector = protector ?? new WindowsBaselineJournalProtector(); }
        private string Folder(Guid id) => Path.Combine(options.DirectoryPath, "verification-" + id.ToString("N"));
        private static string Hash(Frame frame) { string value = frame.Hash; frame.Hash = null; try { return BaselineSetSerialization.Hash(frame); } finally { frame.Hash = value; } }
        private IEnumerable<Frame> ReadFrames(Guid id)
        {
            if (!Directory.Exists(Folder(id))) yield break; long sequence = 0; string previous = "";
            foreach (string path in Directory.EnumerateFiles(Folder(id), "*.json").OrderBy(x => x, StringComparer.Ordinal).Take(101))
            { if (++sequence > 100 || new FileInfo(path).Length > options.MaxJournalBytes) throw new BulkBaselineJournalException("Verification audit limit exceeded.");
                var frame = BaselineSetSerialization.Read<Frame>(File.ReadAllBytes(path)); if (frame.Sequence != sequence || Path.GetFileNameWithoutExtension(path) != sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) || frame.PreviousHash != previous || frame.Hash != Hash(frame)) throw new BulkBaselineJournalException("Verification audit chain mismatch.");
                previous = frame.Hash; yield return frame; }
        }
        public void Record(Guid jobId, string phase, BaselineMetadataSource source, string environment, string setId, string status, string evidenceFingerprint)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("Verification Job ID is required."); Directory.CreateDirectory(Folder(jobId));
            using (new FileStream(Path.Combine(Folder(jobId), "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var last = ReadFrames(jobId).LastOrDefault();
                var entry = new BaselineVerificationAuditEntry { JobId = jobId, RecordedAtUtc = DateTime.UtcNow, Phase = BaselineAuditSanitizer.Text(phase), Source = source, Environment = BaselineAuditSanitizer.Text(environment), SetIdentity = BaselineAuditSanitizer.Text(setId), Status = BaselineAuditSanitizer.Text(status), EvidenceFingerprint = BaselineAuditSanitizer.Text(evidenceFingerprint) };
                var frame = new Frame { Sequence = (last?.Sequence ?? 0) + 1, PreviousHash = last?.Hash ?? "", Payload = Convert.ToBase64String(protector.Protect(BaselineSetSerialization.Bytes(entry))) }; frame.Hash = Hash(frame);
                byte[] bytes = BaselineSetSerialization.Bytes(frame); if (bytes.Length > options.MaxJournalBytes) throw new BulkBaselineJournalException("Verification audit frame exceeds its bound.");
                string target = Path.Combine(Folder(jobId), frame.Sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".json"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); } File.Move(temporary, target); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        public IEnumerable<BaselineVerificationAuditEntry> Read(Guid jobId)
        { foreach (var frame in ReadFrames(jobId)) { var entry = BaselineSetSerialization.Read<BaselineVerificationAuditEntry>(protector.Unprotect(Convert.FromBase64String(frame.Payload))); if (entry.JobId != jobId) throw new BulkBaselineJournalException("Verification audit job mismatch."); yield return entry; } }
    }
}
