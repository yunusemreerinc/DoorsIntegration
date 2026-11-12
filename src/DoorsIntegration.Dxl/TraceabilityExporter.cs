using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class TraceabilityExporter : ITraceabilityExporter
    {
        public void Export(Stream destination, ILinkSnapshot snapshot, TraceabilityFormat format)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!Enum.IsDefined(typeof(TraceabilityFormat), format)) throw InvalidFormat();
            try
            {
                using (var writer = new StreamWriter(destination, new UTF8Encoding(false, true), 16384, true))
                {
                    if (format == TraceabilityFormat.Json)
                    {
                        writer.Write("{\"schemaVersion\":1,\"summary\":");
                        JsonObject(writer, Metadata(snapshot.Summary));
                        writer.Write(",\"links\":[");
                        bool first = true;
                        foreach (var row in snapshot.ReadLinks())
                        {
                            if (!first) writer.Write(','); first = false;
                            JsonObject(writer, Record(row));
                        }
                        writer.Write("]}");
                    }
                    else
                    {
                        // A zero-link/failed scope still exports one Summary row, retaining uncertainty.
                        var meta = Metadata(snapshot.Summary).ToArray();
                        var empty = Record(null).ToArray();
                        CsvRow(writer, meta.Select(field => (object)field.Key).Concat(empty.Select(field => (object)field.Key)));
                        bool any = false;
                        foreach (var row in snapshot.ReadLinks())
                        {
                            CsvRow(writer, meta.Select(field => field.Value).Concat(Record(row).Select(field => field.Value))); any = true;
                        }
                        if (!any) CsvRow(writer, meta.Select(field => field.Value).Concat(empty.Select(field => field.Value)));
                    }
                }
            }
            catch (IntegrationException) { throw; }
            catch (Exception ex) when (FileError(ex) || ex is ObjectDisposedException || ex is EncoderFallbackException) { throw Failure(ex); }
        }
        public void ExportFile(string path, ILinkSnapshot snapshot, TraceabilityFormat format, bool overwrite = false)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!Enum.IsDefined(typeof(TraceabilityFormat), format)) throw InvalidFormat();
            string temporary = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Destination path is required.", nameof(path));
                var fullPath = Path.GetFullPath(path);
                temporary = Path.Combine(Path.GetDirectoryName(fullPath), ".doors-trace-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { Export(stream, snapshot, format); stream.Flush(true); }
                if (overwrite && File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
                else File.Move(temporary, fullPath);
                temporary = null;
            }
            catch (IntegrationException) { throw; }
            catch (Exception ex) when (FileError(ex)) { throw Failure(ex); }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
        }
        private static bool FileError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is ArgumentException || ex is NotSupportedException;
        private static IntegrationException InvalidFormat() => new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Traceability çıktı biçimi JSON veya CSV olmalıdır.");
        private static IntegrationException Failure(Exception cause) => new IntegrationException(IntegrationErrorCode.TraceabilityExportFailed,
            "Traceability JSON/CSV çıktısı yazılamadı. Hedef yolu, dosya izinlerini ve üzerine yazma seçimini kontrol edin.", cause);
        private static KeyValuePair<string, object> Field(string name, object value) => new KeyValuePair<string, object>(name, value);
        private static IEnumerable<KeyValuePair<string, object>> Metadata(LinkReadSummary summary)
        {
            yield return Field("correlationId", summary.CorrelationId.ToString("N"));
            yield return Field("modulePath", summary.Request.ModulePath);
            yield return Field("linkModuleScope", summary.Request.LinkModulePath);
            yield return Field("directionScope", summary.Request.Direction.ToString());
            yield return Field("baseline", summary.Request.Baseline == null ? null : LinkProtocol.BaselineVersion(summary.Request.Baseline));
            yield return Field("targetProfile", DoorsVersions.Format(summary.Request.Target));
            yield return Field("includeDeletedObjects", summary.Request.IncludeDeletedObjects);
            yield return Field("resolveEndpoints", summary.Request.ResolveEndpoints);
            yield return Field("openIfNeeded", summary.Request.OpenIfNeeded);
            yield return Field("surrogateMarkerAttribute", summary.Request.SurrogateMapping?.MarkerAttribute);
            yield return Field("surrogateMarkerValue", summary.Request.SurrogateMapping?.MarkerValue);
            yield return Field("surrogateReferenceAttribute", summary.Request.SurrogateMapping?.ReferenceAttribute);
            yield return Field("moduleStatus", summary.ModuleStatus.ToString());
            yield return Field("linkModuleStatus", summary.LinkModuleStatus.ToString());
            yield return Field("ownership", summary.Ownership.ToString());
            yield return Field("visibility", summary.Visibility);
            yield return Field("isCompleteForReadableScope", summary.IsCompleteForReadableScope);
            yield return Field("isTruncated", summary.IsTruncated);
            yield return Field("enumerationFailed", summary.EnumerationFailed);
            yield return Field("recordCount", summary.RecordCount);
            yield return Field("linkCount", summary.LinkCount);
            yield return Field("unresolvedLinkCount", summary.UnresolvedLinkCount);
            yield return Field("sourceReferenceCount", summary.SourceReferenceCount);
            yield return Field("visitedObjectCount", summary.VisitedObjectCount);
            yield return Field("deniedObjectCount", summary.DeniedObjectCount);
            yield return Field("deletedExcludedCount", summary.DeletedExcludedCount);
            yield return Field("openedModuleCount", summary.OpenedModuleCount);
            yield return Field("cleanupFailureCount", summary.CleanupFailureCount);
            yield return Field("surrogateProblemCount", summary.SurrogateProblemCount);
            yield return Field("maxObjects", summary.Request.MaxObjects);
            yield return Field("maxLinks", summary.Request.MaxLinks);
            yield return Field("maxLinkedModules", summary.Request.MaxLinkedModules);
            yield return Field("maxFileBytes", summary.Request.MaxFileBytes);
        }
        private static IEnumerable<KeyValuePair<string, object>> Record(LinkRecord row)
        {
            yield return Field("recordKind", row?.Kind.ToString() ?? "Summary");
            yield return Field("sequence", row == null ? (object)null : row.Sequence);
            yield return Field("direction", row?.Direction.ToString());
            yield return Field("resolution", row?.Resolution.ToString());
            yield return Field("isEchoed", row == null ? (object)null : row.IsEchoed);
            yield return Field("linkModulePath", row?.LinkModulePath);
            yield return Field("linkModuleResolution", row?.LinkModuleResolution.ToString());
            foreach (var field in Endpoint("source", row?.Source)) yield return field;
            foreach (var field in Endpoint("target", row?.Target)) yield return field;
        }
        private static IEnumerable<KeyValuePair<string, object>> Endpoint(string prefix, LinkEndpoint endpoint)
        {
            yield return Field(prefix + "ModulePath", endpoint?.ModulePath);
            yield return Field(prefix + "VersionKind", endpoint?.VersionKind.ToString());
            yield return Field(prefix + "Version", endpoint?.Version);
            yield return Field(prefix + "AbsoluteNumber", endpoint?.AbsoluteNumber);
            yield return Field(prefix + "ObjectIdentifier", endpoint?.ObjectIdentifier);
            yield return Field(prefix + "Resolution", endpoint?.Resolution.ToString());
            yield return Field(prefix + "SurrogateStatus", endpoint?.SurrogateStatus.ToString());
            yield return Field(prefix + "ExternalReference", endpoint?.ExternalReference);
            yield return Field(prefix + "ReferenceStatus", endpoint?.ReferenceStatus.ToString());
        }
        private static void JsonObject(TextWriter writer, IEnumerable<KeyValuePair<string, object>> fields)
        {
            writer.Write('{'); bool first = true;
            foreach (var field in fields)
            {
                if (!first) writer.Write(','); first = false;
                JsonString(writer, field.Key); writer.Write(':');
                if (field.Value == null) writer.Write("null");
                else if (field.Value is bool boolean) writer.Write(boolean ? "true" : "false");
                else if (field.Value is int || field.Value is long) writer.Write(Convert.ToString(field.Value, CultureInfo.InvariantCulture));
                else JsonString(writer, (string)field.Value);
            }
            writer.Write('}');
        }
        private static void JsonString(TextWriter writer, string value)
        {
            writer.Write('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') { writer.Write('\\'); writer.Write(c); }
                else if (c < 32 || c == '\u2028' || c == '\u2029') { writer.Write("\\u"); writer.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                else writer.Write(c);
            }
            writer.Write('"');
        }
        private static void CsvRow(TextWriter writer, IEnumerable<object> values)
        {
            bool first = true;
            foreach (object value in values)
            {
                if (!first) writer.Write(','); first = false;
                string text = value == null ? "" : value is bool boolean ? (boolean ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (value is string)
                {
                    int start = 0;
                    while (start < text.Length && (char.IsWhiteSpace(text[start]) || char.IsControl(text[start]))) start++;
                    if ((start < text.Length && "=+-@".IndexOf(text[start]) >= 0) || (text.Length > 0 && char.IsControl(text[0]))) text = "'" + text;
                }
                writer.Write('"'); writer.Write(text.Replace("\"", "\"\"")); writer.Write('"');
            }
            writer.Write("\r\n");
        }
    }
}
