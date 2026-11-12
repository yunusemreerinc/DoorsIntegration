using System;
using System.IO;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Export;

namespace DoorsIntegration.Wpf.Services
{
    public sealed class BaselineVerificationSession
    {
        public BaselineVerificationSession(IBaselineSetVerificationService service, BaselineLinkSnapshotService snapshots, BaselineVerificationReportService reports, ITraceabilityGraphService graphs)
        { Service = service; Snapshots = snapshots; Reports = reports; Graphs = graphs; }
        public IBaselineSetVerificationService Service { get; }
        public BaselineLinkSnapshotService Snapshots { get; }
        public BaselineVerificationReportService Reports { get; }
        public ITraceabilityGraphService Graphs { get; }
        internal static BaselineVerificationSession Create(ClientProfile profile, ClientSession session, IIntegrationLogger logger, string storageRoot,
            IBaselineJournalProtector protector = null, IBaselineSetAdapter native = null, MockBaselineSetVerificationAdapter mock = null, IDxlExecutor executor = null)
        {
            IBaselineSetVerificationSource source; IBaselineLinkResolver resolver;
            if (profile.Mode == ClientMode.Mock)
            { mock = mock ?? new MockBaselineSetVerificationAdapter(native: native as MockNativeBaselineSetAdapter); source = mock; resolver = mock; }
            else
            { source = new NativeBaselineSetVerificationSource(native, new NativeBaselineSetVerificationMetadataProvider(new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions())));
                resolver = new ServiceBaselineLinkResolver(session.Requirements, session.Links, new DxlReadOnlyLinkEvidenceReader(executor)); }
            string root = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "WpfBulkBaseline"), profile.Mode.ToString(), DoorsVersions.Format(profile.Version), "verification-audit");
            var audit = new FileBaselineVerificationAuditSink(new BulkBaselineJournalOptions(root), protector); var snapshots = new BaselineLinkSnapshotService(source, resolver);
            return new BaselineVerificationSession(new BaselineSetVerificationService(snapshots, source, new BaselineTraceabilityAnalyzer(), new BaselineTraceabilityComparisonService(), audit, logger), snapshots,
                new BaselineVerificationReportService(session.Export), session.CreateGraphService(logger));
        }
    }
}
