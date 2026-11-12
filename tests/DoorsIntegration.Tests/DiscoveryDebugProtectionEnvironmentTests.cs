using System;
using System.IO;
using System.Security.Cryptography;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("DiscoveryDebugProtectionEnvironment")]
    public class DiscoveryDebugProtectionEnvironmentTests
    {
        [TestMethod]
        public void WindowsCurrentUserDpapiRoundTrip()
        {
            using (var context = new ExecutorTestContext())
            {
                var directory = Path.Combine(context.Root, "dpapi-environment");
                var store = new ProtectedDiscoveryDebugStore(directory);
                var record = new DiscoveryDebugRecord { RequestId = Guid.NewGuid(), RawComResponseUtf16 = DiscoveryDebugRecord.Encode("diagnostic environment probe") };
                try { store.Save(record); }
                catch (CryptographicException) { Assert.Inconclusive("NOT_RUN: CurrentUser DPAPI is unavailable under this execution identity/profile. No plaintext fallback. Protected file IO is tested separately using a test-only protector."); }
                Assert.AreEqual(record.ReadComResponse(), store.Read(record.RequestId).ReadComResponse());
            }
        }
    }
}
