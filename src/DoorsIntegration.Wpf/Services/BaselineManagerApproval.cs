using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    // A batch review grants only exact, unchanged preconditions, once per module and one durable JobId.
    public sealed class BaselineManagerApproval : IBaselineExecutionConfirmation, IBulkBaselineRetryConfirmation, IBaselineApprovalAuditSource
    {
        private readonly IClientReview dialog;
        private readonly ClientMode mode;
        private readonly Func<bool> allowed;
        private readonly object sync = new object();
        private BulkBaselineDryRunPlan approved;
        private Guid? jobId;
        private Guid approvalId;
        private DateTime approvedAtUtc;
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public BaselineManagerApproval(IClientReview dialog, ClientMode mode, Func<bool> allowed)
        { this.dialog = dialog; this.mode = mode; this.allowed = allowed ?? (() => false); }
        public DateTime ExpiresAtUtc { get; private set; }
        public bool IsApproved { get { lock (sync) return approved != null && allowed() && DateTime.UtcNow < ExpiresAtUtc; } }
        public string Fingerprint { get { lock (sync) return approved?.Fingerprint; } }
        public void Clear() { lock (sync) { approved = null; jobId = null; consumed.Clear(); } }
        public void BindJob(Guid id) { lock (sync) { if (!IsApproved || jobId.HasValue || id == Guid.Empty) throw new InvalidOperationException("Fresh batch approval required."); jobId = id; } }
        public Task<bool> ReviewAsync(BulkBaselineDryRunPlan plan, CancellationToken token) => ReviewCoreAsync(plan, token, "");
        private async Task<bool> ReviewCoreAsync(BulkBaselineDryRunPlan plan, CancellationToken token, string context)
        {
            Clear();
            if (dialog == null || !allowed() || plan == null || plan.ReadyCount == 0 || plan.ReadyCount != plan.Items.Count ||
                plan.Items.Any(i => i.Precondition == null || i.Precondition.Facts.Source != Source)) return false;
            DateTime expiry = plan.CreatedAtUtc + plan.Request.MaxPlanAge;
            foreach (var item in plan.Items) { var observationExpiry = item.Precondition.ObservedAtUtc + plan.Request.MaxObservationAge; if (observationExpiry < expiry) expiry = observationExpiry; }
            string body = context + (mode == ClientMode.Mock ? "MOCK · simülasyon; gerçek DOORS verisi değişmez.\n\n" : "REAL DOORS · gerçek veritabanı yazması.\n\n") + Describe(plan);
            string target = mode == ClientMode.Mock ? "MOCK BASELINE " + plan.Items.Count : "REAL BASELINE " + plan.Items.Count;
            if (!await dialog.ReviewAsync(new WriteReview("Bulk baseline — kesin plan onayı", target, body, expiry, true), token).ConfigureAwait(false)) return false;
            if (mode == ClientMode.RealDoors && !await dialog.ReviewAsync(new WriteReview("REAL DOORS — ikinci yazma onayı", "CONFIRM REAL DATABASE WRITE " + plan.Items.Count,
                "İKİNCİ ONAY: Gerçek DOORS veritabanında geri alınamayan baseline oluşturma.\n\n" + body, expiry, true), token).ConfigureAwait(false)) return false;
            token.ThrowIfCancellationRequested();
            lock (sync) { if (!allowed() || DateTime.UtcNow >= expiry) return false; approved = plan; approvalId = Guid.NewGuid(); approvedAtUtc = DateTime.UtcNow; ExpiresAtUtc = expiry; return true; }
        }
        public BaselineApprovedPlan CaptureAcceptedApproval(BaselineExecutionReview review, DateTime acceptedAtUtc)
        {
            lock (sync)
            {
                if (approved == null || jobId != review.JobId || review.Plan.Items.Any(i => !consumed.Contains(i.ModuleId))) throw new InvalidOperationException("No accepted approval for this job/module.");
                return BaselineApprovedPlan.Capture(approved, review.JobId, approvedAtUtc,
                    string.Join("; ", approved.Items.Select(i => i.Precondition.Facts.CurrentUserId).Distinct()), "WPF explicit batch review",
                    mode == ClientMode.RealDoors, approvalId);
            }
        }
        public Task<bool> ConfirmAsync(BaselineExecutionReview review, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!IsApproved || jobId != review.JobId || review.Plan.Items.Count != 1 || approved.Request.Baseline.Type != review.Plan.Request.Baseline.Type || approved.Request.Baseline.Mode != review.Plan.Request.Baseline.Mode) return Task.FromResult(false);
                var fresh = review.Plan.Items.Single(); var old = approved.Items.SingleOrDefault(i => i.ModuleId == fresh.ModuleId);
                bool exact = old != null && old.Module.Matches(fresh.Module) && Equals(old.CurrentVersion, fresh.CurrentVersion) && Equals(old.ExpectedVersion, fresh.ExpectedVersion) &&
                    old.Suffix == fresh.Suffix && old.Annotation == fresh.Annotation && fresh.Status == BulkBaselineItemStatus.Ready && fresh.Precondition != null &&
                    fresh.Precondition.Facts.Source == Source && old.Precondition.HasSameObservedState(fresh.Precondition) && !consumed.Contains(fresh.ModuleId) &&
                    approved.Request.ExecutionPolicy == review.Plan.Request.ExecutionPolicy && approved.Request.Target == review.Plan.Request.Target &&
                    approved.Request.ConnectionMode == review.Plan.Request.ConnectionMode && approved.Request.Timeout == review.Plan.Request.Timeout &&
                    approved.Request.MaxPlanAge == review.Plan.Request.MaxPlanAge && approved.Request.MaxObservationAge == review.Plan.Request.MaxObservationAge;
                if (exact) consumed.Add(fresh.ModuleId);
                return Task.FromResult(exact);
            }
        }
        public async Task<bool> ConfirmAsync(BaselineJobRetryReview review, CancellationToken token)
        {
            // A new explicit review is mandatory for a retry; an old batch ticket cannot authorize it.
            if (!await ReviewCoreAsync(review.FreshPlan, token, "EXPLICIT RETRY · JobId=" + review.Job.JobId + "\nPrevious status=" + review.Module.Status +
                "; Attempts=" + review.Module.Attempts + "\nPrevious error=" + review.Module.FailureReason + "\nFresh exact existence=" + review.Verification.Status + "\n\n").ConfigureAwait(false)) return false;
            BindJob(review.Job.JobId); return true;
        }
        private BaselineMetadataSource Source => mode == ClientMode.Mock ? BaselineMetadataSource.Mock : BaselineMetadataSource.NativeDxl;
        public static string Describe(BulkBaselineDryRunPlan plan)
        {
            var text = new StringBuilder().AppendLine("Modül sayısı: " + plan.Items.Count + " · BaselineType: " + plan.Request.Baseline.Type)
                .AppendLine("Policy: " + plan.Request.ExecutionPolicy).AppendLine("Suffix: " + plan.Request.Baseline.Suffix)
                .AppendLine("Annotation:\n" + plan.Request.Baseline.Annotation)
                .AppendLine("\nRiskler: İşlem transactional değildir. Başarılar otomatik geri alınmaz. Create modülü kapatır. Kullanıcı değişiklikleri save/discard edilmez.")
                .AppendLine("Timeout sonucu belirsiz olabilir; otomatik retry yapılmaz. Aktif COM çağrısı güvenle abort edilemez; Cancel Remaining yalnız yeni modülleri durdurur.")
                .AppendLine("Tüm girdiler ve ön koşullar dispatch öncesinde tekrar kontrol edilir. Değişmiş koşul eski onayla yazılmaz.")
                .AppendLine("Plan SHA256: " + plan.Fingerprint).AppendLine("\nEtkilenecek modüller (tam liste):");
            foreach (var item in plan.Items) text.AppendLine(item.FullPath + "\nID=" + item.ModuleId + " · " + (item.CurrentVersion?.ToString() ?? "None") + " → " + item.ExpectedVersion +
                " · " + item.Status + "\nSuffix=" + item.Suffix + " · Annotation=" + item.Annotation + "\nWarning=" + item.Warning);
            return text.ToString();
        }
    }
}
