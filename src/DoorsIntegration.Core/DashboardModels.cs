using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum DashboardScopeKind { EntireProject, SelectedFolders, SelectedFormalModules, SelectedBaselineSet, SelectedBaselineSnapshot }
    public enum DashboardCompleteness { Complete, Partial, Unknown, Unavailable }
    public enum DashboardSeverity { Critical, High, Medium, Low, Informational, Unknown }
    public enum DashboardBaselineState { UpToDate, ChangedSinceBaseline, NeverBaselined, OpenBaselineSet, MissingExpectedBaseline, VerificationRequired, Unknown }
    public enum DashboardRefreshMode { CacheOnly, RefreshExistingResults, RunNewAnalysis }
    [Flags] public enum DashboardSections { None=0, Overview=1, Baseline=2, Traceability=4, Quality=8, Impact=16, Audit=32, All=63 }
    public enum DashboardDestination { ModuleExplorer, BaselineCompare, BulkBaseline, TraceabilityExplorer, ImpactAnalysis, RequirementQuality, JobHistory }
    public enum DashboardAnalysisKind { Traceability, Quality, Impact, BaselineVerification, BaselineComparison }

    [DataContract] public sealed class DashboardFolderSelection
    {
        [DataMember] public string FullPath { get; set; }
        [DataMember] public bool IncludeSubfolders { get; set; } = true;
    }
    [DataContract] public sealed class DashboardScope
    {
        [DataMember] public DashboardScopeKind Kind { get; set; } = DashboardScopeKind.EntireProject;
        [DataMember] public List<string> Projects { get; set; } = new List<string> { "/Demo" };
        [DataMember] public List<DashboardFolderSelection> Folders { get; set; } = new List<DashboardFolderSelection>();
        [DataMember] public List<BaselineVersionReference> Modules { get; set; } = new List<BaselineVersionReference>();
        [DataMember] public string SetFolder { get; set; }
        [DataMember] public string DefinitionIdentity { get; set; }
        [DataMember] public string SetIdentity { get; set; }
        public bool Contains(string path, string moduleIdentity)
        {
            if (Kind == DashboardScopeKind.SelectedFormalModules || Kind == DashboardScopeKind.SelectedBaselineSnapshot)
                return Modules.Any(v => v.ModuleIdentity == moduleIdentity && v.FullPath == path);
            if (Kind == DashboardScopeKind.SelectedBaselineSet) return true; // Provider must bind membership before aggregation.
            if (Kind == DashboardScopeKind.SelectedFolders) return Folders.Any(f => path == f.FullPath || (f.IncludeSubfolders ? Under(f.FullPath, path) : DoorsItemPath.IsDirectChild(f.FullPath,path)));
            return Projects.Any(p => Under(p,path));
        }
        public static bool Under(string parent, string path) => path == parent || path.StartsWith(parent == "/" ? "/" : parent + "/", StringComparison.Ordinal);
    }
    [DataContract] public sealed class DashboardFilters
    {
        [DataMember] public string Project { get; set; } = "";
        [DataMember] public string Folder { get; set; } = "";
        [DataMember] public DoorsItemKind ModuleType { get; set; } = DoorsItemKind.FormalModule;
        [DataMember] public string RequirementType { get; set; } = "";
        [DataMember] public string RequirementStatus { get; set; } = "";
        [DataMember] public string Criticality { get; set; } = "";
        [DataMember] public DashboardBaselineState? BaselineStatus { get; set; }
        [DataMember] public DashboardSeverity? FindingSeverity { get; set; }
        [DataMember] public string ReviewStatus { get; set; } = "";
        [DataMember] public DateTime? AnalysisFromUtc { get; set; }
        [DataMember] public DateTime? AnalysisToUtc { get; set; }
        [DataMember] public bool FailedJobsOnly { get; set; }
    }
    [DataContract] public sealed class DashboardScorePolicy
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public Dictionary<string,decimal> Weights { get; set; } = new Dictionary<string,decimal> { ["Baseline"] = 25, ["Traceability"] = 30, ["Quality"] = 20, ["ChangeReview"] = 15, ["Verification"] = 10 };
        public void Validate() { if (Weights == null || Weights.Count != 5 || Weights.Values.Any(w => w < 0 || w > 100) || Weights.Values.Sum() != 100 || new[]{"Baseline","Traceability","Quality","ChangeReview","Verification"}.Any(k=>!Weights.ContainsKey(k))) throw new ArgumentException("Project score weights must sum to 100 across five known categories."); }
    }
    [DataContract] public sealed class DashboardRequest
    {
        [DataMember] public string Environment { get; set; } = "mock-doors-demo";
        [DataMember] public string DatabaseIdentity { get; set; } = "mock-doors-demo";
        [DataMember] public bool IsMock { get; set; } = true;
        [DataMember] public DoorsVersion ClientVersion { get; set; } = DoorsVersion.V97;
        [DataMember] public DashboardScope Scope { get; set; } = new DashboardScope();
        [DataMember] public DashboardFilters Filters { get; set; } = new DashboardFilters();
        [DataMember] public string TraceabilityPolicyJson { get; set; } = "";
        [DataMember] public string TraceabilityPolicyVersion { get; set; } = "project-default-v1";
        [DataMember] public string QualityProfileJson { get; set; } = "";
        [DataMember] public string QualityProfileVersion { get; set; } = "General-v1";
        [DataMember] public string QualityStandard { get; set; } = "General";
        [DataMember] public string AnalysisSnapshotVersion { get; set; } = "dashboard-evidence-v1";
        [DataMember] public List<string> LinkModulePaths { get; set; } = new List<string>();
        [DataMember] public List<string> CustomAttributes { get; set; } = new List<string> { "Status", "Requirement Type", "Criticality", "Verification Method" };
        [DataMember] public int CacheTtlSeconds { get; set; } = 300;
        [DataMember] public int MaximumAnalysisAgeSeconds { get; set; } = 86400;
        [DataMember] public int MaximumModules { get; set; } = 1000;
        [DataMember] public int MaximumRequirements { get; set; } = 100000;
        [DataMember] public int MaximumLinks { get; set; } = 250000;
        [DataMember] public int MaximumFindings { get; set; } = 10000;
        [DataMember] public int TimeoutSeconds { get; set; } = 120;
        [DataMember] public DashboardScorePolicy Score { get; set; } = new DashboardScorePolicy();
        public bool ReadOnly => true;
        public string Key { get { Validate(); return DashboardSerialization.Hash(Canonical()); } }
        private DashboardRequest Canonical()
        {
            var r=DashboardSerialization.Copy(this);
            r.Scope.Projects=r.Scope.Projects.Distinct().OrderBy(s=>s,StringComparer.Ordinal).ToList();
            r.Scope.Folders=r.Scope.Folders.GroupBy(f=>f.FullPath).Select(g=>new DashboardFolderSelection { FullPath=g.Key,IncludeSubfolders=g.Any(f=>f.IncludeSubfolders) }).OrderBy(f=>f.FullPath,StringComparer.Ordinal).ToList();
            r.Scope.Modules=r.Scope.Modules.GroupBy(v=>v.ModuleBaselineIdentity).Select(g=>g.First()).OrderBy(v=>v.ModuleBaselineIdentity,StringComparer.Ordinal).ToList();
            r.LinkModulePaths=r.LinkModulePaths.Distinct().OrderBy(s=>s,StringComparer.Ordinal).ToList(); r.CustomAttributes=r.CustomAttributes.Distinct().OrderBy(s=>s,StringComparer.Ordinal).ToList();
            r.Score.Weights=r.Score.Weights.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value);
            return r;
        }
        public void Validate()
        {
            DashboardSerialization.Text(Environment,1024); DashboardSerialization.Text(DatabaseIdentity,1024); DoorsVersions.Format(ClientVersion);
            if (Scope==null || Filters==null || Score==null || Scope.Projects==null || Scope.Folders==null || Scope.Folders.Any(f=>f==null) || Scope.Modules==null || LinkModulePaths==null || CustomAttributes==null) throw new ArgumentException("Dashboard scope is required.");
            if (!Enum.IsDefined(typeof(DashboardScopeKind),Scope.Kind) || !Enum.IsDefined(typeof(DoorsItemKind),Filters.ModuleType) || Filters.ModuleType!=DoorsItemKind.FormalModule) throw new ArgumentException("Only formal modules are dashboard candidates.");
            if(Scope.Projects.Count>1000 || Scope.Folders.Count>1000 || Scope.Modules.Count>10000 || LinkModulePaths.Count>32 || CustomAttributes.Count>64 || MaximumModules<1 || MaximumModules>10000 || MaximumRequirements<1 || MaximumRequirements>1000000 || MaximumLinks<1 || MaximumLinks>1000000 || MaximumFindings<1 || MaximumFindings>100000 || CacheTtlSeconds<1 || CacheTtlSeconds>86400 || MaximumAnalysisAgeSeconds<1 || MaximumAnalysisAgeSeconds>31536000 || TimeoutSeconds<1 || TimeoutSeconds>1800) throw new ArgumentException("Dashboard collection bounds invalid.");
            foreach(var p in Scope.Projects.Concat(Scope.Folders.Select(f=>f.FullPath)).Concat(LinkModulePaths)) if(DoorsItemPath.Normalize(p)!=p) throw new ArgumentException("Canonical paths required.");
            if(Scope.Modules.Any(v=>v==null || !v.IdentityVerified || v.RepositoryId!=DatabaseIdentity) || Scope.Modules.GroupBy(v=>v.ModuleIdentity).Any(g=>g.Select(v=>v.FullPath).Distinct().Count()>1 || g.Select(v=>v.ModuleBaselineIdentity).Distinct().Count()>1)) throw new ArgumentException("One stable version/path per module is required.");
            if((Scope.Kind==DashboardScopeKind.SelectedFormalModules || Scope.Kind==DashboardScopeKind.SelectedBaselineSnapshot) && Scope.Modules.Count==0 || Scope.Kind==DashboardScopeKind.SelectedFolders && Scope.Folders.Count==0 || Scope.Kind==DashboardScopeKind.EntireProject && Scope.Projects.Count==0) throw new ArgumentException("Empty dashboard selection.");
            if(Scope.Kind==DashboardScopeKind.SelectedBaselineSnapshot && Scope.Modules.Any(v=>v.IsCurrent)) throw new ArgumentException("Historical snapshot requires explicit baseline versions.");
            if(Scope.Kind==DashboardScopeKind.SelectedBaselineSet) { DoorsItemPath.Normalize(Scope.SetFolder); DashboardSerialization.Text(Scope.DefinitionIdentity,4096); DashboardSerialization.Text(Scope.SetIdentity,4096); }
            foreach(var text in new[]{TraceabilityPolicyVersion,QualityProfileVersion,QualityStandard,AnalysisSnapshotVersion}) DashboardSerialization.Text(text,256);
            if((TraceabilityPolicyJson??"").Length>131072 || (QualityProfileJson??"").Length>131072 || CustomAttributes.Any(a=>string.IsNullOrWhiteSpace(a)||a.Length>256||a.Any(char.IsControl))) throw new ArgumentException("Policy/attribute bounds exceeded.");
            foreach(var value in new[]{Filters.Project,Filters.Folder,Filters.RequirementType,Filters.RequirementStatus,Filters.Criticality,Filters.ReviewStatus}) if(value==null||value.Length>4096||value.Any(char.IsControl)) throw new ArgumentException("Invalid dashboard filter.");
            if(Filters.FindingSeverity.HasValue&&!Enum.IsDefined(typeof(DashboardSeverity),Filters.FindingSeverity.Value)||Filters.BaselineStatus.HasValue&&!Enum.IsDefined(typeof(DashboardBaselineState),Filters.BaselineStatus.Value)||Scope.Folders.Any(f=>f==null)) throw new ArgumentException("Invalid dashboard filter/selection enum.");
            if(Filters.AnalysisFromUtc?.Kind!=null && Filters.AnalysisFromUtc.Value.Kind!=DateTimeKind.Utc || Filters.AnalysisToUtc?.Kind!=null && Filters.AnalysisToUtc.Value.Kind!=DateTimeKind.Utc || Filters.AnalysisFromUtc>Filters.AnalysisToUtc) throw new ArgumentException("Analysis dates must be an ordered UTC interval.");
            Score.Validate();
        }
    }
    [DataContract] public sealed class DashboardDataQuality
    {
        [DataMember] public DashboardCompleteness Completeness { get; set; } = DashboardCompleteness.Unknown;
        [DataMember] public bool IsStale { get; set; }
        [DataMember] public List<string> Warnings { get; set; } = new List<string>();
        [DataMember] public int AccessDeniedModules { get; set; }
        [DataMember] public int UnknownModules { get; set; }
        public bool VerifiedComplete => Completeness==DashboardCompleteness.Complete && !IsStale;
    }
    [DataContract] public sealed class DashboardMetric
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public decimal? Value { get; set; }
        [DataMember] public string Unit { get; set; } = "count";
        [DataMember] public string Scope { get; set; }
        [DataMember] public string Source { get; set; }
        [DataMember] public DateTime TimestampUtc { get; set; }
        [DataMember] public DashboardCompleteness Completeness { get; set; }
        [DataMember] public string Warning { get; set; }
        [DataMember] public bool IsStale { get; set; }
        [DataMember] public bool IsLowerBound { get; set; }
        [DataMember] public DashboardDestination Destination { get; set; }
        public string DisplayValue => Value?.ToString("0.##",CultureInfo.InvariantCulture) ?? "Unknown";
        public string DisplayQuality => (IsStale ? "Stale / " : "")+Completeness+(IsLowerBound ? " / observed lower bound" : "");
    }
    [DataContract] public sealed class DashboardModuleHealth
    {
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public string ModulePath { get; set; }
        [DataMember] public string Project { get; set; }
        [DataMember] public BaselineVersionReference Version { get; set; }
        [DataMember] public string Access { get; set; }
        [DataMember] public long Requirements { get; set; }
        [DataMember] public long DeletedRequirements { get; set; }
        [DataMember] public DashboardBaselineState BaselineHealth { get; set; } = DashboardBaselineState.Unknown;
        [DataMember] public string LatestBaseline { get; set; }
        [DataMember] public DateTime? LatestBaselineDateUtc { get; set; }
        [DataMember] public int? LatestMajorVersion { get; set; }
        [DataMember] public int? LatestMinorVersion { get; set; }
        [DataMember] public string BaselineSuffix { get; set; }
        [DataMember] public string CurrentModuleState { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public bool Complete { get; set; }
    }
    [DataContract] public sealed class DashboardFinding
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Category { get; set; }
        [DataMember] public string Code { get; set; }
        [DataMember] public DashboardSeverity Severity { get; set; }
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public string RequirementIdentity { get; set; }
        [DataMember] public string RequirementVersionIdentity { get; set; }
        [DataMember] public int? AbsoluteNumber { get; set; }
        [DataMember] public string AnalysisIdentity { get; set; }
        [DataMember] public string AnalysisSnapshotIdentity { get; set; }
        [DataMember] public string Explanation { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public string Rule { get; set; }
        [DataMember] public string ReviewStatus { get; set; } = "NotReviewed";
        [DataMember] public string RootRequirementIdentity { get; set; }
        [DataMember] public string ImpactKind { get; set; }
        [DataMember] public string TestRecommendation { get; set; }
        [DataMember] public string ReviewEvidenceFingerprint { get; set; }
        [DataMember] public bool Uncertain { get; set; }
        [DataMember] public DateTime TimestampUtc { get; set; }
        [DataMember] public DashboardDestination Destination { get; set; }
    }
    [DataContract] public sealed class DashboardTrendPoint
    {
        [DataMember] public DateTime TimestampUtc { get; set; }
        [DataMember] public string ComparableScopeKey { get; set; }
        [DataMember] public string MetricId { get; set; }
        [DataMember] public decimal? Value { get; set; }
        [DataMember] public string SourceAnalysisIdentity { get; set; }
    }
    [DataContract] public sealed class DashboardChartPoint
    {
        [DataMember] public string Group { get; set; }
        [DataMember] public string Label { get; set; }
        [DataMember] public decimal Value { get; set; }
        [DataMember] public decimal? Percentage { get; set; }
        [DataMember] public string Quality { get; set; }
    }
    [DataContract] public sealed class DashboardBaselineHealth
    { [DataMember] public List<DashboardModuleHealth> Modules { get; set; } = new List<DashboardModuleHealth>(); }
    [DataContract] public sealed class DashboardTraceabilityHealth
    {
        [DataMember] public long Applicable { get; set; }
        [DataMember] public long VerifiedLinked { get; set; }
        [DataMember] public long Missing { get; set; }
        [DataMember] public long Unknown { get; set; }
        [DataMember] public long Excluded { get; set; }
        [DataMember] public long AccessDenied { get; set; }
        [DataMember] public decimal? CoveragePercentage { get; set; }
        [DataMember] public bool DenominatorComplete { get; set; }
        [DataMember] public string PolicyVersion { get; set; }
    }
    [DataContract] public sealed class DashboardImpactSummary
    { [DataMember] public long ImpactCount { get; set; } [DataMember] public long UniqueImpactedRequirements { get; set; } [DataMember] public long PathCount { get; set; } [DataMember] public long PendingReviews { get; set; } }
    [DataContract] public sealed class DashboardQualitySummary
    { [DataMember] public long EvaluatedRequirements { get; set; } [DataMember] public long NotEvaluatedRequirements { get; set; } [DataMember] public long Findings { get; set; } [DataMember] public long UniqueAffectedRequirements { get; set; } [DataMember] public string ProfileVersion { get; set; } [DataMember] public bool ProfileApproved { get; set; } }
    [DataContract] public sealed class DashboardAuditEntry
    {
        [DataMember] public string JobId { get; set; }
        [DataMember] public string JobType { get; set; }
        [DataMember] public string Operator { get; set; }
        [DataMember] public DateTime StartTimeUtc { get; set; }
        [DataMember] public DateTime? EndTimeUtc { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public int AffectedModuleCount { get; set; }
        [DataMember] public long FindingCount { get; set; }
        [DataMember] public string ErrorSummary { get; set; }
        [DataMember] public List<string> ModuleIdentities { get; set; } = new List<string>();
        [DataMember] public string SourceAnalysisIdentity { get; set; }
    }
    [DataContract] public sealed class DashboardAuditSummary
    { [DataMember] public List<DashboardAuditEntry> Recent { get; set; } = new List<DashboardAuditEntry>(); }
    [DataContract] public sealed class DashboardSummary
    {
        [DataMember] public decimal? ConfigurationHealthScore { get; set; }
        [DataMember] public string ScoreStatus { get; set; } = "ScoreUnavailable";
        [DataMember] public List<string> ScoreExplanation { get; set; } = new List<string>();
        [DataMember] public List<DashboardMetric> Metrics { get; set; } = new List<DashboardMetric>();
    }
    [DataContract] public sealed class DashboardSectionSnapshot
    {
        [DataMember] public string ScopeKey { get; set; }
        [DataMember] public DashboardSections Section { get; set; }
        [DataMember] public DateTime CollectedAtUtc { get; set; }
        [DataMember] public DashboardDataQuality Quality { get; set; } = new DashboardDataQuality();
        [DataMember] public List<DashboardMetric> Metrics { get; set; } = new List<DashboardMetric>();
        [DataMember] public List<DashboardFinding> Findings { get; set; } = new List<DashboardFinding>();
        [DataMember] public List<DashboardChartPoint> Charts { get; set; } = new List<DashboardChartPoint>();
        [DataMember] public List<string> AnalysisIds { get; set; } = new List<string>();
        [DataMember] public DashboardBaselineHealth Baseline { get; set; }
        [DataMember] public DashboardTraceabilityHealth Traceability { get; set; }
        [DataMember] public DashboardQualitySummary RequirementQuality { get; set; }
        [DataMember] public DashboardImpactSummary Impact { get; set; }
        [DataMember] public DashboardAuditSummary Audit { get; set; }
    }
    [DataContract] public sealed class DashboardSnapshot
    {
        [DataMember] public int SchemaVersion { get; set; } = 1;
        [DataMember] public Guid SnapshotId { get; set; } = Guid.NewGuid();
        [DataMember] public string RequestKey { get; set; }
        [DataMember] public DashboardRequest Request { get; set; }
        [DataMember] public DateTime CollectedAtUtc { get; set; }
        [DataMember] public DashboardDataQuality DataQuality { get; set; } = new DashboardDataQuality();
        [DataMember] public DashboardSummary Summary { get; set; } = new DashboardSummary();
        [DataMember] public List<DashboardSectionSnapshot> Sections { get; set; } = new List<DashboardSectionSnapshot>();
        [DataMember] public List<DashboardTrendPoint> Trends { get; set; } = new List<DashboardTrendPoint>();
        [DataMember] public List<BaselineVersionReference> NavigationModules { get; set; } = new List<BaselineVersionReference>();
        [DataMember] public List<BaselineVersionReference> ObservedModules { get; set; } = new List<BaselineVersionReference>();
        [DataMember] public double AggregationMilliseconds { get; set; }
        [DataMember] public double RefreshMilliseconds { get; set; }
        [DataMember] public long ManagedMemoryDeltaBytes { get; set; }
        [DataMember] public long PeakWorkingSetBytes { get; set; }
        [DataMember] public int ServiceReadCalls { get; set; }
        [DataMember] public long? ComCallCount { get; set; }
        public string Identity => DashboardSerialization.Hash(new[]{RequestKey,CollectedAtUtc.ToString("O"),SnapshotId.ToString("N")});
        public bool ReadOnly => true;
    }
    public sealed class DashboardRefreshResult
    {
        public DashboardSnapshot Snapshot { get; set; }
        public string Status { get; set; }
        public bool FromCache { get; set; }
        public bool Cancelled { get; set; }
        public bool CacheWriteFailed { get; set; }
        public string Error { get; set; }
        public double SnapshotLoadMilliseconds { get; set; }
        public double CacheHitRatio { get; set; }
    }
    [DataContract] public sealed class DashboardNavigationIntent
    {
        [DataMember] public DoorsVersion ClientVersion { get; set; } = DoorsVersion.V97;
        [DataMember] public DashboardDestination Destination { get; set; }
        [DataMember] public string Environment { get; set; }
        [DataMember] public string DatabaseIdentity { get; set; }
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public string ModulePath { get; set; }
        [DataMember] public BaselineVersionReference Version { get; set; }
        [DataMember] public string RequirementIdentity { get; set; }
        [DataMember] public int? AbsoluteNumber { get; set; }
        [DataMember] public string AnalysisSnapshot { get; set; }
        [DataMember] public string AnalysisIdentity { get; set; }
        [DataMember] public string FindingIdentity { get; set; }
        [DataMember] public string JobId { get; set; }
        [DataMember] public string DefinitionIdentity { get; set; }
        [DataMember] public string SetIdentity { get; set; }
        [DataMember] public DashboardScope Scope { get; set; }
        [DataMember] public string OriginSnapshotIdentity { get; set; }
    }
    public interface IDashboardClock { DateTime UtcNow { get; } }
    public sealed class DashboardClock : IDashboardClock { public DateTime UtcNow => DateTime.UtcNow; }
    public interface IConfigurationDashboardService
    { Task<DashboardRefreshResult> RefreshAsync(DashboardRequest request, DashboardSections sections = DashboardSections.All, DashboardRefreshMode mode = DashboardRefreshMode.RefreshExistingResults, IProgress<string> progress = null, CancellationToken token = default(CancellationToken)); }
    public interface IDashboardDataProvider
    { Task<DashboardEvidence> CollectAsync(DashboardRequest request, DashboardSections sections, bool runNewAnalyses, IProgress<string> progress, CancellationToken token); Task ValidateNavigationAsync(DashboardNavigationIntent intent, CancellationToken token); }
    public static class DashboardSerialization
    {
        public static void Text(string s,int limit) { if(string.IsNullOrWhiteSpace(s)||s.Length>limit||s.Any(char.IsControl)) throw new ArgumentException("Invalid dashboard identity/configuration text."); }
        private static DataContractJsonSerializer Serializer(Type type) => new DataContractJsonSerializer(type,new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=5000000, UseSimpleDictionaryFormat=true });
        public static byte[] Bytes<T>(T value) { using(var stream=new MemoryStream()) { Serializer(typeof(T)).WriteObject(stream,value); return stream.ToArray(); } }
        public static T Read<T>(byte[] bytes) { if(bytes==null || bytes.Length>134217728) throw new InvalidDataException("Dashboard snapshot size limit."); using(var stream=new MemoryStream(bytes,false)) return (T)Serializer(typeof(T)).ReadObject(stream); }
        public static T Copy<T>(T value) => Read<T>(Bytes(value));
        public static string Hash<T>(T value) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Bytes(value))).Replace("-",""); }
    }
}
