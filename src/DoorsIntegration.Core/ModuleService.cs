using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class ModuleService : IModuleService
    {
        private readonly IDoorsHierarchyReader reader;
        private readonly IFormalModuleCreationGateway creation;
        private readonly IModuleCreationConfirmation confirmation;
        private readonly object previewOwner = new object();
        public ModuleService(IDoorsHierarchyReader reader) { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); }
        public ModuleService(IDoorsHierarchyReader reader, IFormalModuleCreationGateway creation, IModuleCreationConfirmation confirmation)
            : this(reader)
        {
            this.creation = creation ?? throw new ArgumentNullException(nameof(creation));
            this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        }
        public Task<DiscoverySnapshot> DiscoverFormalModulesAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken)) =>
            reader.DiscoverAsync(DiscoveryKind.FormalModules, scope, cancellationToken);
        public Task<DiscoverySnapshot> GetFormalModuleAsync(DiscoveryScope scope, CancellationToken cancellationToken = default(CancellationToken)) =>
            reader.DiscoverAsync(DiscoveryKind.ModuleByPath, scope, cancellationToken);

        public async Task<FormalModuleCreationPreview> PreviewFormalModuleCreationAsync(FormalModuleCreateRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireCreation();
            cancellationToken.ThrowIfCancellationRequested();
            var checks = await creation.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            return new FormalModuleCreationPreview(previewOwner, request, checks ?? throw new InvalidOperationException("Missing preflight checks."));
        }

        public async Task<FormalModuleCreationResult> CreateFormalModuleAsync(FormalModuleCreationPreview preview,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            RequireCreation();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview.Owner, previewOwner) || preview.ExpiresAtUtc <= DateTime.UtcNow || !preview.TryConsume())
                throw new IntegrationException(IntegrationErrorCode.ModuleCreationApprovalRequired, "Aynı servis örneğinden yeni ve kullanılmamış bir önizleme gerekir (5 dakika geçerli).");
            if (!preview.CanCreate) throw new IntegrationException(IntegrationErrorCode.ModuleCreationBlocked, "Önizleme oluşturmayı engelliyor. " + preview.Checks.Summary);
            if (!await confirmation.ConfirmAsync(preview, cancellationToken).ConfigureAwait(false))
                throw new IntegrationException(IntegrationErrorCode.ModuleCreationApprovalRequired, "Kullanıcı oluşturmayı onaylamadı; yazma çağrısı başlatılmadı.");
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.ExpiresAtUtc <= DateTime.UtcNow)
                throw new IntegrationException(IntegrationErrorCode.ModuleCreationApprovalRequired, "Onay sırasında önizleme süresi doldu; yeniden önizleyin.");
            preview.AuthorizeCommit();
            FormalModuleCreationResult result;
            try { result = await creation.CommitAsync(preview, cancellationToken).ConfigureAwait(false); }
            finally { preview.RevokeCommitAuthorization(); }
            if (result.Outcome == ModuleCreationOutcome.OutcomeUnknown)
                throw new ModuleCreationUncertainException(preview.PlanId, result.CorrelationId, Task.FromResult(result));
            if (result.Outcome != ModuleCreationOutcome.Created)
                throw new IntegrationException(IntegrationErrorCode.ModuleCreationBlocked, "Oluşturmadan önce durum değişti; modül oluşturulmadı. Outcome=" + result.Outcome + "; " + result.Checks.Summary);
            return result;
        }

        private void RequireCreation()
        {
            if (creation == null || confirmation == null)
                throw new IntegrationException(IntegrationErrorCode.ModuleCreationApprovalRequired, "Oluşturma gateway ve kullanıcı onay sağlayıcısı yapılandırılmamış.");
        }
    }
}
