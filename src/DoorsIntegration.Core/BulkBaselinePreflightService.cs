using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BulkBaselinePreflightService : IBulkBaselinePreflightService
    {
        private readonly IBaselinePreflightStateReader states;
        private readonly IBaselineMetadataService metadata;
        private readonly IBaselinePreflightClock clock;
        private readonly IIntegrationLogger logger;
        public BulkBaselinePreflightService(IBaselinePreflightStateReader states, IBaselineMetadataService metadata, IBaselinePreflightClock clock, IIntegrationLogger logger)
        { this.states = states ?? throw new ArgumentNullException(nameof(states)); this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public async Task<BulkBaselineDryRunPlan> CreatePlanAsync(BulkBaselinePreflightRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request)); cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid(); var fingerprint = BaselinePreflightFingerprint.Request(request); var items = new List<BulkBaselineDryRunItem>();
            bool stopped = false; string context = null;
            foreach (var selection in request.Baseline.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested(); var probe = new BaselinePreflightProbe(request, selection);
                var item = stopped ? Skip(probe) : await EvaluateAsync(probe, fingerprint, id, cancellationToken).ConfigureAwait(false);
                if (item.Status == BulkBaselineItemStatus.Ready)
                {
                    string observedContext = BaselinePreflightFingerprint.Context(item.Precondition.Facts);
                    if (context != null && context != observedContext) item = Block(probe, BaselinePreflightReason.ContextMismatch, "Modüller aynı kullanıcı/veritabanı/oturum bağlamında okunmadı.", "Aynı DOORS oturumuna bağlanıp yeni bir dry-run oluşturun.", item.CurrentVersion, item.ExpectedVersion);
                    else context = observedContext;
                }
                items.Add(item); if (item.UnsafeToContinue || (request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError && item.Status != BulkBaselineItemStatus.Ready)) stopped = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool publicationStopped = false;
            for (int index = 0; index < items.Count; index++)
            {
                items[index] = publicationStopped && items[index].Status == BulkBaselineItemStatus.Ready ? Skip(items[index].Probe) : RequireFreshAtPublication(items[index]);
                if (request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError && items[index].Status != BulkBaselineItemStatus.Ready) publicationStopped = true;
            }
            var plan = new BulkBaselineDryRunPlan(id, request, items, UtcNow(), fingerprint);
            Log("BulkBaselineDryRunPlanned", id, "Ready=" + plan.ReadyCount + "; Blocked=" + plan.BlockedCount + "; Skipped=" + plan.SkippedCount + "; WriteAttempted=False"); return plan;
        }
        public async Task<BaselinePreflightRevalidation> RevalidateAsync(BulkBaselineDryRunPlan plan, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan)); cancellationToken.ThrowIfCancellationRequested();
            if (plan.RequestFingerprint != BaselinePreflightFingerprint.Request(plan.Request) || plan.Fingerprint != BaselinePreflightFingerprint.Plan(plan))
                throw new IntegrationException(IntegrationErrorCode.BulkBaselinePreflightFailed, "Dry-run plan integrity validation failed; create a new plan.");
            var results = new List<BaselineRevalidationItem>(); bool stopped = false;
            foreach (var original in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested(); BulkBaselineDryRunItem fresh; bool matches = false;
                var now = UtcNow(); bool expired = now < plan.CreatedAtUtc || now - plan.CreatedAtUtc > plan.Request.MaxPlanAge;
                if (original.Status != BulkBaselineItemStatus.Ready) fresh = original; // Never promote an unreviewed blocked/skipped module.
                else if (stopped) fresh = Skip(original.Probe);
                else if (expired) fresh = Block(original.Probe, BaselinePreflightReason.PlanExpired, "Önizleme planının süresi doldu veya saat geriye gitti.", "Yeni dry-run planı oluşturup tekrar inceleyin.", original.CurrentVersion, original.ExpectedVersion);
                else
                {
                    fresh = await EvaluateAsync(original.Probe, plan.RequestFingerprint, plan.PlanId, cancellationToken).ConfigureAwait(false);
                    matches = fresh.Status == BulkBaselineItemStatus.Ready && original.Precondition.Fingerprint == fresh.Precondition.Fingerprint;
                    if (fresh.Status == BulkBaselineItemStatus.Ready && !matches)
                        fresh = Block(original.Probe, BaselinePreflightReason.PreconditionChanged, "Plan sonrasında modül, sürüm, erişim, kilit veya kullanıcı oturumu değişti.",
                            "Yeni plan oluşturun ve güncel hedef sürümü tekrar inceleyin; eski planla devam etmeyin.", original.CurrentVersion, original.ExpectedVersion,
                            "Yeni gözlemdeki hedef sürüm: " + (fresh.ExpectedVersion?.ToString() ?? "Unavailable"));
                }
                results.Add(new BaselineRevalidationItem(original, fresh, matches));
                if (fresh.UnsafeToContinue || (plan.Request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError && (fresh.Status != BulkBaselineItemStatus.Ready || !matches))) stopped = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool publicationStopped = false;
            for (int index = 0; index < results.Count; index++)
            {
                var item = results[index];
                if (item.EligibleForPreview)
                {
                    var fresh = publicationStopped ? Skip(item.Fresh.Probe) : RequireFreshAtPublication(item.Fresh);
                    if (fresh.Status != BulkBaselineItemStatus.Ready) results[index] = new BaselineRevalidationItem(item.Original, fresh, false);
                }
                if (plan.Request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError && !results[index].EligibleForPreview) publicationStopped = true;
            }
            var result = new BaselinePreflightRevalidation(plan, results, UtcNow());
            Log("BulkBaselineDryRunRevalidated", plan.PlanId, "Eligible=" + result.EligibleCount + "; WriteAttempted=False; NoAuthorization=True"); return result;
        }
        public async Task<BaselinePreflightRevalidation> DryRunAsync(BulkBaselinePreflightRequest request, CancellationToken cancellationToken = default(CancellationToken))
        { var plan = await CreatePlanAsync(request, cancellationToken).ConfigureAwait(false); return await RevalidateAsync(plan, cancellationToken).ConfigureAwait(false); }
        private async Task<BulkBaselineDryRunItem> EvaluateAsync(BaselinePreflightProbe probe, string requestFingerprint, Guid id, CancellationToken token)
        {
            BaselineVersion current = null, expected = null;
            try
            {
                token.ThrowIfCancellationRequested(); var before = await states.ReadAsync(probe, token).ConfigureAwait(false);
                var blocked = Validate(before, probe); if (blocked != null) return blocked;
                var query = new BaselineHistoryRequest(probe.Module, openIfClosed: true, nextType: probe.Type, suffix: probe.Suffix,
                    timeout: probe.Request.Timeout, target: probe.Request.Target, connectionMode: probe.Request.ConnectionMode);
                var result = await metadata.ReadAsync(query, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                if (result == null || !ReferenceEquals(result.Snapshot.Request, query))
                    return Block(probe, BaselinePreflightReason.InvalidEvidence, "Metadata yanıtı istekle eşleşmiyor.", "Adaptör bağlamını doğrulayıp yeni plan oluşturun.");
                var snapshot = result.Snapshot; current = snapshot.LatestNumberingVersion.Version;
                if (snapshot.Status != BaselineHistoryStatus.Available || !snapshot.HistoryComplete || snapshot.Cleanup == RequirementCleanupStatus.Failed ||
                    !Fresh(snapshot.ObservedAtUtc, probe.Request) || snapshot.Source != before.Facts.Source || snapshot.LatestNumberingVersion.Status == BaselineDesignationStatus.Unknown)
                    return Block(probe, BaselinePreflightReason.MetadataUnavailable, "Baseline metadata tam, güncel veya aynı kaynak bağlamında doğrulanamadı: " + snapshot.Status + ".",
                        "Okuma erişimini, cleanup ve API uyumluluğunu doğrulayıp yeni dry-run çalıştırın.", current);
                var proposal = result.Proposal; expected = proposal?.ExpectedVersion;
                if (proposal == null || proposal.Status != BaselineProposalStatus.Available || expected == null || proposal.Type != probe.Type || proposal.Suffix != probe.Suffix ||
                    proposal.ObservationId != snapshot.ObservationId || !probe.Module.Matches(proposal.Module) || proposal.Source != snapshot.Source)
                    return Block(probe, BaselinePreflightReason.VersionUnverified, "Beklenen Major/Minor sürümü native adaptör tarafından doğrulanmadı.",
                        "İlgili DOORS sürümünde nextMajor/nextMinor read-only çözümlemesini doğrulayın; tahmini sürümle devam etmeyin.", current);
                if (result.Collision == null || !expected.Equals(result.Collision.Target)) return Block(probe, BaselinePreflightReason.InvalidEvidence,
                    "Çakışma sorgusu beklenen sürüme bağlı değil.", "Exact hedef sürüm için yeniden ön kontrol yapın.", current, expected);
                if (result.Collision.Status == BaselineExistenceStatus.Exists) return Block(probe, BaselinePreflightReason.CollisionExists,
                    "Hedef baseline sürümü zaten mevcut.", "Yeni metadata ve yeni hedef planını inceleyin; mevcut baseline'ı silmeyin veya üzerine yazmayın.", current, expected);
                if (result.Collision.Status != BaselineExistenceStatus.AbsentOrUnreadable) return Block(probe, BaselinePreflightReason.CollisionUnknown,
                    "Hedef baseline çakışması doğrulanamadı.", "Exact baselineExists ve görünürlük kontrollerini tekrar doğrulayın.", current, expected);
                var targetProbe = new BaselinePreflightProbe(probe.Request, probe.Selection, expected);
                var after = await states.ReadAsync(targetProbe, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                blocked = Validate(after, targetProbe); if (blocked != null) return Block(probe, blocked.ReasonCode, blocked.Reason, blocked.RecommendedResolution, current, expected, blocked.Warning);
                if (BaselinePreflightFingerprint.State(before.Facts) != BaselinePreflightFingerprint.State(after.Facts))
                    return Block(probe, BaselinePreflightReason.ChangedDuringRead, "Metadata sorgusu sırasında ön koşullar değişti.", "Modül/oturum sabitlendikten sonra yeni plan oluşturun.", current, expected);
                if (after.Facts.TargetAvailability != BaselineTargetAvailability.Free)
                    return Block(probe, after.Facts.TargetAvailability == BaselineTargetAvailability.Exists ? BaselinePreflightReason.CollisionExists : BaselinePreflightReason.CollisionUnknown,
                        "Exact hedef sürümün kullanılabilirliği doğrulanmadı; baselineExists=false tek başına yeterli değildir.",
                        "Hedefin yokluğunu ve erişim kapsamını yetkili adaptörle doğrulayın; bu kontrolü atlamayın.", current, expected);
                string metadataHash = BaselinePreflightFingerprint.Metadata(result);
                var precondition = new BaselinePrecondition(BaselinePreflightFingerprint.Precondition(requestFingerprint, targetProbe, after.Facts, metadataHash),
                    requestFingerprint, after.Facts, metadataHash, after.ObservedAtUtc);
                string warning = after.Facts.Source == BaselineMetadataSource.Mock ? "Mock simülasyonu; gerçek DOORS uyumluluğu doğrulanmış değildir. " : "";
                if (after.Facts.EditState != BaselineEditState.ExclusiveCurrentSession) warning += "Exclusive edit kilidi alınmadı; gerçek işlem öncesinde yeniden kontrol gerekir. ";
                if (string.IsNullOrWhiteSpace(probe.Annotation)) warning += "Annotation boş; inceleme/audit açıklaması eklenebilir.";
                return new BulkBaselineDryRunItem(probe, BulkBaselineItemStatus.Ready, BaselinePreflightReason.None, "Salt okunur ön kontroller geçti.",
                    "Yazma yetkisi verilmedi. Gerçek işlem için ayrı açık kullanıcı onayı ve işlem anında yeniden doğrulama gerekir.", warning, current, expected, precondition);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                bool uncertain = ex is TimeoutException || (ex is IntegrationException integration && (integration.Code == IntegrationErrorCode.DxlTimeout ||
                    integration.Code == IntegrationErrorCode.DxlWaitCancelled || integration.Code == IntegrationErrorCode.DoorsOperationBusy ||
                    integration.Code == IntegrationErrorCode.ComServerDisconnected || integration.Code == IntegrationErrorCode.ComFailure));
                Log("BulkBaselineDryRunReadFailed", id, "ExceptionType=" + ex.GetType().FullName + "; AutomaticRetry=False; Uncertain=" + uncertain);
                return Block(probe, uncertain ? BaselinePreflightReason.OperationUncertain : BaselinePreflightReason.AdapterFailure,
                    uncertain ? "Native işlem/oturum sonucu belirsiz; bu batch içinde yeni çağrı başlatılmaz." : "Modülün read-only ön kontrolü tamamlanamadı.",
                    uncertain ? "Orijinal COM çağrısı ve cleanup tamamlandıktan sonra bağlantıyı tanılayın ve yeni plan oluşturun." : "İlgili tanılama kaydını ve modül erişimini kontrol edip yeni dry-run çalıştırın.",
                    current, expected, unsafeToContinue: uncertain);
            }
        }
        private BulkBaselineDryRunItem Validate(BaselinePreflightObservation observation, BaselinePreflightProbe probe)
        {
            if (observation == null || !ReferenceEquals(observation.Probe, probe) || !probe.Module.Matches(observation.Facts.Module) || !Fresh(observation.ObservedAtUtc, probe.Request))
                return Block(probe, BaselinePreflightReason.InvalidEvidence, "Durum gözlemi kimlik/istek/zaman ile eşleşmiyor.", "Güncel ve exact modül bağlamında yeni ön kontrol okuyun.");
            var f = observation.Facts;
            if (f.Source == BaselineMetadataSource.NotRun) return Block(probe, BaselinePreflightReason.NativeStateUnavailable,
                "Native izin/kilit/oturum/revision adaptörü doğrulanmış değildir; uygunluk varsayılmadı.", "DOORS 9.5/9.7 için read-only durum adaptörünü ve native next-version desteğini doğrulayıp yeni plan oluşturun.");
            if (f.Exists != BaselineCheckState.Passed) return Block(probe, f.Exists == BaselineCheckState.Failed ? BaselinePreflightReason.MissingModule : BaselinePreflightReason.UnknownModule,
                "Modülün varlığı doğrulanamadı.", "Stabil ModuleId ve canonical yolu, modülün silinmediğini ve görünürlüğünü kontrol edin.");
            if (f.Type != DoorsItemKind.FormalModule) return Block(probe, BaselinePreflightReason.NotFormalModule, "Seçilen öğe doğrulanmış formal modül değil.", "Link/diğer modül türlerini seçimden çıkarın; formal modül kimliğini yeniden keşfedin.");
            if (f.ReadAccess != BaselineCheckState.Passed || f.CreateAccess != BaselineCheckState.Passed || f.ModifyAccess != BaselineCheckState.Passed)
                return Block(probe, f.ReadAccess == BaselineCheckState.Failed ? BaselinePreflightReason.ReadDenied : f.CreateAccess == BaselineCheckState.Failed ? BaselinePreflightReason.CreateDenied :
                    f.ModifyAccess == BaselineCheckState.Failed ? BaselinePreflightReason.ModifyDenied : BaselinePreflightReason.PermissionUnknown,
                    "Read/create/modify erişimlerinin tamamı doğrulanmadı.", "Proje/modül yöneticisinden gerekli yetkileri doğrulatın; ACL'yi bu işlem içinde otomatik değiştirmeyin.");
            if (f.SessionValid != BaselineCheckState.Passed || f.DatabaseIdentity == null || f.CurrentUserId == null || f.SessionId == null || f.ConnectionEpoch == null)
                return Block(probe, BaselinePreflightReason.SessionUnknown, "Mevcut kullanıcı oturumu ve gerçek veritabanı bağlamı doğrulanmadı.", "Hedef DOORS kullanıcı/oturumunu yeniden bağlayıp tanılayın ve yeni plan oluşturun.");
            if ((f.LockState == BaselineLockState.Unlocked && (f.LockOwnerSessionId != null || f.LockOwnerUserId != null)) ||
                (f.UnsavedChanges == BaselineUnsavedState.None && f.DirtyOwnerSessionId != null))
                return Block(probe, BaselinePreflightReason.InvalidEvidence, "Kilit veya kaydedilmemiş değişiklik sahipliği çelişkili.", "Sahiplik ve durum kanıtını aynı oturumda yeniden okuyun; çelişkiyi otomatik düzeltmeyin.");
            if (f.LockState == BaselineLockState.OwnedByOtherSession || f.EditState == BaselineEditState.ExclusiveOtherSession)
                return Block(probe, BaselinePreflightReason.OtherSessionLock, "Modül başka bir kullanıcı/oturumun kilidinde.", "Kilit sahibinin kendi oturumunda çalışmasını tamamlayıp kilidi normal yolla bırakmasını bekleyin; otomatik/force unlock uygulamayın.");
            if (f.UnsavedChanges != BaselineUnsavedState.None)
                return Block(probe, f.UnsavedChanges == BaselineUnsavedState.CurrentSession ? BaselinePreflightReason.UnsavedCurrentSession :
                    f.UnsavedChanges == BaselineUnsavedState.OtherSession ? BaselinePreflightReason.UnsavedOtherSession : BaselinePreflightReason.UnsavedUnknown,
                    "Kaydedilmemiş değişiklik var veya değişiklik durumu bilinmiyor.", f.UnsavedChanges == BaselineUnsavedState.CurrentSession ?
                    "Kendi oturumunuzdaki değişiklikleri inceleyin; save/discard kararını ayrı açık onayla verin ve yeni plan oluşturun." :
                    "Değişiklik sahibinin kendi oturumunda inceleme yapmasını bekleyin; başka oturumun verisini kaydetmeyin veya iptal etmeyin.");
            if (f.EditState == BaselineEditState.SharedCurrentSession || f.EditState == BaselineEditState.SharedOtherSession)
                return Block(probe, BaselinePreflightReason.SharedEdit, "Shared edit modu exclusive edit ön koşulunu karşılamıyor.", "Shared oturum sahipleriyle koordinasyon kurup uygun exclusive edit durumunu doğrulayın; otomatik mod dönüşümü yapmayın.");
            bool ownExclusive = f.EditState == BaselineEditState.ExclusiveCurrentSession && f.LockState == BaselineLockState.OwnedByCurrentSession &&
                f.LockOwnerSessionId == f.SessionId && f.LockOwnerUserId == f.CurrentUserId;
            bool canAcquire = (f.EditState == BaselineEditState.Closed || f.EditState == BaselineEditState.ReadOnly) && f.LockState == BaselineLockState.Unlocked && f.ExclusiveAvailability == BaselineCheckState.Passed;
            if (!ownExclusive && !canAcquire) return Block(probe, f.LockState == BaselineLockState.Unknown || f.EditState == BaselineEditState.Unknown ? BaselinePreflightReason.LockUnknown : BaselinePreflightReason.ExclusiveUnavailable,
                "Exclusive edit sahipliği/alınabilirliği read-only olarak doğrulanmadı.", "Kilit/oturum durumunu doğrulatın; dry-run sırasında kilit almayın veya kaldırmayın.");
            if (f.RevisionsReliable != BaselineCheckState.Passed || f.ContentRevision == null || f.PermissionRevision == null || f.LockRevision == null)
                return Block(probe, BaselinePreflightReason.RevisionUnavailable, "İçerik/history, ACL ve kilit değişim token'ları güvenilir biçimde okunamadı.", "Sürüm etiketinden ayrı güvenilir revision/fingerprint kanıtını sağlayan adaptörü doğrulayın; bu kontrolü atlamayın.");
            if (f.SuffixValid != BaselineCheckState.Passed || f.AnnotationValid != BaselineCheckState.Passed)
                return Block(probe, BaselinePreflightReason.InvalidInput, "Suffix/annotation seçili tür ve hedef modül bağlamında doğrulanmadı.", "Suffix karakterlerini ve annotation sınırlarını kontrol edip native doğrulama ile yeni plan oluşturun.");
            return null;
        }
        private bool Fresh(DateTime observed, BulkBaselinePreflightRequest request) { var now = UtcNow(); return observed <= now && now - observed <= request.MaxObservationAge; }
        private BulkBaselineDryRunItem RequireFreshAtPublication(BulkBaselineDryRunItem item) => item.Status == BulkBaselineItemStatus.Ready && !Fresh(item.Precondition.ObservedAtUtc, item.Probe.Request) ?
            Block(item.Probe, BaselinePreflightReason.ObservationExpired, "Batch tamamlanmadan ön kontrol kanıtının güncellik sınırı doldu.",
                "Batch'i daha küçük gruplara ayırın; her modülü gerçek dispatch öncesinde yeniden doğrulayın.", item.CurrentVersion, item.ExpectedVersion) : item;
        private DateTime UtcNow() => BulkBaselineGuard.Utc(clock.UtcNow);
        private static BulkBaselineDryRunItem Block(BaselinePreflightProbe probe, BaselinePreflightReason code, string reason, string fix,
            BaselineVersion current = null, BaselineVersion expected = null, string warning = "", bool unsafeToContinue = false) =>
            new BulkBaselineDryRunItem(probe, BulkBaselineItemStatus.Blocked, code, reason, fix, warning, current, expected, null, unsafeToContinue);
        private static BulkBaselineDryRunItem Skip(BaselinePreflightProbe probe) => new BulkBaselineDryRunItem(probe, BulkBaselineItemStatus.Skipped,
            BaselinePreflightReason.StoppedAfterError, "Policy veya belirsiz native çağrı nedeniyle okunmadan atlandı.", "Engeli çözün ve yeni plan oluşturun.", "Uygunluk kontrol edilmedi.", null, null, null);
        private void Log(string name, Guid id, string message) { try { logger.Write(IntegrationLogLevel.Information, name, id, message); } catch { } }
    }
}
