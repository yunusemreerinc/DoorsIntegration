using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum ChangeImpactComparisonKind { ModuleBaselines, BaselineToCurrent, BaselineSets }
    public enum ImpactGraphChoice { Source, Target, Both }
    public enum ImpactAnalysisStatus { Completed, Incomplete, Unknown }
    public enum RequirementChangeType { Added, Deleted, Modified, Moved, AttributeChanged, LinkAdded, LinkRemoved, LinkTargetChanged, Unchanged, Unknown }
    [Flags] public enum RequirementChangeFlags { None = 0, Added = 1, Deleted = 2, Content = 4, Moved = 8, Attribute = 16, LinkAdded = 32, LinkRemoved = 64, LinkTargetChanged = 128, FormattingOnly = 256, NormalizationOnly = 512, Unknown = 1024 }
    public enum ImpactSeverity { Critical, High, Medium, Low, Informational, Unknown }
    public enum ImpactCandidateKind { DirectImpactCandidate, IndirectImpactCandidate, TestImpactCandidate, ExternalImpactCandidate, UnresolvedImpact, NoImpactPathFound }
    public enum ImpactTestRecommendation { ReviewRequired, RegressionTestCandidate, TraceabilityUpdateCandidate, NoActionRecommended, Unknown }
    public enum ImpactReviewStatus { NotReviewed, InReview, ConfirmedImpacted, NotImpacted, NeedsInvestigation, AcceptedRisk }
    public enum ImpactEvidenceQuality { ObservedPath, PartialEvidence, Unverified }
    public enum ImpactValueKind { Text, Integer, Real, Boolean, Date, Enum, MultiEnum, Unknown }

    public sealed class ImpactSnapshotSelection
    {
        public string Label { get; set; }
        public string MockSnapshotKey { get; set; }
        public string EnvironmentId { get; set; }
        public string RepositoryId { get; set; }
        public DoorsVersion ClientVersion { get; set; } = DoorsVersion.V97;
        public List<BaselineVersionReference> Modules { get; set; } = new List<BaselineVersionReference>();
        public List<string> LinkModulePaths { get; set; } = new List<string>();
        public BaselineSetVerificationRequest BaselineSet { get; set; }
    }
    public sealed class ChangeImpactRequest
    {
        public ChangeImpactRequest(ImpactSnapshotSelection source, ImpactSnapshotSelection target, ImpactAnalysisPolicy policy,
            ChangeImpactComparisonKind kind = ChangeImpactComparisonKind.ModuleBaselines, ImpactGraphChoice graph = ImpactGraphChoice.Both)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source)); Target = target ?? throw new ArgumentNullException(nameof(target));
            Policy = policy ?? throw new ArgumentNullException(nameof(policy)); Policy.Validate();
            if (!Enum.IsDefined(typeof(ChangeImpactComparisonKind), kind) || !Enum.IsDefined(typeof(ImpactGraphChoice), graph)) throw new ArgumentException("Invalid impact comparison kind/graph.");
            Kind = kind; Graph = graph;
            if (source.EnvironmentId != target.EnvironmentId || source.RepositoryId != target.RepositoryId || string.IsNullOrWhiteSpace(source.EnvironmentId) || string.IsNullOrWhiteSpace(source.RepositoryId)) throw new ArgumentException("Source and target must explicitly identify the same environment/repository.");
            if (kind == ChangeImpactComparisonKind.BaselineSets && (source.BaselineSet == null || target.BaselineSet == null)) throw new ArgumentException("Two explicit baseline-set selections are required.");
            if (kind != ChangeImpactComparisonKind.BaselineSets)
            {
                if (source.Modules.Count != 1 || target.Modules.Count != 1 || source.Modules[0].IsCurrent || source.Modules[0].ModuleIdentity != target.Modules[0].ModuleIdentity) throw new ArgumentException("Select one stable module identity and a historical source baseline.");
                if ((kind == ChangeImpactComparisonKind.BaselineToCurrent) != target.Modules[0].IsCurrent) throw new ArgumentException("Target current/baseline selection does not match comparison kind.");
                if (source.Modules[0].ModuleBaselineIdentity == target.Modules[0].ModuleBaselineIdentity) throw new ArgumentException("Select distinct source and target versions.");
            }
        }
        public ImpactSnapshotSelection Source { get; }
        public ImpactSnapshotSelection Target { get; }
        public ImpactAnalysisPolicy Policy { get; }
        public ChangeImpactComparisonKind Kind { get; }
        public ImpactGraphChoice Graph { get; }
        public bool ReadOnly => true;
    }
    [DataContract]
    public sealed class ImpactAttributeValue
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string TypeName { get; set; }
        [DataMember] public string PlainText { get; set; }
        [DataMember] public string RichText { get; set; }
        [DataMember] public RequirementValueStatus PlainStatus { get; set; }
        [DataMember] public RequirementValueStatus RichStatus { get; set; }
        public RequirementValue ToValue() => new RequirementValue(Name, TypeName, PlainStatus, PlainText, RichStatus, RichText);
        public static ImpactAttributeValue From(RequirementValue v) => new ImpactAttributeValue { Name = v.Name, TypeName = v.TypeName, PlainText = v.PlainText, RichText = v.RichTextWithOle, PlainStatus = v.PlainStatus, RichStatus = v.RichTextStatus };
    }
    [DataContract]
    public sealed class ImpactRequirementVersion
    {
        [DataMember] public BaselineVersionReference Version { get; set; }
        [DataMember] public int AbsoluteNumber { get; set; }
        [DataMember] public string DisplayIdentifier { get; set; }
        [DataMember] public int? ParentAbsoluteNumber { get; set; }
        [DataMember] public bool ParentKnown { get; set; }
        [DataMember] public int? Position { get; set; }
        [DataMember] public bool IsDeleted { get; set; }
        [DataMember] public BaselineVerificationReadState Access { get; set; }
        [DataMember] public Dictionary<string, ImpactAttributeValue> Attributes { get; set; } = new Dictionary<string, ImpactAttributeValue>(StringComparer.Ordinal);
        public string RequirementIdentity => Version.ObjectIdentity(AbsoluteNumber);
        public string RequirementVersionIdentity => Version.NodeIdentity(AbsoluteNumber);
        public string Value(string name) => Attributes.TryGetValue(name, out var v) && v.PlainStatus == RequirementValueStatus.Readable ? v.PlainText : null;
    }
    [DataContract]
    public sealed class ImpactAnalysisSnapshot
    {
        [DataMember] public string Label { get; set; }
        [DataMember] public DateTime CapturedAtUtc { get; set; }
        [DataMember] public BaselineLinkSnapshot Links { get; set; }
        [DataMember] public List<ImpactRequirementVersion> Requirements { get; set; } = new List<ImpactRequirementVersion>();
        [DataMember] public List<string> ComparedModuleIdentities { get; set; } = new List<string>();
        [DataMember] public bool IsStale { get; set; }
        [DataMember] public bool TransportUncertain { get; set; }
        [DataMember] public List<string> Limitations { get; set; } = new List<string>();
        public string Fingerprint => ImpactSerialization.SnapshotFingerprint(this);
        public string CacheKey => GraphGuard.Hash(Links.Context.EnvironmentId, Links.Context.RepositoryId, Fingerprint, CapturedAtUtc.ToString("O"));
    }
    [DataContract]
    public sealed class AttributeChange
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public ImpactAttributeValue Before { get; set; }
        [DataMember] public ImpactAttributeValue After { get; set; }
        [DataMember] public bool IsComparable { get; set; }
        [DataMember] public bool SemanticChanged { get; set; }
        [DataMember] public bool RichRepresentationChanged { get; set; }
        [DataMember] public bool NormalizationOnly { get; set; }
        [DataMember] public bool TypeChanged { get; set; }
        [DataMember] public string Reason { get; set; }
    }
    [DataContract]
    public sealed class RequirementChange
    {
        [DataMember] public string Identity { get; set; }
        [DataMember] public ImpactRequirementVersion Before { get; set; }
        [DataMember] public ImpactRequirementVersion After { get; set; }
        [DataMember] public RequirementChangeType PrimaryType { get; set; }
        [DataMember] public RequirementChangeFlags Flags { get; set; }
        [DataMember] public List<AttributeChange> Attributes { get; set; } = new List<AttributeChange>();
        [DataMember] public ImpactSeverity Severity { get; set; }
        [DataMember] public string Criticality { get; set; }
        [DataMember] public int ReviewPriority { get; set; }
        [DataMember] public string Reason { get; set; }
        public bool IsChanged => PrimaryType != RequirementChangeType.Unchanged;
        public bool IsConclusive => (Flags & RequirementChangeFlags.Unknown) == 0;
        public string DisplayIdentifier => (After ?? Before)?.DisplayIdentifier;
        public string Module => (After ?? Before)?.Version.FullPath;
    }
    [DataContract]
    public sealed class LinkChange
    {
        [DataMember] public BaselineTraceabilityChange Observation { get; set; }
        [DataMember] public BaselineLinkInfo Before { get; set; }
        [DataMember] public BaselineLinkInfo After { get; set; }
    }
    [DataContract]
    public sealed class ImpactPathStep
    {
        [DataMember] public string SourceNode { get; set; }
        [DataMember] public string TargetNode { get; set; }
        [DataMember] public string LinkIdentity { get; set; }
        [DataMember] public string LinkModuleIdentity { get; set; }
        [DataMember] public string LinkType { get; set; }
        [DataMember] public TraceabilityDirection Direction { get; set; }
        [DataMember] public int Depth { get; set; }
        [DataMember] public string BaselineContext { get; set; }
        [DataMember] public bool Confirmed { get; set; }
    }
    [DataContract]
    public sealed class ImpactPropagationPath
    {
        [DataMember] public string Identity { get; set; }
        [DataMember] public string RootChangedRequirement { get; set; }
        [DataMember] public string RootVersionIdentity { get; set; }
        [DataMember] public string GraphSnapshotIdentity { get; set; }
        [DataMember] public string GraphSnapshotLabel { get; set; }
        [DataMember] public string TargetRequirement { get; set; }
        [DataMember] public string TargetVersionIdentity { get; set; }
        [DataMember] public string TargetReferenceIdentity { get; set; }
        [DataMember] public List<ImpactPathStep> Steps { get; set; } = new List<ImpactPathStep>();
        public int Depth => Steps.Count;
    }
    [DataContract]
    public sealed class ImpactedRequirement
    {
        [DataMember] public string RequirementIdentity { get; set; }
        [DataMember] public string RequirementVersionIdentity { get; set; }
        [DataMember] public string ReferenceIdentity { get; set; }
        [DataMember] public bool IdentityVerified { get; set; }
        [DataMember] public string RootChangeIdentity { get; set; }
        [DataMember] public ImpactCandidateKind Kind { get; set; }
        [DataMember] public int MinimumDepth { get; set; }
        [DataMember] public ImpactTestRecommendation TestRecommendation { get; set; }
        [DataMember] public bool IsExternal { get; set; }
        [DataMember] public List<string> PathIdentities { get; set; } = new List<string>();
    }
    [DataContract]
    public sealed class ImpactAnalysisFinding
    {
        [DataMember] public string Identity { get; set; }
        [DataMember] public string Code { get; set; }
        [DataMember] public string RootChangeIdentity { get; set; }
        [DataMember] public string ImpactedRequirementIdentity { get; set; }
        [DataMember] public string ImpactedVersionIdentity { get; set; }
        [DataMember] public ImpactCandidateKind Kind { get; set; }
        [DataMember] public string PathIdentity { get; set; }
        [DataMember] public int Depth { get; set; }
        [DataMember] public RequirementChangeFlags ChangeFlags { get; set; }
        [DataMember] public ImpactSeverity Severity { get; set; }
        [DataMember] public ImpactEvidenceQuality EvidenceQuality { get; set; }
        [DataMember] public string EvidenceFingerprint { get; set; }
        [DataMember] public string Reason { get; set; }
        [DataMember] public ImpactReviewStatus ReviewStatus { get; set; }
        [DataMember] public string ReviewerComments { get; set; }
    }
    [DataContract]
    public sealed class ImpactReviewDecision
    {
        [DataMember] public string Reviewer { get; set; }
        [DataMember] public DateTime ReviewedAtUtc { get; set; }
        [DataMember] public ImpactReviewStatus Decision { get; set; }
        [DataMember] public string Rationale { get; set; }
        [DataMember] public string FindingIdentity { get; set; }
        [DataMember] public string AnalysisSnapshotIdentity { get; set; }
        [DataMember] public string EvidenceFingerprint { get; set; }
    }
    [DataContract]
    public sealed class ChangeImpactResult
    {
        [DataMember] public Guid AnalysisId { get; set; }
        [DataMember] public DateTime StartedAtUtc { get; set; }
        [DataMember] public DateTime CompletedAtUtc { get; set; }
        [DataMember] public ChangeImpactComparisonKind Kind { get; set; }
        [DataMember] public ImpactGraphChoice GraphChoice { get; set; }
        [DataMember] public ImpactAnalysisStatus Status { get; set; }
        [DataMember] public ImpactAnalysisSnapshot Source { get; set; }
        [DataMember] public ImpactAnalysisSnapshot Target { get; set; }
        [DataMember] public string PolicyFingerprint { get; set; }
        [DataMember] public ImpactAnalysisPolicy Policy { get; set; }
        [DataMember] public List<RequirementChange> Changes { get; set; } = new List<RequirementChange>();
        [DataMember] public List<LinkChange> LinkChanges { get; set; } = new List<LinkChange>();
        [DataMember] public List<ImpactedRequirement> Impacted { get; set; } = new List<ImpactedRequirement>();
        [DataMember] public List<ImpactPropagationPath> Paths { get; set; } = new List<ImpactPropagationPath>();
        [DataMember] public List<ImpactAnalysisFinding> Findings { get; set; } = new List<ImpactAnalysisFinding>();
        [DataMember] public List<ImpactReviewDecision> Reviews { get; set; } = new List<ImpactReviewDecision>();
        [DataMember] public List<string> Limitations { get; set; } = new List<string>();
        [DataMember] public bool Truncated { get; set; }
        [DataMember] public long ElapsedMilliseconds { get; set; }
        [DataMember] public long ManagedMemoryDeltaBytes { get; set; }
        public string SnapshotIdentity => GraphGuard.Hash(Source?.Fingerprint ?? "", Target?.Fingerprint ?? "", PolicyFingerprint ?? "", Kind.ToString(), GraphChoice.ToString());
        public int UniqueImpactedRequirementCount => Impacted.Where(i => i.IdentityVerified).Select(i => i.RequirementIdentity).Distinct().Count();
        public int UnverifiedImpactReferenceCount => Impacted.Where(i => !i.IdentityVerified).Select(i => i.ReferenceIdentity).Distinct().Count();
        public int ImpactPathCount => Paths.Count;
        public bool ReadOnly => true;
        public TraceabilityGraph SourceGraph { get; set; }
        public TraceabilityGraph TargetGraph { get; set; }
        [DataMember] public string SourceGraphSnapshotIdentity { get; set; }
        [DataMember] public string TargetGraphSnapshotIdentity { get; set; }
    }
    public interface IImpactSnapshotProvider
    { Task<ImpactAnalysisSnapshot> CaptureAsync(ImpactSnapshotSelection selection, ImpactAnalysisPolicy policy, IProgress<string> progress, CancellationToken token); }
    public interface IChangeImpactAnalysisService
    { Task<ChangeImpactResult> AnalyzeAsync(ChangeImpactRequest request, IProgress<string> progress = null, CancellationToken token = default(CancellationToken)); }
    public interface IRequirementChangeDetector
    { IReadOnlyList<RequirementChange> Detect(ImpactAnalysisSnapshot before, ImpactAnalysisSnapshot after, ImpactAnalysisPolicy policy, CancellationToken token = default(CancellationToken)); }
    public interface IImpactPropagationEngine
    { void Propagate(ChangeImpactResult result, ImpactAnalysisPolicy policy, IProgress<string> progress, CancellationToken token); }
    public interface IImpactPolicyProvider { ImpactAnalysisPolicy ReadJson(string json); }
}
