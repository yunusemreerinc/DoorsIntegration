using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using DoorsIntegration.Core;
using DoorsIntegration.Export;

namespace DoorsIntegration.Tests
{
    internal sealed class ExportTestContext : IDisposable
    {
        private readonly ExecutorTestContext files = new ExecutorTestContext();
        public string Root => files.Root;
        public string Destination => Path.Combine(Root, "export");
        public ExecutorTestLog Logger => files.Log;
        public IExportService Service => new ExportService(new IExportFormatWriter[] { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter() }, Logger);
        public ExportRequest Request(ExportFormat format = ExportFormat.Json) => new ExportRequest(Destination, format, progressInterval: 1);
        public static ExportRecord Row(ExportTableKind table, params object[] pairs)
        {
            var values = new Dictionary<string, object>();
            for (int index = 0; index < pairs.Length; index += 2) values.Add((string)pairs[index], pairs[index + 1]);
            return new ExportRecord(table, values);
        }
        public static T Json<T>(string path)
        { using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        public void AssertOnlyReport()
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert.AreEqual(new[] { "report.json" }, Directory.GetFiles(Destination).Select(Path.GetFileName).ToArray());
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, Directory.GetDirectories(Root, ".doors-export-*").Length);
        }
        public void Dispose() => files.Dispose();
    }
    [DataContract] public sealed class ExportTestReport
    {
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "hasPayload")] public bool HasPayload { get; set; }
        [DataMember(Name = "startedAtUtc")] public string Started { get; set; }
        [DataMember(Name = "finishedAtUtc")] public string Finished { get; set; }
        [DataMember(Name = "issueCount")] public long IssueCount { get; set; }
        [DataMember(Name = "csvProtectedCellCount")] public long ProtectedCells { get; set; }
        [DataMember(Name = "issues")] public ExportTestIssue[] Issues { get; set; }
    }
    [DataContract] public sealed class ExportTestIssue
    {
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "column")] public string Column { get; set; }
        [DataMember(Name = "sourceRecordId")] public long? Record { get; set; }
        [DataMember(Name = "errorCode")] public int? ErrorCode { get; set; }
    }
    [DataContract] public sealed class ExportTestDocument
    {
        [DataMember(Name = "schemaVersion")] public int Schema { get; set; }
        [DataMember(Name = "tables")] public ExportTestTables Tables { get; set; }
    }
    [DataContract] public sealed class ExportTestTables
    {
        [DataMember(Name = "Projects")] public ExportTestItem[] Projects { get; set; }
        [DataMember(Name = "Modules")] public ExportTestItem[] Modules { get; set; }
        [DataMember(Name = "Requirements")] public ExportTestRequirement[] Requirements { get; set; }
        [DataMember(Name = "Attributes")] public ExportTestAttribute[] Attributes { get; set; }
        [DataMember(Name = "Links")] public ExportTestLink[] Links { get; set; }
        [DataMember(Name = "Scopes")] public ExportTestScope[] Scopes { get; set; }
        [DataMember(Name = "ValueChunks")] public ExportTestChunk[] Chunks { get; set; }
    }
    [DataContract] public sealed class ExportTestItem
    {
        [DataMember(Name = "fullPath")] public string Path { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "collectedAtUtc")] public string Collected { get; set; }
    }
    [DataContract] public sealed class ExportTestRequirement
    {
        [DataMember(Name = "absoluteNumber")] public long Absolute { get; set; }
        [DataMember(Name = "text")] public string Text { get; set; }
        [DataMember(Name = "isDeleted")] public bool? Deleted { get; set; }
        [DataMember(Name = "recordId")] public long Id { get; set; }
        [DataMember(Name = "chunkedFields")] public string Chunked { get; set; }
    }
    [DataContract] public sealed class ExportTestAttribute
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "plainText")] public string Plain { get; set; }
        [DataMember(Name = "richTextWithOle")] public string Rich { get; set; }
        [DataMember(Name = "plainStatus")] public string Status { get; set; }
        [DataMember(Name = "typedValue")] public decimal? Number { get; set; }
        [DataMember(Name = "typedDateUtc")] public string Date { get; set; }
        [DataMember(Name = "recordId")] public long Id { get; set; }
    }
    [DataContract] public sealed class ExportTestLink
    {
        [DataMember(Name = "sourceAbsoluteNumber")] public int? Source { get; set; }
        [DataMember(Name = "targetAbsoluteNumber")] public int? Target { get; set; }
        [DataMember(Name = "resolution")] public string Resolution { get; set; }
        [DataMember(Name = "sourceSurrogateStatus")] public string Surrogate { get; set; }
    }
    [DataContract] public sealed class ExportTestScope
    {
        [DataMember(Name = "isComplete")] public bool Complete { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "fieldProblemCount")] public long? FieldProblems { get; set; }
    }
    [DataContract] public sealed class ExportTestChunk
    {
        [DataMember(Name = "ownerTable")] public string Table { get; set; }
        [DataMember(Name = "ownerRecordId")] public long Owner { get; set; }
        [DataMember(Name = "column")] public string Column { get; set; }
        [DataMember(Name = "chunkIndex")] public int Index { get; set; }
        [DataMember(Name = "value")] public string Value { get; set; }
    }
    internal static class ExportTestCsv
    {
        public static List<string[]> Read(string path)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false, true)).TrimStart('\uFEFF');
            var rows = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
                else if (!quoted && c == ',') { fields.Add(field.ToString()); field.Clear(); }
                else if (!quoted && c == '\r') { if (text[++i] != '\n') throw new InvalidDataException(); fields.Add(field.ToString()); field.Clear(); rows.Add(fields.ToArray()); fields.Clear(); }
                else field.Append(c);
            }
            if (quoted || fields.Count != 0 || field.Length != 0) throw new InvalidDataException();
            return rows;
        }
        public static string Cell(List<string[]> rows, string column, int row = 1) => rows[row][Array.IndexOf(rows[0], column)];
    }
}
