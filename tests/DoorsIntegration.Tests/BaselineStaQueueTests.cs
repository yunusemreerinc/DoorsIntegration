using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, DoNotParallelize]
    public class BaselineStaQueueTests
    {
        private static DxlExecutor Queued(ExecutorTestContext f) => new DxlExecutor(f.Sessions, new Utf8DxlScriptLoader(), f.Workspaces, f.Log, f.Gate,
            new NoDxlRunLimitAutomation(), new DxlWatchdogSettings(), DoorsStaExecutionQueue.Shared);
        [TestMethod]
        public async Task MetadataAndReviewedMockCreationUseTheSamePersistentSta()
        {
            using (var metadata = new BaselineMetadataTransport(true)) using (var creation = new ModuleCreationTestContext())
            {
                await metadata.Read(); int thread = metadata.Files.Sessions.OpenThread;
                var executor = Queued(creation.Transport); var gateway = new DxlModuleCreationGateway(executor, new ModuleCreationScriptOptions(creation.Transport.SourcePath), creation.Transport.Log);
                var service = new ModuleService(new RecordingHierarchyReader(), gateway, creation.Confirmation); creation.Confirmation.Approved = true;
                var preview = await service.PreviewFormalModuleCreationAsync(creation.Request);
                var result = await service.CreateFormalModuleAsync(preview);
                Assert.AreEqual(ModuleCreationOutcome.Created, result.Outcome); Assert.AreEqual(1, creation.Confirmation.Calls);
                Assert.AreEqual(thread, creation.Transport.Sessions.OpenThread); Assert.IsTrue(creation.Transport.Sessions.Session.ThreadIds.All(id => id == thread));
                Assert.AreEqual(ApartmentState.STA, creation.Transport.Sessions.OpenApartment);
            }
        }
        [TestMethod]
        public async Task QueuedCallsNeverOverlapAndCanceledPendingCallNeverOpensCom()
        {
            using (var first = new ExecutorTestContext()) using (var second = new ExecutorTestContext()) using (var token = new CancellationTokenSource())
            {
                first.Sessions.Session.BlockRun = true; var firstExecutor = Queued(first); var secondExecutor = Queued(second);
                var running = firstExecutor.Start(new DxlExecutionRequest(first.SourcePath));
                Assert.IsTrue(first.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                var pending = secondExecutor.Start(new DxlExecutionRequest(second.SourcePath)); var wait = pending.WaitAsync(token.Token);
                Assert.AreEqual(0, second.Sessions.OpenCalls); token.Cancel();
                await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => wait);
                first.Sessions.Session.ReleaseRun.Set(); using (await running.Completion) { }
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await pending.Completion);
                Assert.AreEqual(0, second.Sessions.OpenCalls); Assert.IsFalse(second.Gate.IsBusy);
            }
        }
        [TestMethod]
        public async Task AFailedQueuedOperationCannotStrandTheQueueOrLease()
        {
            using (var first = new ExecutorTestContext()) using (var second = new ExecutorTestContext())
            {
                first.Sessions.Session.RunFailure = new InvalidOperationException("SECRET"); var one = Queued(first); var two = Queued(second);
                await Assert.ThrowsExceptionAsync<IntegrationException>(() => one.ExecuteAsync(new DxlExecutionRequest(first.SourcePath)));
                using (await two.ExecuteAsync(new DxlExecutionRequest(second.SourcePath))) { }
                Assert.AreEqual(first.Sessions.OpenThread, second.Sessions.OpenThread); Assert.IsFalse(first.Gate.IsBusy); Assert.IsFalse(second.Gate.IsBusy);
            }
        }
    }
}
