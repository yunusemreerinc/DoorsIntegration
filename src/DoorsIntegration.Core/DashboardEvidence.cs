using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace DoorsIntegration.Core
{
    [DataContract] public sealed class DashboardRequirementFact
    {
        [DataMember] public BaselineVersionReference Version { get; set; }
        [DataMember] public int AbsoluteNumber { get; set; }
        [DataMember] public string Type { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public string Criticality { get; set; }
        [DataMember] public string ProjectPath { get; set; }
        [DataMember] public bool IsDeleted { get; set; }
        [DataMember] public bool Readable { get; set; } = true;
        [DataMember] public List<string> CustomAttributeNames { get; set; } = new List<string>();
        public string Identity => Version.ObjectIdentity(AbsoluteNumber);
        public string VersionIdentity => Version.NodeIdentity(AbsoluteNumber);
        public string ModuleIdentity => Version.ModuleIdentity;
        public string Project => ProjectPath ?? "Unknown";
    }
    [DataContract] public sealed class DashboardModuleFact
    {
        [DataMember] public DashboardModuleHealth Health { get; set; } = new DashboardModuleHealth();
        [DataMember] public BaselineVersionReference LatestVersion { get; set; }
        [DataMember] public long BaselineCount { get; set; }
        [DataMember] public bool HistoryComplete { get; set; }
        [DataMember] public bool InventoryComplete { get; set; }
        [DataMember] public bool NeverBaselinedProven { get; set; }
        [DataMember] public bool AttributeCatalogComplete { get; set; }
        [DataMember] public List<string> AttributeCatalog { get; set; } = new List<string>();
    }
    [DataContract] public sealed class DashboardLinkFact
    {
        [DataMember] public string Identity { get; set; }
        [DataMember] public string SourceVersionIdentity { get; set; }
        [DataMember] public string TargetVersionIdentity { get; set; }
        [DataMember] public string LinkModuleIdentity { get; set; }
        [DataMember] public string Resolution { get; set; }
        [DataMember] public bool IsEchoed { get; set; }
        [DataMember] public bool VerifiedBaseline { get; set; }
    }
    [DataContract] public sealed class DashboardCoverageFact
    {
        [DataMember] public string RequirementVersionIdentity { get; set; }
        [DataMember] public bool EligibilityKnown { get; set; }
        [DataMember] public bool Applicable { get; set; }
        [DataMember] public TraceabilityState State { get; set; }
        [DataMember] public bool AccessDenied { get; set; }
        [DataMember] public bool BrokenChain { get; set; }
    }
    [DataContract] public sealed class DashboardChangeFact
    {
        [DataMember] public string RequirementIdentity { get; set; }
        [DataMember] public string RequirementVersionIdentity { get; set; }
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public RequirementChangeType PrimaryType { get; set; }
        [DataMember] public RequirementChangeFlags Flags { get; set; }
        [DataMember] public DashboardSeverity Severity { get; set; }
    }
    [DataContract] public sealed class DashboardQualityEvaluation
    {
        [DataMember] public string RequirementVersionIdentity { get; set; }
        [DataMember] public bool FullyEvaluated { get; set; }
    }
    [DataContract] public sealed class DashboardAnalysisRecord
    {
        [DataMember] public DoorsVersion ClientVersion { get; set; } = DoorsVersion.V97;
        [DataMember] public string AnalysisId { get; set; }
        [DataMember] public string SnapshotIdentity { get; set; }
        [DataMember] public DashboardAnalysisKind Kind { get; set; }
        [DataMember] public string Environment { get; set; }
        [DataMember] public string DatabaseIdentity { get; set; }
        [DataMember] public bool IsMock { get; set; }
        [DataMember] public DateTime TimestampUtc { get; set; }
        [DataMember] public string PolicyVersion { get; set; }
        [DataMember] public string PolicyFingerprint { get; set; }
        [DataMember] public string RequestedPolicyKey { get; set; }
        [DataMember] public string ProfileVersion { get; set; }
        [DataMember] public string ProfileFingerprint { get; set; }
        [DataMember] public string RequestedProfileKey { get; set; }
        [DataMember] public string Standard { get; set; }
        [DataMember] public bool ProfileApproved { get; set; }
        [DataMember] public string AnalysisVersion { get; set; }
        [DataMember] public DashboardCompleteness Completeness { get; set; }
        [DataMember] public string Operator { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public List<BaselineVersionReference> Modules { get; set; } = new List<BaselineVersionReference>();
        [DataMember] public List<BaselineVersionReference> SourceVersions { get; set; } = new List<BaselineVersionReference>();
        [DataMember] public List<DashboardRequirementFact> Requirements { get; set; } = new List<DashboardRequirementFact>();
        [DataMember] public List<DashboardLinkFact> Links { get; set; } = new List<DashboardLinkFact>();
        [DataMember] public List<DashboardCoverageFact> Coverage { get; set; } = new List<DashboardCoverageFact>();
        [DataMember] public List<DashboardFinding> Findings { get; set; } = new List<DashboardFinding>();
        [DataMember] public List<DashboardChangeFact> Changes { get; set; } = new List<DashboardChangeFact>();
        [DataMember] public List<DashboardQualityEvaluation> Evaluations { get; set; } = new List<DashboardQualityEvaluation>();
        [DataMember] public long ImpactPathCount { get; set; }
        [DataMember] public bool OpenSet { get; set; }
        [DataMember] public string SetIdentity { get; set; }
        [DataMember] public string DefinitionIdentity { get; set; }
        [DataMember] public bool ConclusiveComparison { get; set; }
        [DataMember] public List<string> ComparedModuleIdentities { get; set; }=new List<string>();
        [DataMember] public List<string> MissingModuleIdentities { get; set; }=new List<string>();
        [DataMember] public long MissingSetMembers { get; set; }
        [DataMember] public long? BrokenChainCount { get; set; }
        [DataMember] public bool BaselineLinkVerificationAvailable { get; set; }
        [DataMember] public List<string> Warnings { get; set; } = new List<string>();
        public string ComparableScopeKey => DashboardSerialization.Hash(new[]{Environment,DatabaseIdentity,IsMock.ToString(),Kind.ToString(),string.Join(";",Modules.Select(v=>v.ModuleBaselineIdentity).OrderBy(s=>s,StringComparer.Ordinal)),string.Join(";",SourceVersions.Select(v=>v.ModuleBaselineIdentity).OrderBy(s=>s,StringComparer.Ordinal)),PolicyFingerprint,ProfileFingerprint,AnalysisVersion});
        public static string PolicyKey(DashboardRequest request) => DashboardSerialization.Hash(new[]{request.TraceabilityPolicyVersion,request.TraceabilityPolicyJson});
        public static string ProfileKey(DashboardRequest request) => DashboardSerialization.Hash(new[]{request.QualityProfileVersion,request.QualityProfileJson,request.QualityStandard});
    }
    // Background-only evidence. Never retained by a WPF view model or serialized into the aggregate cache.
    public sealed class DashboardSetObservation
    {
        public string Identity { get; set; }
        public string DefinitionIdentity { get; set; }
        public bool IsOpen { get; set; }
        public List<string> ModuleIdentities { get; set; }=new List<string>();
    }
    public sealed class DashboardEvidence
    {
        public string Environment { get; set; }
        public string DatabaseIdentity { get; set; }
        public bool IsMock { get; set; }
        public DateTime CollectedAtUtc { get; set; }
        public DashboardSections CollectedSections { get; set; }
        public bool DiscoveryComplete { get; set; }
        public bool SetInventoryComplete { get; set; }
        public int? BaselineSetCount { get; set; }
        public List<DashboardSetObservation> ObservedSets { get; set; }=new List<DashboardSetObservation>();
        public List<string> ProjectPaths { get; set; } = new List<string>();
        public List<string> FolderPaths { get; set; } = new List<string>();
        public List<DashboardModuleFact> Modules { get; set; } = new List<DashboardModuleFact>();
        public List<DashboardRequirementFact> Requirements { get; set; } = new List<DashboardRequirementFact>();
        public List<DashboardAnalysisRecord> Analyses { get; set; } = new List<DashboardAnalysisRecord>();
        public List<DashboardAuditEntry> Activity { get; set; } = new List<DashboardAuditEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
        public int ServiceReadCalls { get; set; }
        public long? ComCallCount { get; set; }
        public List<string> UnresolvedModulePaths { get; set; } = new List<string>();
    }
    public interface IDashboardAnalysisRepository
    {
        void Save(DashboardAnalysisRecord record);
        IEnumerable<DashboardAnalysisRecord> Read(DashboardRequest request, System.Threading.CancellationToken token);
        bool Contains(string analysisId);
    }
    public interface IDashboardAnalysisDiagnostics { IReadOnlyList<string> ReadWarnings { get; } }
}
