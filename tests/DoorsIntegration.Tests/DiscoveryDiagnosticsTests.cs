using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("DiscoveryDiagnostics")]
    public class DiscoveryDiagnosticsTests
    {
        private sealed class MemoryStore : IDiscoveryDebugStore
        {
            public DiscoveryDebugRecord Record;
            public readonly List<string> Stages = new List<string>();
            public bool Fail;
            public void Save(DiscoveryDebugRecord record)
            {
                if (Fail) throw new IOException("PRIVATE_STORE_FAILURE");
                Record = record; Stages.Add(record.Stage);
            }
        }

        [DataTestMethod, DataRow("\n"), DataRow("\r\n")]
        public async Task CaptureBeforeParserPreservesValidLineEndings(string newline)
        {
            var store = new MemoryStore();
            using (var context = new DiscoveryTestContext(store))
            {
                context.Replies["/P"] = id => DiscoveryTestContext.Batch(id, "/P", "Project").Replace("\n", newline);
                var snapshot = await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P"));
                Assert.IsNotNull(snapshot);
                CollectionAssert.AreEqual(new[] { "ComResultRead", "BeforeDiscoveryParser", "DiscoveryAccepted" }, store.Stages);
                string raw = store.Record.ReadDiscoveryResponse();
                StringAssert.Contains(store.Record.ReadComResponse(), "OK|" + raw);
                Assert.AreEqual(3, store.Record.ResponseLineCount);
                Assert.AreEqual(3, store.Record.LfCount);
                Assert.AreEqual(newline == "\r\n" ? 3 : 0, store.Record.CrLfCount);
                Assert.AreEqual(raw.Length, store.Record.ResponseChars);
                Assert.AreEqual("runStr / Result", store.Record.Method);
                Assert.AreEqual(64, store.Record.ScriptSha256.Length);
                Assert.AreEqual(ApartmentState.STA, context.Transport.Sessions.OpenApartment);
            }
        }

        [DataTestMethod, DataRow("short"), DataRow("empty"), DataRow("debug"), DataRow("bom"), DataRow("version"), DataRow("count")]
        public async Task InvalidPayloadIsCapturedAndRejectedWithoutLeakingContent(string fault)
        {
            var store = new MemoryStore();
            using (var context = new DiscoveryTestContext(store))
            {
                string payload = null;
                context.Replies["/P"] = id =>
                {
                    payload = DiscoveryTestContext.Batch(id, "/P", "Project");
                    if (fault == "short") payload = new string('x', 4118) + "\n";
                    if (fault == "empty") payload = "";
                    if (fault == "debug") payload = "PRIVATE_DEBUG_PATH\n" + payload;
                    if (fault == "bom") payload = "\uFEFF" + payload;
                    if (fault == "version") payload = payload.Replace("DOORS_DISCOVERY|1|", "DOORS_DISCOVERY|2|");
                    if (fault == "count") payload = payload.Replace("END\t0", "END\t1");
                    return payload;
                };
                var error = await Assert.ThrowsExceptionAsync<DiscoveryProtocolException>(() => context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P")));
                string expected = fault == "short" ? "InvalidLineCount" : fault == "empty" ? "MissingResponse" : fault == "count" ? "CountMismatch" : "HeaderMismatch";
                StringAssert.Contains(error.DiagnosticMetadata, "Reason=" + expected);
                Assert.AreEqual(payload, store.Record.ReadDiscoveryResponse());
                Assert.AreEqual(error.DiagnosticMetadata, store.Record.ParserFailureReason);
                Assert.IsTrue(error.DebugCaptureSaved);
                StringAssert.Contains(error.Message, "DebugRequestId=" + store.Record.RequestId.ToString("N"));
                Assert.IsFalse(error.ToString().Contains("PRIVATE_DEBUG_PATH"));
                Assert.IsFalse(string.Join("\n", context.Transport.Log.Entries).Contains("PRIVATE_DEBUG_PATH"));
                if (fault == "short") Assert.AreEqual("Reason=InvalidLineCount; Line=0; ResponseChars=4119", error.DiagnosticMetadata);
                if (fault == "bom") Assert.IsTrue(store.Record.LeadingBom, "Capture the BOM without guessing that it caused the reported failure.");
            }
        }

        [DataTestMethod, DataRow("-E- DXL: PRIVATE_COMPILATION_ERROR\n"), DataRow("DXL_PING_OK"), DataRow("DOORS_DXL|1|00000000000000000000000000000000|OK|DXL_PING_OK")]
        public async Task RawTransportFailureIsSavedBeforeFrameValidation(string raw)
        {
            var store = new MemoryStore();
            using (var context = new DiscoveryTestContext(store))
            {
                context.Reply("/P", "Project");
                context.Transport.Sessions.Session.ResponseOverride = raw;
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P")));
                Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, error.Code);
                Assert.AreEqual(raw, store.Record.ReadComResponse());
                Assert.IsNull(store.Record.ReadDiscoveryResponse());
                CollectionAssert.AreEqual(new[] { "ComResultRead", "Rejected" }, store.Stages);
                Assert.AreEqual("DxlExecutionOrTransportFailure", store.Record.ParserFailureReason);
                Assert.IsFalse(error.Message.Contains("PRIVATE_COMPILATION_ERROR"));
            }
        }

        [TestMethod]
        public async Task LoggingFailurePreservesOriginalProtocolFailure()
        {
            using (var context = new DiscoveryTestContext(new MemoryStore { Fail = true }))
            {
                context.Replies["/P"] = id => "broken\n";
                var error = await Assert.ThrowsExceptionAsync<DiscoveryProtocolException>(() => context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P")));
                Assert.IsFalse(error.DebugCaptureSaved);
                StringAssert.Contains(error.Message, "ProtectedCapture=Unavailable");
                StringAssert.Contains(error.DiagnosticMetadata, "InvalidLineCount");
                Assert.IsFalse(string.Join("\n", context.Transport.Log.Entries).Contains("PRIVATE_STORE_FAILURE"));
            }
        }

        [TestMethod]
        public async Task LaterFolderFailureDoesNotReturnAPartialSuccessfulInventory()
        {
            var store = new MemoryStore();
            using (var context = new DiscoveryTestContext(store))
            {
                context.Reply("/P", "Project", DiscoveryTestContext.Row("I", "/P/F", "Folder"), DiscoveryTestContext.Row("I", "/P/M", "Formal"));
                context.Replies["/P/F"] = id => "broken\n";
                await Assert.ThrowsExceptionAsync<DiscoveryProtocolException>(() => context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope("/P")));
                CollectionAssert.AreEqual(new[] { "/P", "/P/F" }, context.Visits);
                Assert.IsTrue(context.Transport.Log.Entries.All(e => !e.StartsWith("DiscoveryCompleted:", StringComparison.Ordinal)));
            }
        }

        [DataTestMethod, DataRow(DxlInvocationMethod.RunString), DataRow(DxlInvocationMethod.RunFile)]
        public async Task InvocationMethodAndLosslessRawComStringAreRecorded(DxlInvocationMethod method)
        {
            using (var context = new ExecutorTestContext())
            {
                const string raw = "\uFEFFPRIVATE\r\ninvalid\r\uD800";
                var store = new MemoryStore();
                var capture = new DiscoveryDebugCapture(store, context.Log, Guid.NewGuid());
                var request = new DxlExecutionRequest(context.SourcePath, method) { DebugCapture = capture };
                context.Sessions.Session.ResponseOverride = raw;
                await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Executor.ExecuteAsync(request));
                Assert.AreEqual(raw, store.Record.ReadComResponse());
                StringAssert.StartsWith(store.Record.Method, method == DxlInvocationMethod.RunFile ? "runFile" : "runStr");
                Assert.AreEqual(1, store.Record.ComResponseLfCount);
            }
        }

        [TestMethod]
        public void ProtectedCaptureRoundTripsRawAndRestrictsDirectoryAccess()
        {
            using (var context = new ExecutorTestContext())
            {
                var directory = Path.Combine(context.Root, "private-captures");
                var store = new ProtectedDiscoveryDebugStore(directory, new TestBaselineJournalProtector());
                var record = new DiscoveryDebugRecord { RequestId = Guid.NewGuid(), RawComResponseUtf16 = DiscoveryDebugRecord.Encode("PRIVATE_REQUIREMENT_\uD800\r\n"), Stage = "ComResultRead" };
                store.Save(record);
                record.Stage = "Rejected"; store.Save(record);
                var read = store.Read(record.RequestId);
                Assert.AreEqual(record.ReadComResponse(), read.ReadComResponse());
                Assert.AreEqual("Rejected", read.Stage);
                Assert.AreEqual(1, Directory.GetFiles(directory).Length);
                var bytes = File.ReadAllBytes(Directory.GetFiles(directory).Single());
                Assert.IsFalse(System.Text.Encoding.UTF8.GetString(bytes).Contains("RawComResponseUtf16"));
                Assert.IsFalse(System.Text.Encoding.UTF8.GetString(bytes).Contains("PRIVATE_REQUIREMENT"));
                var acl = new DirectoryInfo(directory).GetAccessControl();
                Assert.IsTrue(acl.AreAccessRulesProtected);
                Assert.IsTrue(File.GetAccessControl(Directory.GetFiles(directory).Single()).AreAccessRulesProtected);
                var allowed = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).ToArray();
                Assert.IsTrue(allowed.All(r => r.IdentityReference.Equals(WindowsIdentity.GetCurrent().User) || r.IdentityReference.Value == "S-1-5-18"));
            }
        }

        [TestMethod]
        public void RequestedVersionIsNotClaimedAsObservedComVersion()
        {
            using (var context = new ExecutorTestContext())
            {
                var capture = new DiscoveryDebugCapture(new MemoryStore(), context.Log, Guid.NewGuid());
                capture.Prepare(new DxlExecutionRequest(context.SourcePath, target: DoorsVersion.V95), Guid.NewGuid(), "source", "dispatch");
                Assert.AreEqual("9.5", capture.Record.RequestedVersion);
                StringAssert.Contains(capture.Record.ClientEvidence, "COM server identity/version unverified");
                Assert.IsNotNull(capture.Record.ObservedClients);
            }
        }

        [TestMethod]
        public async Task IndependentPingAcceptsExactFreshPayloadOnSta()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.Payload = "DXL_PING_OK";
                var store = new MemoryStore();
                var service = new DxlPingService(context.Executor, store, context.Log);
                var first = await service.PingAsync();
                string oldResult = context.Sessions.Session.Prefix + "OK|DXL_PING_OK";
                Assert.AreEqual("DXL_PING_OK", first.Payload);
                Assert.AreEqual(ApartmentState.STA, context.Sessions.OpenApartment);
                CollectionAssert.AreEqual(new[] { "SetResult", "runStr", "ReadResult", "Dispose" }, context.Sessions.Session.Calls);
                StringAssert.Contains(context.Sessions.Session.Script, "oleSetResult(\"DXL_PING_OK\");");
                context.Sessions.Session.ResponseOverride = oldResult;
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => service.PingAsync());
                Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, error.Code);
                Assert.AreNotEqual(first.RequestId, store.Record.RequestId);
            }
        }

        [DataTestMethod, DataRow("DXL_PING_OK "), DataRow("dxl_ping_ok"), DataRow(""), DataRow("WRONG")]
        public async Task IndependentPingRejectsIncorrectPayload(string payload)
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.Payload = payload;
                var store = new MemoryStore();
                await Assert.ThrowsExceptionAsync<IntegrationException>(() => new DxlPingService(context.Executor, store, context.Log).PingAsync());
                Assert.AreEqual("IndependentPingFailed", store.Record.ParserFailureReason);
            }
        }

        [TestMethod]
        public void IsolatedPingCliOptionDoesNotAffectExistingPingOrDiagnostics()
        {
            Assert.IsTrue(CommandLineOptions.Parse(new[] { "ping", "--dxl-only", "--target", "9.7" }).IsolatedPing);
            Assert.IsFalse(CommandLineOptions.Parse(new[] { "ping" }).IsolatedPing);
            Assert.ThrowsException<IntegrationException>(() => CommandLineOptions.Parse(new[] { "diagnose", "--dxl-only" }));
            Assert.ThrowsException<IntegrationException>(() => CommandLineOptions.Parse(new[] { "ping", "--dxl-only", "--dxl-only" }));
        }

        [TestMethod]
        public async Task PingDeadlineDoesNotCancelComOrRetry()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.BlockRun = true;
                context.Sessions.Session.Payload = "DXL_PING_OK";
                var error = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() =>
                    new DxlPingService(context.Executor, new MemoryStore(), context.Log).PingAsync(timeout: TimeSpan.FromMilliseconds(50)));
                Assert.IsTrue(context.Executor.IsBusy);
                Assert.IsFalse(error.Operation.Completion.IsCompleted);
                context.Sessions.Session.ReleaseRun.Set();
                using (await error.Operation.Completion) { }
                Assert.IsFalse(context.Executor.IsBusy);
                Assert.AreEqual(1, context.Sessions.OpenCalls);
            }
        }
    }
}
