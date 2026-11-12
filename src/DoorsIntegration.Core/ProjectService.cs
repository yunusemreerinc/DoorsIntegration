using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class ProjectService : IProjectService
    {
        private readonly IDoorsHierarchyReader reader;
        public ProjectService(IDoorsHierarchyReader reader) { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); }
        public Task<DiscoverySnapshot> DiscoverProjectsAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken)) =>
            reader.DiscoverAsync(DiscoveryKind.Projects, scope, cancellationToken);
        public Task<DiscoverySnapshot> DiscoverFoldersAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken)) =>
            reader.DiscoverAsync(DiscoveryKind.Folders, scope, cancellationToken);
        public Task<DiscoverySnapshot> DiscoverHierarchyAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken)) =>
            reader.DiscoverAsync(DiscoveryKind.Hierarchy, scope, cancellationToken);
    }
}
