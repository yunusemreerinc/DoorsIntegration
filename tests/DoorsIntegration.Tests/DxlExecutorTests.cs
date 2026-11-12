using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DxlExecutorTests
    {
        [DataTestMethod]
        [DataRow(DxlInvocationMethod.RunString)]
        [DataRow(DxlInvocationMethod.RunFile)]
        public async Task BothInvocationMethodsLoadExternalScriptAndOwnTheWholeStaLifecycle(DxlInvocationMethod method)
        {
            using (var context = new ExecutorTestContext())
            {
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, method));
                using (var result = await operation.Completion)
                {
                    Assert.AreEqual("sample-result", result.Text);
                    Assert.AreEqual(DoorsConnectionMode.AttachExisting, context.Sessions.Mode);
                    Assert.AreEqual(ApartmentState.STA, context.Sessions.OpenApartment);
                    Assert.IsTrue(context.Sessions.Session.ThreadIds.All(id => id == context.Sessions.OpenThread));
                    CollectionAssert.AreEqual(new[] { "SetResult", method == DxlInvocationMethod.RunString ? "runStr" : "runFile", "ReadResult", "Dispose" }, context.Sessions.Session.Calls);
                    Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
                    Assert.IsFalse(context.Executor.IsBusy);
                    StringAssert.Contains(context.Sessions.Session.Script, "oleSetResult(\"sample-result\")");
                    if (method == DxlInvocationMethod.RunFile) Assert.AreNotEqual(context.SourcePath, context.Sessions.Session.FilePath);
                }
            }
        }

        [TestMethod]
        public async Task LargeResultsRemainFilesUntilResultIsDisposed()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.FileResult = true;
                context.Sessions.Session.OnRun = session =>
                {
                    var path = context.ResultPath(context.Executor.ActiveOperation);
                    using (var output = File.Create(path)) { output.WriteByte(42); output.SetLength(8 * 1024 * 1024); }
                };
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, resultMode: DxlResultMode.File));
                var result = await operation.Completion;
                Assert.IsNull(result.Text);
                Assert.AreEqual(8L * 1024 * 1024, result.ResultFileBytes);
                Assert.IsTrue(File.Exists(result.ResultFilePath));
                using (var stream = result.OpenResultStream()) Assert.AreEqual(42, stream.ReadByte());
                Assert.ThrowsException<IOException>(() => { using (File.OpenWrite(result.ResultFilePath)) { } });
                result.Dispose();
                result.Dispose();
                Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
                Assert.ThrowsException<ObjectDisposedException>(() => result.OpenResultStream());
            }
        }

        [TestMethod]
        public async Task MissingResultFileIsAnErrorAfterFileAcknowledgement()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.FileResult = true;
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, resultMode: DxlResultMode.File));
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await operation.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlResultFileFailed, failure.Code);
                Assert.IsTrue(context.Sessions.Session.Disposed);
                Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
            }
        }

        [TestMethod]
        public async Task OversizedResultsAreRejectedAndCleanedUp()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.FileResult = true;
                context.Sessions.Session.OnRun = session => File.WriteAllBytes(context.ResultPath(context.Executor.ActiveOperation), new byte[128]);
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, resultMode: DxlResultMode.File, maxResultFileBytes: 64));
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await operation.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlResultTooLarge, failure.Code);
                Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
            }
        }

        [TestMethod]
        public async Task InlineSizeLimitAndStaleResponseAreDetected()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.Payload = "too-large";
                var first = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, maxInlineResultChars: 2));
                var sizeFailure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await first.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlResultTooLarge, sizeFailure.Code);
                context.Sessions.Session.ResponseOverride = "stale Result value";
                var second = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                var staleFailure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await second.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, staleFailure.Code);
            }
        }

        [TestMethod]
        public async Task ScriptErrorsAreAvailableExplicitlyWithoutLeakingIntoLogs()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.OnRun = session => session.ResponseOverride = session.Prefix + "ERROR|PRIVATE_REQUIREMENT";
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                var failure = await Assert.ThrowsExceptionAsync<DxlScriptException>(async () => await operation.Completion);
                Assert.AreEqual("PRIVATE_REQUIREMENT", failure.ScriptError);
                Assert.IsFalse(failure.Message.Contains("PRIVATE_REQUIREMENT"));
                Assert.IsFalse(string.Join(" ", context.Log.Entries).Contains("PRIVATE_REQUIREMENT"));
            }
        }

        [TestMethod]
        public async Task CleanupFailureDoesNotReplacePrimaryFailure()
        {
            using (var context = new ExecutorTestContext())
            {
                var original = new IntegrationException(IntegrationErrorCode.ComAccessDenied, "controlled failure");
                context.Sessions.Session.RunFailure = original;
                context.Sessions.Session.DisposeFailure = new IntegrationException(IntegrationErrorCode.ComFailure, "cleanup failure");
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await operation.Completion);
                Assert.AreSame(original, failure);
                Assert.IsFalse(context.Executor.IsBusy);
                Assert.IsTrue(context.Log.Entries.Any(entry => entry.StartsWith("DxlCleanupFailed:", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public async Task ComReleaseFailureAfterAcknowledgedResultKeepsTheResult()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.DisposeFailure = new IntegrationException(IntegrationErrorCode.ComFailure, "cleanup failure");
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                using (var result = await operation.Completion)
                {
                    Assert.AreEqual("sample-result", result.Text);
                    Assert.IsTrue(context.Sessions.Session.Disposed);
                    Assert.IsFalse(context.Executor.IsBusy);
                    Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
                    Assert.IsTrue(context.Log.Entries.Any(entry => entry.StartsWith("DxlCleanupFailed:", StringComparison.Ordinal) &&
                        entry.Contains("ResultAcknowledged=True")));
                    Assert.IsFalse(context.Log.Entries.Any(entry => entry.StartsWith("DxlFailed:", StringComparison.Ordinal)));
                }
            }
        }

        [DataTestMethod]
        [DataRow(DxlInvocationMethod.RunString)]
        [DataRow(DxlInvocationMethod.RunFile)]
        public async Task WorkspaceCleanupFailureAfterAcknowledgedResultKeepsTheResult(DxlInvocationMethod method)
        {
            using (var context = new ExecutorTestContext())
            {
                // A script-created file makes the non-recursive workspace delete fail after a successful run.
                context.Sessions.Session.OnRun = session =>
                    File.WriteAllText(Path.Combine(context.Executor.ActiveOperation.WorkspaceDirectory, "script-created.tmp"), "x");
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, method));
                using (var result = await operation.Completion)
                {
                    Assert.AreEqual("sample-result", result.Text);
                    Assert.IsFalse(context.Executor.IsBusy);
                    Assert.IsTrue(Directory.Exists(operation.WorkspaceDirectory));
                    Assert.IsTrue(File.Exists(Path.Combine(operation.WorkspaceDirectory, "script-created.tmp")));
                    Assert.IsTrue(context.Log.Entries.Any(entry => entry.StartsWith("DxlCleanupFailed:", StringComparison.Ordinal) &&
                        entry.Contains("ResultAcknowledged=True")));
                    Assert.IsFalse(context.Log.Entries.Any(entry => entry.StartsWith("DxlFailed:", StringComparison.Ordinal)));
                }
            }
        }

        [TestMethod]
        public async Task ComReleaseFailureAfterAcknowledgedFileResultKeepsTheFile()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.FileResult = true;
                context.Sessions.Session.DisposeFailure = new IntegrationException(IntegrationErrorCode.ComFailure, "cleanup failure");
                context.Sessions.Session.OnRun = session => File.WriteAllBytes(context.ResultPath(context.Executor.ActiveOperation), new byte[] { 7 });
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, resultMode: DxlResultMode.File));
                var result = await operation.Completion;
                using (var stream = result.OpenResultStream()) Assert.AreEqual(7, stream.ReadByte());
                Assert.IsFalse(context.Executor.IsBusy);
                result.Dispose();
                Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
                Assert.IsTrue(context.Log.Entries.Any(entry => entry.StartsWith("DxlCleanupFailed:", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public async Task TimeoutRetainsWorkerWorkspaceAndGateUntilActualCompletion()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.BlockRun = true;
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, DxlInvocationMethod.RunFile));
                Assert.IsTrue(context.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                var failure = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () =>
                    await operation.WaitAsync(TimeSpan.FromMilliseconds(30)));
                Assert.AreEqual(IntegrationErrorCode.DxlTimeout, failure.Code);
                Assert.AreSame(operation, failure.Operation);
                Assert.IsTrue(context.Executor.IsBusy);
                Assert.IsFalse(context.Sessions.Session.Disposed);
                Assert.IsTrue(File.Exists(context.Sessions.Session.FilePath));
                Assert.ThrowsException<IntegrationException>(() => context.Executor.Start(new DxlExecutionRequest(context.SourcePath)));
                ApartmentTest.Run(ApartmentState.STA, () =>
                {
                    var other = new FakeAutomationFactory();
                    var probe = new ComDoorsDiagnostics(new ReadOnlyProbeProtocol(), new RecordingLogger(), other, context.Gate);
                    var busy = Assert.ThrowsException<IntegrationException>(() => probe.Probe(DoorsVersion.V97, Guid.NewGuid()));
                    Assert.AreEqual(IntegrationErrorCode.DoorsOperationBusy, busy.Code);
                    Assert.AreEqual(0, other.Calls);
                });
                context.Sessions.Session.ReleaseRun.Set();
                using (var result = await operation.Completion) Assert.AreEqual("sample-result", result.Text);
                Assert.IsFalse(context.Executor.IsBusy);
                Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory));
            }
        }

        [TestMethod]
        public async Task WaitCancellationDoesNotDisposeLiveComFromCallingThread()
        {
            using (var context = new ExecutorTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                context.Sessions.Session.BlockRun = true;
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                Assert.IsTrue(context.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                cancellation.Cancel();
                var failure = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () =>
                    await operation.WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token));
                Assert.AreEqual(IntegrationErrorCode.DxlWaitCancelled, failure.Code);
                Assert.IsFalse(context.Sessions.Session.Disposed);
                context.Sessions.Session.ReleaseRun.Set();
                using (await operation.Completion) { }
                Assert.IsTrue(context.Sessions.Session.ThreadIds.All(id => id == context.Sessions.OpenThread));
            }
        }

        [TestMethod]
        public async Task GateAndWorkspaceRemainOwnedWhileComDisposeIsBlocked()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.BlockDispose = true;
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath, DxlInvocationMethod.RunFile));
                Assert.IsTrue(context.Sessions.Session.EnteredDispose.Wait(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () => await operation.WaitAsync(TimeSpan.FromMilliseconds(30)));
                Assert.IsTrue(context.Gate.IsBusy);
                Assert.IsTrue(File.Exists(context.Sessions.Session.FilePath));
                context.Sessions.Session.ReleaseDispose.Set();
                using (await operation.Completion) { }
                Assert.IsFalse(context.Gate.IsBusy);
            }
        }

        [TestMethod]
        public async Task PreCancelledExecutionDoesNotStartOrCreateWorkspace()
        {
            using (var context = new ExecutorTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
                    await context.Executor.ExecuteAsync(new DxlExecutionRequest(context.SourcePath), cancellation.Token));
                Assert.AreEqual(0, context.Sessions.OpenCalls);
                Assert.IsFalse(context.Gate.IsBusy);
            }
        }

        [TestMethod]
        public async Task MissingSourceAndBrokenLogSinkCannotStrandGate()
        {
            using (var context = new ExecutorTestContext())
            {
                var first = context.Executor.Start(new DxlExecutionRequest(Path.Combine(context.Root, "missing.dxl")));
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await first.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlScriptLoadFailed, failure.Code);
                Assert.AreEqual(0, context.Sessions.OpenCalls);
                Assert.IsFalse(context.Executor.IsBusy);
                context.Log.ThrowOnWrite = true;
                using (await context.Executor.ExecuteAsync(new DxlExecutionRequest(context.SourcePath))) { }
                Assert.IsFalse(context.Executor.IsBusy);
            }
        }

        [TestMethod]
        public async Task RunFileUsesUtf8SnapshotWithEscapedParameters()
        {
            using (var context = new ExecutorTestContext())
            {
                File.WriteAllText(context.SourcePath, "// Türkçe kaynak\noleSetResult(di_arg_value)\n", new System.Text.UTF8Encoding(true));
                context.Sessions.Session.OnRun = session =>
                {
                    var bytes = File.ReadAllBytes(session.FilePath);
                    Assert.IsFalse(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                    StringAssert.Contains(new System.Text.UTF8Encoding(false, true).GetString(bytes), "Türkçe kaynak");
                    StringAssert.Contains(session.Script, "string di_arg_value = " + DxlStringLiteral.Encode("Türkçe\"\\\n") + ";");
                };
                using (await context.Executor.ExecuteAsync(new DxlExecutionRequest(context.SourcePath, DxlInvocationMethod.RunFile,
                    parameters: new System.Collections.Generic.Dictionary<string, string> { ["value"] = "Türkçe\"\\\n" }))) { }
            }
        }

        [TestMethod]
        public async Task LateComFailureRemainsObservableAfterWaitTimeout()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.BlockRun = true;
                context.Sessions.Session.RunFailure = new IntegrationException(IntegrationErrorCode.ComServerDisconnected, "controlled late failure");
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                Assert.IsTrue(context.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () => await operation.WaitAsync(TimeSpan.FromMilliseconds(30)));
                context.Sessions.Session.ReleaseRun.Set();
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await operation.Completion);
                Assert.AreEqual(IntegrationErrorCode.ComServerDisconnected, failure.Code);
                Assert.IsFalse(context.Gate.IsBusy);
                Assert.IsTrue(context.Log.Entries.Any(entry => entry.StartsWith("DxlFailed:", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public async Task TimeoutDuringPreparationStopsAtSafePointBeforeOpeningCom()
        {
            using (var context = new ExecutorTestContext())
            using (var loader = new BlockingScriptLoader())
            {
                var executor = context.CreateExecutor(loader);
                var operation = executor.Start(new DxlExecutionRequest(context.SourcePath));
                try
                {
                    Assert.IsTrue(loader.Entered.Wait(TimeSpan.FromSeconds(5)));
                    await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () => await operation.WaitAsync(TimeSpan.FromMilliseconds(30)));
                }
                finally { loader.Release.Set(); }
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await operation.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlTimeout, failure.Code);
                Assert.AreEqual(0, context.Sessions.OpenCalls);
                Assert.IsNull(operation.WorkspaceDirectory);
                Assert.IsFalse(context.Gate.IsBusy);
            }
        }

        private sealed class BlockingScriptLoader : IDxlScriptLoader, IDisposable
        {
            public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();
            public ManualResetEventSlim Release { get; } = new ManualResetEventSlim();
            public string Load(string path, int maxBytes)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return "oleSetResult(\"OK\")";
            }
            public void Dispose() { Entered.Dispose(); Release.Dispose(); }
        }
    }
}
