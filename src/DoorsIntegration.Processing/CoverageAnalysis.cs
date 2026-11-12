using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public enum CoverageDirection { Incoming, Outgoing }
    public enum CoverageVersionPolicy { SameVersion, CurrentTarget, AnyExplicitVersion }
    public enum CoverageState { Covered, Uncovered, Unknown, Excluded }
    public sealed class CoverageNode
    {
        public CoverageNode(ArtifactIdentity identity, bool deleted = false) { Identity = identity ?? throw new ArgumentNullException(nameof(identity)); IsDeleted = deleted; }
        public ArtifactIdentity Identity { get; }
        public bool IsDeleted { get; }
    }
    public sealed class TraceRelation
    {
        public TraceRelation(ArtifactIdentity source, ArtifactIdentity target, string relationship, bool resolved, bool echoed = false, bool deletedEndpoint = false)
        {
            Source = source; Target = target; Relationship = ProcessingGuard.Text(relationship, 4096); IsResolved = resolved; IsEchoed = echoed; HasDeletedEndpoint = deletedEndpoint;
            if (resolved && (source == null || target == null || source.VersionKind == ArtifactVersion.Unknown || target.VersionKind == ArtifactVersion.Unknown)) throw new ArgumentException("Resolved relation requires versioned endpoints.");
        }
        public ArtifactIdentity Source { get; }
        public ArtifactIdentity Target { get; }
        public string Relationship { get; }
        public bool IsResolved { get; }
        public bool IsEchoed { get; }
        public bool HasDeletedEndpoint { get; }
    }
    public sealed class CoveragePolicy
    {
        public CoveragePolicy(string id, string relationship, CoverageDirection direction, int minimumLinks = 1,
            IEnumerable<string> targetContainers = null, CoverageVersionPolicy versionPolicy = CoverageVersionPolicy.SameVersion)
        {
            Id = ProcessingGuard.Text(id, 128); Relationship = ProcessingGuard.Text(relationship, 4096);
            if (!Enum.IsDefined(typeof(CoverageDirection), direction) || !Enum.IsDefined(typeof(CoverageVersionPolicy), versionPolicy) || minimumLinks < 1 || minimumLinks > 1024) throw new ArgumentException("Invalid coverage policy.");
            var targets = (targetContainers ?? Enumerable.Empty<string>()).Take(65).ToArray();
            if (targets.Length > 64 || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length) throw new ArgumentException("Invalid target scope.");
            foreach (string target in targets) ProcessingGuard.Text(target, 4096);
            Direction = direction; MinimumLinks = minimumLinks; TargetContainers = Array.AsReadOnly(targets); VersionPolicy = versionPolicy;
            Fingerprint = ProcessingGuard.Hash(new[] { Id, Relationship, Direction.ToString(), ProcessingGuard.Number(minimumLinks), versionPolicy.ToString() }.Concat(targets).ToArray());
        }
        public string Id { get; }
        public string Relationship { get; }
        public CoverageDirection Direction { get; }
        public int MinimumLinks { get; }
        public IReadOnlyList<string> TargetContainers { get; }
        public CoverageVersionPolicy VersionPolicy { get; }
        public string Fingerprint { get; }
        internal bool Accepts(ArtifactIdentity anchor, ArtifactIdentity other)
        {
            if (other == null || other.VersionKind == ArtifactVersion.Unknown || anchor.RepositoryId != other.RepositoryId ||
                (TargetContainers.Count != 0 && !TargetContainers.Contains(other.ContainerId, StringComparer.Ordinal))) return false;
            return VersionPolicy == CoverageVersionPolicy.AnyExplicitVersion || (VersionPolicy == CoverageVersionPolicy.CurrentTarget ? other.VersionKind == ArtifactVersion.Current :
                anchor.VersionKind == other.VersionKind && anchor.Version == other.Version);
        }
    }
    public sealed class CoverageContext
    {
        public CoverageContext(string repositoryId, bool sourceComplete, string sourceDescription, int maxNodes = 100000, int maxRelations = 250000)
        {
            RepositoryId = ProcessingGuard.Text(repositoryId, 1024); SourceDescription = ProcessingGuard.Text(sourceDescription, 4096);
            if (maxNodes < 1 || maxNodes > 1000000 || maxRelations < 1 || maxRelations > 1000000) throw new ArgumentException("Invalid coverage limits.");
            SourceComplete = sourceComplete; MaxNodes = maxNodes; MaxRelations = maxRelations;
        }
        public string RepositoryId { get; }
        public bool SourceComplete { get; }
        public string SourceDescription { get; }
        public int MaxNodes { get; }
        public int MaxRelations { get; }
    }
    [DataContract]
    public sealed class CoverageObservation
    {
        internal CoverageObservation(CoverageNode node, CoveragePolicy policy, int links, CoverageState state)
        { ArtifactKey = node.Identity.Key; Artifact = node.Identity.Display; PolicyId = policy.Id; PolicyFingerprint = policy.Fingerprint; ObservedLinks = links; MinimumLinks = policy.MinimumLinks; State = state.ToString(); }
        [DataMember] public string ArtifactKey { get; private set; }
        [DataMember] public string Artifact { get; private set; }
        [DataMember] public string PolicyId { get; private set; }
        [DataMember] public string PolicyFingerprint { get; private set; }
        [DataMember] public int ObservedLinks { get; private set; }
        [DataMember] public int MinimumLinks { get; private set; }
        [DataMember] public string State { get; private set; }
    }
    [DataContract]
    public sealed class CoverageSummary
    {
        internal CoverageSummary(CoverageContext context, long nodes, long relations, long covered, long uncovered, long unknown, long excluded,
            long duplicates, bool complete, bool truncated, bool cancelled, bool sourceFailed, int policies)
        {
            SourceDescription = context.SourceDescription; RepositoryId = context.RepositoryId; KnownNodes = nodes; RelationsRead = relations;
            Covered = covered; Uncovered = uncovered; Unknown = unknown; Excluded = excluded; DuplicateObservations = duplicates;
            ScopeComplete = complete && !truncated && !cancelled && !sourceFailed && policies > 0; Truncated = truncated; Cancelled = cancelled; SourceFailed = sourceFailed;
            PolicyCount = policies; CoveragePercent = ScopeComplete && unknown == 0 && covered + uncovered > 0 ? (decimal?)(100m * covered / (covered + uncovered)) : null;
            Disclosure = ProcessingDisclosure.Statement;
        }
        [DataMember] public string SourceDescription { get; private set; }
        [DataMember] public string RepositoryId { get; private set; }
        [DataMember] public long KnownNodes { get; private set; }
        [DataMember] public long RelationsRead { get; private set; }
        [DataMember] public long Covered { get; private set; }
        [DataMember] public long Uncovered { get; private set; }
        [DataMember] public long Unknown { get; private set; }
        [DataMember] public long Excluded { get; private set; }
        [DataMember] public long DuplicateObservations { get; private set; }
        [DataMember] public int PolicyCount { get; private set; }
        [DataMember] public bool ScopeComplete { get; private set; }
        [DataMember] public bool Truncated { get; private set; }
        [DataMember] public bool Cancelled { get; private set; }
        [DataMember] public bool SourceFailed { get; private set; }
        [DataMember] public decimal? CoveragePercent { get; private set; }
        [DataMember] public bool ComplianceAssessed { get; private set; }
        [DataMember] public string Disclosure { get; private set; }
    }
    public interface ICoverageReportSink
    {
        void Write(CoverageObservation observation);
        void Complete(CoverageSummary summary);
    }
    public sealed class TraceabilityCoverageService
    {
        private sealed class Entry
        {
            public Entry(CoverageNode node, int policies) { Node = node; Counts = new int[policies]; }
            public CoverageNode Node { get; }
            public int[] Counts { get; }
        }
        public CoverageSummary Analyze(IEnumerable<CoverageNode> nodes, IEnumerable<TraceRelation> relations, IEnumerable<CoveragePolicy> policies,
            CoverageContext context, ICoverageReportSink sink, CancellationToken token = default(CancellationToken))
        {
            if (nodes == null || relations == null || policies == null || context == null || sink == null) throw new ArgumentNullException();
            var rules = policies.Take(65).ToArray();
            if (rules.Length > 64 || rules.Any(p => p == null) || rules.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length) throw new ArgumentException("Invalid coverage policies.");
            var index = new Dictionary<string, Entry>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
            bool complete = context.SourceComplete, truncated = false, cancelled = false, sourceFailed = false; long read = 0, duplicates = 0;
            try
            {
                foreach (var node in nodes)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    if (node == null || node.Identity.RepositoryId != context.RepositoryId) { complete = false; sourceFailed = true; continue; }
                    if (index.Count >= context.MaxNodes) { truncated = true; break; }
                    if (index.ContainsKey(node.Identity.Key)) { complete = false; sourceFailed = true; continue; }
                    if (node.Identity.VersionKind == ArtifactVersion.Unknown) complete = false;
                    index.Add(node.Identity.Key, new Entry(node, rules.Length));
                }
                if (!cancelled)
                    foreach (var relation in relations)
                    {
                        if (token.IsCancellationRequested) { cancelled = true; break; }
                        if (++read > context.MaxRelations) { truncated = true; break; }
                        if (relation == null || !relation.IsResolved || relation.IsEchoed || relation.HasDeletedEndpoint)
                        { complete = false; continue; }
                        if (relation.Source.RepositoryId != context.RepositoryId || relation.Target.RepositoryId != context.RepositoryId) { complete = false; sourceFailed = true; continue; }
                        string key = ProcessingGuard.Hash(relation.Source.Key, relation.Target.Key, relation.Relationship);
                        if (!seen.Add(key)) { duplicates++; continue; }
                        for (int i = 0; i < rules.Length; i++)
                        {
                            var policy = rules[i]; if (policy.Relationship != relation.Relationship) continue;
                            var anchor = policy.Direction == CoverageDirection.Outgoing ? relation.Source : relation.Target;
                            var other = policy.Direction == CoverageDirection.Outgoing ? relation.Target : relation.Source;
                            if (index.TryGetValue(anchor.Key, out var entry) && !entry.Node.IsDeleted && policy.Accepts(anchor, other)) entry.Counts[i]++;
                        }
                    }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
            catch { sourceFailed = true; complete = false; }
            bool knownScope = complete && !truncated && !cancelled && !sourceFailed;
            long covered = 0, uncovered = 0, unknown = 0, excluded = 0;
            foreach (var entry in index.Values)
                for (int i = 0; i < rules.Length; i++)
                {
                    var state = entry.Node.IsDeleted ? CoverageState.Excluded : entry.Counts[i] >= rules[i].MinimumLinks ? CoverageState.Covered :
                        knownScope ? CoverageState.Uncovered : CoverageState.Unknown;
                    if (state == CoverageState.Covered) covered++; else if (state == CoverageState.Uncovered) uncovered++;
                    else if (state == CoverageState.Unknown) unknown++; else excluded++;
                    sink.Write(new CoverageObservation(entry.Node, rules[i], entry.Counts[i], state));
                }
            var summary = new CoverageSummary(context, index.Count, read, covered, uncovered, unknown, excluded, duplicates, complete, truncated, cancelled, sourceFailed, rules.Length);
            sink.Complete(summary); return summary;
        }
    }
    public sealed class FileCoverageReport : ICoverageReportSink, IDisposable
    {
        private StreamWriter output;
        private readonly long maxBytes;
        private long bytes;
        public FileCoverageReport(string newDirectory, long maxBytes = 268435456)
        {
            if (maxBytes < 65536 || maxBytes > 1073741824) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            this.maxBytes = maxBytes;
            DirectoryPath = Path.GetFullPath(newDirectory ?? throw new ArgumentNullException(nameof(newDirectory)));
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("Analysis output directory already exists.");
            Directory.CreateDirectory(DirectoryPath); output = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "coverage.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false, true)) { NewLine = "\n" };
        }
        public string DirectoryPath { get; }
        public void Write(CoverageObservation observation)
        {
            if (output == null) throw new ObjectDisposedException(nameof(FileCoverageReport)); string line = ProcessingJson.Encode(observation);
            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > maxBytes) throw new ProcessingException("ReportLimit", "Coverage report exceeded its disk limit; no completed summary was published.");
            output.WriteLine(line);
        }
        public void Complete(CoverageSummary summary)
        { if (output == null) throw new ObjectDisposedException(nameof(FileCoverageReport)); Dispose(); File.WriteAllText(Path.Combine(DirectoryPath, "summary.json"), ProcessingJson.Encode(summary), new UTF8Encoding(false, true)); }
        public void Dispose() { output?.Dispose(); output = null; }
    }
}
