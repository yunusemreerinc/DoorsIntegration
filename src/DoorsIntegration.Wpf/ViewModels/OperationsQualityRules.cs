using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed partial class OperationsViewModel
    {
        private Task<Action> SaveRuleTemplate(CancellationToken token, IProgress<string> progress)
        {
            var profile = BuiltinRuleProfiles.CreateTemplate(EnumValue<RuleStandard>(Quality, "standard")); string path = Path.GetFullPath(Quality.Text("save"));
            profile.SaveJson(path);
            return Task.FromResult<Action>(() => { Quality.Field("profile").Value = path; Quality.Report = "Taslak örnek profil kaydedildi: " + path + "\nFingerprint=" + profile.Fingerprint +
                "\nAlanlar/link module proje için eşlenmeli ve profil ayrıca onaylanmalı. Örnek seçim sertifikasyon kararı değildir."; });
        }
        private async Task<Action> ApproveRuleProfile(CancellationToken token, IProgress<string> progress)
        {
            var loaded = RuleProfile.LoadJson(Quality.Text("profile"));
            string project = Quality.Text("project"), reviewer = Quality.Text("reviewer"), reference = Quality.Text("reference"), path = Path.GetFullPath(Quality.Text("save"));
            var draft = loaded.ForProject(project, reference, loaded.Rules);
            if (!draft.CanApprove) throw new InvalidOperationException("Önce alan/link module eşlemelerini yapılandırın; en az bir etkin kural gerekir.");
            if (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 256 || reviewer.Any(char.IsControl)) throw new ArgumentException("Reviewer kimliği gerekli.");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Yeni profil dosyası gerekli; önceki onay sessizce değiştirilmez.");
            var dialog = new WpfReviewDialog(() => Application.Current?.MainWindow, () => !owner.IsClosing,
                "Bu kuralların ve alan/traceability eşlemelerinin belirtilen proje için kullanımını onaylıyorum. Bu bir sertifikasyon kararı değildir.", "Onayla ve kaydet");
            string body = "YEREL PROJE PROFİLİ ONAYI — DOORS verisi değiştirilmez.\nProject=" + project + "; Standard=" + draft.Standard + "; Reviewer=" + reviewer +
                "\nReference=" + reference + "\nFingerprint=" + draft.Fingerprint + "\nYeni dosya=" + path +
                "\nOnay yalnız bu fingerprint ve proje için geçerlidir; içerik değişimi yeniden onay gerektirir. Otomatik son kullanma tarihi uygulanmaz.\n" + ProcessingDisclosure.Statement +
                "\n\n" + string.Join("\n\n", draft.Rules.Cast<ConfiguredRule>().Select(r => r.Id + " / " + r.Kind + "; Severity=" + r.Severity + "; Enabled=" + r.Enabled +
                    "\nFields=" + string.Join(", ", r.Fields) + "; EvidenceFields=" + string.Join(", ", r.EvidenceFields) + "\nTerms=" + string.Join(", ", r.Terms) +
                    "\nMin=" + r.MinimumCount + "; LinkModule=" + r.LinkModulePath + "; Direction=" + r.Direction + "; VersionPolicy=" + r.VersionPolicy +
                    "; Targets=" + string.Join(", ", r.TargetModules) + "; NativeId=" + r.RequiredNativeLinkModuleId + "\nSuggestion=" + r.Suggestion));
            if (!await dialog.ReviewAsync(new WriteReview("Proje kalite profili onayı", draft.Fingerprint, body, DateTime.UtcNow.AddMinutes(5), true), token).ConfigureAwait(false))
                return await Report(Quality, "Profil onayı verilmedi; onaylı dosya yazılmadı.");
            token.ThrowIfCancellationRequested(); var approved = draft.Approve(draft.Fingerprint, reviewer, reference, DateTime.UtcNow); approved.SaveJson(path);
            return () => { Quality.Field("profile").Value = path; Quality.Report = "Proje profili onaylı olarak kaydedildi: " + path + "\nFingerprint=" + approved.Fingerprint + "\n" + approved.Disclosure; };
        }
        private async Task<Action> AnalyzeJsonQuality(CancellationToken token, IProgress<string> progress)
        {
            var profile = RuleProfile.LoadJson(Quality.Text("profile")); var snapshot = owner.RequirementSnapshot; string repo = Quality.Text("repository"), label = Quality.Text("baseline");
            if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("Repository ID gerekli.");
            IRuleTraceabilityProvider trace = null; var links = owner.LinkSnapshot;
            if (links != null && links.Summary.Request.ModulePath == snapshot.Summary.Request.ModulePath && SameBaseline(links.Summary.Request.Baseline, snapshot.Summary.Request.Baseline))
                trace = new SnapshotRuleTraceabilityProvider(links, repo, Null(label), token: token);
            var run = new RuleRunContext(Quality.Text("project"), "RetainedRequirements=" + snapshot.Summary.CorrelationId.ToString("N") + ";CallerBoundRepositoryAndBaseline",
                snapshot.Summary.IsCompleteForReadableObjects, DateTime.UtcNow, EnumValue<RuleExecutionMode>(Quality, "mode"), trace);
            string path = AnalysisDirectory("requirement-rules"); RuleRunSummary summary;
            using (var report = new FileRuleReport(path)) summary = new RequirementsQualityRuleEngine().Evaluate(ProcessingSnapshotAdapter.Requirements(snapshot, repo, Null(label)), profile, run, report, token);
            return await Report(Quality, "Profile=" + summary.ProfileId + "; Project=" + summary.ProjectId + "; Mode=" + summary.Mode + "; ApprovalValid=" + summary.ProfileApprovalValid +
                "\nFindings=" + summary.Findings + "; NotEvaluated=" + summary.NotEvaluated + "; RuleErrors=" + summary.RuleErrors + "; Skipped=" + summary.Skipped +
                "; FullyEvaluated=" + summary.FullyEvaluated + "\nReport=" + path + "\nKanıt/açıklama/öneri ve Pending reviewer disposition results.jsonl içinde.\n" + ProcessingDisclosure.Statement);
        }
    }
}
