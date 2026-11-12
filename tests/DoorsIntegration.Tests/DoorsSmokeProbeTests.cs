using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class SmokeSessions : IDxlSessionFactory
    {
        internal readonly SmokeSession Session = new SmokeSession(); internal DoorsConnectionMode Mode;
        public IDxlExecutionSession Open(DoorsConnectionMode mode) { Mode = mode; Session.Record("CONNECT"); if (Session.BlockStage == "CONNECT") Session.Wait(); return Session; }
    }
    internal sealed class SmokeSession : IDxlExecutionSession
    {
        internal readonly List<string> Calls = new List<string>(); internal readonly List<int> Threads = new List<int>();
        internal readonly List<ApartmentState> Apartments = new List<ApartmentState>(); internal string FilePath, FileSource, Result, WrongResult;
        internal Exception DisposeFailure; internal bool BlockRun; internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim(), Release = new ManualResetEventSlim();
        internal string BlockStage;
        internal void Wait() { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test watchdog."); }
        internal void Record(string call) { Calls.Add(call); Threads.Add(Thread.CurrentThread.ManagedThreadId); Apartments.Add(Thread.CurrentThread.GetApartmentState()); }
        public void SetResult(string value) { Record("RESULT_SET"); Result = value; if (BlockStage == "RESULT_SET") Wait(); }
        public string ReadResult() { Record("RESULT_GET"); return WrongResult ?? Result; }
        public void RunString(string script) { Record("RUNSTR"); Execute(script); }
        public void RunFile(string path) { Record("RUNFILE"); FilePath = path; FileSource = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)); Execute(FileSource); }
        private void Execute(string script)
        {
            Entered.Set(); if (BlockRun && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test watchdog.");
            Result = Regex.Match(script, "DOORS_INTEGRATION\\|1\\|[0-9a-f]{32}\\|OK").Value;
        }
        public void Dispose() { Record("DISPOSE"); if (DisposeFailure != null) throw DisposeFailure; }
    }
    internal sealed class SmokeScopeReaders : IDoorsHierarchyReader, ILinkReader
    {
        internal bool Incomplete; internal DiscoveryScope Scope; internal LinkReadRequest LinkRequest; internal Exception LinkFailure;
        internal int LinkCalls; internal bool LinkDisposed;
        public Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken token)
        { Scope = scope; return Task.FromResult(new DiscoverySnapshot(Guid.NewGuid(), kind, scope,
            new[] { new DoorsDiscoveryItem("/P/M", "M", DoorsItemKind.FormalModule, DoorsAccessStatus.Readable) }, Array.Empty<DiscoveryIssue>(), 1, Incomplete, DateTime.UtcNow)); }
        public Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken token)
        { LinkCalls++; LinkRequest = request; if (LinkFailure != null) return Task.FromException<ILinkSnapshot>(LinkFailure); return Task.FromResult<ILinkSnapshot>(new Snapshot(request, () => LinkDisposed = true)); }
        private sealed class Snapshot : ILinkSnapshot
        {
            private readonly Action dispose;
            internal Snapshot(LinkReadRequest request, Action dispose)
            { this.dispose = dispose; Summary = new LinkReadSummary(Guid.NewGuid(), request, RequirementModuleStatus.Available, LinkModuleStatus.Available, RequirementModuleOwnership.OwnedReadOnly,
                0, 0, 0, 0, 1, 0, false, false, 0, 0, 0); }
            public LinkReadSummary Summary { get; }
            public IEnumerable<LinkRecord> ReadLinks() => Array.Empty<LinkRecord>();
            public Stream OpenRawStream() => throw new NotSupportedException();
            public void Dispose() => dispose();
        }
    }
    [TestClass]
    public class DoorsSmokeProbeTests
    {
        private static DoorsSmokeProbe Probe(ExecutorTestContext files, SmokeSessions sessions, SmokeScopeReaders readers) =>
            new DoorsSmokeProbe(sessions, new ReadOnlyProbeProtocol(), files.Workspaces, files.Gate, readers, readers);
        private static DoorsSmokeRequest Request() => new DoorsSmokeRequest(live: true, modulePath: "/P/M", linkModulePath: "/P/L");
        [DataTestMethod]
        [DataRow(SmokeTest.ComSession)] [DataRow(SmokeTest.RunStr)] [DataRow(SmokeTest.RunFile)] [DataRow(SmokeTest.Result)]
        public async Task RawProbesOwnOneStaAndReferenceAndValidateFixedReadOnlyNonce(SmokeTest test)
        {
            using (var files = new ExecutorTestContext())
            {
                var sessions = new SmokeSessions(); var operation = Probe(files, sessions, new SmokeScopeReaders()).Start(test, Request(), Guid.NewGuid());
                var evidence = await operation.Completion;
                Assert.AreEqual(DoorsConnectionMode.AttachExisting, sessions.Mode); Assert.AreEqual(1, sessions.Session.Threads.Distinct().Count());
                Assert.IsTrue(sessions.Session.Apartments.All(value => value == ApartmentState.STA)); Assert.AreEqual("DISPOSE", sessions.Session.Calls.Last()); Assert.IsFalse(files.Gate.IsBusy);
                if (test == SmokeTest.Result) CollectionAssert.AreEqual(new[] { "CONNECT", "RESULT_SET", "RESULT_GET", "RUNSTR", "RESULT_GET", "DISPOSE" }, sessions.Session.Calls);
                if (test == SmokeTest.RunFile)
                { Assert.IsTrue(Path.IsPathRooted(sessions.Session.FilePath)); Assert.IsFalse(File.Exists(sessions.Session.FilePath)); StringAssert.Contains(sessions.Session.FileSource, "oleSetResult"); Assert.AreEqual(1, sessions.Session.Calls.Count(value => value == "RUNFILE")); }
                if (test != SmokeTest.ComSession) Assert.AreEqual("True", evidence.Metadata["FreshNonceMatched"]);
                Assert.IsTrue(operation.ReadLog().Any(value => value.Phase == "COM_CLEANUP" && value.Status == "PASS"));
            }
        }
        [TestMethod]
        public async Task StaleOrWrongResultFailsWithoutRetryAndStillCleansCom()
        {
            using (var files = new ExecutorTestContext())
            {
                var sessions = new SmokeSessions(); sessions.Session.WrongResult = "STALE_PRIVATE_RESULT";
                var operation = Probe(files, sessions, new SmokeScopeReaders()).Start(SmokeTest.RunStr, Request(), Guid.NewGuid());
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => operation.Completion);
                Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, error.Code); Assert.AreEqual(1, sessions.Session.Calls.Count(value => value == "RUNSTR"));
                Assert.AreEqual("DISPOSE", sessions.Session.Calls.Last()); Assert.IsFalse(string.Join(" ", operation.ReadLog().Select(value => value.Message)).Contains("STALE_PRIVATE_RESULT"));
            }
        }
        [DataTestMethod]
        [DataRow("CONNECT")] [DataRow("RESULT_SET")] [DataRow("RUNSTR")]
        public async Task StopCannotAbortStartedComAndGateStaysHeldUntilOriginalWorkerCleanup(string stage)
        {
            using (var files = new ExecutorTestContext())
            {
                var sessions = new SmokeSessions(); sessions.Session.BlockStage = stage; sessions.Session.BlockRun = stage == "RUNSTR";
                var operation = Probe(files, sessions, new SmokeScopeReaders()).Start(SmokeTest.Result, Request(), Guid.NewGuid());
                try
                {
                    Assert.IsTrue(await Task.Run(() => sessions.Session.Entered.Wait(TimeSpan.FromSeconds(3)))); operation.StopBeforeNextDispatch();
                    Assert.IsFalse(operation.Completion.IsCompleted); Assert.IsTrue(files.Gate.IsBusy);
                    Assert.ThrowsException<IntegrationException>(() => files.Gate.Enter());
                }
                finally { sessions.Session.Release.Set(); }
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation.Completion);
                Assert.IsFalse(files.Gate.IsBusy); Assert.AreEqual("DISPOSE", sessions.Session.Calls.Last()); Assert.AreEqual(stage == "RUNSTR" ? 1 : 0, sessions.Session.Calls.Count(value => value == "RESULT_GET"));
            }
        }
        [TestMethod]
        public async Task CleanupFailureIsNotReportedAsSuccess()
        {
            using (var files = new ExecutorTestContext())
            {
                var sessions = new SmokeSessions(); sessions.Session.DisposeFailure = new IOException("PRIVATE cleanup failure");
                var operation = Probe(files, sessions, new SmokeScopeReaders()).Start(SmokeTest.ComSession, Request(), Guid.NewGuid());
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(() => operation.Completion); Assert.AreEqual(IntegrationErrorCode.ComFailure, failure.Code);
                Assert.IsFalse(files.Gate.IsBusy); Assert.IsTrue(operation.ReadLog().Any(value => value.Phase == "COM_CLEANUP" && value.Status == "FAIL"));
            }
        }
        [TestMethod]
        public async Task ModuleAndLinkReadsUseExistingReadOnlyPortsAndReportPartialLinkEvidence()
        {
            using (var files = new ExecutorTestContext())
            {
                var readers = new SmokeScopeReaders(); var probe = Probe(files, new SmokeSessions(), readers);
                var modules = await probe.Start(SmokeTest.ModuleListing, Request(), Guid.NewGuid()).Completion; Assert.AreEqual("1", modules.Metadata["FormalModuleCount"]);
                Assert.AreEqual(DoorsConnectionMode.AttachExisting, readers.Scope.ConnectionMode);
                var links = await probe.Start(SmokeTest.LinkRead, Request(), Guid.NewGuid()).Completion;
                Assert.AreEqual("False", links.Metadata["IsCompleteForReadableScope"]); Assert.AreEqual("False", links.Metadata["CoverageAssessed"]); Assert.IsTrue(readers.LinkDisposed);
                Assert.IsFalse(readers.LinkRequest.IncludeDeletedObjects); Assert.AreEqual(LinkDirection.Both, readers.LinkRequest.Direction);
                readers.Incomplete = true;
                var failure = await Assert.ThrowsExceptionAsync<SmokeProbeFailure>(() => probe.Start(SmokeTest.ModuleListing, Request(), Guid.NewGuid()).Completion);
                Assert.AreEqual("ModuleListingIncomplete_ReviewBoundsAndAccess", failure.ReasonCode); Assert.AreEqual("True", failure.Evidence.Metadata["Truncated"]);
            }
        }
        [DataTestMethod]
        [DataRow(false)] [DataRow(true)]
        public async Task ReaderDeadlineObservesOriginalExecutorCompletionInsteadOfResubmitting(bool lateFails)
        {
            using (var files = new ExecutorTestContext())
            {
                files.Sessions.Session.BlockRun = true;
                if (lateFails) files.Sessions.Session.RunFailure = new IOException("PRIVATE_LATE_ERROR");
                var original = files.Executor.Start(new DxlExecutionRequest(files.SourcePath, timeout: TimeSpan.FromMilliseconds(30)));
                Assert.IsTrue(await Task.Run(() => files.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(3))));
                var waitFailure = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => original.WaitAsync());
                var readers = new SmokeScopeReaders { LinkFailure = waitFailure };
                var probe = Probe(files, new SmokeSessions(), readers); var operation = probe.Start(SmokeTest.LinkRead, Request(), Guid.NewGuid());
                try { await Task.Delay(50); Assert.IsFalse(operation.Completion.IsCompleted); Assert.IsTrue(files.Gate.IsBusy); }
                finally { files.Sessions.Session.ReleaseRun.Set(); }
                await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => operation.Completion);
                Assert.IsTrue(operation.ReadLog().Any(row => row.Phase == "ORIGINAL_NATIVE_COMPLETION" && row.Status == (lateFails ? "FAIL" : "PASS")));
                Assert.AreEqual(1, readers.LinkCalls); Assert.AreEqual(1, files.Sessions.OpenCalls); Assert.IsFalse(files.Gate.IsBusy);
            }
        }
    }
}
