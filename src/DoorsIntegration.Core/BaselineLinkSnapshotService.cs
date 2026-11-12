using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BaselineLinkSnapshotService
    {
        private readonly IBaselineSetVerificationSource source; private readonly IBaselineLinkResolver resolver;
        public BaselineLinkSnapshotService(IBaselineSetVerificationSource source, IBaselineLinkResolver resolver)
        { this.source = source ?? throw new ArgumentNullException(nameof(source)); this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver)); }
        public static BaselineLinkSnapshot ImportNativeReference(BaselineSetJob origin)
        {
            if (origin?.Before == null || origin.ReferenceLinks == null || (origin.Request.Action != BaselineSetAction.CreateMajorSet && origin.Request.Action != BaselineSetAction.CreateMinorSet)) throw new ArgumentException("An originating set-create journal with a recorded reference is required.");
            var definition = origin.Before.Definitions.SingleOrDefault(d => d.Id == origin.DefinitionId) ?? throw new ArgumentException("Originating definition identity is unavailable.");
            var repositories = definition.Modules.Select(m => m.RepositoryId).Distinct().ToArray(); if (repositories.Length != 1) throw new ArgumentException("Reference repository is not authoritative.");
            var context = new BaselineVerificationContext { Source = origin.Source, EnvironmentId = origin.Before.DatabaseId, RepositoryId = repositories[0], Operator = origin.Before.UserId, ClientVersion = origin.Before.ClientVersion,
                Definition = BaselineSetSerialization.Clone(definition), Revision = origin.Before.Revision, MetadataComplete = origin.Before.Complete && origin.ReferenceLinks.Complete, Reason = "Task 28 protected journal reference; link-only legacy scope, not a reconstructed requirement inventory." };
            var snapshot = new BaselineLinkSnapshot { CapturedAtUtc = origin.StartedAtUtc, Context = context };
            var current = definition.Modules.ToDictionary(m => m.ModuleId, m => new BaselineVersionReference(m.RepositoryId, m.ModuleId, m.FullPath, 0, 0, isCurrent: true, identityVerified: origin.Before.CanRead));
            foreach (var m in definition.Modules) context.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = m.ModuleId, FullPath = m.FullPath, Version = current[m.ModuleId], InDefinition = true, SelectedForVerification = true,
                State = origin.Before.CanRead ? BaselineVerificationReadState.Verified : BaselineVerificationReadState.Unknown, LinksComplete = origin.ReferenceLinks.Complete, InventoryComplete = false, Reason = "Legacy link-only snapshot; no historical object inventory or shareable flag is invented." });
            foreach (var edge in origin.ReferenceLinks.Edges)
            {
                current.TryGetValue(edge.SourceModuleId, out var sourceVersion); current.TryGetValue(edge.TargetModuleId, out var targetVersion);
                if (edge.SourceVersion != "Current") sourceVersion = null; if (edge.TargetVersion != "Current") targetVersion = null;
                snapshot.Links.Add(new BaselineLinkInfo { ObservationId = origin.JobId.ToString("N") + ":reference:" + snapshot.Links.Count, SourceVersion = sourceVersion, TargetVersion = targetVersion, SourceObject = edge.SourceObjectId, TargetObject = edge.TargetObjectId,
                    SourcePath = current.TryGetValue(edge.SourceModuleId, out var sourceModule) ? sourceModule.FullPath : null, TargetPath = current.TryGetValue(edge.TargetModuleId, out var targetModule) ? targetModule.FullPath : null,
                    ObservedSourceModuleIdentity = edge.SourceModuleId, ObservedTargetModuleIdentity = edge.TargetModuleId, LinkModuleIdentity = edge.LinkModuleId, Direction = LinkDirection.Outgoing,
                    SourceAccess = sourceVersion == null ? BaselineVerificationReadState.Unknown : BaselineVerificationReadState.Verified, TargetAccess = targetVersion == null ? BaselineVerificationReadState.Unknown : BaselineVerificationReadState.Verified,
                    Evidence = "Recorded Task 28 reference. Raw source=" + edge.SourceVersion + "; raw target=" + edge.TargetVersion + ". Opaque historical version labels are not parsed or replaced with current data." });
            }
            snapshot.CapturedLinkModuleIdentities = origin.ReferenceLinks.Edges.Select(e => e.LinkModuleId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            snapshot.Limitations.Add("Imported legacy reference has no full requirement inventory/typed historical baseline bindings. Absence remains indeterminate unless a complete comparable reference is supplied."); return snapshot;
        }
        public async Task<BaselineLinkSnapshot> CaptureAsync(BaselineSetVerificationRequest request, IProgress<string> progress = null, CancellationToken token = default(CancellationToken))
        {
            var context = BaselineSetSerialization.Clone(await source.ReadContextAsync(request, token).ConfigureAwait(false));
            if (context == null) throw new InvalidOperationException("Missing verification context.");
            foreach (var m in context.Modules) m.SelectedForVerification = request.SelectedModuleIds.Count == 0 ? m.InDefinition || context.SelectSetMembersByDefault && m.InSet : request.SelectedModuleIds.Contains(m.ModuleIdentity);
            // Do not reinterpret a hidden member as absent. The source must supply authoritative state.
            var before = BaselineSetSerialization.Hash(context);
            var snapshot = await resolver.ResolveAsync(request, context, progress, token).ConfigureAwait(false);
            snapshot.CapturedLinkModuleIdentities = request.LinkModulePaths.Where(context.LinkModuleIdentities.ContainsKey).Select(p => context.LinkModuleIdentities[p]).Distinct().ToList();
            token.ThrowIfCancellationRequested();
            var fresh = BaselineSetSerialization.Clone(await source.ReadContextAsync(request, token).ConfigureAwait(false));
            foreach (var m in fresh.Modules) m.SelectedForVerification = request.SelectedModuleIds.Count == 0 ? m.InDefinition || fresh.SelectSetMembersByDefault && m.InSet : request.SelectedModuleIds.Contains(m.ModuleIdentity);
            if (BaselineSetSerialization.Hash(fresh) != before) { snapshot.ContextChanged = true; snapshot.Limitations.Add("Metadata/session/version evidence changed during non-atomic read; no complete verification claim."); }
            if (snapshot.Requirements.Count > request.MaxRequirements || snapshot.Links.Count > request.MaxLinks) throw GraphGuard.Failure("Resolver exceeded verification bounds.");
            return snapshot;
        }
    }
    // Reads the same requirement/link services used by the existing graph and diff screens.
    public sealed class ServiceBaselineLinkResolver : IBaselineLinkResolver
    {
        private readonly IRequirementService requirements; private readonly ILinkService links;
        private readonly IBaselineNativeLinkEvidenceReader nativeEvidence;
        private readonly bool allowCurrentSnapshot;
        public ServiceBaselineLinkResolver(IRequirementService requirements, ILinkService links, IBaselineNativeLinkEvidenceReader nativeEvidence = null, bool allowCurrentSnapshot = false)
        { this.requirements = requirements ?? throw new ArgumentNullException(nameof(requirements)); this.links = links ?? throw new ArgumentNullException(nameof(links)); this.nativeEvidence = nativeEvidence ?? new UnvalidatedBaselineNativeLinkEvidenceReader(); this.allowCurrentSnapshot = allowCurrentSnapshot; }
        public async Task<BaselineLinkSnapshot> ResolveAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, IProgress<string> progress, CancellationToken token)
        {
            var result = new BaselineLinkSnapshot { Context = context }; long characters = 0;
            foreach (var m in context.Modules.Where(x => x.SelectedForVerification))
            {
                token.ThrowIfCancellationRequested();
                if (m.State != BaselineVerificationReadState.Verified || m.Version == null || m.Version.IsCurrent && !allowCurrentSnapshot || !m.Version.IdentityVerified) continue;
                var attributes = new[] { request.TypeAttribute, request.StatusAttribute }.Where(a => a.Length > 0).Distinct().ToArray();
                var read = new RequirementReadRequest(m.FullPath, attributes, m.Version.Baseline, DeletedObjectMode.MetadataOnly, false, true, request.MaxRequirements, timeout: request.Timeout, target: request.Target);
                progress?.Report("Requirements: " + m.FullPath + " @ " + m.Version.Version);
                using (var snapshot = await requirements.ReadAsync(read, token).ConfigureAwait(false))
                {
                    var s = snapshot.Summary;
                    if (s.Request.ModulePath != read.ModulePath || !Same(s.Request.Baseline, read.Baseline) || s.Request.Target != request.Target || s.Request.AllowAttributeDxl)
                    { m.InventoryComplete = false; m.LinksComplete = false; m.State = BaselineVerificationReadState.Unknown; m.Reason = "Requirement identity/baseline/scope echo mismatch."; continue; }
                    m.InventoryComplete = s.IsCompleteForReadableObjects; m.KnownObjectCount = s.ObjectCount;
                    if (s.ModuleStatus != RequirementModuleStatus.Available)
                    { m.InventoryComplete = m.LinksComplete = false; m.State = s.ModuleStatus == RequirementModuleStatus.AccessDenied ? BaselineVerificationReadState.AccessDenied : BaselineVerificationReadState.Unknown; m.Reason = s.ModuleStatus.ToString(); continue; }
                    if (s.QualifiedModuleId != m.ModuleIdentity)
                    { m.InventoryComplete = m.LinksComplete = false; m.State = BaselineVerificationReadState.Unknown; m.Reason = "Requirement stable module identity mismatch."; continue; }
                    var ids = new HashSet<int>();
                    foreach (var r in snapshot.ReadObjects())
                    {
                        token.ThrowIfCancellationRequested();
                        if (r.AbsoluteNumber <= 0 || !ids.Add(r.AbsoluteNumber)) throw GraphGuard.Failure("Invalid/duplicate persistent object identity in baseline inventory.");
                        if (result.Requirements.Count >= request.MaxRequirements) { result.Truncated = true; m.InventoryComplete = false; break; }
                        var role = Value(r, request.TypeAttribute); if (request.TypeAttribute.Length == 0) role = request.ModuleRoles.TryGetValue(m.ModuleIdentity, out string mapped) ? mapped : m.RequirementType;
                        var node = new BaselineRequirementInfo { Version = m.Version, AbsoluteNumber = r.AbsoluteNumber, DisplayIdentifier = r.ObjectIdentifier, RequirementType = role,
                            RequirementStatus = Value(r, request.StatusAttribute), Heading = r.Heading.PlainText, Text = r.Text.PlainText, RichHeading = r.Heading.RichTextWithOle, RichText = r.Text.RichTextWithOle,
                            IsDeleted = r.IsDeleted, Access = r.Text.PlainStatus == RequirementValueStatus.AccessDenied || r.Heading.PlainStatus == RequirementValueStatus.AccessDenied ? BaselineVerificationReadState.AccessDenied :
                                r.IsDeleted || (r.Text.PlainStatus == RequirementValueStatus.Readable && r.Heading.PlainStatus == RequirementValueStatus.Readable) ? BaselineVerificationReadState.Verified : BaselineVerificationReadState.Unknown };
                        characters += (node.Heading?.Length ?? 0) + (node.Text?.Length ?? 0) + (node.RichHeading?.Length ?? 0) + (node.RichText?.Length ?? 0) + (node.DisplayIdentifier?.Length ?? 0);
                        if (characters > request.MaxMetadataCharacters) { result.Truncated = true; m.InventoryComplete = false; break; }
                        result.Requirements.Add(node);
                    }
                    if (ids.Count != s.ObjectCount) m.InventoryComplete = false;
                }
                m.LinksComplete = true;
                foreach (string path in request.LinkModulePaths)
                {
                    token.ThrowIfCancellationRequested(); progress?.Report("Baseline links: " + m.FullPath + " / " + path);
                    var native = context.Source == BaselineMetadataSource.NativeDxl ? await nativeEvidence.ReadAsync(request, context, m, path, token).ConfigureAwait(false) : null;
                    if (native != null && !native.Complete) { m.LinksComplete = false; result.Limitations.Add(native.Reason); continue; }
                    var evidenceIndex = native?.Links.GroupBy(l => Join(l.SourcePath, l.SourceObject, l.TargetPath, l.TargetObject, l.Direction)).ToDictionary(g => g.Key, g => g.ToArray());
                    var readLinks = new LinkReadRequest(m.FullPath, path, LinkDirection.Both, m.Version.Baseline, maxObjects: request.MaxRequirements, maxLinks: request.MaxLinks, timeout: request.Timeout, target: request.Target);
                    using (var snapshot = await links.ReadAsync(readLinks, token).ConfigureAwait(false))
                    {
                        var s = snapshot.Summary;
                        if (s.Request.ModulePath != m.FullPath || s.Request.LinkModulePath != path || !Same(s.Request.Baseline, m.Version.Baseline) || s.Request.Target != request.Target || s.Request.Direction != LinkDirection.Both)
                            throw GraphGuard.Failure("Baseline link request echo mismatch.");
                        if (!s.IsCompleteForReadableScope) m.LinksComplete = false;
                        if (!string.IsNullOrEmpty(s.QualifiedAnchorModuleId) && s.QualifiedAnchorModuleId != m.ModuleIdentity) throw GraphGuard.Failure("Link anchor stable identity mismatch.");
                        context.LinkModuleIdentities.TryGetValue(path, out string linkId);
                        // Bind only a qualified identity observed by the echoed read, including a known empty scope.
                        if (string.IsNullOrEmpty(linkId) && !string.IsNullOrEmpty(s.QualifiedLinkModuleId) && s.ModuleStatus == RequirementModuleStatus.Available && s.LinkModuleStatus == LinkModuleStatus.Available)
                        { linkId = s.QualifiedLinkModuleId; context.LinkModuleIdentities[path] = linkId; }
                        if (string.IsNullOrEmpty(linkId) || !string.IsNullOrEmpty(s.QualifiedLinkModuleId) && s.QualifiedLinkModuleId != linkId) m.LinksComplete = false;
                        int count = 0;
                        foreach (var r in snapshot.ReadLinks())
                        {
                            token.ThrowIfCancellationRequested(); count++;
                            if (result.Links.Count >= request.MaxLinks) { result.Truncated = true; m.LinksComplete = false; break; }
                            BaselineLinkInfo exact = null;
                            if (evidenceIndex != null && evidenceIndex.TryGetValue(Join(r.Source.ModulePath, r.Source.AbsoluteNumber, r.Target.ModulePath, r.Target.AbsoluteNumber, r.Direction), out var possible))
                            { var versions = possible.GroupBy(l => l.Identity?.VersionedKey ?? l.ObservationId).ToArray(); if (versions.Length == 1) exact = versions[0].First(); }
                            if (native != null && exact == null) m.LinksComplete = false;
                            result.Links.Add(new BaselineLinkInfo { ObservationId = s.CorrelationId.ToString("N") + ":" + r.Sequence, SourceVersion = native == null ? Resolve(context, r.Source) : exact?.SourceVersion, TargetVersion = native == null ? Resolve(context, r.Target) : exact?.TargetVersion,
                                SourceObject = r.Source.AbsoluteNumber, TargetObject = r.Target.AbsoluteNumber, SourcePath = r.Source.ModulePath, TargetPath = r.Target.ModulePath,
                                ObservedSourceModuleIdentity = exact?.SourceModuleIdentity ?? (r.Direction == LinkDirection.Outgoing ? s.QualifiedAnchorModuleId : null), ObservedTargetModuleIdentity = exact?.TargetModuleIdentity ?? (r.Direction == LinkDirection.Incoming ? s.QualifiedAnchorModuleId : null),
                                LinkModuleIdentity = r.LinkModuleResolution == LinkModuleResolution.Verified ? linkId : null, LinkModulePath = path, LinkType = path, Direction = r.Direction, Kind = r.Kind, IsEchoed = r.IsEchoed,
                                SourceAccess = exact != null && exact.SourceAccess != BaselineVerificationReadState.Verified ? exact.SourceAccess : Access(r.Source), TargetAccess = exact != null && exact.TargetAccess != BaselineVerificationReadState.Verified ? exact.TargetAccess : Access(r.Target),
                                Evidence = (native == null ? "LinkService versioned endpoint evidence" : exact == null ? "Native source/target version evidence could not be uniquely joined" : exact.Evidence) + "; " + r.Kind + "; " + r.Resolution + "; physical multiplicity not established" });
                        }
                        if (count != s.RecordCount) m.LinksComplete = false;
                    }
                }
            }
            if (result.Truncated) result.Limitations.Add("Explicit requirement/link/metadata limit reached; coverage denominator may be incomplete.");
            return result;
        }
        public static string LabelKey(string path, LinkVersionKind kind, string rawLabel) => GraphGuard.Hash(path, kind.ToString(), rawLabel ?? "");
        private static string Join(string source, int? sourceObject, string target, int? targetObject, LinkDirection direction) => GraphGuard.Hash(source, sourceObject?.ToString(System.Globalization.CultureInfo.InvariantCulture), target, targetObject?.ToString(System.Globalization.CultureInfo.InvariantCulture), direction.ToString());
        private static BaselineVersionReference Resolve(BaselineVerificationContext context, LinkEndpoint endpoint)
        {
            if (endpoint.VersionKind == LinkVersionKind.Unknown) return null;
            context.ObservedVersionLabels.TryGetValue(LabelKey(endpoint.ModulePath, endpoint.VersionKind, endpoint.Version), out string id);
            var candidates = context.ObservedVersionBindings.Where(v => v.IdentityVerified && v.RepositoryId == context.RepositoryId && v.FullPath == endpoint.ModulePath &&
                v.IsCurrent == (endpoint.VersionKind == LinkVersionKind.Current) && (endpoint.VersionKind == LinkVersionKind.Current || id == v.ModuleBaselineIdentity)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        private static BaselineVerificationReadState Access(LinkEndpoint e) => e.Resolution == LinkEndpointResolution.Resolved ? BaselineVerificationReadState.Verified :
            e.Resolution == LinkEndpointResolution.AccessDenied ? BaselineVerificationReadState.AccessDenied : BaselineVerificationReadState.Unknown;
        private static bool Same(RequirementBaseline a, RequirementBaseline b) => a == null ? b == null : b != null && a.Major == b.Major && a.Minor == b.Minor && a.Suffix == b.Suffix;
        private static string Value(RequirementRecord r, string attribute) => attribute.Length > 0 && r.Values.TryGetValue(attribute, out var v) && v.PlainStatus == RequirementValueStatus.Readable ? v.PlainText : "";
    }
}
