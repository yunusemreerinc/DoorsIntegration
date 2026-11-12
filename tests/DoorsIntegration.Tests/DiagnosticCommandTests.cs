using System.IO;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;

using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DiagnosticCommandTests
    {
        [DataTestMethod]
        [DataRow("--diagnose-com")]
        [DataRow("--diagnose-compatibility")]
        public void OfflineDiagnosticsReportBothViewsWithoutConnecting(string command)
        {
            var diagnostics = new FakeDiagnostics();
            var environment = new FakeEnvironmentDiagnostics();
            var output = new StringWriter();
            Assert.AreEqual(0, Create(diagnostics, environment).Run(new[] { command, "--target", "9.7" }, output, new StringWriter()));
            Assert.AreEqual(0, diagnostics.Calls);
            Assert.AreEqual(1, environment.Calls);
            StringAssert.Contains(output.ToString(), "Registry view X86");
            StringAssert.Contains(output.ToString(), "Registry view X64");
            StringAssert.Contains(output.ToString(), "Process: x86; Apartment: STA");
        }

        [TestMethod]
        public void CompatibilityDistinguishesTargetMismatchFromMissingRegistration()
        {
            var diagnostics = new FakeDiagnostics();
            var environment = new FakeEnvironmentDiagnostics();
            var app = Create(diagnostics, environment);
            Assert.AreEqual(22, app.Run(new[] { "--diagnose-compatibility", "--target", "9.5" }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, app.Run(new[] { "--diagnose-com", "--target", "9.5" }, new StringWriter(), new StringWriter()));
            environment.State = ComRegistrationState.NotRegistered;
            Assert.AreEqual(14, app.Run(new[] { "--diagnose-com" }, new StringWriter(), new StringWriter()));
            environment.State = ComRegistrationState.Unreadable;
            Assert.AreEqual(20, app.Run(new[] { "--diagnose-com" }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, diagnostics.Calls);
        }

        [TestMethod]
        public void UnknownVersionIsNeverReportedAsCompatible()
        {
            var environment = new FakeEnvironmentDiagnostics { Major = null, Minor = null };
            var output = new StringWriter();
            Assert.AreEqual(21, Create(new FakeDiagnostics(), environment).Run(new[] { "--diagnose-compatibility" }, output, new StringWriter()));
            StringAssert.Contains(output.ToString(), "Target file metadata: UNKNOWN");
        }

        [DataTestMethod]
        [DataRow("activate", DoorsConnectionMode.Activate)]
        [DataRow("attach", DoorsConnectionMode.AttachExisting)]
        public void ProbeCommandPassesExplicitConnectionMode(string option, DoorsConnectionMode expected)
        {
            var diagnostics = new FakeDiagnostics();
            Assert.AreEqual(0, Create(diagnostics, new FakeEnvironmentDiagnostics()).Run(
                new[] { "--probe", "--connection", option }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(expected, diagnostics.LastMode);
        }

        [DataTestMethod]
        [DataRow("--probe --connection unknown")]
        [DataRow("--dry-run --connection attach")]
        [DataRow("--diagnose-com --connection activate")]
        [DataRow("--diagnose-com --probe")]
        [DataRow("--diagnose-com --diagnose-compatibility")]
        [DataRow("--probe --connection attach --connection activate")]
        public void InvalidCommandCombinationsDoNotConnect(string command)
        {
            var diagnostics = new FakeDiagnostics();
            var environment = new FakeEnvironmentDiagnostics();
            Assert.AreEqual(2, Create(diagnostics, environment).Run(command.Split(' '), new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, diagnostics.Calls + environment.Calls);
        }
        private static ConsoleApplication Create(FakeDiagnostics diagnostics, FakeEnvironmentDiagnostics environment) =>
            new ConsoleApplication(diagnostics, new ReadOnlyProbeProtocol(), new IntegrationErrorHandler(new RecordingLogger()), environment);
    }
}
