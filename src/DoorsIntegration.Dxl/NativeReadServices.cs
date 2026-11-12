using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DxlReadOnlyBaselineSetAdapter : IBaselineSetAdapter
    {
        private readonly INativeReadObservationSource source;
        public DxlReadOnlyBaselineSetAdapter(INativeReadObservationSource source) { this.source = source ?? throw new ArgumentNullException(nameof(source)); }
        public BaselineMetadataSource Source => BaselineMetadataSource.NativeDxl;
        public bool WritesValidated => false;
        public async Task<BaselineSetInspection> InspectAsync(BaselineSetRequest request, CancellationToken token)
        {
            var paths = request.Modules.Select(m => m.FullPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var result = await source.ReadAsync(new NativeReadQuery(request.Target, request.FolderPath, paths.Take(128)), token).ConfigureAwait(false);
            var revisions = new StringBuilder(result.Revision);
            for (int offset = 128; offset < paths.Length; offset += 128)
            {
                var chunk = await source.ReadAsync(new NativeReadQuery(request.Target, modules: paths.Skip(offset).Take(128)), token).ConfigureAwait(false);
                NativeBaselineSetVerificationMetadataProvider.SameSession(result, chunk); result.ModuleStates.AddRange(chunk.ModuleStates); revisions.Append(':').Append(chunk.Revision);
            }
            if (paths.Length > 128) using (var sha = SHA256.Create()) result.Revision = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(revisions.ToString()))).Replace("-", "");
            return result;
        }
        public Task<BaselineSetAdapterResult> ExecuteAsync(BaselineSetAuthorization authorization, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new BaselineSetAdapterResult(BaselineSetJobStatus.Blocked, "Read-only native adapter. Write compatibility and runtime acceptance are not validated; no write was dispatched.")); }
    }
    public sealed class DxlBaselinePreflightStateReader : IBaselinePreflightStateReader
    {
        private readonly INativeReadObservationSource source;
        public DxlBaselinePreflightStateReader(INativeReadObservationSource source) { this.source = source ?? throw new ArgumentNullException(nameof(source)); }
        public async Task<BaselinePreflightObservation> ReadAsync(BaselinePreflightProbe probe, CancellationToken token)
        {
            var read = await source.ReadAsync(new NativeReadQuery(probe.Request.Target, modules: new[] { probe.Module.CanonicalFullPath }), token).ConfigureAwait(false);
            var state = read.ModuleStates.Single(s => s.FullPath == probe.Module.CanonicalFullPath);
            bool session = read.Source == BaselineMetadataSource.NativeDxl && read.ReadOnlyObservation && read.SessionObserved && read.RepositoryId == probe.Module.RepositoryId;
            bool identity = state.ModuleIdentity == probe.Module.ModuleId;
            var unknown = BaselineCheckState.Unknown;
            var facts = new BaselinePreflightFacts(probe.Module, identity ? state.Exists : unknown, identity ? state.Kind : DoorsItemKind.Unknown,
                identity ? state.CanRead : unknown, identity ? state.CanCreate : unknown, identity ? state.CanModify : unknown,
                identity ? state.EditState : BaselineEditState.Unknown, identity ? state.LockState : BaselineLockState.Unknown, unknown,
                identity ? state.Unsaved : BaselineUnsavedState.Unknown, session ? BaselineCheckState.Passed : BaselineCheckState.Failed,
                read.DatabaseId, read.UserId, read.SessionId, read.SessionId, null, null, null, unknown,
                BaselineCheckState.Passed, BaselineCheckState.Passed, BaselineTargetAvailability.Unknown, BaselineMetadataSource.NativeDxl,
                "Native read-only state observed. " + (!identity ? "Stable module identity mismatch. " : "") +
                "Shareable=" + state.Shareable + ". Reliable revisions, other-session ownership and exclusive availability are not established; execution stays blocked.");
            return new BaselinePreflightObservation(probe, facts, DateTime.UtcNow);
        }
    }
    public sealed class NativeBaselineSetVerificationMetadataProvider : IBaselineSetVerificationMetadataProvider
    {
        private readonly INativeReadObservationSource source;
        public NativeBaselineSetVerificationMetadataProvider(INativeReadObservationSource source = null) { this.source = source; }
        public async Task<BaselineVerificationContext> BindAsync(BaselineSetVerificationRequest request, BaselineSetInspection inspection, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (inspection.Source != BaselineMetadataSource.NativeDxl || !inspection.ReadOnlyObservation || !inspection.SessionObserved) throw new InvalidOperationException("Observed native metadata/session required.");
            var definition = inspection.Definitions.SingleOrDefault(d => d.Id == request.DefinitionId);
            var set = inspection.Sets.SingleOrDefault(s => s.Id == request.SetId && s.DefinitionId == request.DefinitionId);
            var context = Context(inspection); context.Definition = definition; context.Set = set; context.SelectSetMembersByDefault = true;
            var members = (set?.Members ?? new List<BaselineSetModuleBaseline>()).ToDictionary(m => m.Module.ModuleId, StringComparer.Ordinal);
            var references = (definition?.Modules ?? new List<BaselineSetModuleReference>()).Concat(members.Values.Select(m => m.Module)).GroupBy(m => m.ModuleId).Select(g => g.First());
            foreach (var reference in references)
            {
                members.TryGetValue(reference.ModuleId, out var member); var version = member?.TypedVersion;
                bool bound = version != null && version.IdentityVerified && !version.IsCurrent && version.RepositoryId == inspection.RepositoryId && version.ModuleIdentity == reference.ModuleId && version.FullPath == reference.FullPath && version.ModuleBaselineIdentity == member.BaselineIdentity;
                var state = inspection.ModuleStates.SingleOrDefault(m => m.ModuleIdentity == reference.ModuleId);
                context.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = reference.ModuleId, FullPath = reference.FullPath, Version = bound ? version : null,
                    InDefinition = definition?.Modules.Any(m => m.ModuleId == reference.ModuleId) == true, InSet = member != null,
                    State = bound ? BaselineVerificationReadState.Verified : state?.CanRead == BaselineCheckState.Failed ? BaselineVerificationReadState.AccessDenied : BaselineVerificationReadState.Unknown,
                    ShareableConfiguration = state?.Shareable ?? BaselineShareableConfiguration.Unknown,
                    Reason = bound ? "Native typed member version; set version is independent. Scope may be visibility limited." : "No visible typed member evidence; hidden/inaccessible membership cannot be called Missing." });
                if (bound) { context.ObservedVersionBindings.Add(version); context.ObservedVersionLabels[ServiceBaselineLinkResolver.LabelKey(version.FullPath, LinkVersionKind.Baseline, member.ModuleBaselineVersion)] = version.ModuleBaselineIdentity; }
            }
            if (source != null)
            {
                var paths = context.Modules.Select(m => m.FullPath).Distinct().ToArray();
                for (int offset = 0; offset < Math.Max(1, paths.Length); offset += 128)
                {
                    token.ThrowIfCancellationRequested();
                    var observed = await source.ReadAsync(new NativeReadQuery(request.Target, modules: paths.Skip(offset).Take(128), links: offset == 0 ? request.LinkModulePaths : Array.Empty<string>()), token).ConfigureAwait(false);
                    SameSession(inspection, observed); foreach (var pair in observed.ObservedLinkModuleIds) context.LinkModuleIdentities[pair.Key] = pair.Value;
                    foreach (var module in context.Modules.Where(m => observed.ModuleStates.Any(s => s.FullPath == m.FullPath)))
                    {
                        var state = observed.ModuleStates.Single(s => s.FullPath == module.FullPath);
                        module.ShareableConfiguration = state.Shareable;
                        if (state.CanRead == BaselineCheckState.Failed) { module.State = BaselineVerificationReadState.AccessDenied; module.Reason = "Current metadata access denied; no missing baseline or link-loss inference."; }
                        else if (state.ModuleIdentity != module.ModuleIdentity) { module.State = BaselineVerificationReadState.Unknown; module.Version = null; module.Reason = "Observed module stable identity changed; recapture required."; }
                    }
                }
            }
            return context;
        }
        internal static BaselineVerificationContext Context(BaselineSetInspection read) => new BaselineVerificationContext {
            Source = BaselineMetadataSource.NativeDxl, EnvironmentId = read.DatabaseId, RepositoryId = read.RepositoryId, ClientVersion = read.ClientVersion, Operator = read.UserId,
            Revision = read.Revision, MetadataComplete = false, SessionObserved = read.SessionObserved, SessionId = read.SessionId, Reason = read.Reason,
            LinkModuleIdentities = new Dictionary<string, string>(read.ObservedLinkModuleIds, StringComparer.Ordinal) };
        internal static void SameSession(BaselineSetInspection a, BaselineSetInspection b)
        { if (!b.ReadOnlyObservation || !b.SessionObserved || a.DatabaseId != b.DatabaseId || a.RepositoryId != b.RepositoryId || a.UserId != b.UserId || a.ClientVersion != b.ClientVersion || a.SessionId != b.SessionId) throw new InvalidOperationException("DOORS context changed during native metadata reads; recapture is required."); }
    }
    public sealed class DxlImpactReadContextProvider : IImpactReadContextProvider
    {
        private readonly INativeReadObservationSource source;
        public DxlImpactReadContextProvider(INativeReadObservationSource source) { this.source = source ?? throw new ArgumentNullException(nameof(source)); }
        public async Task<BaselineVerificationContext> ObserveAsync(ImpactSnapshotSelection selection, CancellationToken token)
        {
            var read = await source.ReadAsync(new NativeReadQuery(selection.ClientVersion, modules: selection.Modules.Select(m => m.FullPath), links: selection.LinkModulePaths), token).ConfigureAwait(false);
            if (!read.ReadOnlyObservation || !read.SessionObserved || read.DatabaseId != selection.EnvironmentId || read.RepositoryId != selection.RepositoryId) throw new InvalidOperationException("Selected DOORS environment differs from observed native session.");
            var context = NativeBaselineSetVerificationMetadataProvider.Context(read);
            foreach (var version in selection.Modules)
            {
                var state = read.ModuleStates.Single(m => m.FullPath == version.FullPath);
                bool bound = version.IdentityVerified && version.RepositoryId == read.RepositoryId && version.ModuleIdentity == state.ModuleIdentity;
                context.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = version.ModuleIdentity, FullPath = version.FullPath, Version = bound ? version : null, SelectedForVerification = true,
                    InDefinition = true, State = state.CanRead == BaselineCheckState.Failed ? BaselineVerificationReadState.AccessDenied : BaselineVerificationReadState.Unknown,
                    ShareableConfiguration = state.Shareable, Reason = bound ? "Observed module QUID/session; selected version will be validated by requirement and link readers." : "Module identity/path mismatch; no read substitution." });
                if (bound) context.ObservedVersionBindings.Add(version);
            }
            context.MetadataComplete = context.Modules.All(m => m.Version != null && read.ModuleStates.Single(s => s.FullPath == m.FullPath).CanRead == BaselineCheckState.Passed) && selection.LinkModulePaths.All(context.LinkModuleIdentities.ContainsKey);
            context.Reason = "Explicit module-scope observation; no definition membership claim. Version existence and inventory are checked by the read adapters. Current captures remain non-atomic.";
            return context;
        }
    }
}
