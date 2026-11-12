using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConsoleApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class PreflightTestClock : IBaselinePreflightClock
    { internal TimeSpan Offset; public DateTime UtcNow => DateTime.UtcNow + Offset; }
    internal sealed class PreflightTestMetadata : IBaselineMetadataService
    {
        internal readonly Dictionary<string, MockBaselineModuleState> Modules;
        internal int Reads;
        internal Action<BaselineHistoryRequest> BeforeRead;
        internal Func<BaselineMetadataResult, BaselineMetadataResult> Alter;
        internal PreflightTestMetadata(IEnumerable<BaselineModuleReference> modules)
        {
            Modules = modules.ToDictionary(m => m.ModuleId, m => new MockBaselineModuleState(m, new[] { MetadataFixture.Entry(m) },
                new BaselineVersion(3, 7, "removed"), new BaselineVersion(4, 0), new BaselineVersion(3, 8)));
        }
        public async Task<BaselineMetadataResult> ReadAsync(BaselineHistoryRequest request, CancellationToken cancellationToken)
        {
            Reads++; BeforeRead?.Invoke(request); var adapter = new MockBaselineHistoryAdapter(Modules.Values);
            var result = await MetadataFixture.Service(adapter).ReadAsync(request, cancellationToken); return Alter == null ? result : Alter(result);
        }
    }
    internal sealed class PreflightTestFixture
    {
        internal readonly List<MockBaselinePreflightState> States;
        internal readonly MockBaselinePreflightStateReader Reader;
        internal readonly PreflightTestClock Clock = new PreflightTestClock();
        internal readonly PreflightTestMetadata Metadata;
        internal readonly BaselineTestLog Log = new BaselineTestLog();
        internal readonly BulkBaselinePreflightService Service;
        internal PreflightTestFixture(int count = 1)
        {
            States = Enumerable.Range(1, count).Select(n => new MockBaselinePreflightState(new BaselineModuleReference("repo", "native-" + n, "/P/M" + n))).ToList();
            Reader = new MockBaselinePreflightStateReader(States, Clock); Metadata = new PreflightTestMetadata(States.Select(s => s.Module));
            Service = new BulkBaselinePreflightService(Reader, Metadata, Clock, Log);
        }
        internal BulkBaselinePreflightRequest Request(ExecutionPolicy policy = ExecutionPolicy.ContinueOnError, BaselineType type = BaselineType.Minor,
            string suffix = "SRR", string annotation = "İnceleme 注釈") => new BulkBaselinePreflightRequest(new BulkBaselineRequest(
                States.Select(s => new BaselineModuleSelection(s.Module)), type, suffix: suffix, annotation: annotation), policy);
        internal static void LockOther(MockBaselinePreflightState state)
        { state.EditState = BaselineEditState.ExclusiveOtherSession; state.LockState = BaselineLockState.OwnedByOtherSession; state.LockOwnerSessionId = "other-session"; state.LockOwnerUserId = state.CurrentUserId; }
    }
    [TestClass]
    public class BulkBaselinePreflightTests
    {
        [DataTestMethod, DataRow(BaselineType.Major, 4, 0), DataRow(BaselineType.Minor, 3, 8)]
        public async Task DryRunUsesAuthoritativeProposalAndRevalidatesWithoutWriting(BaselineType type, int major, int minor)
        {
            var f = new PreflightTestFixture(); var result = await f.Service.DryRunAsync(f.Request(type: type)); var item = result.Items.Single().Fresh;
            Assert.AreEqual(new BaselineVersion(3, 7, "removed"), item.CurrentVersion); Assert.AreEqual(new BaselineVersion(major, minor, "SRR"), item.ExpectedVersion);
            Assert.AreEqual(BulkBaselineItemStatus.Ready, item.Status); Assert.AreEqual(1, result.EligibleCount);
            Assert.AreEqual(2, f.Metadata.Reads); Assert.AreEqual(4, f.Reader.ReadCount);
            Assert.IsFalse(result.CanExecute); Assert.IsFalse(result.WriteAttempted); Assert.IsFalse(result.LockChanged); Assert.IsFalse(result.ModuleContentChanged);
            Assert.IsFalse(item.Precondition.IsWriteAuthorization); Assert.AreEqual(64, item.Precondition.Fingerprint.Length);
            Assert.AreEqual(BaselineLockState.Unlocked, f.States[0].LockState); Assert.AreEqual(BaselineUnsavedState.None, f.States[0].UnsavedChanges);
        }
        [TestMethod]
        public async Task FirstBaselineCanBeReadyWithoutInventingACurrentVersion()
        {
            var f = new PreflightTestFixture(); var m = f.States[0].Module;
            f.Metadata.Modules[m.ModuleId] = new MockBaselineModuleState(m, Array.Empty<BaselineHistoryEntry>(), null, new BaselineVersion(1, 0), new BaselineVersion(0, 1));
            var item = (await f.Service.CreatePlanAsync(f.Request())).Items.Single(); Assert.IsNull(item.CurrentVersion); Assert.AreEqual(new BaselineVersion(0, 1, "SRR"), item.ExpectedVersion);
            Assert.AreEqual(BulkBaselineItemStatus.Ready, item.Status);
        }
        [DataTestMethod, DataRow("missing", BaselinePreflightReason.MissingModule), DataRow("unknown-module", BaselinePreflightReason.UnknownModule),
            DataRow("link", BaselinePreflightReason.NotFormalModule), DataRow("read", BaselinePreflightReason.ReadDenied),
            DataRow("create", BaselinePreflightReason.CreateDenied), DataRow("modify", BaselinePreflightReason.ModifyDenied),
            DataRow("unknown-access", BaselinePreflightReason.PermissionUnknown), DataRow("session", BaselinePreflightReason.SessionUnknown),
            DataRow("revision", BaselinePreflightReason.RevisionUnavailable), DataRow("suffix", BaselinePreflightReason.InvalidInput), DataRow("annotation", BaselinePreflightReason.InvalidInput),
            DataRow("exclusive", BaselinePreflightReason.ExclusiveUnavailable), DataRow("lock-unknown", BaselinePreflightReason.LockUnknown)]
        public async Task FailedOrUnknownEvidenceBlocksBeforeAnyMetadataCall(string change, BaselinePreflightReason reason)
        {
            var f = new PreflightTestFixture(); var s = f.States[0];
            switch (change)
            {
                case "missing": s.Exists = BaselineCheckState.Failed; break;
                case "unknown-module": s.Exists = BaselineCheckState.Unknown; break;
                case "link": s.Type = DoorsItemKind.LinkModule; break;
                case "read": s.ReadAccess = BaselineCheckState.Failed; break;
                case "create": s.CreateAccess = BaselineCheckState.Failed; break;
                case "modify": s.ModifyAccess = BaselineCheckState.Failed; break;
                case "unknown-access": s.CreateAccess = BaselineCheckState.Unknown; break;
                case "session": s.SessionValid = BaselineCheckState.Unknown; break;
                case "revision": s.ContentRevision = null; break;
                case "suffix": s.SuffixValid = BaselineCheckState.Failed; break;
                case "annotation": s.AnnotationValid = BaselineCheckState.Unknown; break;
                case "exclusive": s.ExclusiveAvailability = BaselineCheckState.Unknown; break;
                case "lock-unknown": s.LockState = BaselineLockState.Unknown; break;
            }
            var item = (await f.Service.CreatePlanAsync(f.Request())).Items.Single();
            Assert.AreEqual(BulkBaselineItemStatus.Blocked, item.Status); Assert.AreEqual(reason, item.ReasonCode); Assert.IsTrue(item.WillBeSkipped);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.RecommendedResolution)); Assert.AreEqual(0, f.Metadata.Reads);
        }
        [TestMethod]
        public async Task SameUserInAnotherSessionIsStillBlockedAndNeverForceUnlocked()
        {
            var f = new PreflightTestFixture(); PreflightTestFixture.LockOther(f.States[0]); var originalLockOwner = f.States[0].LockOwnerSessionId;
            var result = await f.Service.DryRunAsync(f.Request()); Assert.AreEqual(BaselinePreflightReason.OtherSessionLock, result.Items[0].Fresh.ReasonCode);
            Assert.AreEqual(originalLockOwner, f.States[0].LockOwnerSessionId); Assert.AreEqual(BaselineLockState.OwnedByOtherSession, f.States[0].LockState);
            Assert.AreEqual(0, result.EligibleCount); Assert.AreEqual(0, f.Metadata.Reads); StringAssert.Contains(result.Summary, "Tüm modüller");
        }
        [DataTestMethod, DataRow(BaselineUnsavedState.CurrentSession, BaselinePreflightReason.UnsavedCurrentSession),
            DataRow(BaselineUnsavedState.OtherSession, BaselinePreflightReason.UnsavedOtherSession), DataRow(BaselineUnsavedState.Unknown, BaselinePreflightReason.UnsavedUnknown)]
        public async Task DirtyDataIsNeverSavedOrDiscarded(BaselineUnsavedState changes, BaselinePreflightReason reason)
        {
            var f = new PreflightTestFixture(); f.States[0].UnsavedChanges = changes; f.States[0].DirtyOwnerSessionId = changes == BaselineUnsavedState.CurrentSession ? f.States[0].SessionId : "other";
            var result = await f.Service.DryRunAsync(f.Request()); Assert.AreEqual(reason, result.Items[0].Fresh.ReasonCode);
            Assert.AreEqual(changes, f.States[0].UnsavedChanges); Assert.AreEqual(0, f.Metadata.Reads); Assert.IsFalse(result.ModuleContentChanged);
        }
        [DataTestMethod, DataRow(BaselineEditState.SharedCurrentSession), DataRow(BaselineEditState.SharedOtherSession)]
        public async Task SharedModesAreNotAutomaticallyConverted(BaselineEditState mode)
        { var f = new PreflightTestFixture(); f.States[0].EditState = mode; Assert.AreEqual(BaselinePreflightReason.SharedEdit, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode); Assert.AreEqual(mode, f.States[0].EditState); }
        [TestMethod]
        public async Task CurrentExclusiveEditRequiresExactOwnerSessionAndCleanState()
        {
            var f = new PreflightTestFixture(); var s = f.States[0]; s.EditState = BaselineEditState.ExclusiveCurrentSession; s.LockState = BaselineLockState.OwnedByCurrentSession;
            s.LockOwnerSessionId = s.SessionId; s.LockOwnerUserId = s.CurrentUserId;
            Assert.AreEqual(1, (await f.Service.CreatePlanAsync(f.Request())).ReadyCount);
            s.LockOwnerSessionId = "other"; Assert.AreEqual(BaselinePreflightReason.ExclusiveUnavailable, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
        }
        [TestMethod]
        public async Task ConflictingUnlockedOrCleanOwnerEvidenceIsRejected()
        {
            var f = new PreflightTestFixture(); f.States[0].LockOwnerSessionId = "stale";
            Assert.AreEqual(BaselinePreflightReason.InvalidEvidence, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
            f.States[0].LockOwnerSessionId = null; f.States[0].DirtyOwnerSessionId = "stale";
            Assert.AreEqual(BaselinePreflightReason.InvalidEvidence, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
        }
        [DataTestMethod, DataRow(ExecutionPolicy.StopOnFirstError, 0, 2, 1), DataRow(ExecutionPolicy.ContinueOnError, 2, 0, 5)]
        public async Task ExecutionPolicyControlsWhichModulesAreRead(ExecutionPolicy policy, int ready, int skipped, int reads)
        {
            var f = new PreflightTestFixture(3); PreflightTestFixture.LockOther(f.States[0]);
            var plan = await f.Service.CreatePlanAsync(f.Request(policy)); Assert.AreEqual(ready, plan.ReadyCount); Assert.AreEqual(skipped, plan.SkippedCount);
            Assert.AreEqual(1, plan.BlockedCount); Assert.AreEqual(reads, f.Reader.ReadCount); Assert.IsTrue(plan.Request.SkipBlockedModules);
        }
        [DataTestMethod, DataRow("content"), DataRow("permission"), DataRow("lock"), DataRow("database"), DataRow("user"), DataRow("session"), DataRow("connection")]
        public async Task PreconditionDetectsChangesEvenWhenBaselineNumbersStayTheSame(string change)
        {
            var f = new PreflightTestFixture(); var plan = await f.Service.CreatePlanAsync(f.Request()); var s = f.States[0];
            switch (change)
            {
                case "content": s.ContentRevision += "-2"; break;
                case "permission": s.PermissionRevision += "-2"; break;
                case "lock": s.LockRevision += "-2"; break;
                case "database": s.DatabaseIdentity += "-2"; break;
                case "user": s.CurrentUserId += "-2"; break;
                case "session": s.SessionId += "-2"; break;
                case "connection": s.ConnectionEpoch += "-2"; break;
            }
            var check = await f.Service.RevalidateAsync(plan); Assert.AreEqual(0, check.EligibleCount);
            Assert.AreEqual(BaselinePreflightReason.PreconditionChanged, check.Items[0].Fresh.ReasonCode); Assert.IsFalse(check.Items[0].PreconditionsMatch);
            Assert.AreEqual(plan.Items[0].ExpectedVersion, check.Items[0].Fresh.ExpectedVersion);
        }
        [TestMethod]
        public async Task MetadataVersionChangeDoesNotSilentlyRetargetTheOldPlan()
        {
            var f = new PreflightTestFixture(); var plan = await f.Service.CreatePlanAsync(f.Request()); var m = f.States[0].Module;
            f.Metadata.Modules[m.ModuleId] = new MockBaselineModuleState(m, new[] { MetadataFixture.Entry(m) }, new BaselineVersion(4, 1), new BaselineVersion(5, 0), new BaselineVersion(4, 2));
            var check = await f.Service.RevalidateAsync(plan); Assert.AreEqual(BaselinePreflightReason.PreconditionChanged, check.Items[0].Fresh.ReasonCode);
            Assert.AreEqual(new BaselineVersion(3, 8, "SRR"), check.Items[0].Fresh.ExpectedVersion); StringAssert.Contains(check.Items[0].Fresh.Warning, "4.2");
        }
        [TestMethod]
        public async Task BeforeAfterSandwichRejectsChangesDuringTheMetadataQuery()
        {
            var f = new PreflightTestFixture(); f.Metadata.BeforeRead = request => f.States[0].ContentRevision += "-changed";
            var plan = await f.Service.CreatePlanAsync(f.Request()); Assert.AreEqual(BaselinePreflightReason.ChangedDuringRead, plan.Items[0].ReasonCode); Assert.IsNull(plan.Items[0].Precondition);
        }
        [TestMethod]
        public async Task DifferentModuleSessionsCannotBeCombinedIntoOneBatch()
        {
            var f = new PreflightTestFixture(2); f.States[1].SessionId = "different-session";
            var plan = await f.Service.CreatePlanAsync(f.Request()); Assert.AreEqual(1, plan.ReadyCount); Assert.AreEqual(BaselinePreflightReason.ContextMismatch, plan.Items[1].ReasonCode);
        }
        [TestMethod]
        public async Task NegativeBaselineExistsWithoutAuthoritativeAbsenceEvidenceIsBlocked()
        {
            var f = new PreflightTestFixture(); f.States[0].TargetAvailability = BaselineTargetAvailability.Unknown;
            Assert.AreEqual(BaselinePreflightReason.CollisionUnknown, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
            f.States[0].TargetAvailability = BaselineTargetAvailability.Exists;
            Assert.AreEqual(BaselinePreflightReason.CollisionExists, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
        }
        [TestMethod]
        public async Task CollisionAppearingBeforePreviewDispatchStopsThatModule()
        {
            var f = new PreflightTestFixture(); var plan = await f.Service.CreatePlanAsync(f.Request());
            f.Metadata.Alter = result => new BaselineMetadataResult(result.Snapshot, result.Proposal, new BaselineCollisionResult(result.ExpectedVersion, BaselineExistenceStatus.Exists, "Mock race"));
            var check = await f.Service.RevalidateAsync(plan); Assert.AreEqual(0, check.EligibleCount); Assert.AreEqual(BaselinePreflightReason.CollisionExists, check.Items[0].Fresh.ReasonCode);
        }
        [TestMethod]
        public async Task UnchangedSemanticEvidencePassesDespiteNewObservationIdsAndTimestamps()
        {
            var f = new PreflightTestFixture(); var request = f.Request(); var one = await f.Service.CreatePlanAsync(request); var two = await f.Service.CreatePlanAsync(request);
            Assert.AreEqual(one.Items[0].Precondition.Fingerprint, two.Items[0].Precondition.Fingerprint); Assert.AreNotEqual(one.Fingerprint, two.Fingerprint);
            Assert.AreEqual(1, (await f.Service.RevalidateAsync(one)).EligibleCount); Assert.AreEqual(1, (await f.Service.RevalidateAsync(one)).EligibleCount);
        }
        [TestMethod]
        public async Task ExpiredPlanDoesNotUseOldEligibilityOrQueryTheAdapter()
        {
            var f = new PreflightTestFixture(); var plan = await f.Service.CreatePlanAsync(f.Request()); int reads = f.Reader.ReadCount;
            f.Clock.Offset = TimeSpan.FromMinutes(6); var check = await f.Service.RevalidateAsync(plan);
            Assert.AreEqual(BaselinePreflightReason.PlanExpired, check.Items[0].Fresh.ReasonCode); Assert.AreEqual(reads, f.Reader.ReadCount); Assert.AreEqual(0, check.EligibleCount);
        }
        [TestMethod]
        public async Task PreviouslyBlockedModulesAreNotPromotedWithoutANewPlan()
        {
            var f = new PreflightTestFixture(); f.States[0].CreateAccess = BaselineCheckState.Failed; var plan = await f.Service.CreatePlanAsync(f.Request());
            f.States[0].CreateAccess = BaselineCheckState.Passed; int reads = f.Reader.ReadCount; var check = await f.Service.RevalidateAsync(plan);
            Assert.AreEqual(0, check.EligibleCount); Assert.AreEqual(reads, f.Reader.ReadCount); Assert.AreEqual(1, (await f.Service.CreatePlanAsync(f.Request())).ReadyCount);
        }
        [TestMethod]
        public async Task StopOnFirstErrorAlsoStopsRevalidationOfRemainingCandidates()
        {
            var f = new PreflightTestFixture(2); var plan = await f.Service.CreatePlanAsync(f.Request(ExecutionPolicy.StopOnFirstError)); f.States[0].ModifyAccess = BaselineCheckState.Failed;
            int reads = f.Reader.ReadCount; var check = await f.Service.RevalidateAsync(plan); Assert.AreEqual(reads + 1, f.Reader.ReadCount);
            Assert.AreEqual(BulkBaselineItemStatus.Skipped, check.Items[1].Fresh.Status); Assert.AreEqual(0, check.EligibleCount);
        }
        [TestMethod]
        public async Task TimeoutHaltsFurtherNativeCallsEvenUnderContinueOnError()
        {
            var f = new PreflightTestFixture(3); f.Reader.OnRead = (probe, count) => throw new IntegrationException(IntegrationErrorCode.DxlTimeout, "SECRET");
            var plan = await f.Service.CreatePlanAsync(f.Request()); Assert.AreEqual(1, f.Reader.ReadCount); Assert.AreEqual(2, plan.SkippedCount);
            Assert.AreEqual(BaselinePreflightReason.OperationUncertain, plan.Items[0].ReasonCode); Assert.IsFalse(string.Join(" ", f.Log.Messages).Contains("SECRET"));
        }
        [TestMethod]
        public async Task OrdinaryRecordFailureCanContinueWithoutRetryOrLeakingContent()
        {
            var f = new PreflightTestFixture(2); f.Reader.OnRead = (probe, count) => count == 1 ? throw new InvalidOperationException("SECRET") : f.States.Single(s => s.Module.Matches(probe.Module)).ToFacts();
            var plan = await f.Service.CreatePlanAsync(f.Request()); Assert.AreEqual(1, plan.ReadyCount); Assert.AreEqual(1, plan.BlockedCount); Assert.AreEqual(3, f.Reader.ReadCount);
            Assert.IsFalse(string.Join(" ", f.Log.Messages).Contains("SECRET"));
        }
        [TestMethod]
        public async Task CancellationDoesNotPublishPartialPlanOrRetry()
        {
            var f = new PreflightTestFixture(2); using (var token = new CancellationTokenSource())
            {
                f.Reader.OnRead = (probe, count) => { token.Cancel(); return f.States[0].ToFacts(); };
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => f.Service.CreatePlanAsync(f.Request(), token.Token)); Assert.AreEqual(1, f.Reader.ReadCount);
            }
        }
        [TestMethod]
        public void InvalidInputsAreRejectedBeforeReadingModuleState()
        {
            var f = new PreflightTestFixture(); Assert.ThrowsException<ArgumentOutOfRangeException>(() => f.Request((ExecutionPolicy)99));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => f.Request(type: (BaselineType)99));
            Assert.ThrowsException<IntegrationException>(() => f.Request(suffix: "bad\n")); Assert.ThrowsException<ArgumentException>(() => f.Request(annotation: "bad\0")); Assert.AreEqual(0, f.Reader.ReadCount);
        }
        [TestMethod]
        public async Task PartialMetadataCannotProduceAReadyPrecondition()
        {
            var f = new PreflightTestFixture();
            f.Metadata.Alter = result =>
            {
                var s = result.Snapshot; var partial = new BaselineMetadataSnapshot(s.ObservationId, s.Request, s.Status, s.History,
                    s.LatestExisting, s.LatestNumberingVersion, false, s.Ownership, s.Cleanup, s.ObservedAtUtc, s.Source, s.ApiEvidence, s.Collision);
                return new BaselineMetadataResult(partial, result.Proposal, result.Collision);
            };
            Assert.AreEqual(BaselinePreflightReason.MetadataUnavailable, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
        }
        [TestMethod]
        public async Task MissingNextVersionEvidenceIsNotReplacedByArithmetic()
        {
            var f = new PreflightTestFixture(); var m = f.States[0].Module;
            f.Metadata.Modules[m.ModuleId] = new MockBaselineModuleState(m, new[] { MetadataFixture.Entry(m) }, new BaselineVersion(3, 7), new BaselineVersion(4, 0), null);
            var item = (await f.Service.CreatePlanAsync(f.Request())).Items[0]; Assert.AreEqual(BaselinePreflightReason.VersionUnverified, item.ReasonCode); Assert.IsNull(item.ExpectedVersion);
        }
        [TestMethod]
        public async Task ForeignModuleStateIsNotAcceptedByPathAlone()
        {
            var f = new PreflightTestFixture(); f.Reader.OnRead = (probe, count) => new MockBaselinePreflightState(new BaselineModuleReference("repo", "foreign-id", probe.Module.CanonicalFullPath)).ToFacts();
            Assert.AreEqual(BaselinePreflightReason.InvalidEvidence, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode); Assert.AreEqual(0, f.Metadata.Reads);
        }
        [TestMethod]
        public async Task CollisionEvidenceMustBeBoundToTheExactExpectedVersion()
        {
            var f = new PreflightTestFixture(); f.Metadata.Alter = r => new BaselineMetadataResult(r.Snapshot, r.Proposal,
                new BaselineCollisionResult(new BaselineVersion(9, 9), BaselineExistenceStatus.AbsentOrUnreadable, "foreign target"));
            Assert.AreEqual(BaselinePreflightReason.InvalidEvidence, (await f.Service.CreatePlanAsync(f.Request())).Items[0].ReasonCode);
        }
        [TestMethod]
        public async Task ModuleOverridesAreValidatedAndBoundIntoThePlan()
        {
            var f = new PreflightTestFixture();
            var request = new BulkBaselinePreflightRequest(new BulkBaselineRequest(new[] { new BaselineModuleSelection(f.States[0].Module, "override", "module note") },
                suffix: "job", annotation: "job note"));
            var item = (await f.Service.CreatePlanAsync(request)).Items[0]; Assert.AreEqual("override", item.ExpectedVersion.Suffix); Assert.AreEqual("module note", item.Annotation);
        }
        [TestMethod]
        public async Task LongBatchCannotPublishStaleEligibilityForItsFirstModule()
        {
            var f = new PreflightTestFixture(2);
            f.Reader.OnRead = (probe, count) =>
            {
                if (probe.Module.Matches(f.States[1].Module)) f.Clock.Offset = TimeSpan.FromMinutes(3);
                return f.States.Single(s => s.Module.Matches(probe.Module)).ToFacts();
            };
            var plan = await f.Service.CreatePlanAsync(f.Request()); Assert.AreEqual(0, plan.ReadyCount); Assert.AreEqual(BaselinePreflightReason.ObservationExpired, plan.Items[0].ReasonCode);
        }
        [DataTestMethod, DataRow(-3), DataRow(1)]
        public async Task StaleOrFutureStateObservationsAreRejected(int minutes)
        {
            var f = new PreflightTestFixture(); var reader = new TimeShiftReader(f.States[0], minutes);
            var service = new BulkBaselinePreflightService(reader, f.Metadata, f.Clock, f.Log);
            Assert.AreEqual(BaselinePreflightReason.InvalidEvidence, (await service.CreatePlanAsync(f.Request())).Items[0].ReasonCode); Assert.AreEqual(0, f.Metadata.Reads);
        }
        [DataTestMethod, DataRow(ExecutionPolicy.StopOnFirstError, 0, 1), DataRow(ExecutionPolicy.ContinueOnError, 1, 0)]
        public async Task LateObservationExpiryStillHonorsExecutionPolicy(ExecutionPolicy policy, int ready, int skipped)
        {
            var f = new PreflightTestFixture(2);
            f.Reader.OnRead = (probe, count) =>
            {
                if (probe.Module.Matches(f.States[1].Module)) f.Clock.Offset = TimeSpan.FromMinutes(3);
                return f.States.Single(s => s.Module.Matches(probe.Module)).ToFacts();
            };
            f.Metadata.Alter = r =>
            {
                var s = r.Snapshot;
                return new BaselineMetadataResult(new BaselineMetadataSnapshot(s.ObservationId, s.Request, s.Status, s.History,
                    s.LatestExisting, s.LatestNumberingVersion, s.HistoryComplete, s.Ownership, s.Cleanup, f.Clock.UtcNow,
                    s.Source, s.ApiEvidence, s.Collision), r.Proposal, r.Collision);
            };
            var plan = await f.Service.CreatePlanAsync(f.Request(policy)); Assert.AreEqual(ready, plan.ReadyCount); Assert.AreEqual(skipped, plan.SkippedCount);
            Assert.AreEqual(BaselinePreflightReason.ObservationExpired, plan.Items[0].ReasonCode);
        }
        private sealed class TimeShiftReader : IBaselinePreflightStateReader
        {
            private readonly MockBaselinePreflightState state; private readonly int minutes;
            internal TimeShiftReader(MockBaselinePreflightState state, int minutes) { this.state = state; this.minutes = minutes; }
            public Task<BaselinePreflightObservation> ReadAsync(BaselinePreflightProbe probe, CancellationToken cancellationToken) =>
                Task.FromResult(new BaselinePreflightObservation(probe, state.ToFacts(), DateTime.UtcNow.AddMinutes(minutes)));
        }
        [TestMethod]
        public void DefaultNativeCompositionUsesReadOnlyObservationAndKeepsWriteGatesClosed()
        {
            using (var host = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            {
                Assert.IsInstanceOfType(host.GetRequiredService<IBaselinePreflightStateReader>(), typeof(DoorsIntegration.Dxl.DxlBaselinePreflightStateReader));
                Assert.IsInstanceOfType(host.GetRequiredService<IBaselineSetAdapter>(), typeof(DoorsIntegration.Dxl.DxlReadOnlyBaselineSetAdapter));
                Assert.IsFalse(host.GetRequiredService<IBaselineSetAdapter>().WritesValidated);
                Assert.IsInstanceOfType(host.GetRequiredService<DoorsIntegration.Dxl.IBaselineExecutionCompatibility>(), typeof(DoorsIntegration.Dxl.UnvalidatedBaselineExecutionCompatibility));
            }
        }
    }
}
