using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace DoorsIntegration.Core
{
    public interface IImpactReviewRepository
    {
        IEnumerable<ImpactReviewDecision> Read(string findingIdentity);
        void Append(ImpactReviewDecision decision);
    }
    public sealed class FileImpactReviewRepository : IImpactReviewRepository
    {
        private readonly BulkBaselineJournalOptions options; private readonly IBaselineJournalProtector protector;
        [DataContract] private sealed class Frame
        { [DataMember] public int Sequence; [DataMember] public string PreviousHash; [DataMember] public string Payload; [DataMember] public string Hash; }
        public FileImpactReviewRepository(BulkBaselineJournalOptions options, IBaselineJournalProtector protector = null)
        { this.options = options; this.protector = protector ?? new WindowsBaselineJournalProtector(); }
        private string Folder(string identity) => Path.Combine(options.DirectoryPath, "impact-review-" + GraphGuard.Hash(identity));
        private static string FrameHash(Frame frame) => GraphGuard.Hash(frame.Sequence.ToString(CultureInfo.InvariantCulture), frame.PreviousHash, frame.Payload);
        private IEnumerable<Frame> Frames(string identity)
        {
            string directory = Folder(identity); if (!Directory.Exists(directory)) yield break;
            string previous = ""; int sequence = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json").OrderBy(x => x, StringComparer.Ordinal).Take(10001))
            {
                if (++sequence > 10000 || new FileInfo(file).Length > options.MaxJournalBytes) throw new IOException("Impact review history exceeds its bounds.");
                var frame = ImpactSerialization.Read<Frame>(File.ReadAllBytes(file));
                if (frame.Sequence != sequence || frame.PreviousHash != previous || frame.Hash != FrameHash(frame) || Path.GetFileNameWithoutExtension(file) != sequence.ToString("D8", CultureInfo.InvariantCulture)) throw new IOException("Impact review journal integrity mismatch.");
                previous = frame.Hash; yield return frame;
            }
        }
        public IEnumerable<ImpactReviewDecision> Read(string findingIdentity)
        {
            foreach (var frame in Frames(findingIdentity))
            {
                var decision = ImpactSerialization.Read<ImpactReviewDecision>(protector.Unprotect(Convert.FromBase64String(frame.Payload)));
                if (decision.FindingIdentity != findingIdentity) throw new IOException("Impact review finding identity mismatch."); yield return decision;
            }
        }
        public void Append(ImpactReviewDecision decision)
        {
            string folder = Folder(decision.FindingIdentity); Directory.CreateDirectory(folder);
            using (new FileStream(Path.Combine(folder, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var previous = Frames(decision.FindingIdentity).LastOrDefault();
                if (previous?.Sequence >= 10000) throw new IOException("Impact review journal limit reached.");
                var frame = new Frame { Sequence = (previous?.Sequence ?? 0) + 1, PreviousHash = previous?.Hash ?? "", Payload = Convert.ToBase64String(protector.Protect(ImpactSerialization.Bytes(decision))) }; frame.Hash = FrameHash(frame);
                byte[] bytes = ImpactSerialization.Bytes(frame); if (bytes.Length > options.MaxJournalBytes) throw new IOException("Impact review frame exceeds its bound.");
                // Keep the temporary basename no longer than the final basename on legacy net48/MAX_PATH hosts.
                string target = Path.Combine(folder, frame.Sequence.ToString("D8", CultureInfo.InvariantCulture) + ".json"), temp = Path.Combine(folder, Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } File.Move(temp, target); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
    public sealed class ImpactReviewService
    {
        private readonly IImpactReviewRepository repository;
        public ImpactReviewService(IImpactReviewRepository repository) { this.repository = repository ?? throw new ArgumentNullException(nameof(repository)); }
        public ImpactReviewDecision FindApplicableDecision(string findingIdentity,string snapshotIdentity,string evidenceFingerprint)
        {
            var latest=repository.Read(findingIdentity).LastOrDefault();
            return latest!=null&&latest.AnalysisSnapshotIdentity==snapshotIdentity&&latest.EvidenceFingerprint==evidenceFingerprint?latest:null;
        }
        public void Apply(ChangeImpactResult result)
        {
            string snapshotIdentity = result.SnapshotIdentity; result.Reviews.Clear();
            foreach (var finding in result.Findings)
            {
                var latest = repository.Read(finding.Identity).LastOrDefault(); finding.ReviewStatus = ImpactReviewStatus.NotReviewed; finding.ReviewerComments = "";
                if (latest == null) continue;
                if (latest.AnalysisSnapshotIdentity == snapshotIdentity && latest.EvidenceFingerprint == finding.EvidenceFingerprint)
                { finding.ReviewStatus = latest.Decision; finding.ReviewerComments = latest.Rationale; result.Reviews.Add(latest); }
                else finding.ReviewerComments = "Previous evidence/version/policy changed; a new review is required.";
            }
        }
        public ImpactReviewDecision Decide(ChangeImpactResult result, string findingIdentity, string reviewer, ImpactReviewStatus decision, string rationale)
        {
            if (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 256 || string.IsNullOrWhiteSpace(rationale) || rationale.Length > 16384 || decision == ImpactReviewStatus.NotReviewed || !Enum.IsDefined(typeof(ImpactReviewStatus), decision)) throw new ArgumentException("Reviewer, explicit disposition and bounded rationale are required.");
            var finding = result.Findings.Single(f => f.Identity == findingIdentity);
            var value = new ImpactReviewDecision { Reviewer = reviewer, ReviewedAtUtc = DateTime.UtcNow, Decision = decision, Rationale = rationale, FindingIdentity = finding.Identity, AnalysisSnapshotIdentity = result.SnapshotIdentity, EvidenceFingerprint = finding.EvidenceFingerprint };
            repository.Append(value); finding.ReviewStatus = decision; finding.ReviewerComments = rationale;
            result.Reviews.RemoveAll(r => r.FindingIdentity == findingIdentity); result.Reviews.Add(value); return value;
        }
    }
}
