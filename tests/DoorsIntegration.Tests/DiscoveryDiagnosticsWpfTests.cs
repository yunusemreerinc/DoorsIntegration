using System.Threading.Tasks;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("DiscoveryDiagnostics"), DoNotParallelize]
    public class DiscoveryDiagnosticsWpfTests
    {
        [TestMethod]
        public Task MockPingReportsNotRunAndPreservesReadOnlyMode() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await model.IsolatedPingCommand.ExecuteAsync();
                StringAssert.Contains(model.DiagnosticsText, "NOT_RUN");
                Assert.AreEqual(ClientMode.Mock, model.Mode);
                Assert.IsTrue(model.IsReadOnly);
                Assert.AreEqual("", model.ErrorDetails);
            }
        });
    }
}
