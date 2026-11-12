using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    // All commands share the Workspace operation lifetime, cancellation, dispatcher commits and native drain.
    public sealed partial class OperationsViewModel : ObservableModel, IDisposable
    {
        private readonly WorkspaceViewModel owner;
        private readonly IIntegrationLogger logger;
        private readonly IClientSettingsStore settings;
        private readonly DxlWatchdogSettings watchdog;
        private ToolPage selected;
        private RequirementWritePreview requirementPlan;
        private FormalModuleCreationPreview modulePlan;
        private LinkWritePreview linkPlan;
        private BatchService batchService;
        private BatchPreview batchPlan;
        private BatchReport batchResult;
        private bool batchDispatched, retryAvailable;
        private DxlExecutionResult dxlResult;
        private readonly List<UiCommand> commands = new List<UiCommand>();
        public OperationsViewModel(WorkspaceViewModel owner, IIntegrationLogger logger, IClientSettingsStore settings, DxlWatchdogSettings watchdog)
        {
            this.owner = owner; this.logger = logger; this.settings = settings; this.watchdog = watchdog;
            Module = Page("Modül oluştur", "Current formal modül oluşturma. Önizleme + hedefi yazarak açık onay gerekir.");
            Field(Module, "folder", "Hedef klasör", "/"); Field(Module, "name", "Modül adı"); Field(Module, "description", "Açıklama", multiline: true); Timeout(Module);
            Action(Module, "Dry-run / önizleme", PreviewModule); Action(Module, "İncele ve oluştur…", ApplyModule, () => CanWrite && modulePlan?.CanCreate == true);
            Requirements = Page("Gereksinim düzenle", "Gerçek attribute kataloğunu yükleyin. Değiştirilecek alanları seçin. Çoklu enum değerlerini satırlarla ayırın. Zorunluluk kurum kuralıdır; metadata'dan varsayılmaz.");
            Field(Requirements, "module", "Formal modül yolu"); Choice<EditAction>(Requirements, "action", "İşlem"); Field(Requirements, "object", "Absolute Number (update/move)");
            Choice<RequirementPositionKind>(Requirements, "position", "Konum", "FirstInModule"); Field(Requirements, "anchor", "Anchor Absolute Number"); Timeout(Requirements);
            Action(Requirements, "Attribute kataloğunu yükle", LoadSchema); Action(Requirements, "Dry-run / önizleme", PreviewRequirement);
            Action(Requirements, "İncele ve uygula…", ApplyRequirement, () => CanWrite && requirementPlan?.CanApply == true);
            Links = Page("Link düzenle", "Mevcut link module/linkset kullanılır. Remove iki ayrı onay ister. Kaynak/hedef Absolute Number ile tanımlanır.");
            Choice<LinkWriteAction>(Links, "action", "İşlem"); Field(Links, "source", "Kaynak modül"); Field(Links, "sourceId", "Kaynak Absolute Number");
            Field(Links, "target", "Hedef modül"); Field(Links, "targetId", "Hedef Absolute Number"); Field(Links, "link", "Link module tam yolu"); Timeout(Links);
            Action(Links, "Dry-run / önizleme", PreviewLink); Action(Links, "İncele ve uygula…", ApplyLink, () => CanWrite && linkPlan?.CanApply == true);
            Batch = Page("Toplu import / update", "CSV/JSONL → captured preview → batch onayı → her kayıt için native onay. Partial/Unknown otomatik tekrar edilmez. Kalıcı journal kullanılır.");
            Field(Batch, "file", "Girdi dosyası"); Field(Batch, "format", "Format", "JSONL", choices: new[] { "JSONL", "CSV" });
            Field(Batch, "repository", "Beklenen DOORS database ID"); Field(Batch, "namespace", "Kalıcı işlem namespace"); Field(Batch, "max", "Max kayıt", "10000");
            Field(Batch, "attempts", "Key başına ömür boyu max deneme", "3"); Field(Batch, "partial", "Geçerli kayıtlarla devam et (atomik değil)", "false", boolean: true);
            Field(Batch, "quality", "İsteğe bağlı XML quality gate profili"); Field(Batch, "page", "Sayfa (100 kayıt)", "1", invalidates: false);
            Field(Batch, "failure", "Yeni failure CSV dosyası", invalidates: false); Timeout(Batch);
            Action(Batch, "Dry-run / önizleme", PreviewBatch); Action(Batch, "İncele ve uygula…", ApplyBatch, () => CanWrite && !batchDispatched && batchPlan?.CanApply == true);
            Action(Batch, "Güvenli hataları yeniden önizle", RetryBatch, () => CanWrite && retryAvailable);
            Action(Batch, "Sayfayı göster", ShowBatch, () => batchPlan != null); Action(Batch, "Failure CSV kaydet", SaveFailures, () => batchResult != null);
            Quality = Page("Kalite kontrolü", ProcessingDisclosure.Statement + " Retained requirements üzerinde offline çalışır.");
            Field(Quality, "profile", "XML / JSON review profili"); Field(Quality, "repository", "Snapshot repository ID"); Field(Quality, "baseline", "Exact baseline etiketi (baseline ise)");
            Field(Quality, "project", "JSON profil project ID"); Choice<RuleExecutionMode>(Quality, "mode", "JSON analiz modu"); Choice<RuleStandard>(Quality, "standard", "Örnek standart profili");
            Field(Quality, "reviewer", "Profil onaylayan reviewer"); Field(Quality, "reference", "Proje planı / onay referansı"); Field(Quality, "save", "Yeni JSON profil dosyası");
            Action(Quality, "Kalite analizi", AnalyzeQuality, () => owner.RequirementSnapshot != null);
            Action(Quality, "Örnek JSON profili kaydet", SaveRuleTemplate); Action(Quality, "Proje profilini incele ve onayla…", ApproveRuleProfile);
            Coverage = Page("Coverage analizi", ProcessingDisclosure.Statement + " Aynı modül/baseline requirements + links snapshot'ı gerekir. Graph görüntüleme ayrı sekmededir.");
            Field(Coverage, "repository", "Snapshot repository ID"); Field(Coverage, "baseline", "Exact baseline etiketi (baseline ise)");
            Field(Coverage, "attest", "Repository ve sürüm eşleşmesini doğruladım", "false", boolean: true);
            Field(Coverage, "policies", "Her satır: id|link module|Incoming/Outgoing|minimum|SameVersion/CurrentTarget/AnyExplicitVersion|counterpart path;path", multiline: true);
            Coverage.Report += "\nVersion policies: " + string.Join(", ", Enum.GetNames(typeof(CoverageVersionPolicy))) + ". Boş policy alanı mevcut link scope'unda outgoing/minimum 1 kullanır.";
            Action(Coverage, "Coverage analizi", AnalyzeCoverage, () => owner.RequirementSnapshot != null && owner.LinkSnapshot != null);
            Dxl = Page("DXL çalıştır", "Genel DXL veri değiştirebilir. Kaynak, hash ve parametreler onayda gösterilir. Include dosyaları yakalanmaz; yalnız güvenilir kod kullanın.");
            Field(Dxl, "file", "UTF-8 DXL dosyası"); Choice<DxlInvocationMethod>(Dxl, "method", "Çağrı yöntemi"); Choice<DxlResultMode>(Dxl, "result", "Sonuç aktarımı");
            Field(Dxl, "parameters", "Parametreler: her satır name=value (ilk = ayırıcıdır)", multiline: true); Timeout(Dxl);
            Field(Dxl, "inline", "Max inline karakter", "65536"); Field(Dxl, "mib", "Max sonuç MiB", "256"); Field(Dxl, "save", "Tam sonuç için yeni dosya", invalidates: false);
            Action(Dxl, "Kaynağı offline göster", PreviewDxl); Action(Dxl, "Kaynağı incele ve çalıştır…", RunDxl, () => CanWrite);
            Action(Dxl, "Tam sonucu kaydet", SaveDxl, () => dxlResult != null);
            Smoke = Page("Smoke suite", "11 read-only test. Offline varsayılan. Live yalnız Real DOORS ve mevcut oturuma attach eder. Timeout sonrası ilk rapor yazılır, orijinal native completion beklenir; retry yoktur.");
            Field(Smoke, "live", "Live native testleri çalıştır", "false", boolean: true); Field(Smoke, "root", "Keşif kökü", "/");
            Field(Smoke, "module", "Link testi anchor modülü (isteğe bağlı)"); Field(Smoke, "link", "Link module (anchor ile birlikte)");
            Field(Smoke, "maxItems", "Max keşif öğesi", "1000"); Field(Smoke, "maxLinks", "Max link", "1000"); Timeout(Smoke);
            Field(Smoke, "output", "Yeni rapor klasörü (boş: otomatik)"); Action(Smoke, "Smoke testlerini çalıştır", RunSmoke);
            Settings = Page("Settings", "DXL watchdog varsayılan açık. Kapatılırsa runLim denetimi devre dışı kalır; COM wait timeout yine zorla iptal sağlamaz. Mevcut settings.xml tercihi paylaşılır.");
            Field(Settings, "watchdog", "DXL watchdog denetimi açık", watchdog.Enabled ? "true" : "false", boolean: true);
            Field(Settings, "nativeDouble", "Native DOORS Double execution time seçimini otomatik yap (önce istemci bağlayın)", "false", boolean: true);
            Action(Settings, "Çalışan DOORS istemcisini bağla", BindNativeClient, () => owner.Mode == ClientMode.RealDoors && owner.Session.Tools?.Processes != null);
            Action(Settings, "Ayarı kaydet", SaveSettings);
            InitializeOptions();
            selected = Pages[0];
        }
        public enum EditAction { Create, Update, Move }
        public ObservableCollection<ToolPage> Pages { get; } = new ObservableCollection<ToolPage>();
        public ToolPage Selected { get => selected; set => Set(ref selected, value); }
        public ToolPage Module { get; }
        public ToolPage Requirements { get; }
        public ToolPage Links { get; }
        public ToolPage Batch { get; }
        public ToolPage Quality { get; }
        public ToolPage Coverage { get; }
        public ToolPage Dxl { get; }
        public ToolPage Smoke { get; }
        public ToolPage Settings { get; }
        private bool CanWrite => !owner.IsReadOnly && owner.Mode == ClientMode.RealDoors && owner.Session.Tools != null;
        private ToolPage Page(string title, string description) { var p = new ToolPage(title, description); Pages.Add(p); return p; }
        private void Field(ToolPage page, string key, string label, string initial = "", string[] choices = null, bool boolean = false, bool multiline = false, bool invalidates = true) =>
            page.Fields.Add(new ToolField(key, label, initial, () => {
                if (page == Requirements && key == "action") Requirements.Field("position").Value = Requirements.Text("action") == "Create" ? "FirstInModule" : Requirements.Text("action") == "Update" ? "Keep" : "After";
                if (invalidates) Invalidate(page);
            }, choices, boolean, multiline));
        private void Choice<T>(ToolPage page, string key, string label, string initial = null) => Field(page, key, label, initial ?? Enum.GetNames(typeof(T))[0], Enum.GetNames(typeof(T)));
        private void Timeout(ToolPage page) => Field(page, "timeout", "Timeout saniye", "120");
        private void Action(ToolPage page, string label, Func<CancellationToken, IProgress<string>, Task<Action>> work, Func<bool> can = null)
        {
            var command = new UiCommand(() => owner.RunToolAsync(page.Title, async (token, progress) =>
            { var commit = await work(token, progress).ConfigureAwait(false); return () => { commit?.Invoke(); Refresh(); }; }), () => owner.IsIdle && !owner.IsClosing && (can?.Invoke() ?? true));
            commands.Add(command); page.Actions.Add(new ToolAction(label, command));
        }
        private void Invalidate(ToolPage page)
        {
            if (page == ReadOptions) owner.InvalidateReadSettings();
            if (page == Requirements) requirementPlan = null;
            if (page == Module) modulePlan = null;
            if (page == Links) linkPlan = null;
            if (page == Batch) { batchPlan?.Dispose(); batchPlan = null; batchResult = null; batchService = null; retryAvailable = false; batchDispatched = false; }
            if (page == Requirements && Requirements.Attributes.Count > 0 && Requirements.Text("module") != loadedModule)
            { Requirements.Attributes.Clear(); Requirements.SchemaChanged(); }
            Refresh();
        }
        private string loadedModule;
        public void InvalidateContext()
        { requirementPlan = null; modulePlan = null; linkPlan = null; batchPlan?.Dispose(); batchPlan = null; batchService = null; batchResult = null;
            if (owner.Session.Tools?.RunLimits != null) owner.Session.Tools.RunLimits.Enabled = false;
            Settings.Field("nativeDouble").Flag = false;
            dxlResult?.Dispose(); dxlResult = null; Requirements.Attributes.Clear(); Requirements.SchemaChanged(); loadedModule = null; Refresh(); }
        public bool IsIdle => owner.IsIdle;
        public void Refresh() { foreach (var command in commands) command.Refresh(); Changed(nameof(IsIdle)); }
        private static int Number(ToolPage p, string key) => int.Parse(p.Text(key), NumberStyles.None, CultureInfo.InvariantCulture);
        private static T EnumValue<T>(ToolPage p, string key) => (T)Enum.Parse(typeof(T), p.Text(key), false);
        private static TimeSpan Wait(ToolPage p) => TimeSpan.FromSeconds(Number(p, "timeout"));
        private ClientTools RealTools => owner.Session.Tools ?? throw new InvalidOperationException("Bu işlem Real DOORS gerektirir; Mock veri değiştirmez.");
        private RequirementWriteTarget WriteTarget => new RequirementWriteTarget(Requirements.Text("module"), owner.Version, owner.Connection, Wait(Requirements));
        private static Task<Action> Report(ToolPage page, string message) => Task.FromResult<Action>(() => page.Report = message);

        private async Task<Action> PreviewModule(CancellationToken token, IProgress<string> progress)
        {
            modulePlan = null;
            var plan = await RealTools.Modules.PreviewFormalModuleCreationAsync(new FormalModuleCreateRequest(Module.Text("folder"), Module.Text("name"), Module.Text("description"), owner.Version, owner.Connection, Wait(Module)), token).ConfigureAwait(false);
            return () => { modulePlan = plan; Module.Report = WriteReview.From(plan).Body; };
        }
        private async Task<Action> ApplyModule(CancellationToken token, IProgress<string> progress)
        {
            var plan = modulePlan ?? throw new InvalidOperationException("Yeni dry-run gerekli."); modulePlan = null;
            await owner.InvalidateBeforeWriteAsync().ConfigureAwait(false);
            FormalModuleCreationResult result;
            try { result = await RealTools.Modules.CreateFormalModuleAsync(plan, token).ConfigureAwait(false); }
            catch (ModuleCreationUncertainException ex) { progress.Report("Belirsiz sonuç; orijinal completion bekleniyor. Retry yok."); result = await ex.Completion.ConfigureAwait(false); }
            return await Report(Module, "Outcome=" + result.Outcome + "; Path=" + result.FullPath + "; ID=" + result.QualifiedModuleId + "; Cleanup=" + result.Cleanup + "\n" + result.Checks.Summary);
        }
        private async Task<Action> LoadSchema(CancellationToken token, IProgress<string> progress)
        {
            var schema = await owner.Session.Requirements.GetAttributeSchemaAsync(WriteTarget, token).ConfigureAwait(false);
            return () => { requirementPlan = null; loadedModule = schema.ModulePath; Requirements.Attributes.Clear();
                foreach (var a in schema.Attributes) Requirements.Attributes.Add(new AttributeEditRow(a, () => Invalidate(Requirements)));
                Requirements.SchemaChanged(); Requirements.Report = "Available=" + schema.IsAvailable + "; Database=" + schema.DatabaseId + "; Actor=" + schema.UserName +
                    "; Module=" + schema.QualifiedModuleId + "\n" + string.Join("\n", schema.Issues.Select(i => i.Severity + ": " + i.Code + " " + i.AttributeName)); };
        }
        private async Task<Action> PreviewRequirement(CancellationToken token, IProgress<string> progress)
        {
            requirementPlan = null;
            if (loadedModule != Requirements.Text("module")) throw new InvalidOperationException("Önce bu modülün attribute kataloğunu yükleyin.");
            var action = EnumValue<EditAction>(Requirements, "action");
            var kind = EnumValue<RequirementPositionKind>(Requirements, "position");
            var changes = action == EditAction.Move ? new RequirementAttributeChange[0] : Requirements.Attributes.Where(a => a.Include).Select(a =>
                new RequirementAttributeChange(a.Name, a.Value, a.Format, a.Format == RequirementWriteFormat.DoorsRichText ? a.ExpectedPlain : null)).ToArray();
            var position = new RequirementPosition(kind, string.IsNullOrWhiteSpace(Requirements.Text("anchor")) ? (int?)null : Number(Requirements, "anchor"));
            var plan = await owner.Session.Requirements.PreviewWriteAsync(new RequirementWriteRequest(WriteTarget, changes,
                action == EditAction.Create ? (int?)null : Number(Requirements, "object"), position, Requirements.Attributes.Where(a => a.Required).Select(a => a.Name)), token).ConfigureAwait(false);
            return () => { requirementPlan = plan; Requirements.Report = WriteReview.From(plan).Body; };
        }
        private async Task<Action> ApplyRequirement(CancellationToken token, IProgress<string> progress)
        {
            var plan = requirementPlan ?? throw new InvalidOperationException("Yeni dry-run gerekli."); requirementPlan = null;
            await owner.InvalidateBeforeWriteAsync().ConfigureAwait(false);
            RequirementWriteResult result;
            try { result = await owner.Session.Requirements.ApplyWriteAsync(plan, token).ConfigureAwait(false); }
            catch (RequirementWriteUncertainException ex) { progress.Report("Belirsiz sonuç; orijinal completion bekleniyor. Retry yok."); result = await ex.Completion.ConfigureAwait(false); }
            return await Report(Requirements, "Outcome=" + result.Outcome + "; Persistence=" + result.Persistence + "; Cleanup=" + result.Cleanup +
                "; Audit=" + result.AuditRecorded + "; Object=" + result.AbsoluteNumber + "; Identifier=" + result.ObjectIdentifier + "\n" +
                string.Join("\n", result.Steps.Select(s => s.Kind + " " + s.AttributeName + ": " + s.Status + " " + s.Error)));
        }
        private async Task<Action> PreviewLink(CancellationToken token, IProgress<string> progress)
        {
            linkPlan = null;
            var plan = await owner.Session.Links.PreviewWriteAsync(new LinkWriteRequest(EnumValue<LinkWriteAction>(Links, "action"), Links.Text("source"), Number(Links, "sourceId"),
                Links.Text("target"), Number(Links, "targetId"), Links.Text("link"), owner.Version, owner.Connection, Wait(Links)), token).ConfigureAwait(false);
            return () => { linkPlan = plan; Links.Report = WriteReview.From(plan).Body; };
        }
        private async Task<Action> ApplyLink(CancellationToken token, IProgress<string> progress)
        {
            var plan = linkPlan ?? throw new InvalidOperationException("Yeni dry-run gerekli."); linkPlan = null;
            await owner.InvalidateBeforeWriteAsync().ConfigureAwait(false);
            LinkWriteResult result;
            try { result = await owner.Session.Links.ApplyWriteAsync(plan, token).ConfigureAwait(false); }
            catch (LinkWriteUncertainException ex) { progress.Report("Belirsiz link sonucu; orijinal completion bekleniyor."); result = await ex.Completion.ConfigureAwait(false); }
            return await Report(Links, "Outcome=" + result.Outcome + "; Persistence=" + result.Persistence + "; Cleanup=" + result.Cleanup + "; Audit=" + result.AuditRecorded +
                "\n" + string.Join("\n", result.Issues) + "\n" + (result.Comparison == null ? "Traceability comparison unavailable" :
                    "Before=" + result.Before.Count + "; After=" + result.After.Count + "; Added=" + result.Comparison.Added.Count + "; Removed=" + result.Comparison.Removed.Count));
        }

        private async Task<Action> PreviewBatch(CancellationToken token, IProgress<string> progress)
        {
            batchPlan?.Dispose(); batchPlan = null; batchService = null; batchResult = null;
            var tools = RealTools;
            var validators = string.IsNullOrWhiteSpace(Batch.Text("quality")) ? new IBatchChangeValidator[0] : new IBatchChangeValidator[] { new QualityBatchValidator(QualityProfile.LoadXml(Batch.Text("quality")), new ProcessingClock()) };
            // Use the same journal, audit and confirmation semantics; optional quality gate is part of the preview fingerprint.
            var confirmation = tools.Review as ClientConfirmation ?? throw new InvalidOperationException("Batch confirmation unavailable.");
            var service = new BatchService(new DoorsBatchChangeAdapter(owner.Session.Requirements, new DoorsBatchAdapterOptions(owner.Version, owner.Connection, Wait(Batch))),
                new FileBatchJournal(tools.Storage), new FileBatchAuditSink(tools.Storage), confirmation, tools.Storage, new ProcessingClock(), validators);
            IBatchRecordSource source = Batch.Text("format") == "CSV" ? (IBatchRecordSource)new CsvBatchSource(Batch.Text("file")) : new JsonLinesBatchSource(Batch.Text("file"));
            var plan = await service.PreviewAsync(source, new BatchOptions(Batch.Text("repository"), Batch.Text("namespace"), Batch.Flag("partial"), Number(Batch, "attempts"), maxRecords: Number(Batch, "max")), token, new BatchProgressSink(progress)).ConfigureAwait(false);
            string report;
            try { report = BatchSummary(plan) + "\n" + BatchRows(plan, null, 1); } catch { plan.Dispose(); throw; }
            return () => { batchPlan?.Dispose(); batchPlan = plan; batchService = service; batchResult = null; retryAvailable = false; batchDispatched = false; Batch.Report = report; };
        }
        private async Task<Action> ApplyBatch(CancellationToken token, IProgress<string> progress)
        {
            batchDispatched = true; retryAvailable = false;
            await owner.InvalidateBeforeWriteAsync().ConfigureAwait(false);
            var result = await batchService.ApplyAsync(batchPlan, token, new BatchProgressSink(progress)).ConfigureAwait(false);
            bool canRetry = result.ReadRows().Any(r => r.CanRepreview);
            string report = "Applied/historical=" + result.AppliedCount + "; Failed/not attempted=" + result.FailedCount + "; Stopped=" + result.Stopped +
                "\nRows=" + result.RowsPath + "\nFailures=" + result.FailuresPath + "\n" + BatchRows(batchPlan, result, 1);
            return () => { batchResult = result; retryAvailable = canRetry; Batch.Report = report; };
        }
        private async Task<Action> RetryBatch(CancellationToken token, IProgress<string> progress)
        {
            var plan = await batchService.PreviewRetryAsync(batchPlan, batchResult, token, new BatchProgressSink(progress)).ConfigureAwait(false);
            string report;
            try { report = BatchSummary(plan) + "\n" + BatchRows(plan, null, 1); } catch { plan.Dispose(); throw; }
            return () => { batchPlan.Dispose(); batchPlan = plan; batchResult = null; retryAvailable = false; batchDispatched = false; Batch.Report = report; };
        }
        private static string BatchSummary(BatchPreview p) => "Records=" + p.RecordCount + "; Ready=" + p.ReadyCount + "; Blocked=" + p.BlockedCount + "; CanApply=" + p.CanApply +
            "\nCaptured preview=" + p.FilePath + "\nFingerprint=" + p.Fingerprint + "\nExpires UTC=" + p.ExpiresAtUtc.ToString("O");
        private static string BatchRows(BatchPreview plan, BatchReport report, int page)
        {
            if (page < 1 || page > 1000) throw new ArgumentOutOfRangeException(nameof(page));
            int skip = (page - 1) * 100;
            if (report != null) return string.Join("\n", report.ReadRows().Skip(skip).Take(100).Select(r => r.RecordNumber + " " + r.Key + " " + r.Status + " " + r.Code + "; Attempts=" + r.Attempts + "; Audit=" + r.AuditRecorded + "; CanRepreview=" + r.CanRepreview));
            var output = new StringBuilder();
            foreach (var row in plan.ReadRows().Skip(skip).Take(100))
            {
                if (output.Length > 262144) { output.AppendLine("[Önizleme kesildi; tüm kayıtlar captured preview dosyasında]"); break; }
                output.AppendLine(row.RecordNumber + " " + row.Change?.Key + " " + row.Status + " " + row.Code);
                if (row.Inspection != null) foreach (var issue in row.Inspection.Issues) output.AppendLine("CHECK: " + issue);
                foreach (var before in row.Inspection?.BeforeValues ?? new BatchBeforeValue[0])
                { if (output.Length > 262144) break; output.AppendLine("BEFORE " + before.Name).AppendLine(Clip(before.PlainText)).AppendLine("RAW RICH:").AppendLine(Clip(before.RichText)); }
                foreach (var value in row.Change?.Attributes ?? new BatchAttribute[0])
                { if (output.Length > 262144) break; output.AppendLine("REQUESTED " + value.Name + " [" + value.Format + "]").AppendLine(Clip(value.Value)); }
            }
            return output.ToString();
        }
        private Task<Action> ShowBatch(CancellationToken token, IProgress<string> progress) => Report(Batch, BatchSummary(batchPlan) + "\n" + BatchRows(batchPlan, batchResult, Number(Batch, "page")));
        private Task<Action> SaveFailures(CancellationToken token, IProgress<string> progress)
        { BatchReportExporter.ExportFailuresCsv(batchResult, Path.GetFullPath(Batch.Text("failure"))); return Report(Batch, "Failure CSV=" + Batch.Text("failure")); }
        private sealed class BatchProgressSink : IProgress<BatchProgress>
        { private readonly IProgress<string> progress; public BatchProgressSink(IProgress<string> progress) { this.progress = progress; } public void Report(BatchProgress p) => progress.Report(p.Phase + "; Record=" + p.RecordNumber + "; Completed=" + p.Completed); }

        private string AnalysisDirectory(string kind) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "processing", "analysis", kind + "-" + Guid.NewGuid().ToString("N"));
        private async Task<Action> AnalyzeQuality(CancellationToken token, IProgress<string> progress)
        {
            if (string.Equals(Path.GetExtension(Quality.Text("profile")), ".json", StringComparison.OrdinalIgnoreCase)) return await AnalyzeJsonQuality(token, progress).ConfigureAwait(false);
            var snapshot = owner.RequirementSnapshot; var profile = QualityProfile.LoadXml(Quality.Text("profile")); var repo = Quality.Text("repository");
            if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("Repository ID gerekli.");
            string path = AnalysisDirectory("quality"); QualitySummary summary;
            using (var output = new FileQualityReport(path)) summary = new QualityEngine().Evaluate(ProcessingSnapshotAdapter.Requirements(snapshot, repo, Null(Quality.Text("baseline"))), profile,
                new QualityContext("CallerBoundRepository;RequirementRequest=" + snapshot.Summary.CorrelationId, snapshot.Summary.IsCompleteForReadableObjects, DateTime.UtcNow), output, token);
            return await Report(Quality, "Artifacts=" + summary.Artifacts + "; Findings=" + summary.Findings + "; NotEvaluated=" + summary.NotEvaluated + "; RuleErrors=" + summary.RuleErrors +
                "; FullyEvaluated=" + summary.FullyEvaluated + "; Cancelled=" + summary.Cancelled + "; Truncated=" + summary.Truncated + "\nReport=" + path + "\n" + ProcessingDisclosure.Statement);
        }
        private async Task<Action> AnalyzeCoverage(CancellationToken token, IProgress<string> progress)
        {
            var requirements = owner.RequirementSnapshot; var links = owner.LinkSnapshot; var r = requirements.Summary.Request; var l = links.Summary.Request;
            if (r.ModulePath != l.ModulePath || !SameBaseline(r.Baseline, l.Baseline)) throw new ArgumentException("Requirements ve links aynı modül/baseline olmalı.");
            string repo = Coverage.Text("repository"), label = Coverage.Text("baseline"); if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("Repository ID gerekli.");
            var policies = new List<CoveragePolicy>();
            foreach (var line in Lines(Coverage.Text("policies")))
            { var p = line.Split('|'); if (p.Length != 6) throw new ArgumentException("Coverage policy 6 sütun içermeli.");
                policies.Add(new CoveragePolicy(p[0], p[1], (CoverageDirection)Enum.Parse(typeof(CoverageDirection), p[2]), int.Parse(p[3], CultureInfo.InvariantCulture),
                    p[5].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), (CoverageVersionPolicy)Enum.Parse(typeof(CoverageVersionPolicy), p[4]))); }
            if (policies.Count == 0) policies.Add(new CoveragePolicy("required-trace", l.LinkModulePath, CoverageDirection.Outgoing));
            if (policies.Any(p => p.Relationship != l.LinkModulePath || (l.Direction != LinkDirection.Both && l.Direction.ToString() != p.Direction.ToString())))
                throw new ArgumentException("Policy yönü/link module retained link scope'u içinde olmalı.");
            bool complete = Coverage.Flag("attest") && requirements.Summary.IsCompleteForReadableObjects && links.Summary.IsCompleteForReadableScope && (r.Baseline == null || label.Length > 0);
            string path = AnalysisDirectory("coverage"); CoverageSummary summary;
            using (var output = new FileCoverageReport(path)) summary = new TraceabilityCoverageService().Analyze(ProcessingSnapshotAdapter.Nodes(requirements, repo, Null(label)),
                ProcessingSnapshotAdapter.Relations(links, repo), policies, new CoverageContext(repo, complete, "CallerAttestedRepository=" + Coverage.Flag("attest") + "; NonAtomicSnapshots"), output, token);
            return await Report(Coverage, "Covered=" + summary.Covered + "; Uncovered=" + summary.Uncovered + "; Unknown=" + summary.Unknown + "; Excluded=" + summary.Excluded +
                "; ScopeComplete=" + summary.ScopeComplete + "; Coverage=" + (summary.CoveragePercent.HasValue ? summary.CoveragePercent.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "Unavailable") + "\nReport=" + path + "\n" + ProcessingDisclosure.Statement);
        }
        private static bool SameBaseline(RequirementBaseline a, RequirementBaseline b) => a == null ? b == null : b != null && a.Major == b.Major && a.Minor == b.Minor && a.Suffix == b.Suffix;
        private static string Null(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
        private static string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        private static string Clip(string value) => value == null ? "" : value.Length <= 16384 ? value : value.Substring(0, 16384) + "\n[Kesildi; tam değer captured preview/export dosyasında]";

        private Task<Action> PreviewDxl(CancellationToken token, IProgress<string> progress) => Report(Dxl, new Utf8DxlScriptLoader().Load(Path.GetFullPath(Dxl.Text("file")), 1048576));
        private async Task<Action> RunDxl(CancellationToken token, IProgress<string> progress)
        {
            var tools = RealTools; string path = Path.GetFullPath(Dxl.Text("file"));
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Lines(Dxl.Text("parameters"))) { int i = line.IndexOf('='); if (i <= 0) throw new ArgumentException("Parametre name=value olmalı."); parameters.Add(line.Substring(0, i), line.Substring(i + 1)); }
            using (var captured = new CapturedScript(new Utf8DxlScriptLoader().Load(path, 1048576)))
            {
                var request = new DxlExecutionRequest(captured.PathName, EnumValue<DxlInvocationMethod>(Dxl, "method"), EnumValue<DxlResultMode>(Dxl, "result"), parameters,
                    Wait(Dxl), owner.Connection, owner.Version, maxInlineResultChars: Number(Dxl, "inline"), maxResultFileBytes: checked((long)Number(Dxl, "mib") * 1048576));
                if (!await tools.Review.ReviewAsync(new WriteReview("DXL yürütme onayı", path, "Genel DXL veri değiştirebilir. Include bağımlılıkları yakalanmaz.\nPath=" + path +
                    "\nSHA256=" + captured.Sha256 + "\nProfile=" + owner.Version + "; Connection=" + owner.Connection + "; Method=" + request.Method + "; Result=" + request.ResultMode +
                    "; Timeout=" + request.Timeout + "\nPARAMETERS:\n" + string.Join("\n", parameters.Select(p => p.Key + "=" + p.Value)) + "\nSOURCE:\n" + captured.Source, DateTime.UtcNow.AddMinutes(5), true), token).ConfigureAwait(false))
                    return await Report(Dxl, "DXL yürütmesi onaylanmadı; dispatch yapılmadı.");
                token.ThrowIfCancellationRequested(); await owner.InvalidateBeforeWriteAsync().ConfigureAwait(false);
                var operation = tools.Dxl.Start(request); DxlExecutionResult result;
                try { result = await operation.WaitAsync(token).ConfigureAwait(false); }
                catch (DxlExecutionWaitException) { progress.Report("Orijinal DXL completion bekleniyor; retry yok."); result = await operation.Completion.ConfigureAwait(false); }
                string preview;
                try { if (!result.IsFile) preview = result.Text.Length <= 65536 ? result.Text : result.Text.Substring(0, 65536) + "\n[Kesildi; tam sonucu kaydedin]";
                    else using (var stream = result.OpenResultStream()) using (var reader = new StreamReader(stream, Encoding.UTF8)) { var buffer = new char[65537]; int count = reader.ReadBlock(buffer, 0, buffer.Length); preview = new string(buffer, 0, Math.Min(count, 65536)) + (count > 65536 ? "\n[Kesildi; tam sonucu kaydedin]" : ""); } }
                catch { result.Dispose(); throw; }
                return () => { dxlResult?.Dispose(); dxlResult = result; Dxl.Report = "Request=" + result.CorrelationId + "; FileBytes=" + result.ResultFileBytes + "\n" + preview; };
            }
        }
        private Task<Action> SaveDxl(CancellationToken token, IProgress<string> progress)
        { using (var output = new FileStream(Path.GetFullPath(Dxl.Text("save")), FileMode.CreateNew, FileAccess.Write))
            { if (dxlResult.IsFile) { using (var input = dxlResult.OpenResultStream()) input.CopyTo(output); } else using (var writer = new StreamWriter(output, new UTF8Encoding(false, true))) writer.Write(dxlResult.Text); }
            return Report(Dxl, "Tam sonuç kaydedildi: " + Dxl.Text("save")); }
        private async Task<Action> RunSmoke(CancellationToken token, IProgress<string> progress)
        {
            if (Smoke.Flag("live") && owner.Mode != ClientMode.RealDoors) throw new ArgumentException("Live smoke için Real DOORS seçin; Mock native dispatch yapmaz.");
            string destination = string.IsNullOrWhiteSpace(Smoke.Text("output")) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "SmokeReports", Guid.NewGuid().ToString("N")) : Path.GetFullPath(Smoke.Text("output"));
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Rapor için yeni klasör gerekli.");
            var request = new DoorsSmokeRequest(owner.Version, Smoke.Flag("live"), Smoke.Text("root"), Null(Smoke.Text("module")), Null(Smoke.Text("link")), Wait(Smoke), Number(Smoke, "maxItems"), Number(Smoke, "maxLinks"));
            var run = await ClientTools.CreateSmoke(logger).RunAsync(request, new SmokeProgress(progress), token).ConfigureAwait(false);
            var writer = new DoorsSmokeReportWriter();
            try { writer.WriteInitial(destination, run); progress.Report("İlk smoke raporu: " + destination + "; Status=" + run.Status + "; SessionUncertain=" + run.SessionUncertain); }
            finally { await run.Settlement.ConfigureAwait(false); }
            writer.WriteSettlement(destination, run, await run.Settlement.ConfigureAwait(false));
            return await Report(Smoke, "Status=" + run.Status + "; ExitCode=" + run.ExitCode + "; SessionUncertain=" + run.SessionUncertain + "\nReport=" + destination + "\n" +
                string.Join("\n", run.Results.Select(r => r.Test + ": " + r.Status + "; " + r.DurationMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms; " + r.Reason)));
        }
        private sealed class SmokeProgress : IProgress<SmokeTestResult>
        { private readonly IProgress<string> progress; public SmokeProgress(IProgress<string> progress) { this.progress = progress; } public void Report(SmokeTestResult r) => progress.Report(r.Test + ": " + r.Status + "; " + r.Reason); }
        private Task<Action> BindNativeClient(CancellationToken token, IProgress<string> progress)
        {
            var tools = RealTools; tools.RunLimits.Enabled = false;
            var client = tools.Processes.FindSingleClient(); tools.RunLimits.Bind(client);
            return Task.FromResult<Action>(() => { Settings.Field("nativeDouble").Flag = false; Settings.Report = "Bound PID=" + client.ProcessId + "; Session=" + client.SessionId +
                "; Executable=" + client.ExecutablePath + ". Bu process gözlemi ROT sahiplik kanıtı değildir. Native Double seçimini ayrıca açıp Kaydet kullanın; en fazla 4 uzatma/çağrı."; });
        }
        private Task<Action> SaveSettings(CancellationToken token, IProgress<string> progress)
        {
            bool enabled = Settings.Flag("watchdog"), native = enabled && Settings.Flag("nativeDouble");
            var limits = owner.Session.Tools?.RunLimits;
            if (native && (owner.Mode != ClientMode.RealDoors || limits?.Client == null)) throw new ArgumentException("Native Double için tek çalışan DOORS istemcisini önce bağlayın.");
            settings.SaveWatchdogEnabled(enabled); watchdog.Enabled = enabled;
            if (limits != null) limits.Enabled = native;
            return Task.FromResult<Action>(() => { if (!enabled) Settings.Field("nativeDouble").Flag = false; Settings.Report = "DXL watchdog=" + enabled + "; NativeDouble=" + native +
                ". Sonraki işlemde kullanılır. Native seçim her oturum/profil için yeniden bağlanır; 4 uzatma/çağrı sınırı korunur. App wait time otomatik ikiye katlanmaz."; });
        }
        public void Dispose() { batchPlan?.Dispose(); dxlResult?.Dispose(); }
    }
}
