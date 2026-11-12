using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BulkBaselineJobRunnerTests
    {
        [TestMethod]
        public async Task SequentialModulesHaveDurableIntentAndStableJobIdBeforeDispatch()
        {
            using (var f = new JobTestFixture())
            {
                var job = await f.Create(); var progress = new JobProgressObserver();
                f.Adapter.BeforeExecute = auth =>
                {
                    var persisted = f.Raw(job.JobId); var module = persisted.Modules.Single(m => m.ModuleId == auth.Item.ModuleId);
                    Assert.AreEqual(job.JobId, auth.Review.JobId); Assert.AreEqual(BulkBaselineModuleStatus.Running, module.Status);
                    Assert.AreEqual(1, module.Attempts); Assert.IsTrue(module.WriteMayHaveOccurred); Assert.IsTrue(module.AttemptId.HasValue);
                    return Task.CompletedTask;
                };
                var result = await f.Runner().RunAsync(job.JobId, progress);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, result.Status); Assert.AreEqual(3, f.Adapter.Calls); Assert.AreEqual(1, f.Adapter.MaximumConcurrent);
                Assert.AreEqual(3, result.Modules.Count(m => m.EverSucceeded)); Assert.IsTrue(result.Elapsed > TimeSpan.Zero); Assert.IsFalse(result.IsTransactional);
                Assert.IsTrue(progress.Events.Any(p => p.CurrentModule?.Status == BulkBaselineModuleStatus.Prechecking));
                Assert.IsTrue(progress.Events.Any(p => p.CurrentModule?.Status == BulkBaselineModuleStatus.Ready));
                Assert.AreEqual(3, progress.Events.Last().Succeeded); Assert.AreEqual(3, progress.Events.Last().Total);
                Assert.IsFalse(job.Modules.Any(m => m.Attempts != 0), "Original immutable snapshot was changed.");
                Assert.AreEqual(result.Revision, f.Raw(job.JobId).Revision);
            }
        }
        [TestMethod]
        public async Task RestartNeverRecreatesSucceededModules()
        {
            using (var f = new JobTestFixture())
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId);
                var adapter = new JobMockAdapter(f.Server);
                var restarted = f.Runner(new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(f.Root), new TestBaselineJournalProtector()), adapter);
                var reopened = await restarted.OpenAsync(job.JobId); var result = await restarted.RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, reopened.Status); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, result.Status);
                Assert.AreEqual(0, adapter.Calls); Assert.IsTrue(f.Server.Creates.Values.All(count => count == 1));
            }
        }
        [DataTestMethod, DataRow("before-success"), DataRow("after-success")]
        public async Task CrashAroundCompletionCheckpointRecoversExternalSuccessWithoutDuplicate(string point)
        {
            using (var f = new JobTestFixture())
            {
                var job = await f.Create(); var faults = new FaultingJobJournal(f.Journal);
                bool Hit(BulkBaselineJobRecord record) => record.Modules[0].Status == BulkBaselineModuleStatus.Succeeded;
                if (point == "before-success") faults.BeforeSave = Hit; else faults.AfterSave = Hit;
                await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner(faults).RunAsync(job.JobId));
                Assert.AreEqual(1, f.Adapter.Calls); Assert.AreEqual(1, f.Server.Creates[job.Modules[0].ModuleId]);
                var adapter = new JobMockAdapter(f.Server); var restarted = f.Runner(new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(f.Root), new TestBaselineJournalProtector()), adapter);
                var opened = await restarted.OpenAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, opened.Modules[0].Status);
                Assert.AreEqual(BulkBaselineModuleStatus.Pending, opened.Status, "A recovered first module does not complete the entire job.");
                var completed = await restarted.RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, completed.Status); Assert.AreEqual(2, adapter.Calls);
                Assert.IsTrue(f.Server.Creates.Values.All(n => n == 1));
            }
        }
        [TestMethod]
        public async Task CrashAfterRunningIntentNeverAutomaticallyRetriesAbsentTarget()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var faults = new FaultingJobJournal(f.Journal) { AfterSave = j => j.Modules[0].Status == BulkBaselineModuleStatus.Running };
                await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner(faults).RunAsync(job.JobId));
                Assert.AreEqual(0, f.Adapter.Calls); Assert.AreEqual(BulkBaselineModuleStatus.Running, f.Raw(job.JobId).Modules[0].Status);
                var restarted = f.Runner(); var opened = await restarted.OpenAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, opened.Modules[0].Status);
                await restarted.RunAsync(job.JobId); Assert.AreEqual(0, f.Adapter.Calls);
                var retried = await restarted.RetryAsync(job.JobId, job.Modules[0].ModuleId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, retried.Modules[0].Status); Assert.AreEqual(2, retried.Modules[0].Attempts);
                Assert.AreEqual(1, f.RetryConfirmation.Calls); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task FailedWriteAheadCheckpointPreventsNativeDispatch()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var faults = new FaultingJobJournal(f.Journal) { BeforeSave = j => j.Modules[0].Status == BulkBaselineModuleStatus.Running };
                await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner(faults).RunAsync(job.JobId));
                Assert.AreEqual(0, f.Adapter.Calls); Assert.AreEqual(BulkBaselineModuleStatus.Ready, f.Raw(job.JobId).Modules[0].Status);
                var result = await f.Runner().RunAsync(job.JobId); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, result.Status); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [DataTestMethod, DataRow(ExecutionPolicy.ContinueOnError, 3), DataRow(ExecutionPolicy.StopOnFirstError, 1)]
        public async Task KnownFailureRespectsPolicyAndIsNeverAutomaticallyRetried(ExecutionPolicy policy, int calls)
        {
            using (var f = new JobTestFixture())
            {
                var job = await f.Create(policy); f.Adapter.Outcome = auth => auth.Item.ModuleId == job.Modules[0].ModuleId ? BaselineWriteOutcome.NotRun : BaselineWriteOutcome.Created;
                var runner = f.Runner(); var result = await runner.RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Failed, result.Modules[0].Status); Assert.AreEqual(calls, f.Adapter.Calls);
                await runner.RunAsync(job.JobId); Assert.AreEqual(calls, f.Adapter.Calls);
                f.Adapter.Outcome = null; var retried = await runner.RetryAsync(job.JobId, job.Modules[0].ModuleId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, retried.Modules[0].Status); Assert.AreEqual(calls + 1, f.Adapter.Calls);
            }
        }
        [DataTestMethod, DataRow(ExecutionPolicy.ContinueOnError), DataRow(ExecutionPolicy.StopOnFirstError)]
        public async Task UnknownStopsBothPoliciesAndRecordsNeedsReconciliation(ExecutionPolicy policy)
        {
            using (var f = new JobTestFixture())
            {
                var job = await f.Create(policy); var progress = new JobProgressObserver(); f.Adapter.Outcome = _ => BaselineWriteOutcome.NeedsReconciliation;
                var result = await f.Runner().RunAsync(job.JobId, progress);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, result.Status); Assert.AreEqual(1, f.Adapter.Calls);
                Assert.IsTrue(progress.Events.Any(e => e.CurrentModule?.Status == BulkBaselineModuleStatus.Unknown));
                Assert.AreEqual(BulkBaselineModuleStatus.Pending, result.Modules[1].Status); Assert.IsTrue(result.Modules[0].OperationId.HasValue);
            }
        }
        [TestMethod]
        public async Task CancellationObservesActiveCallAndPersistsRequestBeforeStoppingFutureModules()
        {
            using (var f = new JobTestFixture()) using (var token = new CancellationTokenSource())
            {
                var job = await f.Create(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var progress = new JobProgressObserver();
                f.Adapter.BeforeExecute = async _ => { entered.TrySetResult(true); await release.Task; };
                var running = f.Runner().RunAsync(job.JobId, progress, token.Token);
                await entered.Task; token.Cancel();
                Assert.AreEqual(progress.CancelSeen.Task, await Task.WhenAny(progress.CancelSeen.Task, Task.Delay(5000)));
                Assert.IsFalse(running.IsCompleted); Assert.IsFalse(f.Adapter.ReceivedCancelableToken);
                var persisted = f.Raw(job.JobId); Assert.IsTrue(persisted.CancellationRequested); Assert.AreEqual(BulkBaselineModuleStatus.CancelRequested, persisted.Modules[0].Status);
                release.TrySetResult(true); var result = await running;
                Assert.AreEqual(1, f.Adapter.Calls); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, result.Modules[0].Status);
                Assert.AreEqual(2, result.Modules.Count(m => m.Status == BulkBaselineModuleStatus.CancelRequested)); Assert.AreEqual(BulkBaselineModuleStatus.CancelRequested, result.Status);
                f.Adapter.BeforeExecute = null; var resumed = await f.Runner().RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, resumed.Status); Assert.AreEqual(3, f.Adapter.Calls); Assert.IsTrue(f.Server.Creates.Values.All(n => n == 1));
            }
        }
        [DataTestMethod, DataRow(BaselineJobVerificationStatus.AbsentAuthoritatively), DataRow(BaselineJobVerificationStatus.ExistsConflicting), DataRow(BaselineJobVerificationStatus.Unknown)]
        public async Task ContradictorySucceededRecordIsStickyAndNeverRetried(BaselineJobVerificationStatus state)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId); f.Server.Targets[job.Modules[0].ModuleId] = state;
                var opened = await f.Runner().OpenAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, opened.Modules[0].Status); Assert.IsTrue(opened.Modules[0].EverSucceeded);
                await f.Runner().RetryAsync(job.JobId, job.Modules[0].ModuleId);
                Assert.AreEqual(1, f.Adapter.Calls); Assert.AreEqual(0, f.RetryConfirmation.Calls);
            }
        }
        [DataTestMethod, DataRow(BaselineJobVerificationStatus.ExistsMatching), DataRow(BaselineJobVerificationStatus.ExistsConflicting), DataRow(BaselineJobVerificationStatus.Unknown)]
        public async Task ExistingOrUnverifiableUnattemptedTargetNeverCreates(BaselineJobVerificationStatus state)
        {
            using (var f = new JobTestFixture())
            {
                var job = await f.Create(); f.Server.Targets[job.Modules[0].ModuleId] = state;
                var result = await f.Runner().RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, result.Modules[0].Status); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task RetryIsDeniedByDefaultAndRequiresNewEvidenceAfterApproval()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Adapter.Outcome = _ => BaselineWriteOutcome.NotRun; await f.Runner().RunAsync(job.JobId);
                f.RetryConfirmation.Approved = false; await f.Runner().RetryAsync(job.JobId, job.Modules[0].ModuleId); Assert.AreEqual(1, f.Adapter.Calls);
                f.RetryConfirmation.Approved = true; f.Adapter.Outcome = null;
                f.RetryConfirmation.OnConfirm = () => f.Server.Targets[job.Modules[0].ModuleId] = BaselineJobVerificationStatus.ExistsConflicting;
                var result = await f.Runner().RetryAsync(job.JobId, job.Modules[0].ModuleId);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, result.Modules[0].Status); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task RejectCreationApprovalNeverRecordsRunningOrCallsAdapter()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Confirmation.Approved = false;
                var result = await f.Runner().RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Skipped, result.Modules[0].Status); Assert.AreEqual(0, result.Modules[0].Attempts); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task FreshTargetVersionIsNeverSilentlySubstituted()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var module = f.Preflight.States[0].Module;
                f.Preflight.Metadata.Modules[module.ModuleId] = new MockBaselineModuleState(module, new[] { MetadataFixture.Entry(module) },
                    new BaselineVersion(3, 8), new BaselineVersion(4, 0), new BaselineVersion(3, 9));
                var result = await f.Runner().RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, result.Modules[0].Status); Assert.AreEqual(job.Modules[0].ExpectedVersion, result.Modules[0].ExpectedVersion); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task ProgressObserverFailureDoesNotEraseSuccessfulCheckpoint()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var result = await f.Runner().RunAsync(job.JobId, new JobProgressObserver { Throw = true });
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, result.Status); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, f.Raw(job.JobId).Modules[0].Status);
                Assert.IsFalse(string.Join(" ", f.Preflight.Log.Messages).Contains("PRIVATE"));
            }
        }
        [TestMethod]
        public async Task SameJobCannotBeOwnedByTwoRunners()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); using (f.Journal.Acquire(job.JobId))
                    await Assert.ThrowsExceptionAsync<IntegrationException>(() => f.Runner().RunAsync(job.JobId));
                Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task CancellationBeforeNativeDispatchNeverStartsFirstModule()
        {
            using (var f = new JobTestFixture(1)) using (var token = new CancellationTokenSource())
            {
                var job = await f.Create(); f.Confirmation.OnConfirm = _ => token.Cancel();
                var result = await f.Runner().RunAsync(job.JobId, cancellationToken: token.Token);
                Assert.AreEqual(BulkBaselineModuleStatus.CancelRequested, result.Status); Assert.AreEqual(0, f.Adapter.Calls); Assert.AreEqual(0, result.Modules[0].Attempts);
            }
        }
        [TestMethod]
        public async Task SecondJobCannotStartOnTheSameCoordinatorWhileFirstCallIsActive()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var second = await f.Create(); var runner = f.Runner();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Adapter.BeforeExecute = async _ => { entered.TrySetResult(true); await release.Task; };
                var running = runner.RunAsync(job.JobId); await entered.Task;
                await Assert.ThrowsExceptionAsync<IntegrationException>(() => runner.RunAsync(second.JobId));
                release.TrySetResult(true); await running; Assert.AreEqual(1, f.Adapter.Calls); Assert.AreEqual(1, f.Adapter.MaximumConcurrent);
            }
        }
        [TestMethod]
        public void DependencyInjectionKeepsRunnerIndependentAndRetryClosedByDefault()
        {
            using (var provider = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider())
            {
                Assert.IsInstanceOfType(provider.GetRequiredService<IBulkBaselineJobRunner>(), typeof(BulkBaselineJobRunner));
                Assert.AreSame(provider.GetRequiredService<IBulkBaselineJobRunner>(), provider.GetRequiredService<IBulkBaselineJobRunner>());
                Assert.IsInstanceOfType(provider.GetRequiredService<IBulkBaselineRetryConfirmation>(), typeof(RejectBulkBaselineRetryConfirmation));
                Assert.IsInstanceOfType(provider.GetRequiredService<IBulkBaselineJobJournal>(), typeof(FileBulkBaselineJobJournal));
            }
        }
    }
}
