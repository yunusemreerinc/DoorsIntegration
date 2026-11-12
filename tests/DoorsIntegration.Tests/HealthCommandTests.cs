using System.IO;
using System.Threading;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class HealthCommandTests
    {
        [TestMethod]
        public void DiagnoseIsOfflineAndExplainsSkippedChecks()
        {
            var fixture = new HealthFixture();
            var output = new StringWriter();
            Assert.AreEqual(0, Create(fixture).Run(new[] { "diagnose" }, output, new StringWriter()));
            Assert.AreEqual(0, fixture.Factory.Calls);
            StringAssert.Contains(output.ToString(), "[Passed] ComRegistration");
            StringAssert.Contains(output.ToString(), "[Passed] ClientVersion");
            StringAssert.Contains(output.ToString(), "[Skipped] DxlExecution");
            StringAssert.Contains(output.ToString(), "[Skipped] DxlResponse");
            StringAssert.Contains(output.ToString(), "Registry view X86");
            StringAssert.Contains(output.ToString(), "Registry view X64");
        }

        [DataTestMethod]
        [DataRow("ping")]
        [DataRow("diagnose")]
        public void LiveCommandsDefaultToAttachAndPrintRoundTripSteps(string command)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                var output = new StringWriter();
                var error = new StringWriter();
                var args = command == "ping" ? new[] { command } : new[] { command, "--live" };
                Assert.AreEqual(0, Create(fixture).Run(args, output, error));
                Assert.AreEqual(DoorsConnectionMode.AttachExisting, fixture.Factory.LastMode);
                StringAssert.Contains(output.ToString(), "[Passed] DxlExecution");
                StringAssert.Contains(output.ToString(), "[Passed] DxlResponse");
                StringAssert.Contains(output.ToString(), "PONG");
                Assert.AreEqual("", error.ToString());
            });
        }

        [TestMethod]
        public void ActivateIsExplicitAndTargetIsForwarded()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Environment.Minor = 5;
                var output = new StringWriter();
                Assert.AreEqual(0, Create(fixture).Run(new[] { "ping", "--target", "9.5", "--connection", "activate" }, output, new StringWriter()));
                Assert.AreEqual(DoorsConnectionMode.Activate, fixture.Factory.LastMode);
                StringAssert.Contains(output.ToString(), "Target profile: 9.5");
            });
        }

        [TestMethod]
        public void FailedStepHasActionableStderrAndCentralExitCode()
        {
            var fixture = new HealthFixture();
            fixture.Environment.State = ComRegistrationState.NotRegistered;
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.AreEqual(14, Create(fixture).Run(new[] { "diagnose" }, output, error));
            StringAssert.Contains(error.ToString(), "ComRegistration (14/ComNotRegistered)");
            StringAssert.Contains(error.ToString(), "DOORS.Application");
            StringAssert.Contains(error.ToString(), "Request:");
            Assert.AreEqual(0, fixture.Factory.Calls);
        }

        [TestMethod]
        public void HelpForLiveCommandNeverInspectsOrConnects()
        {
            var fixture = new HealthFixture();
            var output = new StringWriter();
            Assert.AreEqual(0, Create(fixture).Run(new[] { "ping", "--help" }, output, new StringWriter()));
            Assert.AreEqual(0, fixture.Environment.Calls);
            Assert.AreEqual(0, fixture.Factory.Calls);
            StringAssert.Contains(output.ToString(), "doors diagnose");
            StringAssert.Contains(output.ToString(), "doors ping");
        }

        [DataTestMethod]
        [DataRow("diagnose", "--probe")]
        [DataRow("ping", "--dry-run")]
        [DataRow("ping", "--live")]
        [DataRow("--dry-run", "--live")]
        [DataRow("diagnose", "ping")]
        [DataRow("--target", "diagnose")]
        [DataRow("ping", "--script")]
        public void InvalidVerbCombinationsNeverInspectOrConnect(string first, string second)
        {
            var fixture = new HealthFixture();
            Assert.AreEqual(2, Create(fixture).Run(new[] { first, second }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, fixture.Environment.Calls);
            Assert.AreEqual(0, fixture.Factory.Calls);
        }

        [TestMethod]
        public void ConnectionIsRejectedForOfflineDiagnoseAndRepeatedLiveIsRejected()
        {
            var fixture = new HealthFixture();
            var app = Create(fixture);
            Assert.AreEqual(2, app.Run(new[] { "diagnose", "--connection", "activate" }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(2, app.Run(new[] { "diagnose", "--live", "--live" }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, fixture.Factory.Calls);
            Assert.AreEqual(0, fixture.Environment.Calls);
        }

        [TestMethod]
        public void DefaultDiResolvesStagedChecksWithHostEnvironmentOverride()
        {
            var fixture = new HealthFixture();
            var services = new ServiceCollection();
            services.AddSingleton<IDoorsEnvironmentDiagnostics>(fixture.Environment);
            services.AddSingleton<IIntegrationLogger>(fixture.Logger);
            services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
            {
                Assert.IsNotNull(provider.GetRequiredService<IDoorsHealthChecks>());
                Assert.AreEqual(0, provider.GetRequiredService<ConsoleApplication>()
                    .Run(new[] { "diagnose" }, new StringWriter(), new StringWriter()));
            }
        }

        private static ConsoleApplication Create(HealthFixture fixture) =>
            new ConsoleApplication(new FakeDiagnostics(), new ReadOnlyProbeProtocol(),
                new IntegrationErrorHandler(fixture.Logger), fixture.Environment, fixture.Service);
    }
}
