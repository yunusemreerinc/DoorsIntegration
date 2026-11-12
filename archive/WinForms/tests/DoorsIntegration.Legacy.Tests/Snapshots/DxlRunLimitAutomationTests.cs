using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using DoorsIntegration.Desktop;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DxlRunLimitAutomationTests
    {
        private static DoorsClientProcess Client() => new DoorsClientProcess(42, 1000, @"C:\DOORS\doors.exe", 1);
        private static DxlRunLimitAutomationSettings EnabledSettings()
        {
            var settings = new DxlRunLimitAutomationSettings();
            settings.Bind(Client());
            settings.Enabled = true;
            return settings;
        }
        private static RunLimitPrompt Prompt(PromptControlKind kind = PromptControlKind.Button) => new RunLimitPrompt
        {
            Identity = "dialog.1", Modal = true, Text = "The DXL interpreter execution time limit has been reached.",
            Controls = new[]
            {
                new PromptControl { Identity = "double.1", Name = "&Double execution time", Kind = kind, Enabled = true, Visible = true, CanAct = true },
                new PromptControl { Identity = "continue.1", Name = "Continue", Kind = kind, Enabled = true, Visible = true, CanAct = true },
                new PromptControl { Identity = "stop.1", Name = "Stop", Kind = kind, Enabled = true, Visible = true, CanAct = true }
            }
        };

        [TestMethod]
        public void SettingsRequireBindingAndOffOnOrRebindingRevokesAnActiveScope()
        {
            var settings = new DxlRunLimitAutomationSettings();
            Assert.IsFalse(settings.Enabled);
            Assert.ThrowsException<InvalidOperationException>(() => settings.Enabled = true);
            settings.Bind(Client());
            settings.Enabled = true;
            var first = settings.Capture();
            Assert.IsTrue(settings.IsCurrent(first));
            settings.Enabled = false;
            settings.Enabled = true;
            Assert.IsFalse(settings.IsCurrent(first), "Re-enabling must not revive an old watcher.");
            var second = settings.Capture();
            settings.Bind(Client());
            Assert.IsFalse(settings.Enabled);
            Assert.IsFalse(settings.IsCurrent(second));
        }

        [DataTestMethod]
        [DataRow(42, 1000L, @"C:\DOORS\doors.exe", 1, true)]
        [DataRow(43, 1000L, @"C:\DOORS\doors.exe", 1, false)]
        [DataRow(42, 1001L, @"C:\DOORS\doors.exe", 1, false)]
        [DataRow(42, 1000L, @"C:\Other\doors.exe", 1, false)]
        [DataRow(42, 1000L, @"C:\DOORS\doors.exe", 2, false)]
        public void BindingRejectsPidReuseDifferentPathsAndOtherWindowsSessions(int pid, long ticks, string path, int session, bool match)
        {
            Assert.AreEqual(match, DoorsClientProcessLocator.SameIdentity(new DoorsClientProcess(pid, ticks, path, session), Client()));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ThreeDirectChoicesCanBeRecognizedWithoutGuessingTheDialogTitle(bool radio)
        {
            var prompt = Prompt(radio ? PromptControlKind.RadioButton : PromptControlKind.Button);
            Assert.AreEqual("double.1", RunLimitPromptMatcher.Match(prompt).Identity);
            prompt.Controls[0].Name = "Continue, doubling the timeout";
            Assert.IsNotNull(RunLimitPromptMatcher.Match(prompt));
        }

        [DataTestMethod]
        [DataRow("not-modal")]
        [DataRow("no-dxl")]
        [DataRow("hidden")]
        [DataRow("disabled")]
        [DataRow("no-pattern")]
        [DataRow("missing-stop")]
        [DataRow("duplicate-double")]
        [DataRow("additional-ok")]
        [DataRow("different-kind")]
        [DataRow("unknown-language")]
        public void OtherDialogsAndIncompleteOrAmbiguousChoicesAreNeverActionable(string scenario)
        {
            var p = Prompt();
            switch (scenario)
            {
                case "not-modal": p.Modal = false; break;
                case "no-dxl": p.Text = "Approve database changes?"; break;
                case "hidden": p.Controls[0].Visible = false; break;
                case "disabled": p.Controls[0].Enabled = false; break;
                case "no-pattern": p.Controls[0].CanAct = false; break;
                case "missing-stop": p.Controls = p.Controls.Take(2).ToArray(); break;
                case "duplicate-double": p.Controls = p.Controls.Concat(new[] { p.Controls[0] }).ToArray(); break;
                case "additional-ok": p.Controls = p.Controls.Concat(new[] { new PromptControl { Name = "OK", Kind = PromptControlKind.Button } }).ToArray(); break;
                case "different-kind": p.Controls[1].Kind = PromptControlKind.RadioButton; break;
                case "unknown-language": p.Controls[0].Name = "Ausführungszeit verdoppeln"; break;
            }
            Assert.IsNull(RunLimitPromptMatcher.Match(p));
        }

        [TestMethod]
        public void OneActionPerAppearanceRequiresActualDisappearanceEvenWhenControlsTemporarilyChange()
        {
            var policy = new RunLimitPromptPolicy();
            var p = Prompt();
            int actions = 0;
            Func<RunLimitPrompt, bool> act = _ => { actions++; return true; };
            Assert.IsTrue(policy.Tick(new[] { p }, act));
            Assert.IsFalse(policy.Tick(new[] { p }, act));
            p.Controls[0].Enabled = false;
            policy.Tick(new[] { p }, act);
            p.Controls[0].Enabled = true;
            policy.Tick(new[] { p }, act);
            Assert.AreEqual(1, actions);
            policy.Tick(new RunLimitPrompt[0], act);
            Assert.IsTrue(policy.Tick(new[] { p }, act), "A later popup may reuse the same runtime identity after disappearance.");
            Assert.AreEqual(2, actions);
        }

        [TestMethod]
        public void FourDoublingsIsFiniteAndAmbiguousOrUncertainActionsAreNeverRetried()
        {
            var policy = new RunLimitPromptPolicy();
            int actions = 0;
            Func<RunLimitPrompt, bool> act = _ => { actions++; return true; };
            for (int i = 0; i < 8; i++)
            {
                policy.Tick(new[] { Prompt() }, act);
                policy.Tick(new RunLimitPrompt[0], act);
            }
            Assert.AreEqual(4, actions);
            Assert.IsTrue(policy.Stopped);
            var ambiguous = new RunLimitPromptPolicy();
            ambiguous.Tick(new[] { Prompt(), Prompt() }, act);
            Assert.IsTrue(ambiguous.Stopped);
            var uncertain = new RunLimitPromptPolicy();
            uncertain.Tick(new[] { Prompt() }, _ => false);
            uncertain.Tick(new[] { Prompt() }, act);
            Assert.IsTrue(uncertain.Stopped);
            Assert.AreEqual(4, actions);
        }

        [TestMethod]
        public void ASelectionThatDoesNotDismissThePromptStopsWithoutPressingAnotherButton()
        {
            var policy = new RunLimitPromptPolicy();
            int actions = 0;
            for (int i = 0; i < 25; i++) policy.Tick(new[] { Prompt() }, _ => { actions++; return true; });
            Assert.AreEqual(1, actions);
            Assert.IsTrue(policy.Stopped);
        }

        [TestMethod]
        public void DisabledMonitorNeverInspectsTheDesktop()
        {
            var backend = new FakePromptBackend();
            var monitor = new DxlRunLimitAutomation(new DxlRunLimitAutomationSettings(), new ExecutorTestLog(), backend);
            using (monitor.Watch(Guid.NewGuid(), () => true)) { }
            Assert.AreEqual(0, backend.Scans);
            Assert.IsFalse(monitor.HasActiveWorker);
        }

        [TestMethod]
        public void ExistingPromptBeforeDispatchIsNotAdopted()
        {
            var backend = new FakePromptBackend { Prompts = new[] { Prompt() } };
            var monitor = new DxlRunLimitAutomation(EnabledSettings(), new ExecutorTestLog(), backend);
            using (monitor.Watch(Guid.NewGuid(), () => true)) { }
            WaitFor(() => !monitor.HasActiveWorker);
            Assert.AreEqual(0, backend.Actions);
        }

        [TestMethod]
        public void WorkerUsesMtaAndRevalidatesDisableBeforeActingWithBrokenLogger()
        {
            var settings = EnabledSettings();
            var backend = new FakePromptBackend();
            var monitor = new DxlRunLimitAutomation(settings, new ExecutorTestLog { ThrowOnWrite = true }, backend);
            using (monitor.Watch(Guid.NewGuid(), () => true))
            {
                backend.BeforeAct = () => settings.Enabled = false;
                backend.Prompts = new[] { Prompt() };
                WaitFor(() => !monitor.HasActiveWorker);
                Assert.AreEqual(ApartmentState.MTA, backend.Apartment);
                Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, backend.ThreadId);
                Assert.AreEqual(0, backend.Actions);
            }
        }

        [TestMethod]
        public void BlockingUiProviderCannotAccumulateWorkersOrHoldTheComWorkerOnDispose()
        {
            var backend = new FakePromptBackend();
            var monitor = new DxlRunLimitAutomation(EnabledSettings(), new ExecutorTestLog(), backend);
            var scope = monitor.Watch(Guid.NewGuid(), () => true);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                backend.BeforeAct = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
                backend.Prompts = new[] { Prompt() };
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                    scope.Dispose();
                    using (monitor.Watch(Guid.NewGuid(), () => true)) { }
                    Assert.IsTrue(monitor.HasActiveWorker);
                }
                finally { scope.Dispose(); release.Set(); WaitFor(() => !monitor.HasActiveWorker); }
                Assert.AreEqual(0, backend.Actions, "The stopped scope must be checked after a blocking provider returns.");
            }
        }

        [TestMethod]
        public void WatcherLogsExtensionsAndStopsWhenCallerWithdrawsPermission()
        {
            var backend = new FakePromptBackend();
            var log = new ExecutorTestLog();
            var monitor = new DxlRunLimitAutomation(EnabledSettings(), log, backend);
            int allowed = 1;
            using (monitor.Watch(Guid.NewGuid(), () => Volatile.Read(ref allowed) != 0))
            {
                backend.Prompts = new[] { Prompt() };
                WaitFor(() => backend.Actions == 1);
                Interlocked.Exchange(ref allowed, 0);
                WaitFor(() => !monitor.HasActiveWorker);
            }
            Assert.IsTrue(log.Entries.Any(e => e.StartsWith("DxlRunLimitDoubleSelected:")));
            Assert.AreEqual(1, backend.Actions);
        }

        [DataTestMethod]
        [DataRow(DxlInvocationMethod.RunString, false)]
        [DataRow(DxlInvocationMethod.RunFile, false)]
        [DataRow(DxlInvocationMethod.RunString, true)]
        public async Task ExecutorScopeCoversOnlyNativeRunAndClosesOnSuccessOrComFailure(DxlInvocationMethod method, bool failure)
        {
            using (var context = new ExecutorTestContext())
            {
                var automation = new RecordingAutomation();
                var executor = new DxlExecutor(context.Sessions, new Utf8DxlScriptLoader(), context.Workspaces,
                    context.Log, context.Gate, automation);
                context.Sessions.Session.OnRun = _ =>
                {
                    Assert.IsTrue(automation.Active);
                    Assert.IsTrue(automation.MayContinue());
                    Assert.IsNotNull(context.Sessions.Session.Pending, "Result must be initialised before the monitor starts.");
                    StringAssert.Contains(context.Sessions.Session.Script, "pragma runLim, 1000000");
                    Assert.IsFalse(context.Sessions.Session.Script.Contains("pragma runLim, 0"));
                };
                if (failure) context.Sessions.Session.RunFailure = new COMException("test", unchecked((int)0x80004005));
                var op = executor.Start(new DxlExecutionRequest(context.SourcePath, method: method));
                if (failure) await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await op.Completion);
                else using (await op.Completion) { }
                Assert.IsFalse(automation.Active);
                Assert.AreEqual(1, automation.Starts);
                Assert.IsFalse(context.Gate.IsBusy);
                Assert.IsTrue(context.Sessions.Session.Disposed);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SoftTimeoutRetainsWatcherButExplicitCancellationWithdrawsAutomation(bool cancel)
        {
            using (var context = new ExecutorTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var automation = new RecordingAutomation();
                var executor = new DxlExecutor(context.Sessions, new Utf8DxlScriptLoader(), context.Workspaces,
                    context.Log, context.Gate, automation);
                context.Sessions.Session.BlockRun = true;
                var op = executor.Start(new DxlExecutionRequest(context.SourcePath));
                try
                {
                    Assert.IsTrue(context.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                    if (cancel) cancellation.Cancel();
                    await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () =>
                        await op.WaitAsync(cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(20), cancellation.Token));
                    Assert.AreEqual(!cancel, automation.MayContinue());
                    Assert.IsTrue(automation.Active);
                    Assert.IsTrue(context.Gate.IsBusy);
                    if (!cancel)
                    {
                        cancellation.Cancel();
                        Assert.IsFalse(automation.MayContinue(), "A cancellation token remains effective after a soft timeout returned.");
                    }
                }
                finally { context.Sessions.Session.ReleaseRun.Set(); using (await op.Completion) { } }
                Assert.IsFalse(automation.Active);
                Assert.AreEqual(1, context.Sessions.Session.Calls.Count(c => c == "runStr"));
            }
        }

        [TestMethod]
        public void DesktopNativeOptionRemainsAvailableWithoutTheRemovedAppWaitCheckbox()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var native = EnabledSettings();
                native.Enabled = false;
                var diagnostics = new FakeDiagnostics();
                var services = new ServiceCollection();
                services.AddSingleton(native);
                services.AddSingleton<IDoorsDiagnostics>(diagnostics);
                services.AddDoorsDesktop();
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                {
                    var window = provider.GetRequiredService<MainForm>();
                    var checkbox = window.Controls.Find("AutoDoubleRunLimitCheckBox", true).OfType<CheckBox>().Single();
                    Assert.AreEqual(0, window.Controls.Find("DoubleExecutionTimeCheckBox", true).Length);
                    Assert.IsFalse(checkbox.Checked);
                    Assert.IsTrue(checkbox.Enabled);
                    checkbox.Checked = true;
                    Assert.IsTrue(native.Enabled);
                    checkbox.Checked = false;
                    Assert.IsFalse(native.Enabled);
                    Assert.AreEqual(0, diagnostics.Calls);
                }
            });
        }

        [TestMethod]
        public void FreshDesktopKeepsNativeAutomationOffUntilAnActualClientIsBound()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var services = new ServiceCollection();
                services.AddDoorsDesktop();
                using (var provider = services.BuildServiceProvider())
                {
                    var window = provider.GetRequiredService<MainForm>();
                    var checkbox = window.Controls.Find("AutoDoubleRunLimitCheckBox", true).OfType<CheckBox>().Single();
                    Assert.IsFalse(checkbox.Enabled);
                    Assert.IsFalse(checkbox.Checked);
                    Assert.IsNull(provider.GetRequiredService<DxlRunLimitAutomationSettings>().Client);
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ProbeMonitorIsClosedOnSuccessOrNativeFailure(bool failure)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                var automation = new RecordingAutomation();
                var gate = new DoorsOperationGate();
                var diagnostics = new ComDoorsDiagnostics(new ReadOnlyProbeProtocol(), new RecordingLogger(), factory, gate, automation);
                if (failure) factory.Session.RunFailure = new COMException("test failure");
                if (failure) Assert.ThrowsException<IntegrationException>(() => diagnostics.Probe(DoorsVersion.V97, Guid.NewGuid()));
                else diagnostics.Probe(DoorsVersion.V95, Guid.NewGuid());
                Assert.AreEqual(1, automation.Starts);
                Assert.IsFalse(automation.Active);
                Assert.IsTrue(factory.Session.Disposed);
                Assert.IsFalse(gate.IsBusy);
            });
        }

        [DataTestMethod]
        [DataRow(false, 0)]
        [DataRow(true, 1)]
        public void HealthMonitorIsUsedOnlyForLiveDxlExecution(bool live, int expected)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                var automation = new RecordingAutomation();
                var service = new DoorsHealthChecks(fixture.Environment, fixture.Factory, new ReadOnlyProbeProtocol(),
                    fixture.Gate, new IntegrationErrorHandler(fixture.Logger), fixture.Logger, automation);
                service.Run(DoorsVersion.V97, Guid.NewGuid(), live);
                Assert.AreEqual(expected, automation.Starts);
                Assert.IsFalse(automation.Active);
                Assert.IsFalse(fixture.Gate.IsBusy);
            });
        }

        private static void WaitFor(Func<bool> condition) => Assert.IsTrue(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "Background worker did not reach the expected state.");

        private sealed class FakePromptBackend : IRunLimitPromptBackend
        {
            private IReadOnlyList<RunLimitPrompt> prompts = new RunLimitPrompt[0];
            private int actions;
            private int scans;
            public IReadOnlyList<RunLimitPrompt> Prompts { get => Volatile.Read(ref prompts); set => Volatile.Write(ref prompts, value); }
            public int Actions => Volatile.Read(ref actions);
            public int Scans => Volatile.Read(ref scans);
            public Action BeforeAct { get; set; }
            public ApartmentState Apartment { get; private set; }
            public int ThreadId { get; private set; }
            public IReadOnlyList<RunLimitPrompt> Scan(DoorsClientProcess client)
            {
                Apartment = Thread.CurrentThread.GetApartmentState();
                ThreadId = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref scans);
                return Prompts;
            }
            public bool TryDouble(DoorsClientProcess client, RunLimitPrompt observed, Func<bool> mayAct)
            {
                BeforeAct?.Invoke();
                if (!mayAct()) return false;
                Interlocked.Increment(ref actions);
                return true;
            }
        }

        private sealed class RecordingAutomation : IDxlRunLimitAutomation
        {
            public bool Active { get; private set; }
            public int Starts { get; private set; }
            public Func<bool> MayContinue { get; private set; }
            public IDisposable Watch(Guid correlationId, Func<bool> mayContinue)
            { Active = true; Starts++; MayContinue = mayContinue; return new Lease(() => Active = false); }
            private sealed class Lease : IDisposable
            {
                private readonly Action dispose;
                public Lease(Action dispose) { this.dispose = dispose; }
                public void Dispose() { dispose(); }
            }
        }
    }
}
