using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    // Only read ports are held. Capture includes tombstone content where the existing reader permits it.
    public sealed class ServiceImpactSnapshotProvider : IImpactSnapshotProvider
    {
        // Reuse the captured, typed read-only inventory without issuing another COM object read.
        public static IRequirementReader CapturedRequirements(ImpactAnalysisSnapshot snapshot) => new CapturedRequirementReader(snapshot);
        private readonly IRequirementService requirements;
        private readonly ILinkService links;
        private readonly IBaselineSetVerificationService sets;
        private readonly IBaselineNativeLinkEvidenceReader native;
        private readonly IImpactReadContextProvider readContext;
        public ServiceImpactSnapshotProvider(IRequirementService requirements, ILinkService links, IBaselineSetVerificationService sets, IBaselineNativeLinkEvidenceReader native, IImpactReadContextProvider readContext = null)
        { this.requirements = requirements; this.links = links; this.sets = sets; this.native = native; this.readContext = readContext; }
        public async Task<ImpactAnalysisSnapshot> CaptureAsync(ImpactSnapshotSelection selection, ImpactAnalysisPolicy policy, IProgress<string> progress, CancellationToken token)
        {
            var snapshot = new ImpactAnalysisSnapshot { Label = selection.Label, CapturedAtUtc = DateTime.UtcNow };
            string observedContextFingerprint = null;
            if (selection.BaselineSet != null)
            {
                var verified = await sets.VerifyAsync(selection.BaselineSet, progress, token).ConfigureAwait(false);
                if (verified.Snapshot == null) throw new IntegrationException(IntegrationErrorCode.BaselineComparisonFailed, "Baseline-set evidence is unavailable; analysis cannot infer historical contents.");
                snapshot.Links = verified.Snapshot.Copy();
                snapshot.ComparedModuleIdentities = snapshot.Links.Context.Modules.Where(m => m.SelectedForVerification).Select(m => m.ModuleIdentity).ToList();
                snapshot.Limitations.AddRange(verified.Findings.Where(f => f.Uncertain).Select(f => f.Explanation));
            }
            else
            {
                if (selection.Modules.Count == 0 || selection.Modules.Select(v => v.ModuleIdentity).Distinct().Count() != selection.Modules.Count) throw new ArgumentException("Select unique stable module identities.");
                var c = readContext == null ? new BaselineVerificationContext { Source = BaselineMetadataSource.NativeDxl, EnvironmentId = selection.EnvironmentId, RepositoryId = selection.RepositoryId, MetadataComplete = true }
                    : await readContext.ObserveAsync(selection, token).ConfigureAwait(false);
                if (readContext != null) observedContextFingerprint = BaselineSetSerialization.Hash(c);
                foreach (var version in readContext == null ? selection.Modules : new List<BaselineVersionReference>())
                {
                    if (!version.IdentityVerified || version.RepositoryId != selection.RepositoryId) throw new ArgumentException("Explicit repository-scoped module identities are required.");
                    c.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = version.ModuleIdentity, FullPath = version.FullPath, Version = version, SelectedForVerification = true, InDefinition = true, InSet = false, State = BaselineVerificationReadState.Unknown });
                    c.ObservedVersionBindings.Add(version);
                }
                snapshot.Links = new BaselineLinkSnapshot { SnapshotId = Guid.NewGuid(), CapturedAtUtc = snapshot.CapturedAtUtc, Context = c };
                snapshot.ComparedModuleIdentities = selection.Modules.Select(v => v.ModuleIdentity).ToList();
            }
            var context = snapshot.Links.Context;
            if (context.EnvironmentId != selection.EnvironmentId || context.RepositoryId != selection.RepositoryId) throw new ArgumentException("Observed snapshot environment/repository does not match selection.");
            long characters = 0;
            foreach (var module in context.Modules.Where(m => m.SelectedForVerification))
            {
                token.ThrowIfCancellationRequested();
                if (module.Version == null) { module.InventoryComplete = false; continue; }
                if (selection.BaselineSet != null && module.State != BaselineVerificationReadState.Verified) { module.InventoryComplete = false; snapshot.Limitations.Add(module.ModuleIdentity + ": set baseline metadata is " + module.State + "; no substitute baseline/current read was attempted."); continue; }
                progress?.Report("Reading impact attributes: " + module.FullPath + " · " + module.Version.Version);
                var request = new RequirementReadRequest(module.FullPath, policy.CustomAttributes, module.Version.Baseline, DeletedObjectMode.IncludeContent,
                    false, true, policy.MaximumRequirements, timeout: TimeSpan.FromSeconds(policy.TimeoutSeconds), target: selection.ClientVersion);
                try
                {
                    using (var read = await requirements.ReadAsync(request, token).ConfigureAwait(false))
                    {
                        var s = read.Summary;
                        bool scope = s.Request.ModulePath == request.ModulePath && s.Request.Target == request.Target && !s.Request.AllowAttributeDxl && Same(s.Request.Baseline, request.Baseline) && s.Request.CustomAttributes.SequenceEqual(request.CustomAttributes);
                        if (!scope) { module.State = BaselineVerificationReadState.Unknown; module.InventoryComplete = false; module.Reason = "Requirement scope echo mismatch."; continue; }
                        if (s.ModuleStatus != RequirementModuleStatus.Available) { module.State = s.ModuleStatus == RequirementModuleStatus.AccessDenied ? BaselineVerificationReadState.AccessDenied : BaselineVerificationReadState.Unknown; module.InventoryComplete = false; module.Reason = s.ModuleStatus.ToString(); continue; }
                        if (s.QualifiedModuleId != module.ModuleIdentity) { module.State = BaselineVerificationReadState.Unknown; module.InventoryComplete = false; module.Reason = "Stable module identity differs from selected module."; continue; }
                        module.State = BaselineVerificationReadState.Verified; module.InventoryComplete = s.IsCompleteForReadableObjects && s.DeniedObjectCount == 0 && s.UnavailableObjectCount == 0; int count = 0;
                        foreach (var r in read.ReadObjects())
                        {
                            token.ThrowIfCancellationRequested(); count++;
                            var v = new ImpactRequirementVersion { Version = module.Version, AbsoluteNumber = r.AbsoluteNumber, DisplayIdentifier = r.ObjectIdentifier, ParentAbsoluteNumber = r.ParentAbsoluteNumber,
                                ParentKnown = r.ParentStatus != RequirementParentStatus.Unavailable, Position = r.SourceOrder, IsDeleted = r.IsDeleted, Access = BaselineVerificationReadState.Verified,
                                Attributes = r.Values.ToDictionary(p => p.Key, p => ImpactAttributeValue.From(p.Value), StringComparer.Ordinal) };
                            characters += v.Attributes.Sum(p => (long)(p.Value.PlainText?.Length ?? 0) + (p.Value.RichText?.Length ?? 0));
                            if (snapshot.Requirements.Count >= policy.MaximumRequirements || characters > policy.MaximumSnapshotCharacters) { snapshot.Links.Truncated = true; module.InventoryComplete = false; break; }
                            snapshot.Requirements.Add(v);
                        }
                        module.InventoryComplete &= count == s.ObjectCount; module.KnownObjectCount = count;
                    }
                }
                catch (IntegrationException ex)
                {
                    module.InventoryComplete = false; module.LinksComplete = false; module.State = ex.Code == IntegrationErrorCode.ComAccessDenied ? BaselineVerificationReadState.AccessDenied : BaselineVerificationReadState.Unknown; module.Reason = "Read unavailable: " + ex.Code + ". COM timeout may still be executing; no automatic retry."; snapshot.Limitations.Add(module.Reason);
                    if (ex.Code == IntegrationErrorCode.DxlTimeout || ex.Code == IntegrationErrorCode.DxlWaitCancelled) { snapshot.TransportUncertain = true; context.MetadataComplete = false; break; }
                }
            }
            if (snapshot.TransportUncertain) { snapshot.Links.Requirements = snapshot.Requirements.Select(v => ToBaseline(v, policy)).ToList(); return snapshot; }
            if (selection.BaselineSet == null && selection.LinkModulePaths.Count > 0)
            {
                // Reuse captured objects when the existing resolver requests type/status: no second object COM read.
                var resolver = new ServiceBaselineLinkResolver(new RequirementService(new CapturedRequirementReader(snapshot)), links, native, allowCurrentSnapshot: true);
                var request = EvidenceRequest(selection, policy);
                var resolved = await resolver.ResolveAsync(request, context, progress, token).ConfigureAwait(false);
                resolved.CapturedLinkModuleIdentities = request.LinkModulePaths.Where(context.LinkModuleIdentities.ContainsKey).Select(p => context.LinkModuleIdentities[p]).Distinct().ToList();
                snapshot.Links = resolved;
            }
            else snapshot.Links.Requirements = snapshot.Requirements.Select(v => ToBaseline(v, policy)).ToList();
            if (selection.LinkModulePaths.Count == 0 && selection.BaselineSet == null) { foreach (var m in context.Modules) m.LinksComplete = false; snapshot.Limitations.Add("No link-module scope selected; absence of impact paths is not proved."); }
            if (context.Modules.Any(m => m.State != BaselineVerificationReadState.Verified || !m.InventoryComplete)) snapshot.Limitations.Add("One or more module inventories are unavailable or incomplete; Added/Deleted is not inferred for those scopes.");
            if (context.Modules.Any(m => m.Version?.IsCurrent == true)) snapshot.Limitations.Add("Current module capture is a non-atomic read observation; no hidden cache or transactional consistency is claimed.");
            if (observedContextFingerprint != null)
            {
                var fresh = await readContext.ObserveAsync(selection, token).ConfigureAwait(false);
                if (BaselineSetSerialization.Hash(fresh) != observedContextFingerprint)
                { snapshot.Links.ContextChanged = true; snapshot.Limitations.Add("Native context, permission, edit state or link-module identity changed during capture; recapture is required."); }
            }
            return snapshot;
        }
        private static bool Same(RequirementBaseline a, RequirementBaseline b) => a == null ? b == null : b != null && a.Major == b.Major && a.Minor == b.Minor && a.Suffix == b.Suffix;
        internal static BaselineRequirementInfo ToBaseline(ImpactRequirementVersion v, ImpactAnalysisPolicy p) => new BaselineRequirementInfo { Version = v.Version, AbsoluteNumber = v.AbsoluteNumber, DisplayIdentifier = v.DisplayIdentifier,
            RequirementType = v.Value(p.TypeAttribute), RequirementStatus = v.Value(p.StatusAttribute), Heading = v.Value("Object Heading"), Text = v.Value("Object Text"), RichHeading = v.Attributes.TryGetValue("Object Heading", out var h) ? h.RichText : null,
            RichText = v.Attributes.TryGetValue("Object Text", out var t) ? t.RichText : null, IsDeleted = v.IsDeleted, Access = v.Access };
        internal static BaselineSetVerificationRequest EvidenceRequest(ImpactSnapshotSelection s, ImpactAnalysisPolicy p) => s.BaselineSet ?? new BaselineSetVerificationRequest("impact-analysis", "/", "module-scope", "module-scope", new TraceabilityPolicy("impact-evidence"),
            s.LinkModulePaths, target: s.ClientVersion, maxRequirements: p.MaximumRequirements, maxLinks: p.MaximumLinks, typeAttribute: p.TypeAttribute, statusAttribute: p.StatusAttribute, timeout: TimeSpan.FromSeconds(p.TimeoutSeconds));
        private sealed class CapturedRequirementReader : IRequirementReader
        {
            private readonly ImpactAnalysisSnapshot snapshot;
            internal CapturedRequirementReader(ImpactAnalysisSnapshot snapshot) { this.snapshot = snapshot; }
            public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var m = snapshot.Links.Context.Modules.Single(x => x.FullPath == request.ModulePath && Same(x.Version.Baseline, request.Baseline));
                var rows = snapshot.Requirements.Where(v => v.Version.ModuleIdentity == m.ModuleIdentity).ToArray();
                return Task.FromResult<IRequirementSnapshot>(new CapturedSnapshot(request, m, rows));
            }
        }
        private sealed class CapturedSnapshot : IRequirementSnapshot
        {
            private readonly ImpactRequirementVersion[] rows;
            internal CapturedSnapshot(RequirementReadRequest request, ModuleBaselineVerificationResult m, ImpactRequirementVersion[] rows)
            { this.rows = rows; Summary = new RequirementReadSummary(Guid.NewGuid(), request, m.State == BaselineVerificationReadState.Verified ? RequirementModuleStatus.Available : m.State == BaselineVerificationReadState.AccessDenied ? RequirementModuleStatus.AccessDenied : RequirementModuleStatus.ReadFailed,
                RequirementModuleOwnership.BorrowedReadOnly, RequirementCleanupStatus.NotOwned, rows.Length, rows.Length, 0, 0, 0, 0, !m.InventoryComplete, false, m.ModuleIdentity); }
            public RequirementReadSummary Summary { get; }
            public IEnumerable<RequirementRecord> ReadObjects() => rows.Select(v => new RequirementRecord(v.Position ?? 0, v.AbsoluteNumber, v.DisplayIdentifier, "", RequirementValueStatus.NotRequested, v.ParentAbsoluteNumber.HasValue ? 2 : 1, v.ParentAbsoluteNumber,
                !v.ParentKnown ? RequirementParentStatus.Unavailable : v.ParentAbsoluteNumber.HasValue ? RequirementParentStatus.Resolved : RequirementParentStatus.Root, v.IsDeleted, v.Attributes.Values.Select(a => a.ToValue())));
            public System.IO.Stream OpenRawStream() => throw new NotSupportedException("Captured in-memory view has no raw transport file.");
            public void Dispose() { }
        }
    }
}
