using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace DoorsIntegration.Core
{
    public sealed class FileBaselineSetJobRepository : IBaselineSetJobRepository
    {
        private readonly BulkBaselineJournalOptions options; private readonly IBaselineJournalProtector protector;
        public FileBaselineSetJobRepository(BulkBaselineJournalOptions options, IBaselineJournalProtector protector = null)
        { this.options = options ?? throw new ArgumentNullException(nameof(options)); this.protector = protector ?? new WindowsBaselineJournalProtector(); }
        public IDisposable Acquire()
        { Directory.CreateDirectory(options.DirectoryPath); try { return new FileStream(Path.Combine(options.DirectoryPath, "native-set-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); } catch (IOException) { throw new BulkBaselineJournalException("A Native Baseline Set worker owns this journal; no new mutation dispatched."); } }
        [DataContract] private sealed class Frame
        {
            [DataMember] public Guid JobId;
            [DataMember] public long Revision;
            [DataMember] public string PreviousHash;
            [DataMember] public string Payload;
            [DataMember] public string Hash;
            [DataMember] public string Action;
            [DataMember] public string Status;
            [DataMember] public string Reason;
            [DataMember] public string PlanFingerprint;
        }
        private string Folder(Guid id) => Path.Combine(options.DirectoryPath, "set-" + id.ToString("N"));
        private string Hash(Frame frame) { string old = frame.Hash; frame.Hash = null; try { return BaselineSetSerialization.Hash(frame); } finally { frame.Hash = old; } }
        private IEnumerable<Frame> Frames(Guid id)
        {
            string root = Folder(id); if (!Directory.Exists(root)) yield break;
            var paths = Directory.EnumerateFiles(root, "*.json").Take(100001).ToArray(); if (paths.Length > 100000) throw new BulkBaselineJournalException("Native set audit revision limit exceeded.");
            string previous = ""; long revision = -1; long bytes = 0;
            foreach (string path in paths.OrderBy(x => x, StringComparer.Ordinal))
            {
                var info = new FileInfo(path); bytes += info.Length;
                if (info.Length > options.MaxJournalBytes || bytes > 4294967296L) throw new BulkBaselineJournalException("Native set audit size limit exceeded.");
                Frame frame; try { frame = BaselineSetSerialization.Read<Frame>(File.ReadAllBytes(path)); } catch (Exception) { throw new BulkBaselineJournalException("Native set audit frame is unreadable; no history was reset."); }
                if (frame.JobId != id || frame.Revision != ++revision || Path.GetFileNameWithoutExtension(path) != revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) || frame.PreviousHash != previous || frame.Hash != Hash(frame)) throw new BulkBaselineJournalException("Native set audit chain/identity/revision mismatch.");
                previous = frame.Hash; yield return frame;
            }
        }
        public void Save(BaselineSetJob job)
        {
            var last = Frames(job.JobId).LastOrDefault();
            if (job.JobId == Guid.Empty || job.Revision != (last == null ? 0 : last.Revision + 1)) throw new BulkBaselineJournalException("Native set journal revision conflict.");
            if (last != null)
            { var old = Decode(last); if (BaselineSetSerialization.Hash(old.Request) != BaselineSetSerialization.Hash(job.Request) || old.Source != job.Source || old.PlanFingerprint != job.PlanFingerprint) throw new BulkBaselineJournalException("Immutable Native set intent changed."); }
            var frame = new Frame { JobId = job.JobId, Revision = job.Revision, PreviousHash = last?.Hash ?? "", Payload = Convert.ToBase64String(protector.Protect(BaselineSetSerialization.Bytes(job))),
                Action = job.Request.Action.ToString(), Status = job.Status.ToString(), Reason = BaselineSetSerialization.Text(job.Reason), PlanFingerprint = job.PlanFingerprint };
            frame.Hash = Hash(frame); byte[] bytes = BaselineSetSerialization.Bytes(frame); if (bytes.Length > options.MaxJournalBytes) throw new BulkBaselineJournalException("Native set checkpoint exceeds its configured bound.");
            Directory.CreateDirectory(Folder(job.JobId)); string destination = Path.Combine(Folder(job.JobId), job.Revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json"); string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); } File.Move(temporary, destination); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private BaselineSetJob Decode(Frame frame)
        {
            try { var job = BaselineSetSerialization.Read<BaselineSetJob>(protector.Unprotect(Convert.FromBase64String(frame.Payload))); if (job.JobId != frame.JobId || job.Revision != frame.Revision || job.PlanFingerprint != frame.PlanFingerprint || job.Status.ToString() != frame.Status || job.Request.Action.ToString() != frame.Action) throw new SerializationException(); return job; }
            catch (Exception) { throw new BulkBaselineJournalException("Native set protected payload is invalid/unavailable; no retry authorized."); }
        }
        public BaselineSetJob Get(Guid id) { var frame = Frames(id).LastOrDefault(); return frame == null ? null : Decode(frame); }
        public IEnumerable<BaselineSetJob> ReadHistory(Guid id) { foreach (var frame in Frames(id)) yield return Decode(frame); }
        public IReadOnlyList<BaselineSetJob> List()
        {
            if (!Directory.Exists(options.DirectoryPath)) return Array.Empty<BaselineSetJob>(); var result = new List<BaselineSetJob>();
            foreach (string path in Directory.EnumerateDirectories(options.DirectoryPath, "set-*").Take(10001))
            { if (result.Count == 10000) throw new BulkBaselineJournalException("Native set history scan limit reached."); if (Guid.TryParseExact(Path.GetFileName(path).Substring(4), "N", out var id)) { var job = Get(id); if (job != null) result.Add(job); } }
            return result.OrderByDescending(j => j.StartedAtUtc).ToArray();
        }
    }
}
