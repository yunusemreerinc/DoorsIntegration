using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class UnavailableBaselinePreflightStateReader : IBaselinePreflightStateReader
    {
        public Task<BaselinePreflightObservation> ReadAsync(BaselinePreflightProbe probe, CancellationToken cancellationToken)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe)); cancellationToken.ThrowIfCancellationRequested();
            var u = BaselineCheckState.Unknown;
            return Task.FromResult(new BaselinePreflightObservation(probe, new BaselinePreflightFacts(probe.Module, u, DoorsItemKind.Unknown, u, u, u,
                BaselineEditState.Unknown, BaselineLockState.Unknown, u, BaselineUnsavedState.Unknown, u, null, null, null, null, null, null, null,
                u, u, u, BaselineTargetAvailability.Unknown, BaselineMetadataSource.NotRun,
                "NOT_RUN: native read-only create/modify/exclusive/session/revision/input evidence reader is not verified"), DateTime.UtcNow));
        }
    }
    // Mutable test/demo fixture only. ToFacts captures an immutable observation; never use as a native evidence source.
    public sealed class MockBaselinePreflightState
    {
        public MockBaselinePreflightState(BaselineModuleReference module) { Module = module ?? throw new ArgumentNullException(nameof(module)); }
        public BaselineModuleReference Module { get; }
        public BaselineCheckState Exists { get; set; } = BaselineCheckState.Passed;
        public DoorsItemKind Type { get; set; } = DoorsItemKind.FormalModule;
        public BaselineCheckState ReadAccess { get; set; } = BaselineCheckState.Passed;
        public BaselineCheckState CreateAccess { get; set; } = BaselineCheckState.Passed;
        public BaselineCheckState ModifyAccess { get; set; } = BaselineCheckState.Passed;
        public BaselineEditState EditState { get; set; } = BaselineEditState.ReadOnly;
        public BaselineLockState LockState { get; set; } = BaselineLockState.Unlocked;
        public BaselineCheckState ExclusiveAvailability { get; set; } = BaselineCheckState.Passed;
        public BaselineUnsavedState UnsavedChanges { get; set; } = BaselineUnsavedState.None;
        public BaselineCheckState SessionValid { get; set; } = BaselineCheckState.Passed;
        public string DatabaseIdentity { get; set; } = "mock-database";
        public string CurrentUserId { get; set; } = "mock-user";
        public string SessionId { get; set; } = "mock-session";
        public string ConnectionEpoch { get; set; } = "mock-connection-1";
        public string ContentRevision { get; set; } = "mock-content-1";
        public string PermissionRevision { get; set; } = "mock-acl-1";
        public string LockRevision { get; set; } = "mock-lock-1";
        public BaselineCheckState RevisionsReliable { get; set; } = BaselineCheckState.Passed;
        public BaselineCheckState SuffixValid { get; set; } = BaselineCheckState.Passed;
        public BaselineCheckState AnnotationValid { get; set; } = BaselineCheckState.Passed;
        public BaselineTargetAvailability TargetAvailability { get; set; } = BaselineTargetAvailability.Free;
        public string LockOwnerSessionId { get; set; }
        public string LockOwnerUserId { get; set; }
        public string DirtyOwnerSessionId { get; set; }
        public BaselinePreflightFacts ToFacts() => new BaselinePreflightFacts(Module, Exists, Type, ReadAccess, CreateAccess, ModifyAccess,
            EditState, LockState, ExclusiveAvailability, UnsavedChanges, SessionValid, DatabaseIdentity, CurrentUserId, SessionId, ConnectionEpoch,
            ContentRevision, PermissionRevision, LockRevision, RevisionsReliable, SuffixValid, AnnotationValid, TargetAvailability, BaselineMetadataSource.Mock,
            "Explicit mock fixture; native permissions/locks/revision/next-version validation NOT_RUN", LockOwnerSessionId, LockOwnerUserId, DirtyOwnerSessionId);
    }
    public sealed class MockBaselinePreflightStateReader : IBaselinePreflightStateReader
    {
        private readonly Dictionary<string, MockBaselinePreflightState> states = new Dictionary<string, MockBaselinePreflightState>(StringComparer.Ordinal);
        private readonly IBaselinePreflightClock clock;
        private int reads;
        public MockBaselinePreflightStateReader(IEnumerable<MockBaselinePreflightState> states, IBaselinePreflightClock clock = null)
        {
            this.clock = clock ?? new BaselinePreflightClock();
            foreach (var state in BulkBaselineGuard.List(states ?? throw new ArgumentNullException(nameof(states)), 10000)) this.states.Add(Key(state.Module), state);
        }
        private static string Key(BaselineModuleReference m) => m.RepositoryId.Length + ":" + m.RepositoryId + m.ModuleId.Length + ":" + m.ModuleId;
        public int ReadCount => Volatile.Read(ref reads);
        public Func<BaselinePreflightProbe, int, BaselinePreflightFacts> OnRead { get; set; }
        public Task<BaselinePreflightObservation> ReadAsync(BaselinePreflightProbe probe, CancellationToken cancellationToken)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe)); cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref reads); BaselinePreflightFacts facts;
            if (OnRead != null) facts = OnRead(probe, call);
            else if (states.TryGetValue(Key(probe.Module), out var state)) facts = state.ToFacts();
            else facts = new MockBaselinePreflightState(probe.Module) { Exists = BaselineCheckState.Failed }.ToFacts();
            return Task.FromResult(new BaselinePreflightObservation(probe, facts, clock.UtcNow));
        }
    }
}
