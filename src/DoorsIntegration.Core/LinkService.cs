using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class LinkService : ILinkService
    {
        private readonly ILinkReader reader;
        private readonly ILinkWriteGateway writes;
        private readonly ILinkWriteConfirmation confirmation;
        private readonly ILinkWriteAuditSink audit;
        private readonly object owner = new object();
        public LinkService(ILinkReader reader) { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); }
        public LinkService(ILinkReader reader, ILinkWriteGateway writes, ILinkWriteConfirmation confirmation, ILinkWriteAuditSink audit) : this(reader)
        {
            this.writes = writes ?? throw new ArgumentNullException(nameof(writes));
            this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
            this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }
        public Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return reader.ReadAsync(request, cancellationToken);
        }
        public async Task<LinkWritePreview> PreviewWriteAsync(LinkWriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireWrites(); cancellationToken.ThrowIfCancellationRequested();
            var inspection = await writes.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            if (inspection == null || inspection.Outcome != LinkWriteOutcome.Preview || inspection.Persistence != LinkWritePersistence.NotAttempted ||
                inspection.AfterVerified || inspection.After.Count != 0) throw new IntegrationException(IntegrationErrorCode.LinkWriteProtocolFailure, "Invalid link dry-run response.");
            var preview = new LinkWritePreview(owner, request, inspection);
            Audit("PreviewCompleted", preview); return preview;
        }
        public async Task<LinkWriteResult> ApplyWriteAsync(LinkWritePreview preview, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            RequireWrites(); cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview.Owner, owner) || preview.ExpiresAtUtc <= DateTime.UtcNow || !preview.Consume())
                throw Approval("A new unused dry-run from this service is required (valid for five minutes).");
            if (!preview.CanApply) throw new IntegrationException(IntegrationErrorCode.LinkWriteBlocked, "Resolve dry-run issues before applying the link change.");
            Audit("ApprovalRequested", preview);
            if (!await confirmation.ConfirmAsync(preview, cancellationToken).ConfigureAwait(false))
            { Audit("ApprovalDeclined", preview); throw Approval("Link change was not explicitly approved."); }
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.ExpiresAtUtc <= DateTime.UtcNow) throw Approval("Dry-run expired; preview again.");
            Audit("ApprovalGranted", preview);
            if (preview.Request.Action == LinkWriteAction.Remove)
            {
                Audit("RemovalApprovalRequested", preview);
                if (!await confirmation.ConfirmRemovalAsync(preview, cancellationToken).ConfigureAwait(false))
                { Audit("RemovalApprovalDeclined", preview); throw Approval("Separate explicit link removal approval is required."); }
                Audit("RemovalApprovalGranted", preview);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.ExpiresAtUtc <= DateTime.UtcNow) throw Approval("Dry-run expired; preview again.");
            Audit("CommitAuthorized", preview); preview.Authorize();
            try { return await writes.CommitAsync(preview, cancellationToken).ConfigureAwait(false); }
            finally { preview.Revoke(); }
        }
        private static IntegrationException Approval(string message) => new IntegrationException(IntegrationErrorCode.LinkWriteApprovalRequired, message);
        private void RequireWrites()
        {
            if (writes == null || confirmation == null || audit == null) throw Approval("A write gateway, explicit confirmation and audit sink must be configured.");
        }
        private void Audit(string phase, LinkWritePreview preview)
        {
            try { audit.Record(new LinkWriteAuditEntry(phase, preview)); }
            catch (Exception ex) { throw new IntegrationException(IntegrationErrorCode.LinkWriteAuditFailed, "Audit failed; link write was not dispatched.", ex); }
        }
    }
}
