using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace DoorsIntegration.Processing
{
    public sealed class JsonLinesBatchSource : IBatchRecordSource
    {
        private readonly string path;
        public JsonLinesBatchSource(string path) { this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); }
        public IEnumerable<BatchInputRecord> Read(CancellationToken cancellationToken)
        {
            using (var reader = ProcessingJson.Reader(path))
            {
                long number = 0; string line;
                while ((line = ProcessingJson.ReadLine(reader, 262144, out bool tooLarge, out bool terminated)) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested(); number++;
                    BatchChange change = null; string error = tooLarge ? "RecordTooLarge" : "";
                    if (!tooLarge)
                    {
                        try { change = Parse(line); }
                        catch (Exception ex) when (ex is ArgumentException || ex is XmlException || ex is FormatException || ex is InvalidOperationException)
                        { error = "InvalidJsonRecord"; }
                    }
                    yield return new BatchInputRecord(number, change, error);
                }
            }
        }
        internal static BatchChange Parse(string line)
        {
            using (var json = JsonReaderWriterFactory.CreateJsonReader(new UTF8Encoding(false, true).GetBytes(line),
                new XmlDictionaryReaderQuotas { MaxDepth = 16, MaxStringContentLength = 262144, MaxArrayLength = 1024, MaxBytesPerRead = 4096, MaxNameTableCharCount = 16384 }))
            {
                var root = XElement.Load(json); var p = Members(root, new[] { "key", "action", "target", "objectId", "position", "anchorId", "attributes", "requiredAttributes" });
                string action = String(p, "action");
                if (!Enum.TryParse(action, false, out BatchAction operation) || operation.ToString() != action) throw new ArgumentException();
                BatchPosition? position = null; string positionText = String(p, "position", true);
                if (positionText != null)
                { if (!Enum.TryParse(positionText, false, out BatchPosition parsed) || parsed.ToString() != positionText) throw new ArgumentException(); position = parsed; }
                if (!p.TryGetValue("attributes", out var attributes) || (string)attributes.Attribute("type") != "array") throw new ArgumentException();
                var values = new List<BatchAttribute>();
                foreach (var element in attributes.Elements())
                {
                    if (values.Count >= 32) throw new ArgumentException();
                    var a = Members(element, new[] { "name", "value", "format", "expectedPlainText" });
                    string format = String(a, "format", true) ?? "PlainText";
                    if (!Enum.TryParse(format, false, out BatchValueFormat parsed) || parsed.ToString() != format) throw new ArgumentException();
                    values.Add(new BatchAttribute(String(a, "name"), String(a, "value"), parsed, String(a, "expectedPlainText", true)));
                }
                var required = new List<string>();
                if (p.TryGetValue("requiredAttributes", out var array))
                {
                    if ((string)array.Attribute("type") != "array") throw new ArgumentException();
                    foreach (var value in array.Elements())
                    { if (required.Count >= 32 || (string)value.Attribute("type") != "string") throw new ArgumentException(); required.Add(value.Value); }
                }
                return new BatchChange(String(p, "key"), operation, String(p, "target"), values, String(p, "objectId", true), position,
                    String(p, "anchorId", true), required);
            }
        }
        private static Dictionary<string, XElement> Members(XElement element, string[] allowed)
        {
            if ((string)element.Attribute("type") != "object") throw new ArgumentException();
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var child in element.Elements())
            {
                string name = child.Name.LocalName;
                if (!allowed.Contains(name, StringComparer.Ordinal) || result.ContainsKey(name)) throw new ArgumentException();
                result.Add(name, child);
            }
            return result;
        }
        private static string String(Dictionary<string, XElement> values, string key, bool optional = false)
        {
            if (!values.TryGetValue(key, out var element)) { if (optional) return null; throw new ArgumentException(); }
            if (optional && (string)element.Attribute("type") == "null") return null;
            if ((string)element.Attribute("type") != "string" || element.HasElements) throw new ArgumentException();
            return element.Value;
        }
    }

    public sealed class CsvBatchSource : IBatchRecordSource
    {
        private readonly string path;
        public CsvBatchSource(string path) { this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); }
        public IEnumerable<BatchInputRecord> Read(CancellationToken token)
        {
            using (var reader = ProcessingJson.Reader(path))
            {
                var header = Record(reader, token, out bool invalidHeader);
                var reserved = new[] { "key", "action", "target", "objectId", "position", "anchorId" };
                if (header == null || invalidHeader || header.Distinct(StringComparer.Ordinal).Count() != header.Count ||
                    new[] { "key", "action", "target" }.Any(n => !header.Contains(n)) ||
                    header.Any(n => !reserved.Contains(n) && (!n.StartsWith("attr:", StringComparison.Ordinal) || n.Length <= 5)))
                    throw new ProcessingException("InvalidCsvHeader", "CSV requires unique key/action/target columns and explicit attr:Name columns.");
                long number = 0; List<string> cells;
                while ((cells = Record(reader, token, out bool invalid)) != null)
                {
                    number++; token.ThrowIfCancellationRequested(); BatchChange change = null; string error = "";
                    if (invalid || cells.Count != header.Count) error = "InvalidCsvRecord";
                    else
                    {
                        try
                        {
                            var row = header.Select((n, i) => new { Name = n, Value = cells[i] }).ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                            string Optional(string name) => row.TryGetValue(name, out var value) && value.Length != 0 ? value : null;
                            if (!Enum.TryParse(row["action"], false, out BatchAction action) || action.ToString() != row["action"]) throw new ArgumentException();
                            BatchPosition? position = null; string text = Optional("position");
                            if (text != null) { if (!Enum.TryParse(text, false, out BatchPosition parsed) || parsed.ToString() != text) throw new ArgumentException(); position = parsed; }
                            change = new BatchChange(row["key"], action, row["target"], header.Where(h => h.StartsWith("attr:", StringComparison.Ordinal))
                                .Select(h => new BatchAttribute(h.Substring(5), row[h])), Optional("objectId"), position, Optional("anchorId"));
                        }
                        catch (ArgumentException) { error = "InvalidCsvRecord"; }
                    }
                    yield return new BatchInputRecord(number, change, error);
                }
            }
        }
        private static List<string> Record(TextReader reader, CancellationToken token, out bool invalid)
        {
            invalid = false; var cells = new List<string>(); var cell = new StringBuilder();
            bool quoted = false, afterQuote = false, any = false, start = true; int total = 0, next;
            while ((next = reader.Read()) >= 0)
            {
                if ((total++ & 4095) == 0) token.ThrowIfCancellationRequested();
                any = true; char c = (char)next;
                if (total > 262144) invalid = true;
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"') { reader.Read(); total++; if (cell.Length < 65536) cell.Append('"'); else invalid = true; }
                        else { quoted = false; afterQuote = true; }
                    }
                    else if (cell.Length < 65536) cell.Append(c); else invalid = true;
                    continue;
                }
                if (c == ',' || c == '\n' || c == '\r')
                {
                    if (cells.Count < 40) cells.Add(cell.ToString()); else invalid = true;
                    cell.Clear(); start = true; afterQuote = false;
                    if (c != ',') { if (c == '\r' && reader.Peek() == '\n') reader.Read(); return cells; }
                }
                else if (c == '"' && start) { quoted = true; start = false; }
                else
                {
                    if (afterQuote || c == '"') invalid = true;
                    start = false; if (cell.Length < 65536) cell.Append(c); else invalid = true;
                }
            }
            if (!any) return null;
            if (quoted) invalid = true;
            if (cells.Count < 40) cells.Add(cell.ToString()); else invalid = true;
            return cells;
        }
    }
}
