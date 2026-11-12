using System;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DxlProtocolTests
    {
        private readonly ReadOnlyProbeProtocol protocol = new ReadOnlyProbeProtocol();

        [DataTestMethod]
        [DataRow(DoorsVersion.V95)]
        [DataRow(DoorsVersion.V97)]
        public void BothProfilesGenerateOnlyTheApprovedDiagnostic(DoorsVersion version)
        {
            var id = Guid.Parse("2a12e0fb-05ea-4814-b5d5-34852ecb54bb");
            Assert.AreEqual("// DoorsIntegration: database-read-only probe\n" +
                "pragma runLim, 1000000\n" +
                "oleSetResult(\"DOORS_INTEGRATION|1|2a12e0fb05ea4814b5d534852ecb54bb|OK\")\n",
                protocol.CreateScript(version, id));
        }

        [TestMethod]
        public void CurrentResponseIsAccepted()
        {
            var id = Guid.NewGuid();
            protocol.ValidateResponse(id, "DOORS_INTEGRATION|1|" + id.ToString("N") + "|OK");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("OK")]
        [DataRow("DOORS_INTEGRATION|1|00000000000000000000000000000000|OK")]
        [DataRow("DOORS_INTEGRATION|2|00000000000000000000000000000000|OK")]
        public void MissingMalformedOrStaleResponsesFail(string response)
        {
            var ex = Assert.ThrowsException<IntegrationException>(() => protocol.ValidateResponse(Guid.NewGuid(), response));
            Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, ex.Code);
        }

        [TestMethod]
        public void UnsupportedTargetAndEmptyIdFailBeforeExecution()
        {
            Assert.ThrowsException<IntegrationException>(() => protocol.CreateScript((DoorsVersion)96, Guid.NewGuid()));
            Assert.ThrowsException<ArgumentException>(() => protocol.CreateScript(DoorsVersion.V95, Guid.Empty));
            Assert.ThrowsException<ArgumentException>(() => protocol.ValidateResponse(Guid.Empty, "OK"));
        }
    }
}
