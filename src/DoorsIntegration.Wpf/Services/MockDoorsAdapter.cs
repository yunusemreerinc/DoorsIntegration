using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    // Deterministic offline read adapter. Real Core services, comparison and export are still used.
    public sealed class MockDoorsAdapter : IDoorsHierarchyReader, IRequirementReader, ILinkReader, IDoorsHealthChecks, IBaselineMetadataProvider, IImpactSnapshotProvider
    {
        public MockChangeImpactSnapshotProvider ChangeImpactSnapshots { get; } = new MockChangeImpactSnapshotProvider();
        public Task<ImpactAnalysisSnapshot> CaptureAsync(ImpactSnapshotSelection selection, ImpactAnalysisPolicy policy, IProgress<string> progress, CancellationToken token)
            => ChangeImpactSnapshots.CaptureAsync(selection, policy, progress, token);
        public const string ModulePath = "/Demo/Flight requirements";
        public const string LinkPath = "/Demo/Verification links";
        public const string BaselineRepositoryId = "mock-doors-demo";
        public MockNativeBaselineSetAdapter NativeBaselineSets { get; } = new MockNativeBaselineSetAdapter();
        public MockBaselineSetVerificationAdapter BaselineVerification { get; } = new MockBaselineSetVerificationAdapter();
        public static ClientSession CreateSession(IIntegrationLogger logger)
        {
            var adapter = new MockDoorsAdapter();
            var bulk = new MockModuleSelectionAdapter(adapter);
            var history = bulk.CreateBaselineHistoryAdapter();
            var metadata = new BaselineMetadataService(history, history, logger);
            var session = new ClientSession(new ProjectService(adapter), new RequirementService(adapter), new LinkService(adapter),
                new BaselineComparisonService(adapter, logger), ClientSessionFactory.CreateExporter(logger), adapter,
                null, null, new ModuleService(adapter), ClientSessionFactory.CreateBulkBaselineService(bulk, logger),
                new FolderModuleSelectionService(bulk, bulk, logger), metadata,
                new BulkBaselinePreflightService(bulk.CreatePreflightStateReader(), metadata, new BaselinePreflightClock(), logger));
            session.BaselineVerification = BaselineVerificationSession.Create(new ClientProfile(ClientMode.Mock, DoorsVersion.V97, DoorsConnectionMode.AttachExisting), session, logger, null, mock: adapter.BaselineVerification);
            session.ChangeImpact = ChangeImpactSession.Create(new ClientProfile(ClientMode.Mock, DoorsVersion.V97, DoorsConnectionMode.AttachExisting), session, logger, null, mock: adapter);
            return session;
        }
        // Explicit simulated observations, never inferred from a live DOORS session or native display version.
        public Task<BaselineModuleMetadata> ReadAsync(BaselineModuleReference module, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            cancellationToken.ThrowIfCancellationRequested();
            bool known = module.RepositoryId == BaselineRepositoryId &&
                (module.CanonicalFullPath == ModulePath || module.CanonicalFullPath == "/Demo/Verification/Test cases") &&
                module.ModuleId == "mock-qualified:" + module.CanonicalFullPath;
            return Task.FromResult(known ? new BaselineModuleMetadata(module, BaselineMetadataStatus.Available,
                new BaselineVersion(1, 2), BaselineCheckState.Passed, BaselineCheckState.Passed, BaselineCheckState.Passed,
                new[] { new BaselineVersion(1, 0), new BaselineVersion(1, 1), new BaselineVersion(1, 2) }, true, DateTime.UtcNow,
                "mock-" + Guid.NewGuid().ToString("N")) : BaselineModuleMetadata.Unavailable(module, "MOCK: stable module identity/path is not in the simulated catalog.", DateTime.UtcNow));
        }
        public async Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken cancellationToken)
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            var items = new[] { Item("/Demo", DoorsItemKind.Project), Item(ModulePath, DoorsItemKind.FormalModule),
                Item("/Demo/Verification", DoorsItemKind.Folder), Item("/Demo/Verification/Test cases", DoorsItemKind.FormalModule),
                Item(LinkPath, DoorsItemKind.LinkModule), Item("/Demo/Restricted", DoorsItemKind.FormalModule, DoorsAccessStatus.AccessDenied) }
                .Where(item => scope.RootPath == "/" || item.FullPath == scope.RootPath || item.FullPath.StartsWith(scope.RootPath + "/", StringComparison.Ordinal));
            items = items.Where(item => kind == DiscoveryKind.Hierarchy || (kind == DiscoveryKind.Projects && item.Type == DoorsItemKind.Project) ||
                (kind == DiscoveryKind.Folders && item.Type == DoorsItemKind.Folder) || (kind == DiscoveryKind.FormalModules && item.Type == DoorsItemKind.FormalModule) ||
                (kind == DiscoveryKind.ModuleByPath && item.FullPath == scope.RootPath && item.Type == DoorsItemKind.FormalModule));
            return new DiscoverySnapshot(Guid.NewGuid(), kind, scope, items, Array.Empty<DiscoveryIssue>(), 2, false, DateTime.UtcNow);
        }
        private static DoorsDiscoveryItem Item(string path, DoorsItemKind kind, DoorsAccessStatus access = DoorsAccessStatus.Readable) =>
            new DoorsDiscoveryItem(path, DoorsItemPath.LeafName(path), kind, access);
        public async Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
            if (request.ModulePath == "/Demo/Restricted") throw new IntegrationException(IntegrationErrorCode.ComAccessDenied, "Mock: bu modüle okuma izni yok.");
            if (request.ModulePath != ModulePath && request.ModulePath != "/Demo/Verification/Test cases")
                throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Mock örnek ağacından bir formal modül seçin.");
            return new MockRequirementSnapshot(request);
        }
        Task<ILinkSnapshot> ILinkReader.ReadAsync(LinkReadRequest request, CancellationToken cancellationToken) => ReadLinksAsync(request, cancellationToken);
        private static async Task<ILinkSnapshot> ReadLinksAsync(LinkReadRequest request, CancellationToken token)
        { await Task.Delay(160, token).ConfigureAwait(false); return new MockLinkSnapshot(request); }
        public DoorsHealthReport Run(DoorsVersion target, Guid correlationId, bool live = false, DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting, Action<DoorsHealthStepResult> progress = null)
        {
            var steps = Enum.GetValues(typeof(DoorsHealthStep)).Cast<DoorsHealthStep>().Select(step => new DoorsHealthStepResult(step,
                live ? DoorsHealthStatus.Passed : DoorsHealthStatus.Skipped, "MOCK / simülasyon; gerçek COM kaydı veya DOORS bağlantısı sınanmadı.")).ToArray();
            foreach (var step in steps) progress?.Invoke(step);
            return new DoorsHealthReport(correlationId, new DoorsCompatibilityReport(target, Environment.Is64BitOperatingSystem, Environment.Is64BitProcess,
                Thread.CurrentThread.GetApartmentState().ToString(), Array.Empty<ComRegistrationSnapshot>()), steps);
        }
        private sealed class MockRequirementSnapshot : IRequirementSnapshot
        {
            private bool disposed;
            public MockRequirementSnapshot(RequirementReadRequest request)
            {
                Summary = new RequirementReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly,
                    RequirementCleanupStatus.Closed, 240, 240, 0, 0, 0, 0, false, false, "mock-qualified:" + request.ModulePath);
            }
            public RequirementReadSummary Summary { get; }
            public IEnumerable<RequirementRecord> ReadObjects()
            {
                if (disposed) throw new ObjectDisposedException(nameof(MockRequirementSnapshot));
                bool revised = Summary.Request.Baseline == null || Summary.Request.Baseline.Major >= 2;
                for (int id = 1; id <= 240; id++)
                {
                    bool deleted = revised ? id == 8 : id == 9;
                    string text = "The system shall verify input " + id + " before processing. Türkçe / Unicode örneği.";
                    if (revised && id % 7 == 0) text += " Invalid input shall be reported.";
                    var values = new List<RequirementValue> { Value("Object Heading", "Input validation " + id, deleted), Value("Object Text", text, deleted) };
                    foreach (string attribute in Summary.Request.CustomAttributes)
                        values.Add(Value(attribute, attribute == "Status" ? (id % 3 == 0 ? "Reviewed" : "Draft") : "Demo value " + id, deleted));
                    yield return new RequirementRecord(id, id, "FLT-" + id, id.ToString(System.Globalization.CultureInfo.InvariantCulture), RequirementValueStatus.Readable,
                        1, null, RequirementParentStatus.Root, deleted, values);
                }
            }
            private static RequirementValue Value(string name, string value, bool deleted) => new RequirementValue(name, "Text",
                deleted ? RequirementValueStatus.NotRequested : RequirementValueStatus.Readable, deleted ? "" : value,
                deleted ? RequirementValueStatus.NotRequested : RequirementValueStatus.Readable, deleted ? "" : "{\\rtf1 " + value + "}");
            public Stream OpenRawStream() => throw new NotSupportedException("Use snapshot records or the shared exporter.");
            public void Dispose() { disposed = true; }
        }
        private sealed class MockLinkSnapshot : ILinkSnapshot
        {
            private bool disposed;
            public MockLinkSnapshot(LinkReadRequest request)
            {
                if (request.LinkModulePath != LinkPath) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Mock link module yolu: " + LinkPath);
                Summary = new LinkReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, LinkModuleStatus.Available,
                    RequirementModuleOwnership.OwnedReadOnly, 24, 240, 0, 0, 1, 0, false, false, 1, 0, 0);
            }
            public LinkReadSummary Summary { get; }
            public IEnumerable<LinkRecord> ReadLinks()
            {
                if (disposed) throw new ObjectDisposedException(nameof(MockLinkSnapshot));
                for (int i = 1; i <= 24; i++)
                    yield return new LinkRecord(i, LinkDirection.Outgoing, LinkRecordKind.Link, i == 24 ? LinkResolution.EndpointUnresolved : LinkResolution.Resolved,
                        false, Summary.Request.LinkModulePath, LinkModuleResolution.Verified,
                        new LinkEndpoint(Summary.Request.ModulePath, LinkVersionKind.Current, "Current", i, "FLT-" + i, LinkEndpointResolution.Resolved),
                        new LinkEndpoint("/Demo/Verification/Test cases", LinkVersionKind.Current, "Current", i, "TEST-" + i,
                            i == 24 ? LinkEndpointResolution.AccessDenied : LinkEndpointResolution.Resolved));
            }
            public Stream OpenRawStream() => throw new NotSupportedException();
            public void Dispose() { disposed = true; }
        }
    }
}
