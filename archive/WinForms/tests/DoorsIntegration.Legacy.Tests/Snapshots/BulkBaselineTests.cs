using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BulkBaselineTests
    {
        private static readonly DateTime Utc = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        private static BaselineModuleReference Module(string id = "native-id-1", string path = "/P/M") => new BaselineModuleReference("repository-1", id, path);
        private static BulkBaselineRequest Request(BaselineType type = BaselineType.Minor, string suffix = "review", string annotation = "Türkçe açıklama\n第二行") =>
            new BulkBaselineRequest(new[] { new BaselineModuleSelection(Module()) }, type, suffix: suffix, annotation: annotation);
        private static BaselineModuleMetadata Metadata(BaselineModuleReference module = null, BaselineVersion version = null,
            BaselineCheckState permission = BaselineCheckState.Passed, BaselineCheckState availableLock = BaselineCheckState.Passed,
            BaselineCheckState formal = BaselineCheckState.Passed, bool complete = true, IEnumerable<BaselineVersion> versions = null, DateTime? utc = null) =>
            new BaselineModuleMetadata(module ?? Module(), BaselineMetadataStatus.Available, version ?? new BaselineVersion(2, 7, "old"),
                formal, permission, availableLock, versions, complete, utc ?? Utc, "observation-1");
        private static BaselinePlan Plan(BulkBaselineRequest request = null, BaselineModuleMetadata metadata = null) =>
            new BaselinePlanner().Build(Guid.NewGuid(), request ?? Request(), new[] { metadata ?? Metadata() }, Utc);
        private static BulkBaselineService Service(IBaselineMetadataProvider metadata = null, IBaselineJobRepository jobs = null,
            IBaselineExecutor executor = null, IIntegrationLogger logger = null) => new BulkBaselineService(metadata ?? new Provider(),
                executor ?? new ReadOnlyBaselineExecutor(), jobs ?? new InMemoryBaselineJobRepository(), logger ?? new BaselineTestLog(),
                new BaselinePlanner(), new BaselinePreflightChecker());

        [DataTestMethod, DataRow(BaselineType.Major, 3, 0), DataRow(BaselineType.Minor, 2, 8)]
        public void StructuredVersionProposalsKeepStableIdentity(BaselineType type, int major, int minor)
        {
            var plan = Plan(Request(type)); var item = plan.Items.Single();
            Assert.AreEqual(new BaselineVersion(major, minor, "review"), item.ExpectedVersion);
            Assert.AreEqual(new BaselineVersion(2, 7, "old"), item.CurrentVersion);
            Assert.AreEqual("native-id-1", item.ModuleId); Assert.AreEqual("/P/M", item.Module.CanonicalFullPath);
            Assert.AreEqual(plan.JobId, item.JobId); Assert.IsTrue(plan.IsReadOnly); Assert.IsTrue(plan.Request.IsReadOnly);
        }
        [TestMethod]
        public void JobDefaultsAndExplicitEmptyOverridesRemainDistinct()
        {
            var request = new BulkBaselineRequest(new[] { new BaselineModuleSelection(Module()),
                new BaselineModuleSelection(Module("id-2", "/P/M2"), "", ""),
                new BaselineModuleSelection(Module("id-3", "/P/M3"), "custom", "module note") }, suffix: "job", annotation: "job note");
            var plan = new BaselinePlanner().Build(Guid.NewGuid(), request, request.Modules.Select(m => Metadata(m.Module)).ToArray(), Utc);
            CollectionAssert.AreEqual(new[] { "job", "", "custom" }, plan.Items.Select(i => i.Suffix).ToArray());
            CollectionAssert.AreEqual(new[] { "job note", "", "module note" }, plan.Items.Select(i => i.Annotation).ToArray());
        }
        [TestMethod]
        public void RequestCopiesSelectionAndRejectsDuplicateOrCrossRepositoryTargets()
        {
            var selection = new List<BaselineModuleSelection> { new BaselineModuleSelection(Module()) };
            var request = new BulkBaselineRequest(selection); selection.Clear(); Assert.AreEqual(1, request.Modules.Count);
            Assert.ThrowsException<ArgumentException>(() => new BulkBaselineRequest(new[] { new BaselineModuleSelection(Module()), new BaselineModuleSelection(Module(path: "/Renamed")) }));
            Assert.ThrowsException<ArgumentException>(() => new BulkBaselineRequest(new[] { new BaselineModuleSelection(Module()), new BaselineModuleSelection(Module("other-id")) }));
            Assert.ThrowsException<ArgumentException>(() => new BulkBaselineRequest(new[] { new BaselineModuleSelection(Module()), new BaselineModuleSelection(new BaselineModuleReference("other-repo", "other-id", "/Q/M")) }));
            Assert.ThrowsException<ArgumentException>(() => new BulkBaselineRequest(Array.Empty<BaselineModuleSelection>()));
        }
        [TestMethod]
        public void UnsupportedModesInvalidPathsAndMalformedTextAreRejected()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new BulkBaselineRequest(Request().Modules, mode: (BaselineMode)1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => Request((BaselineType)5));
            Assert.ThrowsException<ArgumentException>(() => Module(path: "/P/M/"));
            Assert.ThrowsException<IntegrationException>(() => Module(path: "/P/../M"));
            Assert.ThrowsException<ArgumentException>(() => Module(id: ""));
            Assert.ThrowsException<IntegrationException>(() => Request(suffix: "\ud800"));
            Assert.ThrowsException<ArgumentException>(() => Request(annotation: "\0"));
            Assert.ThrowsException<ArgumentException>(() => new BulkBaselineRequest(Enumerable.Range(0, 10001).Select(i =>
                new BaselineModuleSelection(Module("id-" + i, "/P/M" + i)))));
        }
        [DataTestMethod, DataRow(true), DataRow(false)]
        public void PlannerDoesNotMatchOnlyByPathOrOnlyById(bool sameId)
        {
            var item = Plan(metadata: Metadata(Module(sameId ? "native-id-1" : "other-id", sameId ? "/Renamed" : "/P/M"))).Items.Single();
            Assert.AreEqual(BaselinePlanItemStatus.Blocked, item.Status); Assert.IsNull(item.ExpectedVersion);
            Assert.AreEqual("native-id-1", item.ModuleId);
        }
        [TestMethod]
        public void NullOrDeniedMetadataCannotFabricateAVersion()
        {
            var plan = new BaselinePlanner().Build(Guid.NewGuid(), Request(), new BaselineModuleMetadata[] { null }, Utc);
            Assert.IsFalse(plan.IsReadyForPreflight); Assert.IsNull(plan.Items[0].ExpectedVersion);
            var denied = new BaselineModuleMetadata(Module(), BaselineMetadataStatus.AccessDenied, null, BaselineCheckState.Unknown,
                BaselineCheckState.Unknown, BaselineCheckState.Unknown, null, false, Utc, "denied");
            Assert.AreEqual(BaselinePlanItemStatus.Blocked, Plan(metadata: denied).Items[0].Status);
        }
        [DataTestMethod, DataRow(BaselineType.Major), DataRow(BaselineType.Minor)]
        public void VersionOverflowIsReportedPerModule(BaselineType type)
        {
            var item = Plan(Request(type), Metadata(version: new BaselineVersion(1000000, 1000000))).Items[0];
            Assert.AreEqual(BaselinePlanItemStatus.Blocked, item.Status); Assert.IsNull(item.ExpectedVersion);
        }
        [TestMethod]
        public void PreflightSuccessNeverAuthorizesAWrite()
        {
            var plan = Plan(); var result = new BaselinePreflightChecker().Check(plan, new[] { Metadata() }, Utc);
            Assert.IsTrue(result.Passed); Assert.IsFalse(result.CanExecute);
            Assert.AreSame(plan, result.Plan); Assert.AreEqual(Utc, result.CheckedAtUtc);
        }
        [DataTestMethod]
        [DataRow(BaselineCheckState.Failed, BaselineCheckState.Passed, BaselinePreflightStatus.Blocked)]
        [DataRow(BaselineCheckState.Unknown, BaselineCheckState.Passed, BaselinePreflightStatus.Unavailable)]
        [DataRow(BaselineCheckState.Passed, BaselineCheckState.Failed, BaselinePreflightStatus.Blocked)]
        [DataRow(BaselineCheckState.Passed, BaselineCheckState.Unknown, BaselinePreflightStatus.Unavailable)]
        public void PermissionAndLockFailuresRemainExplicit(BaselineCheckState permission, BaselineCheckState lockState, BaselinePreflightStatus expected)
        {
            var result = new BaselinePreflightChecker().Check(Plan(), new[] { Metadata(permission: permission, availableLock: lockState) }, Utc);
            Assert.AreEqual(expected, result.Items[0].Status); Assert.IsFalse(result.Passed); Assert.IsFalse(result.CanExecute);
        }
        [DataTestMethod, DataRow(BaselineCheckState.Failed), DataRow(BaselineCheckState.Unknown)]
        public void FormalModuleTypeMustBeConfirmed(BaselineCheckState state)
        {
            Assert.IsFalse(Plan(metadata: Metadata(formal: state)).IsReadyForPreflight);
            Assert.IsFalse(new BaselinePreflightChecker().Check(Plan(), new[] { Metadata(formal: state) }, Utc).Passed);
        }
        [TestMethod]
        public void ChangedVersionOrExactCollisionBlocksPreflight()
        {
            var plan = Plan();
            var changed = new BaselinePreflightChecker().Check(plan, new[] { Metadata(version: new BaselineVersion(2, 8)) }, Utc);
            Assert.AreEqual(BaselinePreflightStatus.Blocked, changed.Items[0].Status); StringAssert.Contains(changed.Items[0].FailureReason, "changed");
            var collision = new BaselinePreflightChecker().Check(plan, new[] { Metadata(versions: new[] { new BaselineVersion(2, 8, "review") }) }, Utc);
            Assert.AreEqual(BaselinePreflightStatus.Blocked, collision.Items[0].Status);
            Assert.IsTrue(new BaselinePreflightChecker().Check(plan, new[] { Metadata(versions: new[] { new BaselineVersion(2, 8, "other") }) }, Utc).Passed);
        }
        [TestMethod]
        public void IncompleteVersionListCannotProveAbsenceOfCollision()
        {
            var result = new BaselinePreflightChecker().Check(Plan(), new[] { Metadata(complete: false) }, Utc);
            Assert.AreEqual(BaselinePreflightStatus.Unavailable, result.Items[0].Status);
        }
        [DataTestMethod, DataRow(-1), DataRow(1), DataRow(-301)]
        public void StaleOrFuturePreflightMetadataIsNotAccepted(int seconds)
        {
            var result = new BaselinePreflightChecker().Check(Plan(), new[] { Metadata(utc: Utc.AddSeconds(seconds)) }, Utc);
            Assert.AreEqual(BaselinePreflightStatus.Unavailable, result.Items[0].Status);
        }
        [TestMethod]
        public void MetadataCannotMoveOrReplaceAPlannedModule()
        {
            var result = new BaselinePreflightChecker().Check(Plan(), new[] { Metadata(Module(path: "/Moved/M")) }, Utc);
            Assert.AreEqual(BaselinePreflightStatus.Blocked, result.Items[0].Status);
        }
        [TestMethod]
        public void UtcAndVersionListValidationAreEnforced()
        {
            Assert.ThrowsException<ArgumentException>(() => Metadata(utc: DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified)));
            Assert.ThrowsException<ArgumentException>(() => Metadata(versions: new[] { new BaselineVersion(1, 0), new BaselineVersion(1, 0) }));
        }
        [TestMethod]
        public async Task CompleteJobLifecycleIsReadOnlyAndRepeatedPreviewDoesNotDispatchAgain()
        {
            var provider = new Provider(); var executor = new Executor(); var repository = new InMemoryBaselineJobRepository();
            var log = new BaselineTestLog(); var service = Service(provider, repository, executor, log);
            var planned = await service.CreatePlanAsync(Request()); Assert.AreEqual(0L, planned.Revision);
            var checkedJob = await service.PreflightAsync(planned.JobId); Assert.IsTrue(checkedJob.Preflight.Passed);
            var preview = await service.PreviewExecutionAsync(planned.JobId);
            Assert.AreEqual(BaselineJobStatus.Previewed, preview.Status); Assert.AreEqual(2L, preview.Revision);
            Assert.AreEqual(BaselineExecutionStatus.NotRun, preview.Execution.Status); Assert.IsFalse(preview.Execution.WriteAttempted);
            Assert.IsNull(preview.Execution.Modules[0].CreatedBaselineId); Assert.IsTrue(preview.Execution.IsPreview);
            Assert.AreEqual(1, executor.Calls); Assert.AreEqual(2, provider.Calls);
            Assert.AreSame(preview, await service.PreviewExecutionAsync(planned.JobId)); Assert.AreEqual(1, executor.Calls);
            Assert.IsNull(planned.Preflight); Assert.IsNull(checkedJob.Execution); Assert.AreEqual(planned.CreatedAtUtc, preview.CreatedAtUtc);
            Assert.IsTrue(preview.ExecutionCompletedAtUtc >= preview.ExecutionStartedAtUtc);
            Assert.AreEqual("Türkçe açıklama\n第二行", preview.Execution.Modules[0].Annotation);
            Assert.IsFalse(string.Join(" ", log.Messages).Contains("Türkçe"));
            // A new observation clears the old preview; it never silently reuses a stale result.
            var rechecked = await service.PreflightAsync(planned.JobId); Assert.IsNull(rechecked.Execution);
            Assert.AreEqual(3L, rechecked.Revision);
        }
        [TestMethod]
        public async Task DefaultCompositionReturnsNotRunWithoutComActivation()
        {
            var services = new ServiceCollection().AddDoorsIntegration();
            using (var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            {
                Assert.IsInstanceOfType(host.GetRequiredService<IBaselineMetadataProvider>(), typeof(UnavailableBaselineMetadataProvider));
                var service = host.GetRequiredService<IBulkBaselineService>(); var planned = await service.CreatePlanAsync(Request());
                Assert.AreEqual(BaselinePlanItemStatus.Unavailable, planned.Plan.Items[0].Status);
                Assert.AreSame(planned, await host.GetRequiredService<IBulkBaselineService>().GetJobAsync(planned.JobId));
                var checkedJob = await service.PreflightAsync(planned.JobId); Assert.IsFalse(checkedJob.Preflight.Passed);
                var preview = await service.PreviewExecutionAsync(planned.JobId);
                Assert.IsFalse(preview.Execution.WriteAttempted); StringAssert.Contains(preview.Execution.Modules[0].FailureReason, "NOT_RUN");
            }
        }
        [TestMethod]
        public void DesktopDiAndHostOverridesResolveWithoutReplacingExistingPorts()
        {
            var metadata = new Provider(); var repository = new InMemoryBaselineJobRepository(); var executor = new Executor();
            var services = new ServiceCollection(); services.AddSingleton<IBaselineMetadataProvider>(metadata);
            services.AddSingleton<IBaselineJobRepository>(repository); services.AddSingleton<IBaselineExecutor>(executor);
            DoorsIntegration.Desktop.ServiceCollectionExtensions.AddDoorsDesktop(services);
            DoorsIntegration.Desktop.ServiceCollectionExtensions.AddDoorsDesktop(services);
            using (var host = services.BuildServiceProvider())
            {
                Assert.AreSame(metadata, host.GetRequiredService<IBaselineMetadataProvider>());
                Assert.AreSame(repository, host.GetRequiredService<IBaselineJobRepository>());
                Assert.AreSame(executor, host.GetRequiredService<IBaselineExecutor>());
                Assert.IsNotNull(host.GetRequiredService<IBulkBaselineService>());
                Assert.IsNotNull(host.GetRequiredService<IBaselineComparisonService>());
                Assert.AreEqual(1, services.Count(d => d.ServiceType == typeof(IBulkBaselineService)));
            }
        }
        [TestMethod]
        public async Task MockWpfSessionProvidesExplicitSimulatedMetadata()
        {
            var session = MockDoorsAdapter.CreateSession(new BaselineTestLog());
            var module = new BaselineModuleReference(MockDoorsAdapter.BaselineRepositoryId, "mock-qualified:" + MockDoorsAdapter.ModulePath, MockDoorsAdapter.ModulePath);
            var job = await session.BulkBaselines.CreatePlanAsync(new BulkBaselineRequest(new[] { new BaselineModuleSelection(module) }));
            Assert.AreEqual(new BaselineVersion(1, 3), job.Plan.Items[0].ExpectedVersion);
            Assert.IsTrue((await session.BulkBaselines.PreflightAsync(job.JobId)).Preflight.Passed);
            Assert.IsFalse((await session.BulkBaselines.PreviewExecutionAsync(job.JobId)).Execution.WriteAttempted);
            Assert.IsNotNull(session.Baselines); Assert.IsNotNull(session.Requirements); Assert.IsNotNull(session.Links);
        }
        [TestMethod]
        public async Task RealWpfSessionDoesNotInventBaselineMetadata()
        {
            var session = new ClientSessionFactory().Create(new ClientProfile(ClientMode.RealDoors, DoorsVersion.V97, DoorsConnectionMode.AttachExisting), new BaselineTestLog());
            var job = await session.BulkBaselines.CreatePlanAsync(Request());
            Assert.AreEqual(BaselinePlanItemStatus.Unavailable, job.Plan.Items[0].Status);
            Assert.IsNotNull(session.Baselines); await session.DrainAsync();
        }
        [TestMethod]
        public async Task MetadataFailureIsPerModuleSanitizedAndNeverRetried()
        {
            var provider = new Provider { OnRead = (module, call) => call == 1 ? throw new InvalidOperationException("SECRET_CONTENT") : Metadata(module, utc: DateTime.UtcNow) };
            var log = new BaselineTestLog(); var service = Service(provider, logger: log);
            var request = new BulkBaselineRequest(new[] { new BaselineModuleSelection(Module()), new BaselineModuleSelection(Module("id-2", "/P/M2")) });
            var job = await service.CreatePlanAsync(request);
            Assert.AreEqual(2, provider.Calls); Assert.AreEqual(BaselinePlanItemStatus.Unavailable, job.Plan.Items[0].Status);
            Assert.AreEqual(BaselinePlanItemStatus.Planned, job.Plan.Items[1].Status);
            Assert.IsFalse(string.Join(" ", log.Messages).Contains("SECRET_CONTENT"));
            Assert.IsFalse(job.Plan.Items[0].FailureReason.Contains("SECRET_CONTENT"));
        }
        [TestMethod]
        public async Task CancellationDoesNotPublishPartialJobOrCallTheExecutor()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var provider = new Provider { OnRead = (module, call) => { cancellation.Cancel(); return Metadata(module, utc: DateTime.UtcNow); } };
                var repository = new Repository(); var executor = new Executor(); var service = Service(provider, repository, executor);
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => service.CreatePlanAsync(Request(), cancellation.Token));
                Assert.AreEqual(0, repository.Adds); Assert.AreEqual(0, executor.Calls);
            }
        }
        [TestMethod]
        public async Task MissingPreflightOrJobDoesNotDispatchExecutor()
        {
            var executor = new Executor(); var service = Service(executor: executor); var job = await service.CreatePlanAsync(Request());
            await Assert.ThrowsExceptionAsync<IntegrationException>(() => service.PreviewExecutionAsync(job.JobId));
            await Assert.ThrowsExceptionAsync<IntegrationException>(() => service.GetJobAsync(Guid.NewGuid()));
            Assert.AreEqual(0, executor.Calls);
        }
        [TestMethod]
        public async Task BoundedRepositoryRejectsDuplicatesAndNeverEvicts()
        {
            var repository = new InMemoryBaselineJobRepository(1); var service = Service(jobs: repository);
            var job = await service.CreatePlanAsync(Request());
            await Assert.ThrowsExceptionAsync<IntegrationException>(() => repository.AddAsync(job));
            await Assert.ThrowsExceptionAsync<IntegrationException>(() => service.CreatePlanAsync(Request()));
            Assert.AreSame(job, await repository.GetAsync(job.JobId));
        }
        [TestMethod]
        public async Task ConcurrentPreflightHasOneWinnerAndNoAutomaticRetry()
        {
            var provider = new BlockingProvider(); var repository = new InMemoryBaselineJobRepository(); var service = Service(provider, repository);
            var job = await service.CreatePlanAsync(Request()); provider.Block = true;
            var one = service.PreflightAsync(job.JobId); var two = service.PreflightAsync(job.JobId);
            await provider.BothStarted.Task; provider.Release.SetResult(true);
            var completed = await Task.WhenAll(Observe(one), Observe(two));
            Assert.AreEqual(1, completed.Count(success => success));
            Assert.AreEqual(1L, (await repository.GetAsync(job.JobId)).Revision); Assert.AreEqual(3, provider.Calls);
        }
        [TestMethod]
        public async Task FailedRepositoryCommitDoesNotRetryOrPublishSuccess()
        {
            var repository = new Repository { RejectUpdates = true }; var service = Service(jobs: repository);
            var job = await service.CreatePlanAsync(Request());
            var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => service.PreflightAsync(job.JobId));
            Assert.AreEqual(IntegrationErrorCode.BulkBaselineFailed, error.Code); Assert.AreEqual(1, repository.Updates);
            Assert.IsNull((await service.GetJobAsync(job.JobId)).Preflight);
        }
        [TestMethod]
        public async Task ExecutorFailureOrForeignPlanIsNotCommittedOrRetried()
        {
            var executor = new Executor { Throw = true }; var service = Service(executor: executor);
            var job = await service.CreatePlanAsync(Request()); await service.PreflightAsync(job.JobId);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.PreviewExecutionAsync(job.JobId));
            Assert.AreEqual(1, executor.Calls); Assert.IsNull((await service.GetJobAsync(job.JobId)).Execution);
            executor.Throw = false; executor.Foreign = true;
            await Assert.ThrowsExceptionAsync<IntegrationException>(() => service.PreviewExecutionAsync(job.JobId));
            Assert.IsNull((await service.GetJobAsync(job.JobId)).Execution);
        }
        private static async Task<bool> Observe(Task<BaselineJob> task)
        { try { await task; return true; } catch (IntegrationException ex) { Assert.AreEqual(IntegrationErrorCode.BulkBaselineFailed, ex.Code); return false; } }

        private class Provider : IBaselineMetadataProvider
        {
            internal int Calls;
            internal Func<BaselineModuleReference, int, BaselineModuleMetadata> OnRead;
            public virtual Task<BaselineModuleMetadata> ReadAsync(BaselineModuleReference module, CancellationToken cancellationToken = default(CancellationToken))
            { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(OnRead == null ? Metadata(module, utc: DateTime.UtcNow) : OnRead(module, Calls)); }
        }
        private sealed class BlockingProvider : Provider
        {
            internal bool Block;
            internal readonly TaskCompletionSource<bool> BothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task<BaselineModuleMetadata> ReadAsync(BaselineModuleReference module, CancellationToken cancellationToken = default(CancellationToken))
            {
                var call = Interlocked.Increment(ref Calls);
                if (Block) { if (call == 3) BothStarted.TrySetResult(true); await Release.Task; }
                cancellationToken.ThrowIfCancellationRequested(); return Metadata(module, utc: DateTime.UtcNow);
            }
        }
        private sealed class Executor : IBaselineExecutor
        {
            internal int Calls; internal bool Throw; internal bool Foreign;
            public Task<BaselineExecutionResult> PreviewAsync(BaselinePlan plan, BaselinePreflightResult preflight, CancellationToken cancellationToken = default(CancellationToken))
            {
                Calls++; if (Throw) throw new InvalidOperationException("Preview failed.");
                if (Foreign) { plan = Plan(); preflight = new BaselinePreflightChecker().Check(plan, new[] { Metadata() }, Utc); }
                return Task.FromResult(BaselineExecutionResult.Preview(plan, preflight, DateTime.UtcNow, DateTime.UtcNow));
            }
        }
        private sealed class Repository : IBaselineJobRepository
        {
            private readonly InMemoryBaselineJobRepository inner = new InMemoryBaselineJobRepository();
            internal int Adds; internal int Updates; internal bool RejectUpdates;
            public Task AddAsync(BaselineJob job, CancellationToken cancellationToken = default(CancellationToken)) { Adds++; return inner.AddAsync(job, cancellationToken); }
            public Task<BaselineJob> GetAsync(Guid id, CancellationToken cancellationToken = default(CancellationToken)) => inner.GetAsync(id, cancellationToken);
            public Task<bool> TryUpdateAsync(BaselineJob job, long expectedRevision, CancellationToken cancellationToken = default(CancellationToken))
            { Updates++; return RejectUpdates ? Task.FromResult(false) : inner.TryUpdateAsync(job, expectedRevision, cancellationToken); }
        }
    }
}
