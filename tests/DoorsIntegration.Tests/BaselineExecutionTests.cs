using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class BaselineExecutionTestConfirmation : IBaselineExecutionConfirmation
    {
        internal bool Approved;
        internal int Calls;
        internal Action<BaselineExecutionReview> OnConfirm;
        public Task<bool> ConfirmAsync(BaselineExecutionReview review, CancellationToken token)
        { Calls++; OnConfirm?.Invoke(review); token.ThrowIfCancellationRequested(); return Task.FromResult(Approved); }
    }
    internal sealed class BaselineExecutionTestAdapter : IBaselineExecutionAdapter
    {
        public BaselineMetadataSource Source { get; set; } = BaselineMetadataSource.Mock;
        internal int Calls;
        internal Action<BaselineExecutionAuthorization, int> OnExecute;
        internal BaselineWriteOutcome Outcome = BaselineWriteOutcome.Created;
        public Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization auth, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.IsTrue(auth.TryBeginWrite()); Assert.IsFalse(auth.TryBeginWrite());
            Calls++; OnExecute?.Invoke(auth, Calls); var now = DateTime.UtcNow;
            return Task.FromResult(new BaselineModuleExecutionResult(auth.Review.JobId, auth.Item, Outcome, Source,
                Outcome == BaselineWriteOutcome.Created || Outcome == BaselineWriteOutcome.NeedsReconciliation, "Unit-test adapter outcome.", now, now));
        }
    }
    [TestClass]
    public class BaselineExecutionTests
    {
        [DataTestMethod, DataRow(BaselineType.Major, 4, 0), DataRow(BaselineType.Minor, 3, 8)]
        public async Task ExplicitApprovalCreatesOnlyInMockAndPreservesVersionsAndInputs(BaselineType type, int major, int minor)
        {
            var f = new PreflightTestFixture(2); var confirmation = new BaselineExecutionTestConfirmation { Approved = true };
            var adapter = new MockBaselineExecutionAdapter(); var service = new BaselineExecutionService(f.Service, adapter, confirmation, f.Clock, f.Log);
            var plan = await f.Service.CreatePlanAsync(f.Request(type: type, suffix: "SRR", annotation: "İnceleme\n注釈"));
            var report = await service.ExecuteAsync(plan);
            Assert.AreEqual(1, confirmation.Calls); Assert.AreEqual(2, adapter.Attempts);
            foreach (var item in report.Items)
            {
                Assert.AreEqual(BaselineWriteOutcome.Created, item.Outcome); Assert.AreEqual(BaselineMetadataSource.Mock, item.Source);
                Assert.AreEqual(new BaselineVersion(major, minor, "SRR"), item.ExpectedVersion); Assert.AreEqual("İnceleme\n注釈", item.Annotation);
                Assert.IsFalse(item.AutomaticRetryAllowed); Assert.IsTrue(item.CompletedAtUtc >= item.StartedAtUtc);
            }
            Assert.IsFalse(report.NeedsReconciliation); Assert.IsFalse(report.AutomaticRetryAllowed);
        }
        [TestMethod]
        public async Task DefaultConfirmationRejectsAndNeverCallsAdapter()
        {
            var f = new PreflightTestFixture(); var adapter = new BaselineExecutionTestAdapter();
            var report = await new BaselineExecutionService(f.Service, adapter, new RejectBaselineExecutionConfirmation(), f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request()));
            Assert.AreEqual(BaselineWriteOutcome.Declined, report.Items.Single().Outcome); Assert.AreEqual(0, adapter.Calls);
        }
        [TestMethod]
        public async Task AllBlockedNeverAsksForWriteApproval()
        {
            var f = new PreflightTestFixture(); f.States[0].ModifyAccess = BaselineCheckState.Failed;
            var confirmation = new BaselineExecutionTestConfirmation { Approved = true }; var adapter = new BaselineExecutionTestAdapter();
            var report = await new BaselineExecutionService(f.Service, adapter, confirmation, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request()));
            Assert.AreEqual(BaselineWriteOutcome.Blocked, report.Items.Single().Outcome); Assert.AreEqual(0, confirmation.Calls); Assert.AreEqual(0, adapter.Calls);
        }
        [DataTestMethod, DataRow("lock"), DataRow("dirty"), DataRow("session"), DataRow("expiry")]
        public async Task ConfirmationNeverBypassesPostApprovalPreflight(string change)
        {
            var f = new PreflightTestFixture(); var adapter = new BaselineExecutionTestAdapter();
            var confirmation = new BaselineExecutionTestConfirmation { Approved = true, OnConfirm = _ =>
            {
                if (change == "lock") PreflightTestFixture.LockOther(f.States[0]);
                if (change == "dirty") f.States[0].UnsavedChanges = BaselineUnsavedState.CurrentSession;
                if (change == "session") f.States[0].SessionId = "changed";
                if (change == "expiry") f.Clock.Offset = TimeSpan.FromHours(1);
            } };
            var report = await new BaselineExecutionService(f.Service, adapter, confirmation, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request()));
            Assert.AreEqual(BaselineWriteOutcome.Blocked, report.Items.Single().Outcome); Assert.AreEqual(0, adapter.Calls);
        }
        [TestMethod]
        public async Task MockEvidenceCannotDispatchToRealAdapter()
        {
            var f = new PreflightTestFixture(); var adapter = new BaselineExecutionTestAdapter { Source = BaselineMetadataSource.NativeDxl };
            var report = await new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request()));
            Assert.AreEqual(BaselineWriteOutcome.Blocked, report.Items.Single().Outcome); Assert.AreEqual(0, adapter.Calls);
        }
        [TestMethod]
        public async Task AUsedPlanCannotBeReplayedEvenAfterFailure()
        {
            var f = new PreflightTestFixture(); var adapter = new BaselineExecutionTestAdapter { Outcome = BaselineWriteOutcome.Blocked };
            var service = new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log);
            var plan = await f.Service.CreatePlanAsync(f.Request()); await service.ExecuteAsync(plan);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ExecuteAsync(plan)); Assert.AreEqual(1, adapter.Calls);
        }
        [DataTestMethod, DataRow(ExecutionPolicy.ContinueOnError), DataRow(ExecutionPolicy.StopOnFirstError)]
        public async Task UnknownStopsEveryPolicyWithoutRetry(ExecutionPolicy policy)
        {
            var f = new PreflightTestFixture(3); var adapter = new BaselineExecutionTestAdapter { Outcome = BaselineWriteOutcome.NeedsReconciliation };
            var report = await new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request(policy)));
            Assert.IsTrue(report.NeedsReconciliation); Assert.AreEqual(1, adapter.Calls); Assert.AreEqual(2, report.Items.Count(i => i.Outcome == BaselineWriteOutcome.NotRun));
        }
        [DataTestMethod, DataRow(ExecutionPolicy.ContinueOnError, 3), DataRow(ExecutionPolicy.StopOnFirstError, 1)]
        public async Task KnownBlockedResultsRespectExecutionPolicy(ExecutionPolicy policy, int calls)
        {
            var f = new PreflightTestFixture(3); var adapter = new BaselineExecutionTestAdapter { Outcome = BaselineWriteOutcome.Blocked };
            await new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request(policy)));
            Assert.AreEqual(calls, adapter.Calls);
        }
        [TestMethod]
        public async Task SuccessfulEarlierWriteDoesNotInvalidateRemainingStopOnErrorItems()
        {
            var f = new PreflightTestFixture(3); var adapter = new BaselineExecutionTestAdapter { OnExecute = (auth, count) => f.States[count - 1].ContentRevision = "after-create" };
            var report = await new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request(ExecutionPolicy.StopOnFirstError)));
            Assert.AreEqual(3, adapter.Calls); Assert.IsTrue(report.Items.All(i => i.Outcome == BaselineWriteOutcome.Created));
        }
        [TestMethod]
        public async Task CancellationAfterFirstWritePreservesCompletedResultAndSkipsRemainder()
        {
            using (var token = new CancellationTokenSource())
            {
                var f = new PreflightTestFixture(3); var adapter = new BaselineExecutionTestAdapter { OnExecute = (_, count) => { if (count == 1) token.Cancel(); } };
                var report = await new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request()), token.Token);
                Assert.AreEqual(1, adapter.Calls); Assert.AreEqual(BaselineWriteOutcome.Created, report.Items[0].Outcome); Assert.AreEqual(2, report.Items.Count(i => i.Outcome == BaselineWriteOutcome.NotRun));
            }
        }
        [TestMethod]
        public async Task PartialAdapterFailurePreservesEarlierSuccessAndStops()
        {
            var f = new PreflightTestFixture(3); var adapter = new BaselineExecutionTestAdapter { OnExecute = (_, count) => { if (count == 2) throw new InvalidOperationException("PRIVATE"); } };
            var report = await new BaselineExecutionService(f.Service, adapter, new BaselineExecutionTestConfirmation { Approved = true }, f.Clock, f.Log).ExecuteAsync(await f.Service.CreatePlanAsync(f.Request()));
            Assert.AreEqual(2, adapter.Calls); Assert.AreEqual(BaselineWriteOutcome.Created, report.Items[0].Outcome); Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, report.Items[1].Outcome); Assert.AreEqual(BaselineWriteOutcome.NotRun, report.Items[2].Outcome);
            Assert.IsFalse(string.Join(" ", f.Log.Messages).Contains("PRIVATE"));
        }
        [TestMethod]
        public void ProductionDiKeepsExplicitApprovalAndCompatibilityClosedByDefault()
        {
            using (var provider = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider())
            {
                Assert.IsInstanceOfType(provider.GetRequiredService<IBaselineExecutionService>(), typeof(BaselineExecutionService));
                Assert.IsInstanceOfType(provider.GetRequiredService<IBaselineExecutionConfirmation>(), typeof(RejectBaselineExecutionConfirmation));
                Assert.IsInstanceOfType(provider.GetRequiredService<IBaselineExecutionCompatibility>(), typeof(UnvalidatedBaselineExecutionCompatibility));
                Assert.AreSame(provider.GetRequiredService<IBaselineExecutionService>(), provider.GetRequiredService<IBaselineExecutionService>());
                Assert.IsTrue(((DxlExecutor)provider.GetRequiredService<IDxlExecutor>()).UsesStaQueue);
            }
        }
    }
}
