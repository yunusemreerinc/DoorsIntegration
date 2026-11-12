using System;
using System.IO;
using DoorsIntegration.Core;
using DoorsIntegration.Export;

namespace DoorsIntegration.Wpf.Services
{
    public sealed class ChangeImpactSession
    {
        public ChangeImpactSession(IChangeImpactAnalysisService service, ImpactReviewService reviews, ImpactAnalysisReportService reports)
        { Service = service; Reviews = reviews; Reports = reports; }
        public IChangeImpactAnalysisService Service { get; }
        public ImpactReviewService Reviews { get; }
        public ImpactAnalysisReportService Reports { get; }
        internal static ChangeImpactSession Create(ClientProfile profile, ClientSession session, IIntegrationLogger logger, string storageRoot,
            IBaselineJournalProtector protector = null, IImpactSnapshotProvider mock = null, IBaselineNativeLinkEvidenceReader native = null, IImpactReadContextProvider readContext = null)
        {
            string root = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "WpfBulkBaseline"), "change-impact", profile.Mode.ToString(), DoorsVersions.Format(profile.Version));
            var reviews = new ImpactReviewService(new FileImpactReviewRepository(new BulkBaselineJournalOptions(Path.Combine(root, "reviews")), protector));
            IImpactSnapshotProvider snapshots = profile.Mode == ClientMode.Mock ? mock ?? new MockDoorsAdapter() : new ServiceImpactSnapshotProvider(session.Requirements, session.Links, session.BaselineVerification.Service, native ?? new UnvalidatedBaselineNativeLinkEvidenceReader(), readContext);
            var audit = new FileBaselineVerificationAuditSink(new BulkBaselineJournalOptions(Path.Combine(root, "audit")), protector);
            return new ChangeImpactSession(new ChangeImpactAnalysisService(snapshots, new RequirementChangeDetector(), new ImpactPropagationEngine(), new ImpactClassificationService(), reviews, logger, audit), reviews, new ImpactAnalysisReportService(session.Export));
        }
    }
}
