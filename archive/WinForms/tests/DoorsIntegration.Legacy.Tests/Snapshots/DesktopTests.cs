using System;
using System.Linq;
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
    public class DesktopTests
    {
        [TestMethod]
        public async Task RunnerExecutesOnSeparateStaThread()
        {
            var callerId = Thread.CurrentThread.ManagedThreadId;
            var runner = new StaOperationRunner();
            var workerId = await runner.RunAsync(() =>
            {
                Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return Thread.CurrentThread.ManagedThreadId;
            });
            Assert.AreNotEqual(callerId, workerId);
        }

        [TestMethod]
        public async Task RunnerPreservesExceptionAndAllowsNextOperation()
        {
            var runner = new StaOperationRunner();
            var cause = new InvalidOperationException("test failure");
            var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await runner.RunAsync<int>(() => { throw cause; }));
            Assert.AreSame(cause, failure);
            Assert.AreEqual(42, await runner.RunAsync(() => 42));
        }

        [TestMethod]
        public async Task RunnerRejectsOverlappingOperations()
        {
            var runner = new StaOperationRunner();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var first = runner.RunAsync(() =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    return 1;
                });
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                    var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () =>
                        await runner.RunAsync(() => 2));
                    Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration, failure.Code);
                }
                finally { release.Set(); }
                Assert.AreEqual(1, await first);
            }
        }

        [TestMethod]
        public void PreviewUsesSelectedProfileWithoutCallingDiagnostics()
        {
            var fake = new FakeDiagnostics();
            var logger = new RecordingLogger();
            var operations = Create(fake, logger);
            var result = operations.CreatePreview(DoorsVersion.V95);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(0, fake.Calls);
            StringAssert.Contains(result.Script, result.CorrelationId.ToString("N"));
            StringAssert.Contains(logger.Entries.Single(), "TargetProfile=9.5");
        }

        [TestMethod]
        public void InvalidPreviewIsHandledWithoutConnecting()
        {
            var fake = new FakeDiagnostics();
            var result = Create(fake, new RecordingLogger()).CreatePreview((DoorsVersion)99);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration, result.Failure.Code);
            Assert.AreEqual(0, fake.Calls);
        }

        [TestMethod]
        public async Task DesktopProbeKeepsComLifecycleOnOneStaThread()
        {
            var factory = new FakeAutomationFactory();
            var logger = new RecordingLogger();
            var diagnostics = new ComDoorsDiagnostics(new ReadOnlyProbeProtocol(), logger, factory);
            var result = await Create(diagnostics, logger).RunProbeAsync(DoorsVersion.V97);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(ApartmentState.STA, factory.ConnectApartment);
            Assert.AreEqual(factory.ConnectThreadId, factory.Session.RunThreadId);
            Assert.AreEqual(factory.ConnectThreadId, factory.Session.DisposeThreadId);
            Assert.IsTrue(factory.Session.Disposed);
            Assert.IsTrue(logger.CorrelationIds.All(id => id == result.CorrelationId));
        }

        [TestMethod]
        public async Task ProbeErrorsUseCentralHandlerAndSameRequestId()
        {
            var fake = new FakeDiagnostics
            {
                Failure = new IntegrationException(IntegrationErrorCode.ClientUnavailable, "Client unavailable")
            };
            var logger = new RecordingLogger();
            var result = await Create(fake, logger).RunProbeAsync(DoorsVersion.V95);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(IntegrationErrorCode.ClientUnavailable, result.Failure.Code);
            Assert.AreEqual(fake.LastId, result.CorrelationId);
            Assert.AreEqual(fake.LastId, logger.CorrelationIds.Single());
        }

        [TestMethod]
        public async Task InvalidProbeProfileNeverReachesDiagnostics()
        {
            var fake = new FakeDiagnostics();
            var result = await Create(fake, new RecordingLogger()).RunProbeAsync((DoorsVersion)100);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(0, fake.Calls);
        }

        [TestMethod]
        public void ActivityBufferKeepsLatestFiveHundredEntries()
        {
            using (var activity = new DesktopActivityLog())
            {
                for (var index = 0; index < 505; index++)
                    activity.Write(IntegrationLogLevel.Information, "Test", Guid.NewGuid(), "Status=OK");
                var entries = activity.ReadAfter(0);
                Assert.AreEqual(500, entries.Length);
                Assert.AreEqual(6L, entries[0].Sequence);
                Assert.AreEqual(505L, entries[499].Sequence);
                Assert.AreEqual(1, activity.ReadAfter(504).Length);
            }
        }

        [TestMethod]
        public async Task ActivityLogSupportsWorkerWritesAndUiSnapshots()
        {
            using (var activity = new DesktopActivityLog())
            {
                var runner = new StaOperationRunner();
                await runner.RunAsync(() =>
                {
                    activity.Write(IntegrationLogLevel.Information, "WorkerEvent", Guid.NewGuid(), "Status=OK");
                    return true;
                });
                Assert.AreEqual("WorkerEvent", activity.ReadAfter(0).Single().EventName);
            }
        }

        [TestMethod]
        public void ResolvingWindowShowsOfflinePreviewAndPreservesHostOverrides()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fake = new FakeDiagnostics();
                var services = new ServiceCollection();
                services.AddSingleton<IDoorsDiagnostics>(fake);
                services.AddDoorsDesktop();
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true, ValidateScopes = true
                }))
                {
                    var window = provider.GetRequiredService<MainForm>();
                    var script = (RichTextBox)window.Controls.Find("PreviewTextBox", true).Single();
                    Assert.IsTrue(script.ReadOnly);
                    StringAssert.Contains(script.Text, "oleSetResult");
                    Assert.AreEqual("9.7", ((ComboBox)window.Controls.Find("TargetProfileComboBox", true).Single()).SelectedItem);
                    Assert.AreEqual(0, fake.Calls);
                }
            });
        }

        private static DesktopOperations Create(IDoorsDiagnostics diagnostics, IIntegrationLogger logger) =>
            new DesktopOperations(diagnostics, new ReadOnlyProbeProtocol(), new StaOperationRunner(),
                new IntegrationErrorHandler(logger), logger);
    }
}
