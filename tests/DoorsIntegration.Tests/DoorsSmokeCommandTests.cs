using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DoorsSmokeCommandTests
    {
        private static ConsoleApplication App(SmokeInspectorDouble inspector, SmokeProbeDouble probe) => new ConsoleApplication(new FakeDiagnostics(), new ReadOnlyProbeProtocol(),
            new IntegrationErrorHandler(new ExecutorTestLog()), new FakeEnvironmentDiagnostics(), null,
            new DoorsSmokeCommand(new DoorsSmokeSuite(inspector, probe, new ExecutorTestLog()), new DoorsSmokeReportWriter()));
        [TestMethod]
        public void OfflineCommandProducesNotRunAndElevenLogsWithoutDispatching()
        {
            using (var files = new ExecutorTestContext())
            {
                var probe = new SmokeProbeDouble(); string target = Path.Combine(files.Root, "report"); var output = new StringWriter();
                int code = App(new SmokeInspectorDouble { Registered = false, ProcessCount = 0 }, probe).Run(new[] { "smoke", "--report", target }, output, new StringWriter());
                Assert.AreEqual(3, code); Assert.AreEqual(0, probe.Calls.Count); StringAssert.Contains(output.ToString(), "NOT_RUN");
                Assert.AreEqual(11, Directory.GetFiles(Path.Combine(target, "logs"), "*.jsonl").Length);
                Assert.IsTrue(File.Exists(Path.Combine(target, "settlement.json")));
            }
        }
        [DataTestMethod]
        [DataRow("--retry", "2")] [DataRow("--retry-count", "1")] [DataRow("--script", "danger.dxl")]
        [DataRow("--write", "yes")] [DataRow("--connection", "activate")] [DataRow("--target", "9.6")]
        [DataRow("--timeout-seconds", "0")] [DataRow("--timeout-seconds", "1801")]
        public void ForbiddenOrInvalidFlagsCannotInvokeNativeProbe(string option, string value)
        {
            var probe = new SmokeProbeDouble(); Assert.AreEqual(2, App(new SmokeInspectorDouble(), probe).Run(new[] { "smoke", option, value }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, probe.Calls.Count);
        }
        [TestMethod]
        public void MissingPairedScopeDuplicateFlagsAndExistingReportDirectoryFailBeforeNativeProbe()
        {
            using (var files = new ExecutorTestContext())
            {
                var probe = new SmokeProbeDouble(); var app = App(new SmokeInspectorDouble(), probe);
                foreach (var args in new[] { new[] { "smoke", "--module", "/P/M" }, new[] { "smoke", "--live", "--live" }, new[] { "smoke", "--target", "9.7", "--target", "9.5" },
                    new[] { "smoke", "--live", "--report", files.Root } })
                    Assert.AreEqual(2, app.Run(args, new StringWriter(), new StringWriter()));
                Assert.AreEqual(0, probe.Calls.Count);
            }
        }
        [TestMethod]
        public void Live95CommandForwardsScopeAndCompletesWithReport()
        {
            using (var files = new ExecutorTestContext())
            {
                var probe = new SmokeProbeDouble(); string directory = Path.Combine(files.Root, "report");
                int code = App(new SmokeInspectorDouble { Minor = 5 }, probe).Run(new[] { "smoke", "--live", "--target", "9.5", "--root", "/P", "--module", "/P/M", "--link-module", "/P/L", "--report", directory }, new StringWriter(), new StringWriter());
                Assert.AreEqual(0, code); Assert.AreEqual(6, probe.Calls.Count);
                Assert.AreEqual("9.5", ExportTestContext.Json<SmokeReportTestDto>(Path.Combine(directory, "report.json")).Tests.First().Environment.TargetProfile);
            }
        }
        [TestMethod]
        public async Task InitialTimeoutReportIsPublishedBeforeCliWaitsForOriginalCompletion()
        {
            using (var files = new ExecutorTestContext())
            {
                var original = new TaskCompletionSource<SmokeProbeEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
                var probe = new SmokeProbeDouble { Dispatch = test => new SmokeProbeOperation(original.Task, () => { }) };
                var command = new DoorsSmokeCommand(new DoorsSmokeSuite(new SmokeInspectorDouble(), probe, new ExecutorTestLog()), new DoorsSmokeReportWriter());
                string directory = Path.Combine(files.Root, "report"); var output = new StringWriter(); var error = new StringWriter();
                var pending = command.RunAsync(new[] { "--live", "--timeout-seconds", "1", "--report", directory }, output, error, CancellationToken.None);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(Path.Combine(directory, "report.json"))) { if (DateTime.UtcNow >= deadline) Assert.Fail("Initial report not published."); await Task.Delay(10); }
                Assert.IsFalse(pending.IsCompleted); original.SetResult(new SmokeProbeEvidence()); Assert.AreEqual(4, await pending);
                Assert.IsTrue(File.Exists(Path.Combine(directory, "settlement.json"))); StringAssert.Contains(error.ToString(), "SESSION UNCERTAIN");
            }
        }
        [TestMethod]
        public void DefaultCompositionResolvesSuiteAndPreservesInjectedReadOnlyPort()
        {
            var services = new ServiceCollection(); var probe = new SmokeProbeDouble(); services.AddSingleton<IDoorsSmokeProbe>(probe); services.AddDoorsIntegration();
            services.Replace(ServiceDescriptor.Singleton<IDoorsSmokeEnvironmentInspector>(new SmokeInspectorDouble()));
            using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            { Assert.AreSame(probe, provider.GetRequiredService<IDoorsSmokeProbe>()); Assert.IsNotNull(provider.GetRequiredService<IDoorsSmokeSuite>()); }
        }
    }
}
