using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class RequirementService : IRequirementService
    {
        private readonly IRequirementReader reader;
        private readonly IRequirementWriteGateway writes;
        private readonly IRequirementWriteConfirmation confirmation;
        private readonly IRequirementWriteAuditSink audit;
        private readonly object owner = new object();
        public RequirementService(IRequirementReader reader) { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); }
        public RequirementService(IRequirementReader reader, IRequirementWriteGateway writes, IRequirementWriteConfirmation confirmation, IRequirementWriteAuditSink audit)
            : this(reader)
        {
            this.writes = writes ?? throw new ArgumentNullException(nameof(writes));
            this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
            this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }
        public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return reader.ReadAsync(request, cancellationToken);
        }

        public Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget target, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            RequireWrites(); cancellationToken.ThrowIfCancellationRequested();
            return writes.GetAttributeSchemaAsync(target, cancellationToken);
        }

        public async Task<RequirementWritePreview> PreviewWriteAsync(RequirementWriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireWrites(); cancellationToken.ThrowIfCancellationRequested();
            var inspection = await writes.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            if (inspection != null) inspection = RequirementWriteValidation.Validate(request, inspection);
            var preview = new RequirementWritePreview(owner, request, inspection ?? throw new InvalidOperationException("Missing inspection."));
            Audit("PreviewCompleted", preview);
            return preview;
        }

        public async Task<RequirementWriteResult> ApplyWriteAsync(RequirementWritePreview preview, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            RequireWrites(); cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview.Owner, owner) || preview.ExpiresAtUtc <= DateTime.UtcNow || !preview.Consume())
                throw new IntegrationException(IntegrationErrorCode.RequirementWriteApprovalRequired, "Aynı servis örneğinden yeni, kullanılmamış önizleme gerekir (5 dakika geçerli).");
            if (!preview.CanApply) throw new IntegrationException(IntegrationErrorCode.RequirementWriteBlocked, "Önizlemedeki hatalar giderilmeden yazma yapılamaz.");
            Audit("ApprovalRequested", preview);
            if (!await confirmation.ConfirmAsync(preview, cancellationToken).ConfigureAwait(false))
            {
                Audit("ApprovalDeclined", preview);
                throw new IntegrationException(IntegrationErrorCode.RequirementWriteApprovalRequired, "Kullanıcı yazmayı açıkça onaylamadı; yazma başlatılmadı.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.ExpiresAtUtc <= DateTime.UtcNow)
                throw new IntegrationException(IntegrationErrorCode.RequirementWriteApprovalRequired, "Önizleme süresi doldu; yeniden önizleyin.");
            Audit("CommitAuthorized", preview);
            preview.Authorize();
            try { return await writes.CommitAsync(preview, cancellationToken).ConfigureAwait(false); }
            finally { preview.Revoke(); }
        }

        private void RequireWrites()
        {
            if (writes == null || confirmation == null || audit == null)
                throw new IntegrationException(IntegrationErrorCode.RequirementWriteApprovalRequired, "Yazma gateway, açık onay ve audit sağlayıcısı yapılandırılmamış.");
        }
        private void Audit(string phase, RequirementWritePreview preview)
        {
            try
            {
                var schema = preview.Inspection.Schema;
                audit.Record(new RequirementWriteAuditEntry(phase, preview.PlanId, schema.CorrelationId, preview.ApprovalFingerprint,
                    schema.ModulePath, schema.UserName, schema.DatabaseId, schema.QualifiedModuleId, request: preview.Request, inspection: preview.Inspection));
            }
            catch (Exception ex) { throw new IntegrationException(IntegrationErrorCode.RequirementWriteAuditFailed, "Audit kaydı yazılamadı; yazma başlatılmadı.", ex); }
        }
    }
}
