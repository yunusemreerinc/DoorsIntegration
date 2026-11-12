using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Export;
using DoorsIntegration.Com;

namespace DoorsIntegration.Wpf.Services
{
    public sealed class BaselineManagerSession
    {
        public BaselineManagerSession(ClientMode mode, DoorsVersion version, IDoorsHierarchyReader hierarchy, IFolderModuleSelectionService selection,
            IBulkBaselinePreflightService preflight, IBaselinePreflightStateReader states, IBulkBaselineJobRunner runner,
            IBulkBaselineJobJournal journal, BulkBaselineJournalOptions storage, BaselineManagerApproval approval, bool canWrite, string limitation, IIntegrationLogger logger = null)
        { Mode = mode; Version = version; Hierarchy = hierarchy; Selection = selection; Preflight = preflight; States = states; Runner = runner; Journal = journal; Storage = storage; Approval = approval; CanWrite = canWrite; Limitation = limitation;
            var audit = journal as IBaselineJobAuditStore ?? throw new ArgumentException("Audit-capable journal required.");
            JobHistory = new JobHistoryService(journal, audit);
            Reports = new BulkBaselineReportService(journal, audit, ClientSessionFactory.CreateExporter(logger ?? new ClientLog())); }
        public IJobHistoryService JobHistory { get; }
        public IBulkBaselineReportService Reports { get; }
        public ClientMode Mode { get; }
        public DoorsVersion Version { get; }
        public bool CanWrite { get; }
        public string Limitation { get; }
        public IDoorsHierarchyReader Hierarchy { get; }
        public IFolderModuleSelectionService Selection { get; }
        public IBulkBaselinePreflightService Preflight { get; }
        public IBaselinePreflightStateReader States { get; }
        public IBulkBaselineJobRunner Runner { get; }
        public IBulkBaselineJobJournal Journal { get; }
        public BulkBaselineJournalOptions Storage { get; }
        public BaselineManagerApproval Approval { get; }
        public static BaselineManagerSession Create(ClientProfile profile, IIntegrationLogger logger, IClientReview dialog, string storageRoot,
            Func<bool> allowed, IDoorsHierarchyReader hierarchy, IDxlExecutor executor = null, IBaselineJournalProtector protector = null)
        {
            string root = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "WpfBulkBaseline"), profile.Mode.ToString(), DoorsVersions.Format(profile.Version));
            var clock = new BaselinePreflightClock(); var approval = new BaselineManagerApproval(dialog, profile.Mode, allowed);
            var storage = new BulkBaselineJournalOptions(Path.Combine(root, "jobs")); var journal = new FileBulkBaselineJobJournal(storage, protector);
            IBaselineMetadataService metadata; IBaselinePreflightStateReader states; IBaselineExecutionAdapter writer; IModuleSelectionMetadataProvider selectionMetadata;
            if (profile.Mode == ClientMode.Mock)
            {
                var catalog = new MockModuleSelectionAdapter(new MockDoorsAdapter()); hierarchy = catalog;
                var mock = new MockBaselineManagerStore(catalog, Path.Combine(root, "mock-baselines"));
                metadata = mock; states = mock; writer = mock; selectionMetadata = mock;
            }
            else
            {
                var history = new DxlBaselineHistoryAdapter(executor, new BaselineMetadataScriptOptions());
                metadata = new BaselineMetadataService(history, history, logger); states = new DxlBaselinePreflightStateReader(new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions()));
                writer = new DxlBaselineExecutionAdapter(executor, new BaselineExecutionScriptOptions(), new UnvalidatedBaselineExecutionCompatibility(), logger);
                selectionMetadata = new DxlModuleSelectionMetadataProvider(executor, new ModuleSelectionScriptOptions());
            }
            var preflight = new BulkBaselinePreflightService(states, metadata, clock, logger);
            var runner = new BulkBaselineJobRunner(journal, preflight, writer, approval, approval, new BaselineJobStateVerifier(metadata, states, clock), clock, logger,
                new ComBaselineJobAuditContextProvider(new ComRegistrationInspector()));
            return new BaselineManagerSession(profile.Mode, profile.Version, hierarchy, new FolderModuleSelectionService(hierarchy, selectionMetadata, logger),
                preflight, states, runner, journal, storage, approval, profile.Mode == ClientMode.Mock,
                profile.Mode == ClientMode.Mock ? "MOCK: yalnız simüle baseline ve yerel journal. Gerçek DOORS değişmez." : "REAL: native state/next-version ve deployment doğrulaması bekliyor. Yazma kapalı; ikinci onay bu engeli kaldırmaz.");
        }
    }
}
