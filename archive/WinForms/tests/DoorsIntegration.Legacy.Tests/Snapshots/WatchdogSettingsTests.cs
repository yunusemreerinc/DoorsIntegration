using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Desktop;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class WatchdogSettingsTests
    {
        [TestMethod]
        public void MissingPreferenceDefaultsToEnabledAndSavedChoiceSurvivesRestartAndCanBeRestored()
        {
            using (var context = new ExecutorTestContext())
            {
                var path = Path.Combine(context.Root, "preferences", "settings.xml");
                var store = new DesktopSettingsStore(path, context.Log);
                Assert.IsTrue(new DxlWatchdogSettings().Enabled);
                Assert.IsTrue(store.LoadWatchdogEnabled());
                store.SaveWatchdogEnabled(false);
                Assert.IsFalse(new DesktopSettingsStore(path, context.Log).LoadWatchdogEnabled());
                store.SaveWatchdogEnabled(true);
                Assert.IsTrue(new DesktopSettingsStore(path, context.Log).LoadWatchdogEnabled());
                Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length);
            }
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("<broken")]
        [DataRow("<DoorsIntegrationSettings version='2' dxlWatchdogEnabled='false' />")]
        [DataRow("<DoorsIntegrationSettings version='1' />")]
        [DataRow("<DoorsIntegrationSettings version='1' dxlWatchdogEnabled='not-a-boolean' />")]
        [DataRow("<DoorsIntegrationSettings version='1' dxlWatchdogEnabled='false' /><extra />")]
        [DataRow("<!DOCTYPE x [<!ENTITY choice 'false'>]><DoorsIntegrationSettings version='1' dxlWatchdogEnabled='&choice;' />")]
        public void CorruptOrUnsupportedPreferencesFailBackToEnabledAndLogWithoutRewritingTheFile(string content)
        {
            using (var context = new ExecutorTestContext())
            {
                var path = Path.Combine(context.Root, "settings.xml");
                File.WriteAllText(path, content);
                Assert.IsTrue(new DesktopSettingsStore(path, context.Log).LoadWatchdogEnabled());
                Assert.AreEqual(content, File.ReadAllText(path));
                Assert.IsTrue(context.Log.Entries.Any(e => e.StartsWith("DesktopSettingsLoadFailed:")));
            }
        }

        [TestMethod]
        public void OversizedSettingsAndBrokenLoggerStillUseEnabledDefault()
        {
            using (var context = new ExecutorTestContext())
            {
                var path = Path.Combine(context.Root, "settings.xml");
                File.WriteAllText(path, new string('x', 5000));
                context.Log.ThrowOnWrite = true;
                Assert.IsTrue(new DesktopSettingsStore(path, context.Log).LoadWatchdogEnabled());
            }
        }

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

        [TestMethod]
        public void DesktopLoadsTheSharedPreferenceIntoSettingsTabAndOfflinePreviewWithoutConnecting()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var store = new MemorySettingsStore { Enabled = false };
                var diagnostics = new FakeDiagnostics();
                var services = new ServiceCollection();
                services.AddSingleton<IDesktopSettingsStore>(store);
                services.AddSingleton<IDoorsDiagnostics>(diagnostics);
                services.AddDoorsDesktop();
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                {
                    var window = provider.GetRequiredService<MainForm>();
                    Assert.AreEqual(1, store.Loads);
                    Assert.IsFalse(provider.GetRequiredService<DxlWatchdogSettings>().Enabled);
                    Assert.IsFalse(window.Controls.Find("DxlWatchdogEnabledCheckBox", true).OfType<CheckBox>().Single().Checked);
                    Assert.IsNotNull(window.Controls.Find("SettingsTabPage", true).Single());
                    var preview = window.Controls.Find("PreviewTextBox", true).OfType<RichTextBox>().Single();
                    StringAssert.Contains(preview.Text, "pragma runLim, 0");
                    Assert.IsFalse(window.Controls.Find("AutoDoubleRunLimitCheckBox", true).OfType<CheckBox>().Single().Enabled);
                    Assert.AreEqual(0, diagnostics.Calls);
                    Assert.AreEqual(0, store.Saves);
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SettingsPanelAppliesOnlyAfterSaveAndDoesNotApplyWhenPersistenceFails(bool failSave)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var settings = new DxlWatchdogSettings();
                var store = new MemorySettingsStore { FailSave = failSave };
                var logger = new RecordingLogger();
                using (var panel = new SettingsPanel(settings, store, new IntegrationErrorHandler(logger), logger, new DoorsOperationGate()))
                {
                    var checkbox = panel.Controls.Find("DxlWatchdogEnabledCheckBox", true).OfType<CheckBox>().Single();
                    var save = panel.Controls.Find("SaveSettingsButton", true).OfType<Button>().Single();
                    checkbox.Checked = false;
                    Assert.IsTrue(settings.Enabled, "A draft edit must not apply before saving.");
                    save.PerformClick();
                    Assert.AreEqual(1, store.Saves);
                    Assert.AreEqual(failSave, settings.Enabled);
                    if (!failSave) Assert.IsFalse(store.Enabled);
                }
            });
        }

        [TestMethod]
        public void SettingsCannotBeSavedWhileAnotherDoorsOperationOwnsTheGate()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var gate = new DoorsOperationGate();
                var settings = new DxlWatchdogSettings();
                var store = new MemorySettingsStore();
                var logger = new RecordingLogger();
                using (var panel = new SettingsPanel(settings, store, new IntegrationErrorHandler(logger), logger, gate))
                using (gate.Enter())
                {
                    panel.RefreshAvailability();
                    Assert.IsFalse(panel.Controls.Find("DxlWatchdogEnabledCheckBox", true).Single().Enabled);
                    var save = panel.Controls.Find("SaveSettingsButton", true).OfType<Button>().Single();
                    Assert.IsFalse(save.Enabled);
                    save.PerformClick();
                    Assert.AreEqual(0, store.Saves);
                    Assert.IsTrue(settings.Enabled);
                }
            });
        }

        private sealed class MemorySettingsStore : IDesktopSettingsStore
        {
            public bool Enabled { get; set; } = true;
            public bool FailSave { get; set; }
            public int Loads { get; private set; }
            public int Saves { get; private set; }
            public bool LoadWatchdogEnabled() { Loads++; return Enabled; }
            public void SaveWatchdogEnabled(bool enabled)
            {
                Saves++;
                if (FailSave) throw new IOException("test persistence failure");
                Enabled = enabled;
            }
        }

        private sealed class CountingAutomation : IDxlRunLimitAutomation
        {
            public int Calls { get; private set; }
            public IDisposable Watch(Guid correlationId, Func<bool> mayContinue)
            { Calls++; return new NoDxlRunLimitAutomation().Watch(correlationId, mayContinue); }
        }
    }
}
