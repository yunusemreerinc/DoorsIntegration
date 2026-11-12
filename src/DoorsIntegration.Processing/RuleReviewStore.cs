using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace DoorsIntegration.Processing
{
    // Local review ledger. Hash chaining detects accidental edits; it is not authentication or a certification signature.
    public sealed class FileRuleReviewStore
    {
        private readonly string path;
        private readonly IProcessingClock clock;
        private const int MaxRecords = 100000;
        private const long MaxBytes = 67108864;
        public FileRuleReviewStore(string path, IProcessingClock clock)
        { this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); }
        public RuleResult ApplyLatest(RuleResult result)
        {
            Validate(result); if (!File.Exists(path)) return result;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { var state = Scan(stream, result.ResultFingerprint); return state.Latest == null ? result : result.WithDecision(Decision(state.Latest)); }
        }
        public RuleResult Save(RuleResult result, string acknowledgedResultFingerprint, ReviewerDisposition disposition, string reviewer, string reason, string reference, string expectedRevisionHash = "")
        {
            Validate(result);
            if (acknowledgedResultFingerprint != result.ResultFingerprint || !Enum.IsDefined(typeof(ReviewerDisposition), disposition)) throw new ArgumentException("Review must acknowledge this exact result fingerprint.");
            ProcessingGuard.Text(reviewer, 256); ProcessingGuard.Text(reason, 4096, true); ProcessingGuard.Text(reference, 1024); ProcessingGuard.Text(expectedRevisionHash, 128, empty: true);
            var now = clock.UtcNow; if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC review clock required.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var state = Scan(stream, result.ResultFingerprint);
                if ((state.Latest?.Hash ?? "") != expectedRevisionHash) throw new ProcessingException("StaleReviewerDisposition", "Reload the current reviewer disposition before saving.");
                if (state.Records >= MaxRecords) throw new ProcessingException("ReviewLedgerLimit", "Review ledger record budget reached.");
                var row = new Entry { ResultFingerprint = result.ResultFingerprint, ProjectId = result.ProjectId, ProfileFingerprint = result.ProfileFingerprint, Disposition = disposition.ToString(),
                    Reviewer = reviewer, Reason = reason, Reference = reference, AtUtc = now.ToString("O"), Previous = state.LastHash };
                row.Hash = Hash(row); byte[] data = new UTF8Encoding(false, true).GetBytes(ProcessingJson.Encode(row) + "\n");
                if (stream.Length + data.Length > MaxBytes) throw new ProcessingException("ReviewLedgerLimit", "Review ledger byte budget reached.");
                stream.Position = stream.Length; stream.Write(data, 0, data.Length); stream.Flush(true); return result.WithDecision(Decision(row));
            }
        }
        private static void Validate(RuleResult result)
        { if (result == null || !result.VerifyIntegrity()) throw new ArgumentException("Result evidence fingerprint is invalid; reload the original analysis result."); ProcessingGuard.Text(result.ProjectId, 256); }
        private static RuleReviewDecision Decision(Entry row) => new RuleReviewDecision((ReviewerDisposition)Enum.Parse(typeof(ReviewerDisposition), row.Disposition), row.Reviewer, row.Reason, row.Reference,
            DateTime.ParseExact(row.AtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), row.Hash);
        private static State Scan(FileStream stream, string result)
        {
            if (stream.Length > MaxBytes) throw new ProcessingException("ReviewLedgerLimit", "Review ledger exceeds byte limit.");
            var state = new State(); stream.Position = 0;
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true))
            {
                string line;
                while ((line = ProcessingJson.ReadLine(reader, 65536, out bool oversized, out bool terminated)) != null)
                {
                    if (++state.Records > MaxRecords || oversized || !terminated) throw new ProcessingException("ReviewLedgerCorrupt", "Incomplete or oversized review ledger; nothing was overwritten.");
                    var row = ProcessingJson.Decode<Entry>(line);
                    if (row == null || row.Previous != state.LastHash || row.Hash != Hash(row) || !Enum.TryParse(row.Disposition, false, out ReviewerDisposition d) || !Enum.IsDefined(typeof(ReviewerDisposition), d) || d.ToString() != row.Disposition ||
                        !DateTime.TryParseExact(row.AtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || at.Kind != DateTimeKind.Utc)
                        throw new ProcessingException("ReviewLedgerCorrupt", "Invalid review ledger evidence chain.");
                    foreach (var hash in new[] { row.ResultFingerprint, row.ProfileFingerprint, row.Hash }) if (hash == null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ProcessingException("ReviewLedgerCorrupt", "Invalid review fingerprint.");
                    ProcessingGuard.Text(row.ProjectId, 256); ProcessingGuard.Text(row.Reviewer, 256); ProcessingGuard.Text(row.Reason, 4096, true); ProcessingGuard.Text(row.Reference, 1024);
                    state.LastHash = row.Hash; if (row.ResultFingerprint == result) state.Latest = row;
                }
            }
            return state;
        }
        private static string Hash(Entry e) => RuleGuard.Hash(new[] { "RuleReview-v1", e.ResultFingerprint, e.ProjectId, e.ProfileFingerprint, e.Disposition, e.Reviewer, e.Reason, e.Reference, e.AtUtc, e.Previous });
        private sealed class State { internal int Records; internal string LastHash = ""; internal Entry Latest; }
        [DataContract] private sealed class Entry
        {
            [DataMember] public string ResultFingerprint;
            [DataMember] public string ProjectId;
            [DataMember] public string ProfileFingerprint;
            [DataMember] public string Disposition;
            [DataMember] public string Reviewer;
            [DataMember] public string Reason;
            [DataMember] public string Reference;
            [DataMember] public string AtUtc;
            [DataMember] public string Previous;
            [DataMember] public string Hash;
        }
    }
}
