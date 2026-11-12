using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DxlPreparationTests
    {
        [TestMethod]
        public void QuotesBackslashesAndNewlinesAreEscapedAsData()
        {
            Assert.AreEqual("\"a\\\"\\\\\\r\\n\\tb\"", DxlStringLiteral.Encode("a\"\\\r\n\tb"));
            var attack = "\");\nhalt\n#include <evil.dxl> //";
            var encoded = DxlStringLiteral.Encode(attack);
            Assert.IsFalse(encoded.Contains("\n"));
            Assert.IsTrue(encoded.Contains("\\n"));
            Assert.IsTrue(encoded.StartsWith("\"\\\"", StringComparison.Ordinal));
        }

        [DataTestMethod]
        [DataRow("\0")]
        [DataRow("\u0001")]
        [DataRow("\u2028")]
        [DataRow("\u2029")]
        public void UnsupportedControlCharactersAreRejected(string value)
        {
            Assert.ThrowsException<ArgumentException>(() => DxlStringLiteral.Encode(value));
        }

        [TestMethod]
        public void UnicodeIsPreservedWithoutInventingDxlUnicodeEscapes()
        {
            Assert.AreEqual("\"Türkçe 😀\"", DxlStringLiteral.Encode("Türkçe 😀"));
            Assert.ThrowsException<ArgumentException>(() => DxlStringLiteral.Encode(new string('\uD800', 1)));
        }

        [DataTestMethod]
        [DataRow("x;quit_()")]
        [DataRow("x\nstring y")]
        [DataRow("1name")]
        [DataRow("with space")]
        public void UnsafeParameterNamesAreRejected(string name)
        {
            Assert.ThrowsException<ArgumentException>(() => new DxlExecutionRequest("unused.dxl",
                parameters: new Dictionary<string, string> { [name] = "value" }));
        }

        [TestMethod]
        public void RequestCopiesParametersAndPreparationUsesReservedPrefix()
        {
            var parameters = new Dictionary<string, string> { ["z"] = "\"; halt", ["a"] = @"C:\folder\file" };
            var request = new DxlExecutionRequest("unused.dxl", parameters: parameters);
            parameters["z"] = "changed";
            var id = Guid.NewGuid();
            var script = DxlExecutionProtocol.Compose(request, id, "oleSetResult(\"OK\")", @"C:\owned\result.dat");
            StringAssert.StartsWith(script, "pragma encoding, \"UTF-8\"");
            StringAssert.Contains(script, "string di_arg_z = " + DxlStringLiteral.Encode("\"; halt") + ";");
            Assert.IsTrue(script.IndexOf("di_arg_a", StringComparison.Ordinal) < script.IndexOf("di_arg_z", StringComparison.Ordinal));
            StringAssert.Contains(script, DxlExecutionProtocol.Prefix(id));
        }

        [TestMethod]
        public void Utf8LoaderAcceptsBomAndRejectsUtf16OrOversizedFiles()
        {
            using (var context = new ExecutorTestContext())
            {
                File.WriteAllText(context.SourcePath, "// Türkçe\noleSetResult(\"OK\")", new UTF8Encoding(true));
                var loader = new Utf8DxlScriptLoader();
                StringAssert.StartsWith(loader.Load(context.SourcePath, 1024), "// Türkçe");
                Assert.ThrowsException<IntegrationException>(() => loader.Load(context.SourcePath, 2));
                File.WriteAllText(context.SourcePath, "oleSetResult(\"OK\")", Encoding.Unicode);
                var failure = Assert.ThrowsException<IntegrationException>(() => loader.Load(context.SourcePath, 1024));
                Assert.AreEqual(IntegrationErrorCode.DxlScriptLoadFailed, failure.Code);
            }
        }

        [TestMethod]
        public void CompletionProtocolRejectsRawPendingOrOtherRequestResults()
        {
            var request = new DxlExecutionRequest("unused.dxl");
            var id = Guid.NewGuid();
            foreach (var invalid in new[] { "raw old result", DxlExecutionProtocol.Pending(id), DxlExecutionProtocol.Prefix(Guid.NewGuid()) + "OK|value" })
                Assert.ThrowsException<IntegrationException>(() => DxlExecutionProtocol.ValidateResponse(request, id, invalid));
            Assert.AreEqual("a|b\nvalue", DxlExecutionProtocol.ValidateResponse(request, id, DxlExecutionProtocol.Prefix(id) + "OK|a|b\nvalue"));
        }

        [TestMethod]
        public void InvalidTimeoutsAndParameterLimitsFailBeforeExecution()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DxlExecutionRequest("unused.dxl", timeout: TimeSpan.Zero));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DxlExecutionRequest("unused.dxl", timeout: TimeSpan.FromDays(2)));
            Assert.ThrowsException<ArgumentException>(() => new DxlExecutionRequest("unused.dxl",
                parameters: new Dictionary<string, string> { ["large"] = new string('a', 65537) }));
        }
    }
}
