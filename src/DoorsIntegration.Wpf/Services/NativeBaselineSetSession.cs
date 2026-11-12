using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Export;

namespace DoorsIntegration.Wpf.Services
{
    public sealed class NativeBaselineSetSession
    {
        public NativeBaselineSetSession(INativeBaselineSetService service, NativeBaselineSetApproval approval, NativeBaselineSetReportService reports, IBaselineSetAdapter readAdapter = null)
        { Service = service; Approval = approval; Reports = reports; ReadAdapter = readAdapter; }
        internal IBaselineSetAdapter ReadAdapter { get; }
        public INativeBaselineSetService Service { get; }
        public NativeBaselineSetApproval Approval { get; }
        public NativeBaselineSetReportService Reports { get; }
        internal static NativeBaselineSetSession Create(ClientProfile profile, IIntegrationLogger logger, IClientReview review,
            string storageRoot, Func<bool> allowed, ILinkService links, IBaselineJournalProtector protector, MockNativeBaselineSetAdapter mock = null, IDxlExecutor executor = null)
        {
            string root = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "WpfBulkBaseline"), profile.Mode.ToString(), DoorsVersions.Format(profile.Version), "native-sets");
            var jobs = new FileBaselineSetJobRepository(new BulkBaselineJournalOptions(Path.Combine(root, "jobs")), protector);
            var approval = new NativeBaselineSetApproval(review, profile.Mode, allowed);
            IBaselineSetAdapter adapter; IBaselineSetLinkReader reader;
            if (profile.Mode == ClientMode.Mock)
            { mock = mock ?? new MockNativeBaselineSetAdapter(Path.Combine(root, "mock-state.bin"), protector); adapter = mock; reader = mock; }
            else
            { adapter = new DxlReadOnlyBaselineSetAdapter(new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions())); reader = new ServiceBaselineSetLinkReader(links, new UnconfiguredBaselineSetLinkScopeProvider()); }
            return new NativeBaselineSetSession(new NativeBaselineSetService(adapter, reader, jobs, approval, logger, allowed), approval,
                new NativeBaselineSetReportService(jobs, ClientSessionFactory.CreateExporter(logger)), adapter);
        }
    }
    public sealed class NativeBaselineSetApproval : IBaselineSetConfirmation
    {
        private readonly IClientReview dialog; private readonly ClientMode mode; private readonly Func<bool> allowed;
        private string fingerprint; private DateTime expires;
        public NativeBaselineSetApproval(IClientReview dialog, ClientMode mode, Func<bool> allowed) { this.dialog = dialog; this.mode = mode; this.allowed = allowed; }
        public void Clear() { fingerprint = null; expires = DateTime.MinValue; }
        public bool HasApproval(BaselineSetPreflight plan) => plan != null && plan.Ready && fingerprint == plan.Fingerprint && DateTime.UtcNow < expires && allowed();
        public async Task<bool> ReviewAsync(BaselineSetPreflight plan, CancellationToken token)
        { Clear(); if (dialog == null || !plan.Ready || !allowed()) return false; bool approved = await dialog.ReviewAsync(Review(plan, false), token).ConfigureAwait(false); if (approved && allowed() && !token.IsCancellationRequested) { fingerprint = plan.Fingerprint; expires = plan.ExpiresAtUtc; } return HasApproval(plan); }
        public async Task<bool> ConfirmAsync(BaselineSetPreflight plan, bool secondRealConfirmation, CancellationToken token)
        {
            if (!allowed() || token.IsCancellationRequested) return false;
            if (secondRealConfirmation) return mode == ClientMode.RealDoors && dialog != null && await dialog.ReviewAsync(Review(plan, true), token).ConfigureAwait(false);
            bool approved = HasApproval(plan); Clear(); return approved;
        }
        private WriteReview Review(BaselineSetPreflight p, bool second) => new WriteReview(second ? "REAL DOORS — second native set confirmation" : "Native Baseline Set — explicit approval",
            p.Request.Action + ": " + (p.Request.Name.Length > 0 ? p.Request.Name : p.Request.DefinitionId),
            "MODE=" + mode + "\nAction=" + p.Request.Action + "\nFolder=" + p.Request.FolderPath + "\nDefinition=" + p.Request.DefinitionId + "\nSet=" + p.Request.SetId +
            "\nSuffix=" + p.Request.Suffix + "\nAnnotation=" + p.Request.Annotation + "\nFingerprint=" + p.Fingerprint + "\nSelected modules=" + p.Request.Modules.Count +
            "\n" + string.Join("\n", p.Request.Modules.Select(m => m.ModuleId + " " + m.FullPath)) +
            "\nDefinition modules:\n" + string.Join("\n", p.Inspection.Definitions.Where(d => d.Id == p.Request.DefinitionId).SelectMany(d => d.Modules).Select(m => m.ModuleId + " " + m.FullPath)) +
            "\nNative addBaselines creates module baselines. Set and module versions are separate. Partial/Unknown results require reconciliation. No automatic close, retry, delete or rollback. Configured-shareable modules are blocked because links may be lost. Audit does not replace DOORS history.", p.ExpiresAtUtc, p.Ready);
    }
}
