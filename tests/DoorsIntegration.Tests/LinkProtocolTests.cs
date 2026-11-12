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
    public class LinkProtocolTests
    {
        [DataTestMethod]
        [DataRow(1, "2")]
        [DataRow(2, "Both")]
        [DataRow(2, "Incoming")]
        [DataRow(3, "Other")]
        [DataRow(4, "0")]
        [DataRow(5, "2")]
        [DataRow(6, "FilterOnly")]
        [DataRow(7, "/P/WrongLinks")]
        [DataRow(8, "/P/Other")]
        [DataRow(9, "Unknown")]
        [DataRow(11, "")]
        [DataRow(12, "")]
        [DataRow(13, "AccessDenied")]
        [DataRow(17, "/P/N/")]
        [DataRow(18, "Baseline")]
        [DataRow(20, "-1")]
        [DataRow(22, "AccessDenied")]
        [DataRow(23, "Matched")]
        [DataRow(24, "secret")]
        [DataRow(24, "bad\\q")]
        [DataRow(25, "NotApplicable")]
        public async Task InvalidRecordNeverEscapesAsSnapshot(int column, string replacement)
        {
            using (var context = new LinkTestContext())
            {
                var fields = LinkTestContext.Row(1).TrimEnd('\n').Split('\t'); fields[column] = replacement;
                context.Reply = id => LinkTestContext.Batch(id, context.Request, string.Join("\t", fields) + "\n");
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.LinkProtocolFailure, error.Code);
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }
        [DataTestMethod]
        [DataRow("id")]
        [DataRow("scope")]
        [DataRow("baseline")]
        [DataRow("footer")]
        [DataRow("count")]
        [DataRow("unresolvedCount")]
        [DataRow("gapCount")]
        [DataRow("missingNewline")]
        [DataRow("trailing")]
        [DataRow("unavailable")]
        [DataRow("openedLimit")]
        [DataRow("cleanupCount")]
        public async Task InvalidEnvelopeAndCountersAreRejected(string mutation)
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id =>
                {
                    string result = LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1));
                    switch (mutation)
                    {
                        case "id": return result.Replace(id.ToString("N"), Guid.NewGuid().ToString("N"));
                        case "scope": return result.Replace("M\t/P/M", "M\t/P/Wrong");
                        case "baseline": return result.Replace("Current\t\t\t\t0", "Current\t\t\t\t1");
                        case "footer": return result.Substring(0, result.IndexOf("END\t", StringComparison.Ordinal));
                        case "count": return result.Replace("END\tAvailable\tAvailable\t1", "END\tAvailable\tAvailable\t0");
                        case "unresolvedCount": return LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1), unresolved: 1);
                        case "gapCount": return LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1), gaps: 1);
                        case "missingNewline": return result.TrimEnd('\n');
                        case "trailing": return result + "extra\n";
                        case "unavailable": return result.Replace("END\tAvailable", "END\tReadFailed");
                        case "openedLimit": return LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1), opened: 66);
                        case "cleanupCount": return LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1), cleanupFailures: 2);
                        default: throw new InvalidOperationException();
                    }
                };
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.LinkProtocolFailure, error.Code);
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }
        [TestMethod]
        public async Task BaselineEndpointMustMatchExactRequestedVersion()
        {
            var request = new LinkReadRequest("/P/M", "/P/Links", baseline: new RequirementBaseline(2, 1, "review"));
            using (var context = new LinkTestContext(request))
            {
                context.Reply = id => LinkTestContext.Batch(id, request, LinkTestContext.Row(1,
                    source: LinkTestContext.Endpoint("/P/M", 10, "REQ-10", kind: "Baseline", version: "2.1(review)")));
                using (var snapshot = await context.Reader.ReadAsync(request))
                    Assert.AreEqual("2.1(review)", snapshot.ReadLinks().Single().Source.Version);
            }
            using (var context = new LinkTestContext(request))
            {
                context.Reply = id => LinkTestContext.Batch(id, request, LinkTestContext.Row(1,
                    source: LinkTestContext.Endpoint("/P/M", 10, "REQ-10", kind: "Baseline", version: "2.0")));
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(request));
                Assert.AreEqual(IntegrationErrorCode.LinkProtocolFailure, error.Code);
            }
        }
        [TestMethod]
        public async Task Utf8BomCrLfAreAcceptedButMalformedUtf8IsRejected()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => "\uFEFF" + LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1)).Replace("\n", "\r\n");
                using (var snapshot = await context.Reader.ReadAsync(context.Request)) Assert.AreEqual(1, snapshot.ReadLinks().Count());
            }
            using (var context = new LinkTestContext())
            {
                context.RawReply = new byte[] { 0xc3, 0x28, 10 };
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.LinkProtocolFailure, error.Code);
            }
        }
        [TestMethod]
        public async Task ExecutorRejectsOversizeFileBeforeParsing()
        {
            using (var context = new LinkTestContext(new LinkReadRequest("/P/M", "/P/Links", maxFileBytes: 65536)))
            {
                context.RawReply = new byte[65537];
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.DxlResultTooLarge, error.Code);
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }
        [TestMethod]
        public async Task LargeFileIsValidatedAndEnumeratedWithoutMaterializingWholeMatrix()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, string.Concat(Enumerable.Range(1, 15000).Select(i => LinkTestContext.Row(i))), count: 15000);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    using (var stream = snapshot.OpenRawStream()) Assert.IsTrue(stream.Length > 2 * 1024 * 1024);
                    Assert.AreEqual(15000, snapshot.ReadLinks().Count());
                    Assert.AreEqual("SYS-20", snapshot.ReadLinks().First().Target.ObjectIdentifier);
                }
            }
        }
        [TestMethod]
        public async Task MissingExternalScriptDoesNotConnect()
        {
            using (var context = new LinkTestContext())
            {
                File.Delete(context.Transport.SourcePath);
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.DxlScriptLoadFailed, error.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
        }
        [DataTestMethod]
        [DataRow("wildcard")]
        [DataRow("root")]
        [DataRow("same")]
        [DataRow("direction")]
        [DataRow("links")]
        [DataRow("modules")]
        [DataRow("marker")]
        [DataRow("markerValue")]
        [DataRow("reference")]
        public void InvalidRequestsFailBeforeTransport(string mutation)
        {
            var error = Assert.ThrowsException<IntegrationException>(() =>
            {
                switch (mutation)
                {
                    case "wildcard": new LinkReadRequest("/P/M", "/P/*"); break;
                    case "root": new LinkReadRequest("/", "/P/Links"); break;
                    case "same": new LinkReadRequest("/P/M", "/P/M"); break;
                    case "direction": new LinkReadRequest("/P/M", "/P/Links", (LinkDirection)99); break;
                    case "links": new LinkReadRequest("/P/M", "/P/Links", maxLinks: 0); break;
                    case "modules": new LinkReadRequest("/P/M", "/P/Links", maxLinkedModules: 257); break;
                    case "marker": new SurrogateMapping("a\nb", "proxy"); break;
                    case "markerValue": new SurrogateMapping("Kind", "\ud800"); break;
                    case "reference": new SurrogateMapping("Kind", "Proxy", ""); break;
                }
            });
            Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration, error.Code);
        }
        [TestMethod]
        public void ScriptGuardsUseVersionedLinksAndReadOnlyLoadsWithoutMutationOrGuessedSurrogateApi()
        {
            var code = Regex.Replace(File.ReadAllText(new LinkScriptOptions().ScriptPath, new UTF8Encoding(false, true)), @"//[^\r\n]*", "");
            StringAssert.Contains(code, "sourceVersion(incoming)");
            StringAssert.Contains(code, "targetVersion(outgoing)");
            StringAssert.Contains(code, "sourceAbsNo(incoming)");
            StringAssert.Contains(code, "targetAbsNo(outgoing)");
            StringAssert.Contains(code, "load(version, false)");
            StringAssert.Contains(code, "each anchorObject <- di_arg_linkPath");
            StringAssert.Contains(code, "close(lxOwned[ownedIndex], false)");
            Assert.IsFalse(Regex.IsMatch(code, @"\b(edit|share|save|undelete|setAccess|isSurrogate)\s*\("));
            Assert.IsFalse(Regex.IsMatch(code, @"\w+\.attributeName\s*="));
            foreach (Match match in Regex.Matches(code, @"\bdelete\(([^)]+)\)"))
                Assert.IsTrue(new[] { "buffer", "record", "seenSources", "targetVersionRef", "sourceVersionRef", "candidateTarget", "lxAnchorVersion" }.Contains(match.Groups[1].Value));
        }
    }
}
