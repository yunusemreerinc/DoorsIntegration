using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Dxl;
using DoorsIntegration.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal static class MetadataFixture
    {
        internal static BaselineModuleReference Module(string path = "/P/M", string id = "qualified-native-1") => new BaselineModuleReference("repo-1", id, path);
        internal static BaselineHistoryEntry Entry(BaselineModuleReference module, int major = 2, int minor = 4, string suffix = "old") =>
            new BaselineHistoryEntry(new BaselineIdentity(module, new BaselineVersion(major, minor, suffix)), "İnceleme\n注釈", "reviewer-1",
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "2026-01-02 03:04:05Z");
        internal static MockBaselineHistoryAdapter Adapter(bool first = false, bool allDeleted = false, bool open = true, BaselineHistoryStatus status = BaselineHistoryStatus.Available)
        {
            var module = Module(); return new MockBaselineHistoryAdapter(new[] { new MockBaselineModuleState(module,
                first || allDeleted ? Array.Empty<BaselineHistoryEntry>() : new[] { Entry(module) }, first ? null : new BaselineVersion(3, 7, "removed"),
                new BaselineVersion(first ? 1 : 4, 0), new BaselineVersion(first ? 0 : 3, first ? 1 : 8), open, status) });
        }
        internal static BaselineMetadataService Service(IBaselineHistoryAdapter history, IBaselineNextVersionAdapter next = null, IIntegrationLogger logger = null) =>
            new BaselineMetadataService(history, next ?? (IBaselineNextVersionAdapter)history, logger ?? new BaselineTestLog());
    }
    [TestClass]
    public class BaselineMetadataTests
    {
        [DataTestMethod, DataRow(BaselineType.Major, 4, 0), DataRow(BaselineType.Minor, 3, 8)]
        public async Task ConfiguredMockNextOutcomesAreIndependentOfLatestExisting(BaselineType type, int major, int minor)
        {
            var result = await MetadataFixture.Service(MetadataFixture.Adapter()).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: type, suffix: "SRR"));
            Assert.AreEqual(new BaselineVersion(2, 4, "old"), result.Snapshot.LatestExisting.Version);
            Assert.AreEqual(new BaselineVersion(3, 7, "removed"), result.Snapshot.LatestNumberingVersion.Version);
            Assert.IsTrue(result.Snapshot.LatestNumberingVersion.MayBeDeletedOrInaccessible);
            Assert.AreEqual(new BaselineVersion(major, minor, "SRR"), result.ExpectedVersion);
            Assert.AreEqual(BaselineMetadataSource.Mock, result.Proposal.Source); Assert.IsFalse(result.Proposal.IsReserved); Assert.IsFalse(result.CanCreateBaseline);
            Assert.AreEqual(result.Snapshot.ObservationId, result.Proposal.ObservationId);
        }
        [DataTestMethod, DataRow(BaselineType.Major, 1, 0), DataRow(BaselineType.Minor, 0, 1)]
        public async Task FirstBaselineUsesExplicitMockNextOutcomes(BaselineType type, int major, int minor)
        {
            var result = await MetadataFixture.Service(MetadataFixture.Adapter(first: true)).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: type));
            Assert.IsTrue(result.Snapshot.HasNoBaselineHistory); Assert.IsNull(result.Snapshot.LatestExisting.Version); Assert.IsNull(result.Snapshot.LatestNumberingVersion.Version);
            Assert.AreEqual(new BaselineVersion(major, minor), result.ExpectedVersion); Assert.IsFalse(result.CanCreateBaseline);
        }
        [TestMethod]
        public async Task AllExistingBaselinesMissingDoesNotResetNumbering()
        {
            var result = await MetadataFixture.Service(MetadataFixture.Adapter(allDeleted: true)).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: BaselineType.Minor));
            Assert.AreEqual(0, result.Snapshot.History.Count); Assert.AreEqual(BaselineDesignationStatus.None, result.Snapshot.LatestExisting.Status);
            Assert.IsFalse(result.Snapshot.HasNoBaselineHistory); Assert.AreEqual(new BaselineVersion(3, 8), result.ExpectedVersion);
        }
        [TestMethod]
        public async Task HistoryDtosKeepUnicodeCreatorDateAndModuleScopedIdentity()
        {
            var result = await MetadataFixture.Service(MetadataFixture.Adapter()).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module()));
            var row = result.Snapshot.History.Single(); Assert.AreEqual("İnceleme\n注釈", row.Annotation); Assert.AreEqual("reviewer-1", row.Creator);
            Assert.AreEqual(DateTimeKind.Utc, row.CreationDateUtc.Value.Kind); Assert.AreEqual("2026-01-02 03:04:05Z", row.CreationDateRaw);
            Assert.IsFalse(row.Identity.NativeIdentityAvailable); Assert.IsFalse(string.IsNullOrWhiteSpace(row.BaselineKey));
            Assert.AreEqual(row.BaselineKey, new BaselineIdentity(MetadataFixture.Module("/Renamed"), row.Identity.Version).Key);
            Assert.AreNotEqual(row.BaselineKey, new BaselineIdentity(MetadataFixture.Module(id: "other-module"), row.Identity.Version).Key);
            Assert.IsNull(result.Proposal); Assert.AreEqual(BaselineExistenceStatus.NotRequested, result.Collision.Status);
        }
        [TestMethod]
        public async Task ExplicitCollisionChecksExactMajorMinorAndSuffix()
        {
            var service = MetadataFixture.Service(MetadataFixture.Adapter());
            var exists = await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), collisionTarget: new BaselineVersion(2, 4, "old")));
            Assert.IsTrue(exists.Collision.HasConfirmedCollision); Assert.IsFalse(exists.Collision.AuthorizesCreation);
            var other = await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), collisionTarget: new BaselineVersion(2, 4, "other")));
            Assert.AreEqual(BaselineExistenceStatus.AbsentOrUnreadable, other.Collision.Status); Assert.IsFalse(other.Collision.AuthorizesCreation);
        }
        [TestMethod]
        public async Task ProposalCollisionIsCheckedFreshAndMayAppearAfterHistoryRead()
        {
            var adapter = new WrappedAdapter(MetadataFixture.Adapter()) { OnCollision = (request, target) => new BaselineCollisionResult(target, BaselineExistenceStatus.Exists, "Mock race: target appeared.") };
            var result = await MetadataFixture.Service(adapter).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: BaselineType.Major));
            Assert.AreEqual(1, adapter.Reads); Assert.AreEqual(1, adapter.CollisionReads); Assert.IsTrue(result.Collision.HasConfirmedCollision); Assert.IsFalse(result.CanCreateBaseline);
        }
        [DataTestMethod, DataRow(BaselineHistoryStatus.AccessDenied), DataRow(BaselineHistoryStatus.DeniedOrNotFound), DataRow(BaselineHistoryStatus.ModuleDeleted),
            DataRow(BaselineHistoryStatus.NotFormalModule), DataRow(BaselineHistoryStatus.AlreadyOpenForWrite), DataRow(BaselineHistoryStatus.ReadFailed)]
        public async Task UnavailableModulesHaveNoInferredExpectedVersion(BaselineHistoryStatus status)
        {
            var result = await MetadataFixture.Service(MetadataFixture.Adapter(status: status)).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: BaselineType.Major));
            Assert.AreEqual(status, result.Snapshot.Status); Assert.IsFalse(result.Snapshot.HistoryComplete); Assert.AreEqual(0, result.Snapshot.History.Count);
            Assert.IsNull(result.ExpectedVersion); Assert.AreEqual(BaselineProposalStatus.Unavailable, result.Proposal.Status);
        }
        [TestMethod]
        public async Task ClosedModulesRequireExplicitReadOnlyOpenPolicy()
        {
            var service = MetadataFixture.Service(MetadataFixture.Adapter(open: false));
            var closed = await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module())); Assert.AreEqual(BaselineHistoryStatus.ClosedByPolicy, closed.Snapshot.Status);
            var opened = await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), true));
            Assert.AreEqual(BaselineHistoryStatus.Available, opened.Snapshot.Status); Assert.AreEqual(RequirementModuleOwnership.OwnedReadOnly, opened.Snapshot.Ownership);
            Assert.AreEqual(RequirementCleanupStatus.Closed, opened.Snapshot.Cleanup);
        }
        [TestMethod]
        public async Task IdentityMismatchDoesNotAttachByPathOrRetargetOnRename()
        {
            var service = MetadataFixture.Service(MetadataFixture.Adapter());
            Assert.AreEqual(BaselineHistoryStatus.IdentityMismatch, (await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module("/Renamed")))).Snapshot.Status);
            Assert.AreEqual(BaselineHistoryStatus.DeniedOrNotFound, (await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(id: "different")))).Snapshot.Status);
        }
        [TestMethod]
        public async Task PartialHistoryRetainsAuthoritativeDesignationsWithoutClaimingCompleteness()
        {
            var module = MetadataFixture.Module(); var adapter = new MockBaselineHistoryAdapter(new[] { new MockBaselineModuleState(module,
                new[] { MetadataFixture.Entry(module, 1, 0), MetadataFixture.Entry(module, 2, 4) }, new BaselineVersion(3, 7), new BaselineVersion(4, 0), new BaselineVersion(3, 8)) });
            var result = await MetadataFixture.Service(adapter).ReadAsync(new BaselineHistoryRequest(module, maxBaselines: 1));
            Assert.AreEqual(1, result.Snapshot.History.Count); Assert.IsFalse(result.Snapshot.HistoryComplete); Assert.IsFalse(result.Snapshot.HasNoBaselineHistory);
            Assert.AreEqual(new BaselineVersion(2, 4, "old"), result.Snapshot.LatestExisting.Version);
        }
        [TestMethod]
        public async Task NativeNextAdapterDoesNotGuessFromVisibleOrNumberingVersions()
        {
            using (var files = new ExecutorTestContext())
            {
                var native = new DxlBaselineHistoryAdapter(files.Executor, new BaselineMetadataScriptOptions());
                var service = MetadataFixture.Service(MetadataFixture.Adapter(), native);
                var result = await service.ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: BaselineType.Minor));
                Assert.AreEqual(BaselineProposalStatus.RequiresEnvironmentValidation, result.Proposal.Status); Assert.IsNull(result.ExpectedVersion); Assert.AreEqual(0, files.Sessions.OpenCalls);
            }
        }
        [DataTestMethod, DataRow("observation"), DataRow("module"), DataRow("type"), DataRow("suffix")]
        public async Task ForeignProposalCannotBeUsedForCollisionOrVersionDisplay(string mismatch)
        {
            var adapter = new WrappedAdapter(MetadataFixture.Adapter());
            adapter.OnProposal = (snapshot, type, suffix) => new BaselineVersionProposal(mismatch == "type" ? BaselineType.Minor : type,
                mismatch == "suffix" ? "other" : suffix, BaselineProposalStatus.Available, new BaselineVersion(4, 0, mismatch == "suffix" ? "other" : suffix),
                BaselineMetadataSource.Mock, "fixture", observationId: mismatch == "observation" ? Guid.NewGuid() : snapshot.ObservationId,
                module: mismatch == "module" ? MetadataFixture.Module(id: "other") : snapshot.Module);
            var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => MetadataFixture.Service(adapter).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), nextType: BaselineType.Major)));
            Assert.AreEqual(IntegrationErrorCode.BaselineMetadataFailed, error.Code); Assert.AreEqual(0, adapter.CollisionReads);
        }
        [TestMethod]
        public async Task CancellationAndFailuresDoNotRetryOrLogMetadataContent()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); var adapter = new WrappedAdapter(MetadataFixture.Adapter());
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => MetadataFixture.Service(adapter).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module()), cancellation.Token)); Assert.AreEqual(0, adapter.Reads);
            }
            var failing = new WrappedAdapter(MetadataFixture.Adapter()) { Fail = true }; var log = new BaselineTestLog();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => MetadataFixture.Service(failing, logger: log).ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module())));
            Assert.AreEqual(1, failing.Reads); Assert.IsFalse(string.Join(" ", log.Messages).Contains("SECRET"));
        }
        [TestMethod]
        public void DuplicateBaselineIdentityAndNonUtcDatesAreRejected()
        {
            var module = MetadataFixture.Module(); var entry = MetadataFixture.Entry(module);
            Assert.ThrowsException<ArgumentException>(() => new BaselineHistoryEntry(entry.Identity, "", "", DateTime.Now, "raw"));
            var request = new BaselineHistoryRequest(module);
            Assert.ThrowsException<ArgumentException>(() => new BaselineMetadataSnapshot(Guid.NewGuid(), request, BaselineHistoryStatus.Available,
                new[] { entry, entry }, new BaselineDesignation(BaselineDesignationStatus.Available, entry.Identity.Version, BaselineExistenceStatus.Exists),
                new BaselineDesignation(BaselineDesignationStatus.Available, entry.Identity.Version, BaselineExistenceStatus.Exists), true,
                RequirementModuleOwnership.BorrowedReadOnly, RequirementCleanupStatus.NotOwned, DateTime.UtcNow, BaselineMetadataSource.Mock, "fixture",
                new BaselineCollisionResult(null, BaselineExistenceStatus.NotRequested, "fixture")));
        }
        [TestMethod]
        public async Task WpfSessionExposesMetadataDtosWithoutChangingReadOnlyBaselineExecution()
        {
            var session = MockDoorsAdapter.CreateSession(new BaselineTestLog());
            var module = new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, "mock-bulk-native-01-01-001", "/Bulk/P01/F01/Requirement module 001");
            var result = await session.BaselineMetadata.ReadAsync(new BaselineHistoryRequest(module, nextType: BaselineType.Minor));
            Assert.AreEqual(2, result.Snapshot.History.Count); Assert.AreEqual(new BaselineVersion(2, 1), result.ExpectedVersion); Assert.IsFalse(result.CanCreateBaseline);
            Assert.IsNotNull(session.BulkBaselines); Assert.IsNotNull(session.ModuleSelection);
        }
        [TestMethod]
        public async Task DefaultDiResolvesSharedQueueAndReturnsExplicitUnverified95StatusWithoutCom()
        {
            var services = new ServiceCollection().AddDoorsIntegration();
            using (var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            {
                Assert.AreSame(DoorsStaExecutionQueue.Shared, host.GetRequiredService<IDoorsStaExecutionQueue>());
                var result = await host.GetRequiredService<IBaselineMetadataService>().ReadAsync(new BaselineHistoryRequest(MetadataFixture.Module(), target: DoorsVersion.V95));
                Assert.AreEqual(BaselineHistoryStatus.ApiNotValidated, result.Snapshot.Status); Assert.IsFalse(result.CanCreateBaseline);
            }
        }
        private sealed class WrappedAdapter : IBaselineHistoryAdapter, IBaselineNextVersionAdapter
        {
            private readonly MockBaselineHistoryAdapter source;
            internal int Reads, CollisionReads; internal bool Fail;
            internal Func<BaselineHistoryRequest, BaselineVersion, BaselineCollisionResult> OnCollision;
            internal Func<BaselineMetadataSnapshot, BaselineType, string, BaselineVersionProposal> OnProposal;
            internal WrappedAdapter(MockBaselineHistoryAdapter source) { this.source = source; }
            public Task<BaselineMetadataSnapshot> ReadAsync(BaselineHistoryRequest request, CancellationToken token)
            { Reads++; if (Fail) throw new InvalidOperationException("SECRET"); return source.ReadAsync(request, token); }
            public Task<BaselineVersionProposal> ProposeAsync(BaselineMetadataSnapshot snapshot, BaselineType type, string suffix, CancellationToken token) =>
                OnProposal == null ? source.ProposeAsync(snapshot, type, suffix, token) : Task.FromResult(OnProposal(snapshot, type, suffix));
            public Task<BaselineCollisionResult> CheckCollisionAsync(BaselineHistoryRequest request, BaselineVersion target, CancellationToken token)
            { CollisionReads++; return OnCollision == null ? source.CheckCollisionAsync(request, target, token) : Task.FromResult(OnCollision(request, target)); }
        }
    }
}
