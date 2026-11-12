using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class SmokeInspectorDouble : IDoorsSmokeEnvironmentInspector
    {
        internal int ProcessCount = 1; internal bool Complete = true; internal bool Registered = true; internal bool UnknownVersion;
        internal int Minor = 7; internal Exception Failure;
        internal string Clsid = "{12345678-1234-1234-1234-123456789012}";
        public Task<DoorsSmokeEnvironment> InspectAsync(DoorsVersion target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (Failure != null) throw Failure;
            var registrations = new[] { ComArchitecture.X86, ComArchitecture.X64 }.Select(arch => new ComRegistrationSnapshot(arch,
                Registered ? ComRegistrationState.Registered : ComRegistrationState.NotRegistered, Clsid, "C:\\DOORS\\doors.exe", "9." + Minor,
                UnknownVersion ? (int?)null : 9, UnknownVersion ? (int?)null : Minor, "x86 (PE machine)"));
            var clients = Enumerable.Range(1, ProcessCount).Select(pid => new SmokeClientProcess(pid, 2, DateTime.UtcNow.AddMinutes(-10), "C:\\DOORS\\doors.exe", "9." + Minor,
                UnknownVersion ? (int?)null : 9, UnknownVersion ? (int?)null : Minor, "x86 (PE machine)", "Observed"));
            return Task.FromResult(new DoorsSmokeEnvironment(new DoorsCompatibilityReport(target, true, Environment.Is64BitProcess, "TestDouble", registrations), clients, Complete, 2, 0.1, 0.2, 0.3));
        }
    }
    internal sealed class SmokeProbeDouble : IDoorsSmokeProbe
    {
        internal readonly List<SmokeTest> Calls = new List<SmokeTest>();
        internal Func<SmokeTest, SmokeProbeOperation> Dispatch;
        public SmokeOperationSemantics Semantics { get; set; } = SmokeOperationSemantics.IdempotentReadOnly;
        public SmokeProbeOperation Start(SmokeTest test, DoorsSmokeRequest request, Guid id)
        { Calls.Add(test); return Dispatch?.Invoke(test) ?? new SmokeProbeOperation(Task.FromResult(new SmokeProbeEvidence(new Dictionary<string,string> { ["TestDouble"] = "True" })), () => { }); }
    }
    [TestClass]
    public class DoorsSmokeTests
    {
        private static DoorsSmokeRequest Request(bool live = true, TimeSpan? timeout = null) => new DoorsSmokeRequest(live: live, modulePath: "/P/M", linkModulePath: "/P/L", timeout: timeout);
        private static DoorsSmokeSuite Suite(SmokeInspectorDouble inspector, SmokeProbeDouble probe, IIntegrationLogger logger = null) => new DoorsSmokeSuite(inspector, probe, logger ?? new ExecutorTestLog());
        [TestMethod]
        public async Task CompleteReadOnlySuiteReportsAllElevenCasesExactlyOnceWithMetadataLogsAndDuration()
        {
            var probe = new SmokeProbeDouble(); var run = await Suite(new SmokeInspectorDouble(), probe).RunAsync(Request());
            Assert.AreEqual(11, run.Results.Count); Assert.AreEqual(6, probe.Calls.Count); Assert.AreEqual(6, probe.Calls.Distinct().Count());
            Assert.AreEqual(SmokeStatus.PASS, run.Status); Assert.AreEqual(0, run.ExitCode); Assert.IsFalse(run.Request.AutomaticRetryAllowed);
            Assert.IsTrue(run.Request.ReadOnly); Assert.AreEqual(DoorsConnectionMode.AttachExisting, run.Request.ConnectionMode);
            foreach (var row in run.Results)
            { Assert.IsNotNull(row.Environment); Assert.IsTrue(row.DurationMs >= 0); Assert.IsTrue(row.Log.Count >= 2); Assert.IsTrue(row.Attempts <= 1); Assert.AreEqual(0, row.Retries); Assert.AreNotEqual(Guid.Empty, row.CorrelationId); }
            Assert.AreEqual(0, (await run.Settlement).Count);
        }
        [DataTestMethod]
        [DataRow("offline")] [DataRow("noRegistration")] [DataRow("noProcess")] [DataRow("unknownVersion")] [DataRow("incompleteProcess")]
        public async Task MissingRealPrerequisitesOrExplicitLiveOptInCannotBecomeMockPass(string missing)
        {
            var inspector = new SmokeInspectorDouble { Registered = missing != "noRegistration", ProcessCount = missing == "noProcess" ? 0 : 1,
                UnknownVersion = missing == "unknownVersion", Complete = missing != "incompleteProcess" };
            var probe = new SmokeProbeDouble(); var run = await Suite(inspector, probe).RunAsync(Request(missing != "offline"));
            Assert.AreEqual(0, probe.Calls.Count); Assert.AreEqual(SmokeStatus.NOT_RUN, run.Status); Assert.AreEqual(3, run.ExitCode);
            Assert.IsTrue(run.Results.Where(row => row.Test > SmokeTest.ArchitectureCompatibility).All(row => row.Status == SmokeStatus.NOT_RUN && row.Attempts == 0));
        }
        [TestMethod]
        public async Task VersionMismatchAndAmbiguousClientsBlockNativeDispatch()
        {
            foreach (var inspector in new[] { new SmokeInspectorDouble { Minor = 5 }, new SmokeInspectorDouble { ProcessCount = 2 } })
            { var probe = new SmokeProbeDouble(); var run = await Suite(inspector, probe).RunAsync(Request()); Assert.AreEqual(SmokeStatus.FAIL, run.Status); Assert.AreEqual(0, probe.Calls.Count); }
        }
        [TestMethod]
        public async Task FailedProgIdPrerequisiteCannotBeOverriddenByEligibleVersionMetadata()
        {
            var probe = new SmokeProbeDouble(); var run = await Suite(new SmokeInspectorDouble { Clsid = "invalid" }, probe).RunAsync(Request());
            Assert.AreEqual(SmokeStatus.FAIL, run.Results.Single(row => row.Test == SmokeTest.ProgId).Status);
            Assert.AreEqual(0, probe.Calls.Count); Assert.AreEqual(SmokeStatus.FAIL, run.Status);
            Assert.IsTrue(run.Results.Where(row => row.Test > SmokeTest.ArchitectureCompatibility).All(row => row.Status == SmokeStatus.NOT_RUN));
        }
        [TestMethod]
        public async Task Profile95CanBeTestedWithoutInventingComVersionApi()
        {
            var run = await Suite(new SmokeInspectorDouble { Minor = 5 }, new SmokeProbeDouble()).RunAsync(new DoorsSmokeRequest(DoorsVersion.V95, true, modulePath: "/P/M", linkModulePath: "/P/L"));
            Assert.AreEqual(SmokeStatus.PASS, run.Status); Assert.AreEqual(DoorsVersion.V95, run.Results.First().Environment.Compatibility.Target);
            StringAssert.Contains(run.Results.Single(row => row.Test == SmokeTest.ArchitectureCompatibility).Evidence.Metadata["VersionEvidence"], "not a COM-reported client version");
        }
        [TestMethod]
        public async Task MissingLinkTestDataIsExplicitNotRunWhileOtherNativeCasesExecute()
        {
            var probe = new SmokeProbeDouble(); var run = await Suite(new SmokeInspectorDouble(), probe).RunAsync(new DoorsSmokeRequest(live: true));
            Assert.AreEqual(5, probe.Calls.Count); var link = run.Results.Single(row => row.Test == SmokeTest.LinkRead);
            Assert.AreEqual(SmokeStatus.NOT_RUN, link.Status); Assert.AreEqual("LinkTestPathsNotConfigured", link.Reason);
        }
        [DataTestMethod]
        [DataRow(SmokeOperationSemantics.NonIdempotent)] [DataRow(SmokeOperationSemantics.Unknown)]
        public async Task NonIdempotentOrUndeclaredAdapterIsRejectedBeforeAnyInvocation(SmokeOperationSemantics semantics)
        {
            var probe = new SmokeProbeDouble { Semantics = semantics }; var run = await Suite(new SmokeInspectorDouble(), probe).RunAsync(Request());
            Assert.AreEqual(0, probe.Calls.Count); Assert.AreEqual(SmokeStatus.FAIL, run.Status); Assert.IsTrue(run.Results.All(row => row.Attempts == 0));
        }
        [TestMethod]
        public async Task TimeoutQuarantinesFollowingCasesAndTracksOriginalCompletionWithoutRetry()
        {
            var original = new TaskCompletionSource<SmokeProbeEvidence>(TaskCreationOptions.RunContinuationsAsynchronously); int stopCalls = 0;
            var probe = new SmokeProbeDouble { Dispatch = test => new SmokeProbeOperation(original.Task, () => stopCalls++) };
            var run = await Suite(new SmokeInspectorDouble(), probe).RunAsync(Request(timeout: TimeSpan.FromMilliseconds(30)));
            Assert.AreEqual(SmokeStatus.TIMEOUT, run.Status); Assert.AreEqual(4, run.ExitCode); Assert.IsTrue(run.SessionUncertain);
            Assert.AreEqual(1, probe.Calls.Count); Assert.AreEqual(1, stopCalls); Assert.IsFalse(run.Settlement.IsCompleted);
            Assert.IsTrue(run.Results.Where(row => row.Test > SmokeTest.ComSession).All(row => row.Status == SmokeStatus.NOT_RUN));
            original.SetResult(new SmokeProbeEvidence()); var settled = await run.Settlement;
            Assert.AreEqual(1, settled.Count); StringAssert.Contains(settled.Single().Outcome, "NoRedispatch"); Assert.AreEqual(1, probe.Calls.Count);
            Assert.AreEqual(SmokeStatus.TIMEOUT, run.Status); // Late success never rewrites the original deadline result.
        }
        [TestMethod]
        public async Task CancellationKeepsOriginalFaultObservedAndNoFollowingProbeStarts()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var original = new TaskCompletionSource<SmokeProbeEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
                var probe = new SmokeProbeDouble { Dispatch = test => { started.SetResult(true); return new SmokeProbeOperation(original.Task, () => { }); } };
                var task = Suite(new SmokeInspectorDouble(), probe).RunAsync(Request(), cancellationToken: cancellation.Token);
                await started.Task; cancellation.Cancel(); var run = await task;
                Assert.AreEqual(SmokeStatus.CANCELLED, run.Status); Assert.AreEqual(130, run.ExitCode); Assert.IsFalse(run.Settlement.IsCompleted);
                original.SetException(new IOException("PRIVATE_NATIVE_ERROR")); var settled = await run.Settlement;
                Assert.AreEqual(typeof(IOException).FullName, settled.Single().ExceptionType); Assert.AreEqual(1, probe.Calls.Count);
            }
        }
        [TestMethod]
        public async Task InaccessibleRotReportsNotRunAndDoesNotActivateOrRetry()
        {
            var probe = new SmokeProbeDouble { Dispatch = test => new SmokeProbeOperation(Task.FromException<SmokeProbeEvidence>(
                new IntegrationException(IntegrationErrorCode.ActiveSessionUnavailable, "private")), () => { }) };
            var run = await Suite(new SmokeInspectorDouble(), probe).RunAsync(Request());
            Assert.AreEqual(SmokeStatus.NOT_RUN, run.Status); Assert.AreEqual(1, probe.Calls.Count);
        }
        [TestMethod]
        public async Task InspectorFailureAndPreCancellationStillProduceElevenReportableCases()
        {
            var probe = new SmokeProbeDouble(); var failed = await Suite(new SmokeInspectorDouble { Failure = new IOException("PRIVATE") }, probe).RunAsync(Request());
            Assert.AreEqual(11, failed.Results.Count); Assert.AreEqual(SmokeStatus.FAIL, failed.Status);
            var cancelled = await Suite(new SmokeInspectorDouble(), probe).RunAsync(Request(), cancellationToken: new CancellationToken(true));
            Assert.AreEqual(11, cancelled.Results.Count); Assert.AreEqual(SmokeStatus.CANCELLED, cancelled.Status); Assert.AreEqual(0, probe.Calls.Count);
        }
        [TestMethod]
        public async Task BrokenLogAndProgressCannotRedispatchOrChangeTestOutcomes()
        {
            var probe = new SmokeProbeDouble(); var run = await Suite(new SmokeInspectorDouble(), probe, new ExecutorTestLog { ThrowOnWrite = true }).RunAsync(Request(), new ThrowingSmokeProgress());
            Assert.AreEqual(SmokeStatus.PASS, run.Status); Assert.AreEqual(6, probe.Calls.Count);
        }
        private sealed class ThrowingSmokeProgress : IProgress<SmokeTestResult> { public void Report(SmokeTestResult value) { throw new IOException(); } }
        [TestMethod]
        public async Task ReportsHavePerTestLogsEnvironmentAndImmutableDeadlinePlusLateSettlement()
        {
            using (var files = new ExecutorTestContext())
            {
                var original = new TaskCompletionSource<SmokeProbeEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
                var probe = new SmokeProbeDouble { Dispatch = test => new SmokeProbeOperation(original.Task, () => { }) };
                var run = await Suite(new SmokeInspectorDouble(), probe).RunAsync(Request(timeout: TimeSpan.FromMilliseconds(20)));
                string directory = Path.Combine(files.Root, "report"); var writer = new DoorsSmokeReportWriter(); writer.WriteInitial(directory, run);
                var before = File.ReadAllBytes(Path.Combine(directory, "report.json")); var parsed = ExportTestContext.Json<SmokeReportTestDto>(Path.Combine(directory, "report.json"));
                Assert.AreEqual("TIMEOUT", parsed.Status); Assert.IsTrue(parsed.ReadOnly); Assert.IsFalse(parsed.AutoRetry); Assert.AreEqual(11, parsed.Tests.Length);
                foreach (var test in parsed.Tests) { Assert.IsTrue(File.Exists(Path.Combine(directory, test.LogFile))); Assert.IsNotNull(test.Environment.TargetProfile); }
                original.SetException(new IOException("PRIVATE_NATIVE_ERROR")); writer.WriteSettlement(directory, run, await run.Settlement);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory, "report.json")));
                Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "settlement.json")).Contains("PRIVATE_NATIVE_ERROR"));
                Assert.ThrowsException<IntegrationException>(() => writer.WriteInitial(directory, run));
            }
        }
        [DataTestMethod]
        [DataRow(0)] [DataRow(1801)]
        public void InvalidTimeoutIsRejectedBeforeEnvironmentAccess(int seconds) => Assert.ThrowsException<ArgumentException>(() => new DoorsSmokeRequest(timeout: TimeSpan.FromSeconds(seconds)));
    }
    [DataContract] public sealed class SmokeReportTestDto
    {
        [DataMember(Name="status")] public string Status;
        [DataMember(Name="readOnly")] public bool ReadOnly;
        [DataMember(Name="automaticRetryAllowed")] public bool AutoRetry;
        [DataMember(Name="tests")] public SmokeCaseTestDto[] Tests;
    }
    [DataContract] public sealed class SmokeCaseTestDto
    {
        [DataMember(Name="test")] public string Test;
        [DataMember(Name="status")] public string Status;
        [DataMember(Name="logFile")] public string LogFile;
        [DataMember(Name="environment")] public SmokeEnvironmentTestDto Environment;
    }
    [DataContract] public sealed class SmokeEnvironmentTestDto
    { [DataMember(Name="targetProfile")] public string TargetProfile; }
}
