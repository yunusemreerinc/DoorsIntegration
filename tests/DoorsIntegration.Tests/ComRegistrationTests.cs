using System;
using System.IO;
using System.Threading;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ComRegistrationTests
    {
        [TestMethod]
        public void ReadsCorrectViewAndRegisteredBinaryMetadataWithoutCom()
        {
            var registry = new FakeRegistryReader();
            Seed(registry, ComArchitecture.X86);
            var binary = new FakeBinaryInspector();
            var inspector = new ComRegistrationInspector(registry, binary);
            var snapshot = inspector.Inspect(ComArchitecture.X86);
            Assert.AreEqual(ComRegistrationState.Registered, snapshot.State);
            Assert.AreEqual(@"C:\Program Files\IBM\doors.exe", binary.LastPath);
            Assert.AreEqual("9.7.2.10", snapshot.FileVersion);
            Assert.AreEqual("x64 (PE machine)", snapshot.PeMachine); // View != server machine.
            Assert.AreEqual(true, snapshot.MatchesTarget(DoorsVersion.V97));
            Assert.AreEqual(false, snapshot.MatchesTarget(DoorsVersion.V95));
            Assert.AreEqual(ComRegistrationState.NotRegistered, inspector.Inspect(ComArchitecture.X64).State);
        }

        [TestMethod]
        public void ExplicitServerExecutableTakesPriorityOverCommandLine()
        {
            var registry = new FakeRegistryReader();
            var classPath = Seed(registry, ComArchitecture.X64);
            registry.Set(ComArchitecture.X64, classPath + @"\LocalServer32", @"D:\IBM\doors.exe", "ServerExecutable");
            var binary = new FakeBinaryInspector();
            new ComRegistrationInspector(registry, binary).Inspect(ComArchitecture.X64);
            Assert.AreEqual(@"D:\IBM\doors.exe", binary.LastPath);
        }

        [DataTestMethod]
        [DataRow("\"C:\\Program Files\\IBM\\doors.exe\" -Embedding", "C:\\Program Files\\IBM\\doors.exe")]
        [DataRow("C:\\IBM\\doors.exe -Embedding", "C:\\IBM\\doors.exe")]
        [DataRow("C:\\Program Files\\IBM\\doors.exe -Embedding", null)]
        [DataRow("\"C:\\broken.exe", null)]
        [DataRow("", null)]
        public void ParsesOnlyUnambiguousExecutablePaths(string command, string expected)
        {
            Assert.AreEqual(expected, ComRegistrationInspector.ParseExecutablePath(null, command));
        }

        [TestMethod]
        public void MissingClsidAndMissingClassAreReportedWithoutBinaryAccess()
        {
            var registry = new FakeRegistryReader();
            var binary = new FakeBinaryInspector();
            var inspector = new ComRegistrationInspector(registry, binary);
            Assert.AreEqual(ComRegistrationState.NotRegistered, inspector.Inspect(ComArchitecture.X86).State);
            registry.Set(ComArchitecture.X86, @"DOORS.Application\CLSID", "invalid-guid");
            Assert.AreEqual(ComRegistrationState.Invalid, inspector.Inspect(ComArchitecture.X86).State);
            registry.Set(ComArchitecture.X86, @"DOORS.Application\CLSID", Guid.NewGuid().ToString("B"));
            Assert.AreEqual(ComRegistrationState.Invalid, inspector.Inspect(ComArchitecture.X86).State);
            Assert.IsNull(binary.LastPath);
        }

        [TestMethod]
        public void RegistryAccessDeniedIsReportedWithoutExternalDetails()
        {
            var registry = new FakeRegistryReader { Failure = new UnauthorizedAccessException("PRIVATE PATH") };
            var result = new ComRegistrationInspector(registry, new FakeBinaryInspector()).Inspect(ComArchitecture.X86);
            Assert.AreEqual(ComRegistrationState.Unreadable, result.State);
            Assert.IsFalse(result.Note.Contains("PRIVATE PATH"));
        }

        [TestMethod]
        public void X64ViewIsUnavailableOn32BitWindows()
        {
            var registry = new FakeRegistryReader { Is64BitOperatingSystem = false, Failure = new Exception("must not read") };
            var snapshot = new ComRegistrationInspector(registry, new FakeBinaryInspector()).Inspect(ComArchitecture.X64);
            Assert.AreEqual(ComRegistrationState.ViewUnavailable, snapshot.State);
        }

        [TestMethod]
        public void MissingBinaryKeepsRegistrationButVersionRemainsUnknown()
        {
            var registry = new FakeRegistryReader();
            Seed(registry, ComArchitecture.X86);
            var binary = new FakeBinaryInspector { Failure = new FileNotFoundException("PRIVATE DATA") };
            var snapshot = new ComRegistrationInspector(registry, binary).Inspect(ComArchitecture.X86);
            Assert.AreEqual(ComRegistrationState.Registered, snapshot.State);
            Assert.IsNull(snapshot.MatchesTarget(DoorsVersion.V97));
            Assert.IsFalse(snapshot.Note.Contains("PRIVATE DATA"));
        }

        [TestMethod]
        public void UncServerMetadataIsNotReadByOfflineDiagnostics()
        {
            var registry = new FakeRegistryReader();
            var classPath = Seed(registry, ComArchitecture.X86);
            registry.Set(ComArchitecture.X86, classPath + @"\LocalServer32", "\"\\\\server\\share\\doors.exe\"");
            var binary = new FakeBinaryInspector();
            var snapshot = new ComRegistrationInspector(registry, binary).Inspect(ComArchitecture.X86);
            Assert.IsNull(binary.LastPath);
            Assert.IsNull(snapshot.MatchesTarget(DoorsVersion.V95));
        }

        [TestMethod]
        public void EnvironmentPlaceholdersDoNotGuessCrossArchitecturePaths()
        {
            var registry = new FakeRegistryReader();
            var classPath = Seed(registry, ComArchitecture.X64);
            registry.Set(ComArchitecture.X64, classPath + @"\LocalServer32", "\"%ProgramFiles%\\IBM\\doors.exe\" -Embedding");
            var binary = new FakeBinaryInspector();
            var snapshot = new ComRegistrationInspector(registry, binary).Inspect(ComArchitecture.X64);
            Assert.IsNull(binary.LastPath);
            Assert.IsNull(snapshot.MatchesTarget(DoorsVersion.V97));
        }

        [TestMethod]
        public void EnvironmentReportReadsBothViewsAndRecordsCallerApartment()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var inspector = new FakeRegistrationInspector();
                var report = new DoorsEnvironmentDiagnostics(inspector).Inspect(DoorsVersion.V95);
                CollectionAssert.AreEqual(new[] { ComArchitecture.X86, ComArchitecture.X64 }, inspector.Reads);
                Assert.AreEqual("STA", report.Apartment);
                Assert.AreEqual(Environment.Is64BitProcess, report.Is64BitProcess);
                Assert.AreEqual(DoorsVersion.V95, report.Target);
            });
        }

        [TestMethod]
        public void BinaryInspectorReadsPeMachineWithoutExecutingBinary()
        {
            var info = new ServerBinaryInspector().Inspect(typeof(DoorsVersion).Assembly.Location);
            Assert.IsTrue(info.Machine.StartsWith(Environment.Is64BitProcess ? "x64" : "x86", StringComparison.Ordinal));
            Assert.IsNotNull(info.Version);
        }

        private static string Seed(FakeRegistryReader registry, ComArchitecture architecture)
        {
            var clsid = Guid.NewGuid().ToString("B"); // Test CLSID, never a hardcoded DOORS CLSID.
            var path = @"CLSID\" + clsid;
            registry.Set(architecture, @"DOORS.Application\CLSID", clsid);
            registry.Keys.Add(architecture + ":" + path);
            registry.Set(architecture, path + @"\LocalServer32", "\"C:\\Program Files\\IBM\\doors.exe\" -Embedding");
            return path;
        }
    }
}
