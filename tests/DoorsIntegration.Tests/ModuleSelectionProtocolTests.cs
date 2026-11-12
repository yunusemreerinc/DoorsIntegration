using System;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ModuleSelectionProtocolTests
    {
        private static string Reply(Guid id, string ending = "\n") => ("MODULE_SELECTION|1|" + id.ToString("N") +
            "\nM\t/P/M\tnative-qualified-ID\tReadable\tAvailable\t2\t7\tsuffix\tTürkçe \\n注釈\tClosed\nEND\n").Replace("\n", ending);
        private static DxlModuleSelectionMetadataProvider Provider(ExecutorTestContext files) => new DxlModuleSelectionMetadataProvider(files.Executor,
            new ModuleSelectionScriptOptions { ScriptPath = new ModuleSelectionScriptOptions().ScriptPath });
        private static Task<ModuleSelectionMetadata> Read(ExecutorTestContext files, TimeSpan? timeout = null, CancellationToken token = default(CancellationToken)) =>
            Provider(files).ReadAsync("repo", "/P/M", DoorsVersion.V97, DoorsConnectionMode.AttachExisting, timeout ?? TimeSpan.FromSeconds(5), token);
        [DataTestMethod, DataRow("\n"), DataRow("\r\n")]
        public async Task NativeProtocolKeepsIdentityVersionUnicodeAndWindowsNewlines(string ending)
        {
            using (var files = new ExecutorTestContext())
            {
                files.Sessions.Session.OnRun = session => session.Payload = Reply(Guid.ParseExact(session.Prefix.Split('|')[2], "N"), ending);
                var result = await Read(files); Assert.AreEqual("native-qualified-ID", result.Module.ModuleId);
                Assert.AreEqual(new BaselineVersion(2, 7, "suffix"), result.LatestBaseline); Assert.AreEqual("Türkçe \n注釈", result.Annotation);
                Assert.AreEqual(ApartmentState.STA, files.Sessions.OpenApartment); Assert.IsFalse(files.Gate.IsBusy); Assert.IsTrue(files.Sessions.Session.Disposed);
            }
        }
        [DataTestMethod, DataRow("None"), DataRow("Unavailable")]
        public async Task NoBaselineAndUnknownBaselineAreNotFabricatedAsVersionZero(string status)
        {
            using (var files = new ExecutorTestContext())
            {
                files.Sessions.Session.OnRun = session => session.Payload = "MODULE_SELECTION|1|" + session.Prefix.Split('|')[2] +
                    "\nM\t/P/M\tnative-id\tReadable\t" + status + "\t0\t0\t\t\tNotOwned\nEND\n";
                var result = await Read(files); Assert.AreEqual(status, result.BaselineStatus.ToString()); Assert.IsNull(result.LatestBaseline); Assert.IsNotNull(result.Module);
            }
        }
        [DataTestMethod, DataRow("id"), DataRow("path"), DataRow("numeric-access"), DataRow("missing-identity"), DataRow("failed-cleanup"),
            DataRow("truncated"), DataRow("escape"), DataRow("trailing"), DataRow("negative-version")]
        public async Task InvalidNativeEvidenceIsRejectedWithoutAcceptingIdentity(string mutation)
        {
            using (var files = new ExecutorTestContext())
            {
                files.Sessions.Session.OnRun = session =>
                {
                    string payload = Reply(Guid.ParseExact(session.Prefix.Split('|')[2], "N"));
                    switch (mutation)
                    {
                        case "id": payload = Reply(Guid.NewGuid()); break;
                        case "path": payload = payload.Replace("/P/M", "/Other/M"); break;
                        case "numeric-access": payload = payload.Replace("Readable", "0"); break;
                        case "missing-identity": payload = payload.Replace("native-qualified-ID", ""); break;
                        case "failed-cleanup": payload = payload.Replace("Closed", "Failed"); break;
                        case "truncated": payload = payload.Substring(0, payload.Length - 1); break;
                        case "escape": payload = payload.Replace("suffix", "bad\\q"); break;
                        case "trailing": payload += "M\tINJECTED\n"; break;
                        case "negative-version": payload = payload.Replace("\t2\t7\t", "\t-1\t7\t"); break;
                    }
                    session.Payload = payload;
                };
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => Read(files));
                Assert.AreEqual(IntegrationErrorCode.DiscoveryProtocolFailure, error.Code); Assert.IsFalse(error.Message.Contains("native-qualified-ID")); Assert.IsFalse(files.Gate.IsBusy);
            }
        }
        [TestMethod]
        public async Task TimedOutComMetadataRemainsBusyUntilOriginalCallAndCleanupFinish()
        {
            using (var files = new ExecutorTestContext())
            {
                files.Sessions.Session.BlockRun = true;
                files.Sessions.Session.OnRun = session => session.Payload = Reply(Guid.ParseExact(session.Prefix.Split('|')[2], "N"));
                await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => Read(files, TimeSpan.FromMilliseconds(50)));
                Assert.IsTrue(files.Gate.IsBusy); Assert.AreEqual(1, files.Sessions.OpenCalls);
                files.Sessions.Session.ReleaseRun.Set(); using (await files.Executor.ActiveOperation.Completion) { }
                Assert.IsFalse(files.Gate.IsBusy);
            }
        }
        [TestMethod]
        public async Task PreCancelledMetadataReadDoesNotStartCom()
        {
            using (var files = new ExecutorTestContext()) using (var cancellation = new CancellationTokenSource())
            { cancellation.Cancel(); await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Read(files, token: cancellation.Token)); Assert.AreEqual(0, files.Sessions.OpenCalls); }
        }
    }
}
