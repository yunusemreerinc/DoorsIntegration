using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    // Explicit fixture outcomes, not an assertion about an untested DOORS release's numbering algorithm.
    public sealed class MockBaselineModuleState
    {
        public MockBaselineModuleState(BaselineModuleReference module, IEnumerable<BaselineHistoryEntry> existingHistory,
            BaselineVersion latestNumberingVersion, BaselineVersion nextMajor, BaselineVersion nextMinor, bool isOpen = true,
            BaselineHistoryStatus status = BaselineHistoryStatus.Available)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            ExistingHistory = BulkBaselineGuard.List(existingHistory ?? throw new ArgumentNullException(nameof(existingHistory)), 100000);
            if (ExistingHistory.Any(e => !module.Matches(e.Identity.Module) || e.Existence != BaselineExistenceStatus.Exists)) throw new ArgumentException("Mock history must contain exact-module existing records.");
            LatestNumberingVersion = latestNumberingVersion; NextMajor = nextMajor; NextMinor = nextMinor; IsOpen = isOpen;
            BulkBaselineGuard.Defined(status); Status = status;
        }
        public BaselineModuleReference Module { get; }
        public IReadOnlyList<BaselineHistoryEntry> ExistingHistory { get; }
        public BaselineVersion LatestNumberingVersion { get; }
        public BaselineVersion NextMajor { get; }
        public BaselineVersion NextMinor { get; }
        public bool IsOpen { get; }
        public BaselineHistoryStatus Status { get; }
    }
    public sealed class MockBaselineHistoryAdapter : IBaselineHistoryAdapter, IBaselineNextVersionAdapter
    {
        private readonly Dictionary<string, MockBaselineModuleState> states;
        public MockBaselineHistoryAdapter(IEnumerable<MockBaselineModuleState> states)
        {
            this.states = new Dictionary<string, MockBaselineModuleState>(StringComparer.Ordinal);
            foreach (var state in BulkBaselineGuard.List(states ?? throw new ArgumentNullException(nameof(states)), 10000))
                this.states.Add(Key(state.Module), state);
        }
        private static string Key(BaselineModuleReference module) => module.RepositoryId.Length + ":" + module.RepositoryId + module.ModuleId.Length + ":" + module.ModuleId;
        private MockBaselineModuleState Find(BaselineModuleReference module) => states.TryGetValue(Key(module), out var state) ? state : null;
        public Task<BaselineMetadataSnapshot> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request)); cancellationToken.ThrowIfCancellationRequested();
            var state = Find(request.Module); var status = state == null ? BaselineHistoryStatus.DeniedOrNotFound :
                !state.Module.Matches(request.Module) ? BaselineHistoryStatus.IdentityMismatch : state.Status != BaselineHistoryStatus.Available ? state.Status :
                !state.IsOpen && !request.OpenIfClosed ? BaselineHistoryStatus.ClosedByPolicy : BaselineHistoryStatus.Available;
            if (status != BaselineHistoryStatus.Available) return Task.FromResult(BaselineMetadataSnapshot.Unavailable(request, status, "MOCK: read-only history status=" + status, "Explicit mock fixture; native runtime NOT_RUN"));
            var latest = state.ExistingHistory.LastOrDefault()?.Identity.Version;
            var existing = new BaselineDesignation(latest == null ? BaselineDesignationStatus.None : BaselineDesignationStatus.Available, latest,
                latest == null ? BaselineExistenceStatus.Unknown : BaselineExistenceStatus.Exists);
            var numbering = new BaselineDesignation(state.LatestNumberingVersion == null ? BaselineDesignationStatus.None : BaselineDesignationStatus.Available,
                state.LatestNumberingVersion, state.LatestNumberingVersion == null ? BaselineExistenceStatus.Unknown :
                    state.ExistingHistory.Any(e => e.Identity.Version.Equals(state.LatestNumberingVersion)) ? BaselineExistenceStatus.Exists : BaselineExistenceStatus.AbsentOrUnreadable);
            return Task.FromResult(new BaselineMetadataSnapshot(Guid.NewGuid(), request, status, state.ExistingHistory.Take(request.MaxBaselines), existing, numbering,
                state.ExistingHistory.Count <= request.MaxBaselines, state.IsOpen ? RequirementModuleOwnership.BorrowedReadOnly : RequirementModuleOwnership.OwnedReadOnly,
                state.IsOpen ? RequirementCleanupStatus.NotOwned : RequirementCleanupStatus.Closed, DateTime.UtcNow, BaselineMetadataSource.Mock,
                "Explicit mock nextMajor/nextMinor outcomes; no native compatibility claim", Collision(state, request.CollisionTarget)));
        }
        public Task<BaselineVersionProposal> ProposeAsync(BaselineMetadataSnapshot snapshot, BaselineType type, string suffix, CancellationToken cancellationToken)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot)); cancellationToken.ThrowIfCancellationRequested();
            BulkBaselineGuard.Defined(type); var state = Find(snapshot.Module); var configured = type == BaselineType.Major ? state?.NextMajor : state?.NextMinor;
            var expected = configured == null ? null : new BaselineVersion(configured.Major, configured.Minor, suffix);
            return Task.FromResult(new BaselineVersionProposal(type, suffix, expected == null ? BaselineProposalStatus.Unavailable : BaselineProposalStatus.Available,
                expected, BaselineMetadataSource.Mock, "Explicit configured mock result for nextMajor/nextMinor; native validation required",
                expected == null ? "Mock next-version outcome was not configured." : null, snapshot.ObservationId, snapshot.Module));
        }
        public Task<BaselineCollisionResult> CheckCollisionAsync(BaselineHistoryRequest request, BaselineVersion target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var state = Find(request.Module);
            if (state == null || !state.Module.Matches(request.Module) || state.Status != BaselineHistoryStatus.Available || (!state.IsOpen && !request.OpenIfClosed))
                return Task.FromResult(new BaselineCollisionResult(target, BaselineExistenceStatus.Unknown, "MOCK: module metadata is unavailable."));
            return Task.FromResult(Collision(state, target));
        }
        private static BaselineCollisionResult Collision(MockBaselineModuleState state, BaselineVersion target) => new BaselineCollisionResult(target,
            target == null ? BaselineExistenceStatus.NotRequested : state.ExistingHistory.Any(e => e.Identity.Version.Equals(target)) ?
                BaselineExistenceStatus.Exists : BaselineExistenceStatus.AbsentOrUnreadable,
            "Mock baselineExists observation. A negative result is not write authorization and does not prove absence under native ACLs.");
    }
}
