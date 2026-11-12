using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;

using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class WatchdogSettingsTests
    {
        [DataTestMethod]
        [DataRow(DxlInvocationMethod.RunString, true)]
        [DataRow(DxlInvocationMethod.RunString, false)]
        [DataRow(DxlInvocationMethod.RunFile, true)]
        [DataRow(DxlInvocationMethod.RunFile, false)]
        public async Task BothDispatchMethodsUseTheCapturedSettingAndUnlimitedCallsDoNotStartPromptAutomation(DxlInvocationMethod method, bool enabled)
        {
            using (var context = new ExecutorTestContext())
            {
                var settings = new DxlWatchdogSettings { Enabled = enabled };
                var monitor = new CountingAutomation();
                var executor = new DxlExecutor(context.Sessions, new Utf8DxlScriptLoader(), context.Workspaces,
                    context.Log, context.Gate, monitor, settings);
                var operation = executor.Start(new DxlExecutionRequest(context.SourcePath, method: method));
                using (await operation.Completion) { }
                Assert.AreEqual(enabled, operation.WatchdogEnabled);
                var script = context.Sessions.Session.Script;
                StringAssert.StartsWith(script, "pragma encoding, \"UTF-8\"");
                StringAssert.Contains(script, enabled ? "pragma runLim, 1000000" : "pragma runLim, 0");
                Assert.AreEqual(1, script.Split(new[] { "pragma runLim," }, StringSplitOptions.None).Length - 1);
                Assert.AreEqual(enabled ? 1 : 0, monitor.Calls);
                Assert.AreEqual(1, context.Sessions.Session.Calls.Count(c => c == "runStr" || c == "runFile"));
                Assert.IsFalse(context.Gate.IsBusy);
            }
        }

        [TestMethod]
        public async Task ChangingThePolicyNeverRewritesAnInFlightScriptAndReEnablingRestoresTheFiniteLimit()
        {
            using (var context = new ExecutorTestContext())
            {
                var settings = new DxlWatchdogSettings { Enabled = false };
                var executor = new DxlExecutor(context.Sessions, new Utf8DxlScriptLoader(), context.Workspaces,
                    context.Log, context.Gate, new NoDxlRunLimitAutomation(), settings);
                context.Sessions.Session.BlockRun = true;
                var first = executor.Start(new DxlExecutionRequest(context.SourcePath));
                try
                {
                    Assert.IsTrue(context.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                    settings.Enabled = true;
                    Assert.IsFalse(first.WatchdogEnabled);
                    StringAssert.Contains(context.Sessions.Session.Script, "pragma runLim, 0");
                }
                finally { context.Sessions.Session.ReleaseRun.Set(); using (await first.Completion) { } }
                var second = executor.Start(new DxlExecutionRequest(context.SourcePath));
                using (await second.Completion) { }
                Assert.IsTrue(second.WatchdogEnabled);
                StringAssert.Contains(context.Sessions.Session.Script, "pragma runLim, 1000000");
                Assert.AreEqual(TimeSpan.FromSeconds(30), second.EffectiveTimeout, "Watchdog setting must not change app wait time.");
            }
        }

        [DataTestMethod]
        [DataRow(DoorsVersion.V95)]
        [DataRow(DoorsVersion.V97)]
        public void ProbeAndOfflinePreviewUseThePolicyAndRetainTheResponseProtocol(DoorsVersion version)
        {
            var settings = new DxlWatchdogSettings();
            var protocol = new ReadOnlyProbeProtocol(settings);
            var id = Guid.NewGuid();
            StringAssert.Contains(protocol.CreateScript(version, id), "pragma runLim, 1000000\n");
            settings.Enabled = false;
            var script = protocol.CreateScript(version, id);
            StringAssert.Contains(script, "pragma runLim, 0\n");
            StringAssert.Contains(script, "oleSetResult(\"DOORS_INTEGRATION|1|" + id.ToString("N") + "|OK\")");
            protocol.ValidateResponse(id, "DOORS_INTEGRATION|1|" + id.ToString("N") + "|OK");
            settings.Enabled = true;
            StringAssert.Contains(protocol.CreateScript(version, id), "pragma runLim, 1000000\n");
        }

        [TestMethod]
        public void PackagedRequirementAndLinkSourcesNoLongerOverrideTheCentralWatchdog()
        {
            foreach (var path in new[] { new RequirementScriptOptions().ScriptPath, new LinkScriptOptions().ScriptPath })
                Assert.IsFalse(File.ReadAllText(path).Contains("pragma runLim,"), path);
        }
        private sealed class CountingAutomation : IDxlRunLimitAutomation
        {
            public int Calls { get; private set; }
            public IDisposable Watch(Guid correlationId, Func<bool> mayContinue)
            { Calls++; return new NoDxlRunLimitAutomation().Watch(correlationId, mayContinue); }
        }
    }
}
