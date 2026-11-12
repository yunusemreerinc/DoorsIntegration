using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    // Scope policy must enumerate ALL applicable link modules/directions and supply observed
    // stable identities. Paths alone and a readable-only subset cannot certify integrity.
    public sealed class BaselineSetLinkScope
    {
        public bool CoverageValidated { get; set; }
        public string Evidence { get; set; }
        public List<LinkReadRequest> Requests { get; } = new List<LinkReadRequest>();
        public Dictionary<string, string> ModuleIdentities { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }
    public interface IBaselineSetLinkScopeProvider
    { Task<BaselineSetLinkScope> GetAsync(BaselineSetDefinitionDetails definition, NativeBaselineSetDetails set, CancellationToken token); }
    public sealed class UnconfiguredBaselineSetLinkScopeProvider : IBaselineSetLinkScopeProvider
    { public Task<BaselineSetLinkScope> GetAsync(BaselineSetDefinitionDetails definition, NativeBaselineSetDetails set, CancellationToken token) => Task.FromResult(new BaselineSetLinkScope { Evidence = "NOT_RUN: full link scope, stable endpoint IDs and native set module-version binding are not validated." }); }
    public sealed class ServiceBaselineSetLinkReader : IBaselineSetLinkReader
    {
        private readonly ILinkService links; private readonly IBaselineSetLinkScopeProvider policy;
        public ServiceBaselineSetLinkReader(ILinkService links, IBaselineSetLinkScopeProvider policy) { this.links = links ?? throw new ArgumentNullException(nameof(links)); this.policy = policy ?? throw new ArgumentNullException(nameof(policy)); }
        public async Task<BaselineSetLinkSnapshot> CaptureAsync(BaselineSetDefinitionDetails definition, NativeBaselineSetDetails set, CancellationToken token)
        {
            var scope = await policy.GetAsync(definition, set, token).ConfigureAwait(false);
            var result = new BaselineSetLinkSnapshot { Complete = scope.CoverageValidated, Context = set?.Id ?? "Current", Reason = scope.Evidence };
            if (!scope.CoverageValidated) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in scope.Requests)
            {
                token.ThrowIfCancellationRequested();
                using (var snapshot = await links.ReadAsync(request, token).ConfigureAwait(false))
                {
                    if (!snapshot.Summary.IsCompleteForReadableScope) result.Complete = false;
                    foreach (var record in snapshot.ReadLinks())
                    {
                        if (record.Kind != LinkRecordKind.Link || record.Resolution != LinkResolution.Resolved || record.LinkModuleResolution != LinkModuleResolution.Verified ||
                            record.Source.Resolution != LinkEndpointResolution.Resolved || record.Target.Resolution != LinkEndpointResolution.Resolved || !record.Source.AbsoluteNumber.HasValue || !record.Target.AbsoluteNumber.HasValue ||
                            !scope.ModuleIdentities.TryGetValue(record.Source.ModulePath, out string sourceId) || !scope.ModuleIdentities.TryGetValue(record.Target.ModulePath, out string targetId) ||
                            !scope.ModuleIdentities.TryGetValue(record.LinkModulePath, out string linkId)) { result.Complete = false; continue; }
                        var edge = new BaselineSetLinkEdge { SourceModuleId = sourceId, TargetModuleId = targetId, LinkModuleId = linkId, SourceObjectId = record.Source.AbsoluteNumber.Value,
                            TargetObjectId = record.Target.AbsoluteNumber.Value, SourceVersion = record.Source.VersionKind == LinkVersionKind.Current ? "Current" : record.Source.Version,
                            TargetVersion = record.Target.VersionKind == LinkVersionKind.Current ? "Current" : record.Target.Version };
                        // The same directed link can be echoed by incoming/outgoing enumeration.
                        if (seen.Add(edge.Key)) result.Edges.Add(edge);
                        if (result.Edges.Count > 100000) throw new IntegrationException(IntegrationErrorCode.BulkBaselineJobFailed, "Link evidence exceeds the bounded native-set scope.");
                    }
                }
            }
            if (!result.Complete) result.Reason = "Incomplete/unresolved link scope; integrity is not verified.";
            result.Edges = result.Edges.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(); return result;
        }
    }
}
