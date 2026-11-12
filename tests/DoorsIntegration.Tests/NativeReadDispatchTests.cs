using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("NativeReadOnly"), DoNotParallelize]
    public class NativeReadDispatchTests
    {
        private static DxlExecutor Queued(ExecutorTestContext files) => new DxlExecutor(files.Sessions, new Utf8DxlScriptLoader(), files.Workspaces, files.Log, files.Gate, new NoDxlRunLimitAutomation(), new DxlWatchdogSettings(), DoorsStaExecutionQueue.Shared);
        private static void Reply(ExecutorTestContext files, DxlExecutor executor, string body = null)
        {
            files.Sessions.Session.FileResult = true;
            files.Sessions.Session.OnRun = session => File.WriteAllText(files.ResultPath(executor.ActiveOperation), body ?? "NATIVE_READ_SNAPSHOT\t1\t" + session.Prefix.Split('|')[2] + "\n" + NativeReadOnlyTests.Rows + "END\ttrue\t9\n", new UTF8Encoding(false));
        }
        private static NativeReadQuery Query() => new NativeReadQuery(DoorsVersion.V97, "/P", new[] { "/P/A" }, new[] { "/P/Links" });
        [TestMethod]
        public async Task ActualExecutorRunsReadFileOnStaAndCleansItsFile()
        {
            using (var files = new ExecutorTestContext())
            {
                var executor = Queued(files); Reply(files, executor);
                var read = await new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions()).ReadAsync(Query(), CancellationToken.None);
                Assert.AreEqual("db", read.DatabaseId); Assert.AreEqual(ApartmentState.STA, files.Sessions.OpenApartment); Assert.AreEqual(DoorsConnectionMode.AttachExisting, files.Sessions.Mode);
                Assert.AreEqual(1, files.Sessions.OpenCalls); Assert.IsTrue(files.Sessions.Session.Calls.Contains("runFile")); Assert.IsFalse(executor.IsBusy);
                Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(files.Root, "operations")).Length);
            }
        }
        [TestMethod]
        public async Task MissingStaQueueIsRejectedBeforeOpeningCom()
        {
            using (var files = new ExecutorTestContext())
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new DxlNativeReadObservationSource(files.Executor, new NativeReadScriptOptions()).ReadAsync(Query(), CancellationToken.None));
                Assert.AreEqual(0, files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task InvalidResponseIsRejectedAndFileIsReleased()
        {
            using (var files = new ExecutorTestContext())
            {
                var executor = Queued(files); Reply(files, executor, "not a native response");
                await Assert.ThrowsExceptionAsync<IntegrationException>(() => new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions()).ReadAsync(Query(), CancellationToken.None));
                Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(files.Root, "operations")).Length); Assert.AreEqual(1, files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task CancellationStopsWaitingButDoesNotAbortComOrRetry()
        {
            using (var files = new ExecutorTestContext()) using (var cancel = new CancellationTokenSource())
            {
                var executor = Queued(files); Reply(files, executor); files.Sessions.Session.BlockRun = true;
                var pending = new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions()).ReadAsync(Query(), cancel.Token);
                Assert.IsTrue(files.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5))); var operation = executor.ActiveOperation; cancel.Cancel();
                try
                {
                    var error = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => pending); Assert.AreEqual(IntegrationErrorCode.DxlWaitCancelled, error.Code);
                    Assert.IsTrue(executor.IsBusy); Assert.IsFalse(operation.Completion.IsCompleted); Assert.IsTrue(Directory.Exists(operation.WorkspaceDirectory));
                }
                finally { files.Sessions.Session.ReleaseRun.Set(); await operation.Completion; }
                for (int i = 0; i < 500 && Directory.Exists(operation.WorkspaceDirectory); i++) await Task.Delay(10);
                Assert.IsFalse(Directory.Exists(operation.WorkspaceDirectory)); Assert.AreEqual(1, files.Sessions.OpenCalls); Assert.IsFalse(executor.IsBusy);
            }
        }
        [DataTestMethod, DataRow(false), DataRow(true)]
        public async Task CurrentEndpointUsesActualNativeVersionsAndRejectsWrongAnchor(bool wrongAnchor)
        {
            using (var files = new ExecutorTestContext())
            {
                var executor = Queued(files); files.Sessions.Session.FileResult = true;
                files.Sessions.Session.OnRun = session => {
                    StringAssert.Contains(session.Script, "string di_arg_current = \"true\""); StringAssert.Contains(session.Script, "intOf(session()) == intOf(di_arg_session)");
                    string row = "L\tOutgoing\tfalse\t/P/Links\tLINK-QUID\t/P/A\t" + (wrongAnchor ? "OTHER" : "A") + "\tCurrent\t\t\t\t\t1\tVerified\t/P/B\tB\tBaseline\t1\t0\tR\t1.0[R]\t2\tVerified";
                    File.WriteAllText(files.ResultPath(executor.ActiveOperation), "BASELINE_LINK_VERSIONS\t1\t" + session.Prefix.Split('|')[2] + "\n" + row + "\nEND\ttrue\t1\n", new UTF8Encoding(false));
                };
                var context = await new DxlImpactReadContextProvider(new NativeReadOnlyTests.Observation(NativeReadOnlyTests.Parse())).ObserveAsync(NativeReadOnlyTests.Selection(), CancellationToken.None);
                var result = await new DxlReadOnlyLinkEvidenceReader(executor).ReadAsync(NativeReadOnlyTests.Request(NativeReadOnlyTests.Parse()), context, context.Modules.Single(), "/P/Links", CancellationToken.None);
                Assert.AreEqual(!wrongAnchor, result.Complete); Assert.IsTrue(result.Links.Single().SourceVersion.IsCurrent); Assert.AreEqual("1.0[R]", result.Links.Single().TargetVersion.Version); Assert.AreEqual(1, files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public void ReadPolicyRequiresObservedSessionAndUnchangedScript()
        {
            using (var files = new ExecutorTestContext())
            {
                var policy = new ObservedReadOnlyLinkEvidenceCompatibility(files.SourcePath); var scripts = new BaselineLinkEvidenceScriptOptions { ScriptPath = files.SourcePath };
                var read = NativeReadOnlyTests.Parse(); var request = NativeReadOnlyTests.Request(read);
                var context = new BaselineVerificationContext { Source = BaselineMetadataSource.NativeDxl, EnvironmentId = "db", RepositoryId = "db", Operator = "user", ClientVersion = "9.7.2.9", SessionId = "42", SessionObserved = true };
                Assert.IsTrue(policy.Matches(request, context, scripts)); context.SessionObserved = false; Assert.IsFalse(policy.Matches(request, context, scripts)); context.SessionObserved = true;
                File.AppendAllText(files.SourcePath, "// changed"); Assert.IsFalse(policy.Matches(request, context, scripts));
            }
        }
    }
}
