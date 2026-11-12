using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class TraceabilityExportTests
    {
        [TestMethod]
        public async Task JsonPreservesTypedMatrixFieldsUnicodeAndEscapedExternalIdentity()
        {
            var request = new LinkReadRequest("/P/M", "/P/Links", surrogateMapping: new SurrogateMapping("Kind", "Proxy", "URI"));
            const string external = "external://Türkçe/😀\"quoted\"\\line\r\n\t\0\u2028";
            using (var context = new LinkTestContext(request))
            {
                context.Reply = id => LinkTestContext.Batch(id, request, LinkTestContext.Row(1,
                    source: LinkTestContext.Endpoint("/P/M", 10, "REQ-10", surrogate: "NotMatched"),
                    target: LinkTestContext.Endpoint("/P/N", 20, "SYS-20", surrogate: "Matched", external: external, reference: "Readable")));
                using (var snapshot = await context.Reader.ReadAsync(request))
                using (var output = new MemoryStream())
                {
                    new TraceabilityExporter().Export(output, snapshot, TraceabilityFormat.Json);
                    Assert.IsTrue(output.CanWrite);
                    output.Position = 0;
                    var json = (JsonDocument)new DataContractJsonSerializer(typeof(JsonDocument)).ReadObject(output);
                    Assert.AreEqual(1, json.SchemaVersion);
                    Assert.IsTrue(json.Summary.Complete);
                    Assert.AreEqual(1, json.Summary.LinkCount);
                    Assert.AreEqual("ReadableLinkScopeOnly", json.Summary.Visibility);
                    Assert.AreEqual(20, json.Links.Single().TargetAbsolute);
                    Assert.AreEqual(external, json.Links.Single().External);
                    Assert.AreEqual("Matched", json.Links.Single().Surrogate);
                    Assert.AreEqual("/P/Links", json.Links.Single().LinkPath);
                    Assert.AreEqual("Resolved", json.Links.Single().Resolution);
                    Assert.AreEqual(1, snapshot.ReadLinks().Count());
                }
            }
        }
        [DataTestMethod]
        [DataRow("=HYPERLINK(\"bad\",\"x\")")]
        [DataRow(" +SUM(1,2)")]
        [DataRow("-1+1")]
        [DataRow("@malicious")]
        [DataRow("\tformula")]
        [DataRow("\0=1+1")]
        public async Task CsvProtectsSpreadsheetFormulaCellsAndPreservesQuotedMultilineValues(string external)
        {
            var request = new LinkReadRequest("/P/M", "/P/Links", surrogateMapping: new SurrogateMapping("Kind", "Proxy", "URI"));
            using (var context = new LinkTestContext(request))
            {
                context.Reply = id => LinkTestContext.Batch(id, request, LinkTestContext.Row(1,
                    source: LinkTestContext.Endpoint("/P/M", 10, "REQ-10", surrogate: "NotMatched"),
                    target: LinkTestContext.Endpoint("/P/N", 20, "Türkçe,\"ID\"\r\n😀", surrogate: "Matched", external: external, reference: "Readable")));
                using (var snapshot = await context.Reader.ReadAsync(request))
                using (var output = new MemoryStream())
                {
                    new TraceabilityExporter().Export(output, snapshot, TraceabilityFormat.Csv);
                    var rows = ParseCsv(new UTF8Encoding(false, true).GetString(output.ToArray()));
                    Assert.AreEqual(2, rows.Count);
                    Assert.AreEqual(rows[0].Length, rows[1].Length);
                    Assert.AreEqual("'" + external, Cell(rows, "targetExternalReference"));
                    Assert.AreEqual("Türkçe,\"ID\"\r\n😀", Cell(rows, "targetObjectIdentifier"));
                    Assert.AreEqual("20", Cell(rows, "targetAbsoluteNumber"));
                    Assert.AreEqual("Link", Cell(rows, "recordKind"));
                    Assert.AreEqual("true", Cell(rows, "isCompleteForReadableScope"));
                }
            }
        }
        [DataTestMethod]
        [DataRow(TraceabilityFormat.Json)]
        [DataRow(TraceabilityFormat.Csv)]
        public async Task EmptyFailedScopeExportsUncertaintyInsteadOfSuccessfulEmptyMatrix(TraceabilityFormat format)
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, count: 0, visited: 0, moduleStatus: "AccessDenied", ownership: "NotLoaded", opened: 0);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                using (var output = new MemoryStream())
                {
                    new TraceabilityExporter().Export(output, snapshot, format);
                    if (format == TraceabilityFormat.Csv)
                    {
                        var rows = ParseCsv(Encoding.UTF8.GetString(output.ToArray()));
                        Assert.AreEqual("Summary", Cell(rows, "recordKind"));
                        Assert.AreEqual("AccessDenied", Cell(rows, "moduleStatus"));
                        Assert.AreEqual("false", Cell(rows, "isCompleteForReadableScope"));
                    }
                    else
                    {
                        output.Position = 0;
                        var json = (JsonDocument)new DataContractJsonSerializer(typeof(JsonDocument)).ReadObject(output);
                        Assert.IsFalse(json.Summary.Complete);
                        Assert.AreEqual("AccessDenied", json.Summary.ModuleStatus);
                        Assert.AreEqual(0, json.Links.Length);
                    }
                }
            }
        }
        [TestMethod]
        public async Task UnresolvedIncomingSourceIsExportedAsReferenceWithNullAbsoluteNumber()
        {
            using (var context = new LinkTestContext())
            {
                context.Reply = id => LinkTestContext.Batch(id, context.Request, LinkTestContext.Row(1, "Incoming", "IncomingSourceReference", "SourceReferenceOnly",
                    verification: "FilterOnly", source: LinkTestContext.Endpoint("/P/S", null, "", "UnresolvedObject", "Unknown"),
                    target: LinkTestContext.Endpoint("/P/M", 10, "REQ-10")), gaps: 1);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                using (var output = new MemoryStream())
                {
                    new TraceabilityExporter().Export(output, snapshot, TraceabilityFormat.Json);
                    output.Position = 0;
                    var json = (JsonDocument)new DataContractJsonSerializer(typeof(JsonDocument)).ReadObject(output);
                    Assert.AreEqual(0, json.Summary.LinkCount);
                    Assert.AreEqual(1, json.Summary.SourceReferences);
                    Assert.IsNull(json.Links.Single().SourceAbsolute);
                    Assert.AreEqual("IncomingSourceReference", json.Links.Single().Kind);
                    Assert.AreEqual("SourceReferenceOnly", json.Links.Single().Resolution);
                }
            }
        }
        [DataTestMethod]
        [DataRow(TraceabilityFormat.Json)]
        [DataRow(TraceabilityFormat.Csv)]
        public async Task AtomicFileExportPreservesExistingFileUntilExplicitOverwrite(TraceabilityFormat format)
        {
            using (var context = new LinkTestContext())
            using (var snapshot = await context.Reader.ReadAsync(context.Request))
            {
                var exporter = new TraceabilityExporter();
                var path = Path.Combine(context.Transport.Root, "matrix.out");
                File.WriteAllText(path, "original");
                var error = Assert.ThrowsException<IntegrationException>(() => exporter.ExportFile(path, snapshot, format));
                Assert.AreEqual(IntegrationErrorCode.TraceabilityExportFailed, error.Code);
                Assert.AreEqual("original", File.ReadAllText(path));
                Assert.AreEqual(0, Directory.GetFiles(context.Transport.Root, ".doors-trace-*.tmp").Length);
                exporter.ExportFile(path, snapshot, format, overwrite: true);
                StringAssert.Contains(File.ReadAllText(path), "SYS-20");
                Assert.AreEqual(0, Directory.GetFiles(context.Transport.Root, ".doors-trace-*.tmp").Length);
            }
        }
        [TestMethod]
        public async Task FailedEnumerationCannotReplaceExistingExportAndTemporaryIsRemoved()
        {
            using (var context = new LinkTestContext())
            using (var snapshot = await context.Reader.ReadAsync(context.Request))
            {
                string path = Path.Combine(context.Transport.Root, "matrix.json");
                File.WriteAllText(path, "original");
                var error = Assert.ThrowsException<IntegrationException>(() => new TraceabilityExporter().ExportFile(path, new FailingSnapshot(snapshot.Summary), TraceabilityFormat.Json, true));
                Assert.AreEqual(IntegrationErrorCode.LinkProtocolFailure, error.Code);
                Assert.AreEqual("original", File.ReadAllText(path));
                Assert.AreEqual(0, Directory.GetFiles(context.Transport.Root, ".doors-trace-*.tmp").Length);
            }
        }
        [TestMethod]
        public async Task StreamFailureHasTypedErrorAndExporterDoesNotDisposeSnapshot()
        {
            using (var context = new LinkTestContext())
            using (var snapshot = await context.Reader.ReadAsync(context.Request))
            using (var output = new ThrowingStream())
            {
                var error = Assert.ThrowsException<IntegrationException>(() => new TraceabilityExporter().Export(output, snapshot, TraceabilityFormat.Json));
                Assert.AreEqual(IntegrationErrorCode.TraceabilityExportFailed, error.Code);
                Assert.AreEqual(1, snapshot.ReadLinks().Count());
            }
        }
        private static string Cell(List<string[]> rows, string column) => rows[1][Array.IndexOf(rows[0], column)];
        private static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (!quoted && (c == ',' || c == '\r'))
                {
                    fields.Add(field.ToString()); field.Clear();
                    if (c == '\r') { Assert.AreEqual('\n', text[++i]); rows.Add(fields.ToArray()); fields.Clear(); }
                }
                else field.Append(c);
            }
            Assert.IsFalse(quoted); Assert.AreEqual(0, field.Length); Assert.AreEqual(0, fields.Count);
            return rows;
        }
        [DataContract] public sealed class JsonDocument
        {
            [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
            [DataMember(Name = "summary")] public JsonSummary Summary { get; set; }
            [DataMember(Name = "links")] public JsonLink[] Links { get; set; }
        }
        [DataContract] public sealed class JsonSummary
        {
            [DataMember(Name = "isCompleteForReadableScope")] public bool Complete { get; set; }
            [DataMember(Name = "moduleStatus")] public string ModuleStatus { get; set; }
            [DataMember(Name = "linkCount")] public int LinkCount { get; set; }
            [DataMember(Name = "sourceReferenceCount")] public int SourceReferences { get; set; }
            [DataMember(Name = "visibility")] public string Visibility { get; set; }
        }
        [DataContract] public sealed class JsonLink
        {
            [DataMember(Name = "recordKind")] public string Kind { get; set; }
            [DataMember(Name = "sourceAbsoluteNumber")] public int? SourceAbsolute { get; set; }
            [DataMember(Name = "targetAbsoluteNumber")] public int? TargetAbsolute { get; set; }
            [DataMember(Name = "targetExternalReference")] public string External { get; set; }
            [DataMember(Name = "targetSurrogateStatus")] public string Surrogate { get; set; }
            [DataMember(Name = "linkModulePath")] public string LinkPath { get; set; }
            [DataMember(Name = "resolution")] public string Resolution { get; set; }
        }
        private sealed class ThrowingStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count) { throw new IOException("PRIVATE_CONTENT"); }
        }
        private sealed class FailingSnapshot : ILinkSnapshot
        {
            public FailingSnapshot(LinkReadSummary summary) { Summary = summary; }
            public LinkReadSummary Summary { get; }
            public IEnumerable<LinkRecord> ReadLinks() { throw new IntegrationException(IntegrationErrorCode.LinkProtocolFailure, "incomplete"); }
            public Stream OpenRawStream() { throw new NotSupportedException(); }
            public void Dispose() { }
        }
    }
}
