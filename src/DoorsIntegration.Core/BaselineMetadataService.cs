using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BaselineMetadataService : IBaselineMetadataService
    {
        private readonly IBaselineHistoryAdapter history;
        private readonly IBaselineNextVersionAdapter versions;
        private readonly IIntegrationLogger logger;
        public BaselineMetadataService(IBaselineHistoryAdapter history, IBaselineNextVersionAdapter versions, IIntegrationLogger logger)
        { this.history = history ?? throw new ArgumentNullException(nameof(history)); this.versions = versions ?? throw new ArgumentNullException(nameof(versions)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public async Task<BaselineMetadataResult> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested(); var id = Guid.NewGuid(); Log("BaselineMetadataStarted", id, "ReadOnly=True");
            try
            {
                var snapshot = await history.ReadAsync(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot == null || !ReferenceEquals(snapshot.Request, request)) throw Failure("History adapter returned a foreign request/observation.");
                BaselineVersionProposal proposal = null; var collision = snapshot.Collision;
                if (request.NextType.HasValue && snapshot.Status == BaselineHistoryStatus.Available && snapshot.Cleanup != RequirementCleanupStatus.Failed)
                {
                    proposal = await versions.ProposeAsync(snapshot, request.NextType.Value, request.Suffix, cancellationToken).ConfigureAwait(false);
                    if (proposal == null || proposal.Type != request.NextType.Value || proposal.Suffix != request.Suffix ||
                        proposal.ObservationId != snapshot.ObservationId || !snapshot.Module.Matches(proposal.Module))
                        throw Failure("Next-version adapter returned a mismatched proposal.");
                    if (proposal.Status == BaselineProposalStatus.Available && request.CollisionTarget == null)
                        collision = await history.CheckCollisionAsync(request, proposal.ExpectedVersion, cancellationToken).ConfigureAwait(false);
                }
                if (request.NextType.HasValue && proposal == null)
                    proposal = new BaselineVersionProposal(request.NextType.Value, request.Suffix, BaselineProposalStatus.Unavailable, null,
                        snapshot.Source, snapshot.ApiEvidence, "Module metadata is unavailable; no expected version was inferred.", snapshot.ObservationId, snapshot.Module);
                var expectedTarget = request.CollisionTarget ?? proposal?.ExpectedVersion;
                if (collision == null || (expectedTarget == null ? collision.Target != null || collision.Status != BaselineExistenceStatus.NotRequested : !expectedTarget.Equals(collision.Target)))
                    throw Failure("Collision adapter returned a mismatched target.");
                cancellationToken.ThrowIfCancellationRequested();
                Log("BaselineMetadataCompleted", id, "ObservationId=" + snapshot.ObservationId.ToString("N") + "; Status=" + snapshot.Status + "; HistoryCount=" + snapshot.History.Count + "; WriteAttempted=False");
                return new BaselineMetadataResult(snapshot, proposal, collision);
            }
            catch (Exception ex) { Log("BaselineMetadataFailed", id, "ExceptionType=" + ex.GetType().FullName + "; AutomaticRetry=False"); throw; }
        }
        private void Log(string name, Guid id, string message) { try { logger.Write(IntegrationLogLevel.Information, name, id, message); } catch { } }
        internal static IntegrationException Failure(string message) => new IntegrationException(IntegrationErrorCode.BaselineMetadataFailed, message);
    }
    public sealed class UnavailableBaselineHistoryAdapter : IBaselineHistoryAdapter, IBaselineNextVersionAdapter
    {
        public Task<BaselineMetadataSnapshot> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(BaselineMetadataSnapshot.Unavailable(request, BaselineHistoryStatus.ApiNotValidated, "NOT_RUN: a verified native baseline-history adapter is not configured.")); }
        public Task<BaselineCollisionResult> CheckCollisionAsync(BaselineHistoryRequest request, BaselineVersion target, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new BaselineCollisionResult(target, BaselineExistenceStatus.Unknown, "Native collision check NOT_RUN.")); }
        public Task<BaselineVersionProposal> ProposeAsync(BaselineMetadataSnapshot snapshot, BaselineType type, string suffix, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new BaselineVersionProposal(type, suffix, BaselineProposalStatus.RequiresEnvironmentValidation, null,
                BaselineMetadataSource.NotRun, "nextMajor/nextMinor handle resolution requires target-client validation",
                "No native expected version was guessed from visible or deleted baseline numbers.", snapshot.ObservationId, snapshot.Module));
        }
    }
}
