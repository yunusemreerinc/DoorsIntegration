using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum BaselineVerificationStatus { Passed, PassedWithWarnings, Failed, Incomplete, Unknown }
    public enum BaselineVerificationReadState { Verified, Missing, AccessDenied, Unknown, Invalid }
    public enum BaselineShareableConfiguration { Unknown, NotConfigured, Configured }
    public enum BaselineLinkStatus { ValidInternalBaselineLink, ValidExternalLink, UnexpectedVersionTarget, UnresolvedSource, UnresolvedTarget, MissingExpectedLink, EchoedLink, AccessDenied, Unknown }
    public enum BaselineLinkChangeKind { AddedLink, RemovedLink, ChangedLinkTarget, ChangedModuleVersion, UnresolvedLink, UnexpectedDuplicateLink, ExpectedVersionRebinding, Indeterminate }

    [DataContract]
    public sealed class BaselineVersionReference
    {
        private string versionIdentity;
        private System.Collections.Concurrent.ConcurrentDictionary<int, string> objectKeys, nodeKeys;
        public BaselineVersionReference(string repositoryId, string moduleIdentity, string fullPath, int major, int minor, string suffix = "", bool isCurrent = false, bool identityVerified = true)
        {
            RepositoryId = GraphGuard.Text(repositoryId, 1024); ModuleIdentity = GraphGuard.Text(moduleIdentity, 1024); FullPath = GraphGuard.Path(fullPath);
            var version = new RequirementBaseline(major, minor, suffix); Major = version.Major; Minor = version.Minor; Suffix = version.Suffix; IsCurrent = isCurrent; IdentityVerified = identityVerified;
        }
        [DataMember] public string RepositoryId { get; private set; }
        [DataMember] public string ModuleIdentity { get; private set; }
        [DataMember] public string FullPath { get; private set; }
        [DataMember] public int Major { get; private set; }
        [DataMember] public int Minor { get; private set; }
        [DataMember] public string Suffix { get; private set; }
        [DataMember] public bool IsCurrent { get; private set; }
        [DataMember] public bool IdentityVerified { get; private set; }
        public string Version => IsCurrent ? "Current" : Major + "." + Minor + "[" + Suffix + "]";
        // Composite version locator, NOT a claimed native baseline GUID.
        public string ModuleBaselineIdentity => versionIdentity ?? (versionIdentity = GraphGuard.Hash(RepositoryId, ModuleIdentity, IsCurrent ? "Current" : "Baseline", Version));
        public RequirementBaseline Baseline => IsCurrent ? null : new RequirementBaseline(Major, Minor, Suffix);
        public string ObjectIdentity(int absoluteNumber)
        { if (absoluteNumber < 1) throw new ArgumentOutOfRangeException(nameof(absoluteNumber)); if (objectKeys == null) Interlocked.CompareExchange(ref objectKeys, new System.Collections.Concurrent.ConcurrentDictionary<int, string>(), null); return objectKeys.GetOrAdd(absoluteNumber, id => GraphGuard.Hash(RepositoryId, ModuleIdentity, id.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
        public string NodeIdentity(int absoluteNumber)
        { if (absoluteNumber < 1) throw new ArgumentOutOfRangeException(nameof(absoluteNumber)); if (nodeKeys == null) Interlocked.CompareExchange(ref nodeKeys, new System.Collections.Concurrent.ConcurrentDictionary<int, string>(), null); return nodeKeys.GetOrAdd(absoluteNumber, id => GraphGuard.Hash(ModuleBaselineIdentity, ObjectIdentity(id))); }
    }
    [DataContract]
    public sealed class ModuleBaselineVerificationResult
    {
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public string FullPath { get; set; }
        [DataMember] public BaselineVersionReference Version { get; set; }
        [DataMember] public BaselineVerificationReadState State { get; set; } = BaselineVerificationReadState.Unknown;
        [DataMember] public string Reason { get; set; }
        [DataMember] public bool InDefinition { get; set; } = true;
        [DataMember] public bool InSet { get; set; }
        [DataMember] public bool SelectedForVerification { get; set; }
        [DataMember] public string RequirementType { get; set; }
        [DataMember] public bool InventoryComplete { get; set; }
        [DataMember] public bool LinksComplete { get; set; }
        [DataMember] public int? KnownObjectCount { get; set; }
        [DataMember] public BaselineShareableConfiguration ShareableConfiguration { get; set; }
    }
    [DataContract]
    public sealed class BaselineRequirementInfo
    {
        [DataMember] public BaselineVersionReference Version { get; set; }
        [DataMember] public int AbsoluteNumber { get; set; }
        [DataMember] public string DisplayIdentifier { get; set; }
        [DataMember] public string RequirementType { get; set; }
        [DataMember] public string RequirementStatus { get; set; }
        [DataMember] public string Heading { get; set; }
        [DataMember] public string Text { get; set; }
        [DataMember] public string RichHeading { get; set; }
        [DataMember] public string RichText { get; set; }
        [DataMember] public bool IsDeleted { get; set; }
        [DataMember] public BaselineVerificationReadState Access { get; set; }
        public string ObjectIdentity => Version?.ObjectIdentity(AbsoluteNumber);
        public string NodeIdentity => Version?.NodeIdentity(AbsoluteNumber);
    }
    [DataContract]
    public sealed class BaselineLinkIdentity
    {
        public BaselineLinkIdentity(BaselineVersionReference source, int sourceObject, BaselineVersionReference target, int targetObject, string linkModuleIdentity)
        { Source = source ?? throw new ArgumentNullException(nameof(source)); Target = target ?? throw new ArgumentNullException(nameof(target)); if (sourceObject < 1 || targetObject < 1) throw new ArgumentException("Persistent Absolute Numbers are required."); SourceObject = sourceObject; TargetObject = targetObject; LinkModuleIdentity = GraphGuard.Text(linkModuleIdentity, 1024); }
        [DataMember] public BaselineVersionReference Source { get; private set; }
        [DataMember] public BaselineVersionReference Target { get; private set; }
        [DataMember] public int SourceObject { get; private set; }
        [DataMember] public int TargetObject { get; private set; }
        [DataMember] public string LinkModuleIdentity { get; private set; }
        public string PersistentKey => GraphGuard.Hash(Source.ObjectIdentity(SourceObject), Target.ObjectIdentity(TargetObject), LinkModuleIdentity);
        public string VersionedKey => GraphGuard.Hash(PersistentKey, Source.ModuleBaselineIdentity, Target.ModuleBaselineIdentity);
        public string AnchorKey => GraphGuard.Hash(Source.ObjectIdentity(SourceObject), LinkModuleIdentity);
    }
    [DataContract]
    public sealed class BaselineLinkInfo
    {
        [DataMember] public string ObservationId { get; set; }
        [DataMember] public BaselineVersionReference SourceVersion { get; set; }
        [DataMember] public BaselineVersionReference TargetVersion { get; set; }
        [DataMember] public int? SourceObject { get; set; }
        [DataMember] public int? TargetObject { get; set; }
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public string TargetPath { get; set; }
        [DataMember] public string ObservedSourceModuleIdentity { get; set; }
        [DataMember] public string ObservedTargetModuleIdentity { get; set; }
        public string SourceModuleIdentity => SourceVersion?.ModuleIdentity ?? ObservedSourceModuleIdentity;
        public string TargetModuleIdentity => TargetVersion?.ModuleIdentity ?? ObservedTargetModuleIdentity;
        [DataMember] public string LinkModuleIdentity { get; set; }
        [DataMember] public string LinkModulePath { get; set; }
        [DataMember] public string LinkType { get; set; }
        [DataMember] public LinkDirection Direction { get; set; }
        [DataMember] public LinkRecordKind Kind { get; set; }
        [DataMember] public bool IsEchoed { get; set; }
        [DataMember] public BaselineVerificationReadState SourceAccess { get; set; }
        [DataMember] public BaselineVerificationReadState TargetAccess { get; set; }
        [DataMember] public string Evidence { get; set; }
        // Only an adapter-observed physical identity can establish unexpected physical duplicates.
        [DataMember] public string PhysicalLinkIdentity { get; set; }
        [DataMember] public bool PhysicalMultiplicityVerified { get; set; }
        public BaselineLinkIdentity Identity => SourceVersion != null && TargetVersion != null && SourceObject > 0 && TargetObject > 0 && !string.IsNullOrEmpty(LinkModuleIdentity)
            ? new BaselineLinkIdentity(SourceVersion, SourceObject.Value, TargetVersion, TargetObject.Value, LinkModuleIdentity) : null;
    }
    [DataContract]
    public sealed class BaselineLinkVerificationResult
    {
        [DataMember] public BaselineLinkInfo Link { get; set; }
        [DataMember] public BaselineLinkStatus Status { get; set; }
        [DataMember] public BaselineLinkStatus UnderlyingStatus { get; set; }
        [DataMember] public bool CountsAsValid { get; set; }
        [DataMember] public string Reason { get; set; }
    }
    [DataContract]
    public sealed class TraceabilityViolation
    {
        [DataMember] public string Code { get; set; }
        [DataMember] public TraceabilitySeverity Severity { get; set; }
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public string NodeIdentity { get; set; }
        [DataMember] public string LinkIdentity { get; set; }
        [DataMember] public string RuleId { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public string Explanation { get; set; }
        [DataMember] public string Recommendation { get; set; }
        [DataMember] public bool Uncertain { get; set; }
    }
    [DataContract]
    public sealed class TraceabilityCoverageResult
    {
        [DataMember] public int TotalRequirements { get; set; }
        [DataMember] public int RequirementsRequiringLinks { get; set; }
        [DataMember] public int RequirementsWithValidLinks { get; set; }
        [DataMember] public int RequirementsMissingLinks { get; set; }
        [DataMember] public int UnresolvedRequirements { get; set; }
        [DataMember] public int AccessDeniedRequirements { get; set; }
        [DataMember] public int UnknownPolicyEligibility { get; set; }
        [DataMember] public int UnenumeratedModules { get; set; }
        [DataMember] public int BrokenTraceabilityChains { get; set; }
        [DataMember] public bool DenominatorComplete { get; set; }
        [DataMember] public double? TraceabilityCoveragePercentage { get; set; }
        [DataMember] public double? KnownScopeLowerBoundPercentage { get; set; }
        [DataMember] public double? KnownScopeUpperBoundPercentage { get; set; }
        public bool Conclusive => DenominatorComplete && UnresolvedRequirements == 0;
    }
    [DataContract]
    public sealed class BaselineVerificationContext
    {
        [DataMember] public BaselineMetadataSource Source { get; set; }
        [DataMember] public string EnvironmentId { get; set; }
        [DataMember] public string RepositoryId { get; set; }
        [DataMember] public string ClientVersion { get; set; }
        [DataMember] public string Operator { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public bool MetadataComplete { get; set; }
        [DataMember] public bool SessionObserved { get; set; }
        [DataMember] public string SessionId { get; set; }
        [DataMember] public bool SelectSetMembersByDefault { get; set; }
        [DataMember] public string Reason { get; set; }
        [DataMember] public BaselineSetDefinitionDetails Definition { get; set; }
        [DataMember] public NativeBaselineSetDetails Set { get; set; }
        [DataMember] public List<ModuleBaselineVerificationResult> Modules { get; set; } = new List<ModuleBaselineVerificationResult>();
        [DataMember] public List<BaselineVersionReference> ObservedVersionBindings { get; set; } = new List<BaselineVersionReference>();
        [DataMember] public Dictionary<string, string> LinkModuleIdentities { get; set; } = new Dictionary<string, string>();
        [DataMember] public Dictionary<string, string> ObservedVersionLabels { get; set; } = new Dictionary<string, string>();
    }
    [DataContract]
    public sealed class BaselineLinkSnapshot
    {
        [DataMember] public Guid SnapshotId { get; set; } = Guid.NewGuid();
        [DataMember] public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
        [DataMember] public BaselineVerificationContext Context { get; set; }
        [DataMember] public List<BaselineRequirementInfo> Requirements { get; set; } = new List<BaselineRequirementInfo>();
        [DataMember] public List<BaselineLinkInfo> Links { get; set; } = new List<BaselineLinkInfo>();
        [DataMember] public List<string> CapturedLinkModuleIdentities { get; set; } = new List<string>();
        [DataMember] public bool Truncated { get; set; }
        [DataMember] public bool ContextChanged { get; set; }
        [DataMember] public List<string> Limitations { get; set; } = new List<string>();
        public string Fingerprint => BaselineVerificationCopy.Fingerprint(this);
        public BaselineLinkSnapshot Copy() => BaselineVerificationCopy.Snapshot(this);
    }
    [DataContract]
    public sealed class BaselineVersionRebinding
    {
        [DataMember] public string BeforeVersionIdentity { get; set; }
        [DataMember] public string AfterVersionIdentity { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public bool AdapterValidated { get; set; }
    }
    [DataContract]
    public sealed class BaselineTraceabilityChange
    {
        [DataMember] public BaselineLinkChangeKind Kind { get; set; }
        [DataMember] public string BeforeIdentity { get; set; }
        [DataMember] public string AfterIdentity { get; set; }
        [DataMember] public string Evidence { get; set; }
        [DataMember] public bool Confirmed { get; set; }
        [DataMember] public bool SuspectedLinkLoss { get; set; }
    }
    public sealed class BaselineSetVerificationRequest
    {
        public BaselineSetVerificationRequest(string projectId, string folderPath, string definitionId, string setId, TraceabilityPolicy policy,
            IEnumerable<string> linkModulePaths, IEnumerable<string> selectedModuleIds = null, IDictionary<string, string> moduleRoles = null,
            BaselineLinkSnapshot reference = null, IEnumerable<BaselineVersionRebinding> rebindings = null, DoorsVersion target = DoorsVersion.V97,
            int maxRequirements = 100000, int maxLinks = 250000, int maxFindings = 500000, long maxMetadataCharacters = 67108864,
            string typeAttribute = "", string statusAttribute = "", bool allowExternalLinks = true, TimeSpan? timeout = null)
        {
            ProjectId = GraphGuard.Text(projectId, 256); FolderPath = DoorsItemPath.Normalize(folderPath); DefinitionId = GraphGuard.Text(definitionId, 4096); SetId = GraphGuard.Text(setId, 4096);
            Policy = policy ?? throw new ArgumentNullException(nameof(policy)); LinkModulePaths = GraphGuard.Strings(linkModulePaths, 32, 4096).Select(GraphGuard.Path).Distinct().ToArray();
            SelectedModuleIds = GraphGuard.Strings(selectedModuleIds, 10000, 1024); ModuleRoles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(moduleRoles ?? new Dictionary<string, string>(), StringComparer.Ordinal));
            foreach (var pair in ModuleRoles) { GraphGuard.Text(pair.Key, 1024); GraphGuard.Text(pair.Value, 128); }
            if (LinkModulePaths.Count == 0 || maxRequirements < 1 || maxRequirements > 1000000 || maxLinks < 1 || maxLinks > 1000000 || maxFindings < 1 || maxFindings > 2000000 || maxMetadataCharacters < 1024 || maxMetadataCharacters > 1073741824) throw new ArgumentException("Invalid verification limits.");
            if (policy.LinkRules.Any(r => !LinkModulePaths.Contains(r.LinkModulePath)) || policy.CycleRules.Any(r => r.LinkModulePaths.Any(p => !LinkModulePaths.Contains(p)))) throw new ArgumentException("Policy refers to a link module outside the requested scope.");
            DoorsVersions.Format(target); Target = target; MaxRequirements = maxRequirements; MaxLinks = maxLinks; MaxFindings = maxFindings; MaxMetadataCharacters = maxMetadataCharacters;
            TypeAttribute = GraphGuard.Text(typeAttribute, 256, true); StatusAttribute = GraphGuard.Text(statusAttribute, 256, true); AllowExternalLinks = allowExternalLinks;
            Timeout = timeout ?? TimeSpan.FromMinutes(2); if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
            Reference = reference == null ? null : BaselineVerificationCopy.Snapshot(reference); Rebindings = (rebindings ?? Enumerable.Empty<BaselineVersionRebinding>()).Take(10001).Select(BaselineSetSerialization.Clone).ToArray();
            if (Rebindings.Count > 10000) throw new ArgumentException("Too many version transitions.");
        }
        public string ProjectId { get; } public string FolderPath { get; } public string DefinitionId { get; } public string SetId { get; }
        public TraceabilityPolicy Policy { get; } public IReadOnlyList<string> LinkModulePaths { get; } public IReadOnlyList<string> SelectedModuleIds { get; }
        public IReadOnlyDictionary<string, string> ModuleRoles { get; } public BaselineLinkSnapshot Reference { get; } public IReadOnlyList<BaselineVersionRebinding> Rebindings { get; }
        public DoorsVersion Target { get; } public int MaxRequirements { get; } public int MaxLinks { get; } public int MaxFindings { get; } public long MaxMetadataCharacters { get; }
        public string TypeAttribute { get; } public string StatusAttribute { get; } public bool AllowExternalLinks { get; } public TimeSpan Timeout { get; }
        public bool ReadOnly => true;
    }
    [DataContract]
    public sealed class BaselineSetVerificationResult
    {
        [DataMember] public Guid VerificationJobId { get; internal set; }
        [DataMember] public DateTime StartedAtUtc { get; internal set; }
        [DataMember] public DateTime CompletedAtUtc { get; internal set; }
        [DataMember] public string ProjectId { get; internal set; }
        [DataMember] public string PolicyId { get; internal set; }
        [DataMember] public string PolicyFingerprint { get; internal set; }
        [DataMember] public BaselineVerificationStatus Status { get; internal set; }
        [DataMember] public BaselineLinkSnapshot Snapshot { get; internal set; }
        [DataMember] public List<BaselineLinkVerificationResult> Links { get; internal set; } = new List<BaselineLinkVerificationResult>();
        [DataMember] public TraceabilityCoverageResult Coverage { get; internal set; }
        [DataMember] public List<TraceabilityViolation> Findings { get; internal set; } = new List<TraceabilityViolation>();
        [DataMember] public List<BaselineTraceabilityChange> Changes { get; internal set; } = new List<BaselineTraceabilityChange>();
        [DataMember] public bool ReferenceAvailable { get; internal set; }
        public TraceabilityGraph Graph { get; internal set; }
        public bool ReadOnly => true;
    }
    public interface IBaselineSetVerificationSource
    { BaselineMetadataSource Source { get; } Task<BaselineVerificationContext> ReadContextAsync(BaselineSetVerificationRequest request, CancellationToken token); }
    public interface IBaselineLinkResolver
    { Task<BaselineLinkSnapshot> ResolveAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, IProgress<string> progress, CancellationToken token); }
    public sealed class BaselineNativeLinkEvidence
    {
        public bool Complete { get; set; }
        public string Reason { get; set; }
        public List<BaselineLinkInfo> Links { get; } = new List<BaselineLinkInfo>();
    }
    public interface IBaselineNativeLinkEvidenceReader
    { Task<BaselineNativeLinkEvidence> ReadAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, ModuleBaselineVerificationResult module, string linkModulePath, CancellationToken token); }
    public sealed class UnvalidatedBaselineNativeLinkEvidenceReader : IBaselineNativeLinkEvidenceReader
    { public Task<BaselineNativeLinkEvidence> ReadAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, ModuleBaselineVerificationResult module, string linkModulePath, CancellationToken token) => Task.FromResult(new BaselineNativeLinkEvidence { Reason = "NOT_RUN: sourceVersion/targetVersion/LinkRef anchor and native echo runtime behavior is not validated for this client build." }); }
    public interface IBaselineSetVerificationService
    { Task<BaselineSetVerificationResult> VerifyAsync(BaselineSetVerificationRequest request, IProgress<string> progress = null, CancellationToken token = default(CancellationToken)); }
    public interface IBaselineVerificationAuditSink
    { void Record(Guid jobId, string phase, BaselineMetadataSource source, string environment, string setId, string status, string evidenceFingerprint); }
    public sealed class NullBaselineVerificationAuditSink : IBaselineVerificationAuditSink
    { public void Record(Guid jobId, string phase, BaselineMetadataSource source, string environment, string setId, string status, string evidenceFingerprint) { } }
}
