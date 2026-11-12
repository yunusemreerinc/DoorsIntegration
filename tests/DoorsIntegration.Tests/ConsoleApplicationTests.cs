using System;
using System.IO;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ConsoleApplicationTests
    {
        [TestMethod]
        public void DefaultRunDoesNotConnect()
        {
            var fake = new FakeDiagnostics();
            var output = new StringWriter();
            Assert.AreEqual(0, Create(fake).Run(new string[0], output, new StringWriter()));
            Assert.AreEqual(0, fake.Calls);
            StringAssert.Contains(output.ToString(), "DRY RUN");
            StringAssert.Contains(output.ToString(), "9.7");
        }

        [TestMethod]
        public void DryRunSupports95WithoutConnecting()
        {
            var fake = new FakeDiagnostics();
            var output = new StringWriter();
            Assert.AreEqual(0, Create(fake).Run(new[] { "--dry-run", "--target", "9.5" }, output, new StringWriter()));
            Assert.AreEqual(0, fake.Calls);
            StringAssert.Contains(output.ToString(), "9.5");
        }

        [TestMethod]
        public void HelpDoesNotConnectEvenWhenProbeIsPresent()
        {
            var fake = new FakeDiagnostics();
            Assert.AreEqual(0, Create(fake).Run(new[] { "--probe", "--help" }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, fake.Calls);
        }

        [TestMethod]
        public void ExplicitProbeUsesSelectedProfileAndPrintsItsRequestId()
        {
            var fake = new FakeDiagnostics();
            var output = new StringWriter();
            Assert.AreEqual(0, Create(fake).Run(new[] { "--probe", "--target", "9.5" }, output, new StringWriter()));
            Assert.AreEqual(1, fake.Calls);
            Assert.AreEqual(DoorsVersion.V95, fake.LastTarget);
            Assert.AreNotEqual(Guid.Empty, fake.LastId);
            StringAssert.Contains(output.ToString(), fake.LastId.ToString("N"));
        }

        [DataTestMethod]
        [DataRow("--script", "write.dxl")]
        [DataRow("--target", "9.6")]
        [DataRow("--target", "")]
        [DataRow("--dry-run", "--probe")]
        [DataRow("--probe", "--probe")]
        public void InvalidArgumentsNeverConnect(string first, string second)
        {
            var fake = new FakeDiagnostics();
            Assert.AreEqual(2, Create(fake).Run(new[] { first, second }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, fake.Calls);
        }

        [TestMethod]
        public void MissingAndRepeatedTargetsAreRejected()
        {
            var fake = new FakeDiagnostics();
            Assert.AreEqual(2, Create(fake).Run(new[] { "--target" }, new StringWriter(), new StringWriter()));
            Assert.AreEqual(2, Create(fake).Run(new[] { "--target", "9.5", "--target", "9.7" },
                new StringWriter(), new StringWriter()));
            Assert.AreEqual(0, fake.Calls);
        }

        [TestMethod]
        public void ProbeFailureReturnsCentralExitCodeWithSameCorrelationId()
        {
            var fake = new FakeDiagnostics
            {
                Failure = new IntegrationException(IntegrationErrorCode.ClientUnavailable, "Client unavailable")
            };
            var logger = new RecordingLogger();
            var error = new StringWriter();
            var app = new ConsoleApplication(fake, new ReadOnlyProbeProtocol(), new IntegrationErrorHandler(logger), new FakeEnvironmentDiagnostics());
            Assert.AreEqual(10, app.Run(new[] { "--probe" }, new StringWriter(), error));
            StringAssert.Contains(error.ToString(), fake.LastId.ToString("N"));
            Assert.AreEqual(fake.LastId, logger.CorrelationIds[0]);
        }

        private static ConsoleApplication Create(FakeDiagnostics diagnostics) =>
            new ConsoleApplication(diagnostics, new ReadOnlyProbeProtocol(),
                new IntegrationErrorHandler(new RecordingLogger()), new FakeEnvironmentDiagnostics());
    }
}
