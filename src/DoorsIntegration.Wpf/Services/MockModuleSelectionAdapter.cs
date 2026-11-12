using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    // Separate deterministic bulk demo catalog; existing Workspace mock data remains unchanged.
    public sealed class MockModuleSelectionAdapter : IDoorsHierarchyReader, IModuleSelectionMetadataProvider, IBaselineMetadataProvider
    {
        private readonly Dictionary<string, DoorsDiscoveryItem> items = new Dictionary<string, DoorsDiscoveryItem>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> identities = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly IBaselineMetadataProvider fallback;
        public MockModuleSelectionAdapter(IBaselineMetadataProvider fallback = null)
        {
            this.fallback = fallback;
            Add("/Bulk", DoorsItemKind.Folder);
            for (int p = 1; p <= 6; p++)
            {
                string project = "/Bulk/P" + p.ToString("D2"); Add(project, DoorsItemKind.Project, p == 6 ? DoorsAccessStatus.AccessDenied : DoorsAccessStatus.Readable);
                for (int f = 1; f <= 8; f++)
                {
                    string folder = project + "/F" + f.ToString("D2");
                    Add(folder, DoorsItemKind.Folder, p == 2 && f == 8 ? DoorsAccessStatus.AccessDenied : DoorsAccessStatus.Readable);
                    for (int m = 1; m <= 32; m++)
                    {
                        string path = folder + "/Requirement module " + m.ToString("D3");
                        Add(path, DoorsItemKind.FormalModule, m == 32 ? DoorsAccessStatus.AccessDenied : DoorsAccessStatus.Readable);
                        identities[path] = "mock-bulk-native-" + p.ToString("D2") + "-" + f.ToString("D2") + "-" + m.ToString("D3");
                    }
                    Add(folder + "/Links", DoorsItemKind.LinkModule); Add(folder + "/Legacy", DoorsItemKind.DescriptiveModule);
                }
            }
        }
        private void Add(string path, DoorsItemKind type, DoorsAccessStatus access = DoorsAccessStatus.Readable) =>
            items.Add(path, new DoorsDiscoveryItem(path, DoorsItemPath.LeafName(path), type, access));
        private DoorsAccessStatus EffectiveAccess(string path) => items.Values.Any(i => i.AccessStatus != DoorsAccessStatus.Readable &&
            (i.FullPath == path || path.StartsWith(i.FullPath + "/", StringComparison.Ordinal))) ? DoorsAccessStatus.AccessDenied : DoorsAccessStatus.Readable;
        public MockBaselineHistoryAdapter CreateBaselineHistoryAdapter()
        {
            var states = identities.Select(pair => HistoryState(new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, pair.Value, pair.Key),
                pair.Key.EndsWith("007", StringComparison.Ordinal), EffectiveAccess(pair.Key) == DoorsAccessStatus.Readable ? BaselineHistoryStatus.Available : BaselineHistoryStatus.AccessDenied)).ToList();
            states.Add(HistoryState(new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, "mock-qualified:" + MockDoorsAdapter.ModulePath, MockDoorsAdapter.ModulePath), false, BaselineHistoryStatus.Available));
            return new MockBaselineHistoryAdapter(states);
        }
        public MockBaselinePreflightStateReader CreatePreflightStateReader()
        {
            string epoch = Guid.NewGuid().ToString("N");
            var states = identities.Select(pair =>
            {
                var state = new MockBaselinePreflightState(new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, pair.Value, pair.Key)) { ConnectionEpoch = epoch };
                if (EffectiveAccess(pair.Key) != DoorsAccessStatus.Readable) state.ReadAccess = BaselineCheckState.Failed;
                else if (pair.Key.EndsWith("003", StringComparison.Ordinal))
                { state.EditState = BaselineEditState.ExclusiveOtherSession; state.LockState = BaselineLockState.OwnedByOtherSession; state.LockOwnerSessionId = "mock-other-session"; state.LockOwnerUserId = "mock-other-user"; }
                else if (pair.Key.EndsWith("004", StringComparison.Ordinal))
                { state.UnsavedChanges = BaselineUnsavedState.CurrentSession; state.DirtyOwnerSessionId = state.SessionId; }
                else if (pair.Key.EndsWith("005", StringComparison.Ordinal)) state.CreateAccess = BaselineCheckState.Failed;
                return state;
            }).ToList();
            states.Add(new MockBaselinePreflightState(new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, "mock-qualified:" + MockDoorsAdapter.ModulePath, MockDoorsAdapter.ModulePath)) { ConnectionEpoch = epoch });
            return new MockBaselinePreflightStateReader(states);
        }
        private static MockBaselineModuleState HistoryState(BaselineModuleReference module, bool first, BaselineHistoryStatus status)
        {
            var existing = first ? Array.Empty<BaselineHistoryEntry>() : new[] {
                new BaselineHistoryEntry(new BaselineIdentity(module, new BaselineVersion(1, 0)), "İlk inceleme", "mock-reviewer", new DateTime(2026, 1, 1, 12, 3, 0, DateTimeKind.Utc), "2026-01-01T12:03:00Z"),
                new BaselineHistoryEntry(new BaselineIdentity(module, new BaselineVersion(1, 2, "demo")), "Review baseline", "mock-reviewer", new DateTime(2026, 2, 1, 14, 0, 0, DateTimeKind.Utc), "2026-02-01T14:00:00Z") };
            return new MockBaselineModuleState(module, existing, first ? null : new BaselineVersion(2, 0, "removed"),
                new BaselineVersion(first ? 1 : 3, 0), new BaselineVersion(first ? 0 : 2, 1), status: status);
        }
        public Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested(); var result = new List<DoorsDiscoveryItem>(); var issues = new List<DiscoveryIssue>();
            if (scope.RootPath == "/") result.Add(new DoorsDiscoveryItem("/", "/", DoorsItemKind.DatabaseRoot, DoorsAccessStatus.Readable));
            else if (items.TryGetValue(scope.RootPath, out var root)) result.Add(new DoorsDiscoveryItem(root.FullPath, root.Name, root.Type, EffectiveAccess(root.FullPath)));
            else result.Add(new DoorsDiscoveryItem(scope.RootPath, DoorsItemPath.LeafName(scope.RootPath), DoorsItemKind.Unknown, DoorsAccessStatus.DeniedOrNotFound, false));
            if (result[0].AccessStatus == DoorsAccessStatus.Readable)
                result.AddRange(items.Values.Where(i => DoorsItemPath.IsDirectChild(scope.RootPath, i.FullPath)));
            else issues.Add(new DiscoveryIssue(scope.RootPath, DiscoveryIssueCode.AccessRestricted));
            return Task.FromResult(new DiscoverySnapshot(Guid.NewGuid(), kind, scope, result, issues, 1, false, DateTime.UtcNow));
        }
        public Task<ModuleSelectionMetadata> ReadAsync(string repositoryId, string canonicalModulePath, DoorsVersion target,
            DoorsConnectionMode connectionMode, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (repositoryId != MockDoorsAdapter.BaselineRepositoryId || !identities.TryGetValue(canonicalModulePath, out var id))
                return Task.FromResult(ModuleSelectionMetadata.Unavailable(canonicalModulePath, "MOCK: repository/module is outside the simulated catalog."));
            bool none = canonicalModulePath.EndsWith("007", StringComparison.Ordinal);
            return Task.FromResult(new ModuleSelectionMetadata(canonicalModulePath, new BaselineModuleReference(repositoryId, id, canonicalModulePath), EffectiveAccess(canonicalModulePath),
                none ? SelectionBaselineStatus.None : SelectionBaselineStatus.Available, none ? null : new BaselineVersion(1, 2, "demo"), "Mock baseline metadata"));
        }
        Task<BaselineModuleMetadata> IBaselineMetadataProvider.ReadAsync(BaselineModuleReference module, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!identities.TryGetValue(module.CanonicalFullPath, out var id) || module.RepositoryId != MockDoorsAdapter.BaselineRepositoryId || module.ModuleId != id)
                return fallback?.ReadAsync(module, cancellationToken) ?? Task.FromResult(BaselineModuleMetadata.Unavailable(module, "MOCK: stable identity is not in catalog.", DateTime.UtcNow));
            var access = EffectiveAccess(module.CanonicalFullPath);
            return Task.FromResult(new BaselineModuleMetadata(module, access == DoorsAccessStatus.Readable ? BaselineMetadataStatus.Available : BaselineMetadataStatus.AccessDenied,
                access == DoorsAccessStatus.Readable ? new BaselineVersion(1, 2, "demo") : null, BaselineCheckState.Passed,
                access == DoorsAccessStatus.Readable ? BaselineCheckState.Passed : BaselineCheckState.Failed, BaselineCheckState.Passed,
                new[] { new BaselineVersion(1, 2, "demo") }, true, DateTime.UtcNow, "mock-" + Guid.NewGuid().ToString("N")));
        }
    }
}
