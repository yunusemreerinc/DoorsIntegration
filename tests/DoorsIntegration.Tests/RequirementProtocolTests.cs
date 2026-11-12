using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class RequirementProtocolTests
    {
        [DataTestMethod]
        [DataRow("correlation")]
        [DataRow("path")]
        [DataRow("baseline")]
        [DataRow("footer")]
        [DataRow("count")]
        [DataRow("accounting")]
        [DataRow("trailing")]
        [DataRow("unterminated")]
        [DataRow("duplicateId")]
        [DataRow("order")]
        [DataRow("selfParent")]
        [DataRow("rootLevel")]
        [DataRow("leakedDeniedValue")]
        [DataRow("unknownStatus")]
        [DataRow("numericEnum")]
        [DataRow("wrongAttribute")]
        [DataRow("escape")]
        [DataRow("deletedExcluded")]
        [DataRow("notRequested")]
        [DataRow("negativeId")]
        [DataRow("notOwnedCleanup")]
        [DataRow("unloadedAvailable")]
        [DataRow("objectEnd")]
        [DataRow("nonAvailableObjects")]
        public async Task CorruptOrMismatchedFilesAreRejectedBeforeSnapshotIsReturned(string mutation)
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => Mutate(RequirementTestContext.Batch(id, context.Request, RequirementTestContext.Object(1, 10)), mutation, id, context.Request);
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.RequirementProtocolFailure, error.Code, mutation);
                Assert.IsFalse(File.Exists(context.OutputPath));
                Assert.IsFalse(error.Message.Contains("REQ-10"));
            }
        }

        private static string Mutate(string text, string mutation, Guid id, RequirementReadRequest request)
        {
            switch (mutation)
            {
                case "correlation": return text.Replace(id.ToString("N"), Guid.NewGuid().ToString("N"));
                case "path": return text.Replace("M\t/P/M", "M\t/P/Other");
                case "baseline": return text.Replace("Current\t\t\t\t0", "Current\t\t\t\t1");
                case "footer": return text.Substring(0, text.IndexOf("END\t", StringComparison.Ordinal));
                case "count": return text.Replace("END\tAvailable\t1", "END\tAvailable\t0");
                case "accounting": return text.Replace("END\tAvailable\t1\t1", "END\tAvailable\t1\t2");
                case "trailing": return text + "extra\n";
                case "unterminated": return text.TrimEnd('\n');
                case "duplicateId": return RequirementTestContext.Batch(id, request, RequirementTestContext.Object(1, 10) + RequirementTestContext.Object(2, 10), count: 2);
                case "order": return RequirementTestContext.Batch(id, request, RequirementTestContext.Object(2, 10) + RequirementTestContext.Object(1, 11), count: 2);
                case "selfParent": return text.Replace("Readable\t1\t\tRoot", "Readable\t1\t10\tResolved");
                case "rootLevel": return text.Replace("Readable\t1\t\tRoot", "Readable\t2\t\tRoot");
                case "leakedDeniedValue": return text.Replace("\tReadable\tbody", "\tAccessDenied\tbody");
                case "unknownStatus": return text.Replace("\tReadable\tbody", "\tSecretStatus\tbody");
                case "numericEnum": return text.Replace("\tReadable\tbody", "\t0\tbody");
                case "wrongAttribute": return text.Replace("V\tObject Text", "V\tOther");
                case "escape": return text.Replace("\tbody\t", "\tbad\\q\t");
                case "deletedExcluded": return text.Replace("\tRoot\t0", "\tRoot\t1");
                case "notRequested": return text.Replace("\tReadable\tbody", "\tNotRequested\t");
                case "negativeId": return text.Replace("O\t1\t10", "O\t1\t-1");
                case "notOwnedCleanup": return text.Replace("\tClosed\n", "\tNotOwned\n");
                case "unloadedAvailable": return text.Replace("OwnedReadOnly", "NotLoaded").Replace("\tClosed\n", "\tNotOwned\n");
                case "objectEnd": return text.Replace("\nE\n", "\nX\n");
                case "nonAvailableObjects": return text.Replace("END\tAvailable", "END\tReadFailed");
                default: throw new InvalidOperationException();
            }
        }

        [TestMethod]
        public async Task Utf8BomAndCrLfDoNotAlterRichText()
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => "\uFEFF" + RequirementTestContext.Batch(id, context.Request, RequirementTestContext.Object(1, 10)).Replace("\n", "\r\n");
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                    Assert.AreEqual("{\\rtf1 body}", snapshot.ReadObjects().Single().Text.RichTextWithOle);
            }
        }

        [TestMethod]
        public async Task InvalidUtf8IsRejectedAndCleaned()
        {
            using (var context = new RequirementTestContext())
            {
                context.RawReply = new byte[] { 0xc3, 0x28, 10 };
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.RequirementProtocolFailure, error.Code);
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }

        [TestMethod]
        public async Task FieldAndPerObjectLimitsRejectUntrustedOversizePayloads()
        {
            foreach (bool fieldLimit in new[] { true, false })
            {
                var request = new RequirementReadRequest("/P/M", maxFieldCharacters: fieldLimit ? 16 : 2048, maxObjectCharacters: 1024);
                using (var context = new RequirementTestContext(request))
                {
                    context.Reply = id => RequirementTestContext.Batch(id, request, RequirementTestContext.Object(1, 10, new string('x', fieldLimit ? 17 : 800), new string('r', 800)));
                    var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(request));
                    Assert.AreEqual(IntegrationErrorCode.RequirementProtocolFailure, error.Code);
                    Assert.IsFalse(File.Exists(context.OutputPath));
                }
            }
        }

        [TestMethod]
        public async Task FileSizeCapIsEnforcedByExecutorBeforeParsing()
        {
            var request = new RequirementReadRequest("/P/M", maxFileBytes: 65536);
            using (var context = new RequirementTestContext(request))
            {
                context.RawReply = new byte[65537];
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(request));
                Assert.AreEqual(IntegrationErrorCode.DxlResultTooLarge, error.Code);
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }

        [DataTestMethod]
        [DataRow("duplicate")]
        [DataRow("builtin")]
        [DataRow("newline")]
        [DataRow("surrogate")]
        [DataRow("tooMany")]
        [DataRow("maxObjects")]
        [DataRow("maxBytes")]
        [DataRow("root")]
        [DataRow("baseline")]
        public void InvalidRequestsAreRejectedBeforeAnyComWork(string kind)
        {
            var error = Assert.ThrowsException<IntegrationException>(() =>
            {
                switch (kind)
                {
                    case "duplicate": new RequirementReadRequest("/P/M", new[] { "Owner", "Owner" }); break;
                    case "builtin": new RequirementReadRequest("/P/M", new[] { "Object Text" }); break;
                    case "newline": new RequirementReadRequest("/P/M", new[] { "a\nb" }); break;
                    case "surrogate": new RequirementReadRequest("/P/M", new[] { "a\ud800" }); break;
                    case "tooMany": new RequirementReadRequest("/P/M", Enumerable.Range(0, 65).Select(i => "A" + i)); break;
                    case "maxObjects": new RequirementReadRequest("/P/M", maxObjects: 0); break;
                    case "maxBytes": new RequirementReadRequest("/P/M", maxFileBytes: long.MaxValue); break;
                    case "root": new RequirementReadRequest("/"); break;
                    case "baseline": new RequirementBaseline(-1, 0); break;
                }
            });
            Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration, error.Code);
        }

        [TestMethod]
        public async Task MissingExternalScriptFailsBeforeConnecting()
        {
            using (var context = new RequirementTestContext())
            {
                File.Delete(context.Transport.SourcePath);
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.DxlScriptLoadFailed, error.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
        }

        [TestMethod]
        public void ExternalScriptUsesReadOnlyVersionLoadAndRichTextWithOleWithoutDatabaseMutation()
        {
            string script = File.ReadAllText(new RequirementScriptOptions().ScriptPath, new UTF8Encoding(false, true));
            // Static guard only; actual DXL compilation and client behavior are separate manual checks.
            string code = Regex.Replace(script, @"//[^\r\n]*", "");
            StringAssert.Contains(code, "load(rqVersion, false)");
            StringAssert.Contains(code, "baselineExists(rqVersion)");
            StringAssert.Contains(code, "isRead(rqModule)");
            StringAssert.Contains(code, "richTextWithOle(objectRef.attributeName)");
            StringAssert.Contains(code, "entire(rqModule)");
            Assert.IsFalse(Regex.IsMatch(code, @"\b(edit|share|save|undelete|setAccess)\s*\("));
            Assert.IsFalse(Regex.IsMatch(code, @"objectRef\.attributeName\s*="));
        }
    }
}
