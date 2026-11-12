using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DiscoveryScriptOptions
    {
        public DiscoveryScriptOptions() : this(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "discovery.dxl")) { }
        public DiscoveryScriptOptions(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) throw new ArgumentException("Script path is required.", nameof(scriptPath));
            ScriptPath = System.IO.Path.GetFullPath(scriptPath);
        }
        public string ScriptPath { get; }
    }

    internal sealed class DiscoveryBatch
    {
        public DoorsDiscoveryItem Scope { get; set; }
        public List<DoorsDiscoveryItem> Children { get; } = new List<DoorsDiscoveryItem>();
        public List<DiscoveryIssue> Issues { get; } = new List<DiscoveryIssue>();
        public bool IsTruncated { get; set; }
    }

    internal enum DiscoveryRejectionReason
    {
        MissingResponse, ResponseTooLarge, MissingFinalNewline, InvalidLineEnding, InvalidLineCount,
        HeaderMismatch, InvalidRecord, InvalidWarning, InvalidEscape, UnknownItemType, InvalidAccessStatus,
        DuplicatePath, InvalidItemMetadata, ScopeMismatch, UnexpectedChild, InvalidFooter, CountMismatch,
        MissingAccessWarning, MissingTypeWarning
    }

    internal sealed class DiscoveryProtocolException : IntegrationException
    {
        public DiscoveryProtocolException(DiscoveryRejectionReason reason, int lineNumber, int responseLength)
            : base(IntegrationErrorCode.DiscoveryProtocolFailure,
                "DXL keşif yanıtı doğrulanamadı. " + Describe(reason) + " " + Metadata(reason, lineNumber, responseLength))
        {
            DiagnosticMetadata = Metadata(reason, lineNumber, responseLength);
        }

        // Only fixed enum names and numeric metadata: never include database paths or raw DXL output.
        public string DiagnosticMetadata { get; }
        public Guid? DebugRequestId { get; private set; }
        public bool DebugCaptureSaved { get; private set; }
        public override string Message => base.Message + (DebugRequestId.HasValue ?
            " DebugRequestId=" + DebugRequestId.Value.ToString("N") + "; ProtectedCapture=" + (DebugCaptureSaved ? "Saved" : "Unavailable") : "");
        internal void AttachDebugCapture(Guid id, bool saved) { DebugRequestId = id; DebugCaptureSaved = saved; }

        private static string Metadata(DiscoveryRejectionReason reason, int lineNumber, int responseLength) =>
            "Reason=" + reason + "; Line=" + lineNumber.ToString(CultureInfo.InvariantCulture) +
            "; ResponseChars=" + responseLength.ToString(CultureInfo.InvariantCulture);

        private static string Describe(DiscoveryRejectionReason reason)
        {
            switch (reason)
            {
                case DiscoveryRejectionReason.MissingResponse: return "DXL çalışması tamamlandı ancak keşif içeriği boş.";
                case DiscoveryRejectionReason.HeaderMismatch: return "Keşif başlığı/sürümü veya istek kimliği eşleşmiyor. Uygulama ile scripts/dxl/discovery.dxl dosyasını aynı paketten kullanın.";
                case DiscoveryRejectionReason.UnknownItemType: return "Dönen öğe türü tanınmıyor; desteklenmeyen tür envantere alınmadı.";
                case DiscoveryRejectionReason.ScopeMismatch: return "Dönen tam yol istenen kapsamla eşleşmiyor; Database Explorer'daki tam yol yazımını kullanın.";
                case DiscoveryRejectionReason.InvalidItemMetadata: return "Dönen yol, ad veya tür bilgisi tutarlı değil.";
                case DiscoveryRejectionReason.MissingFinalNewline:
                case DiscoveryRejectionReason.InvalidFooter:
                case DiscoveryRejectionReason.CountMismatch: return "Keşif yanıtı eksik veya bitiş kaydı tutarsız.";
                default: return "Keşif yanıtının biçimi veya kapsamı geçersiz; sonuç envanter olarak kullanılmadı.";
            }
        }
    }

    internal static class DiscoveryProtocol
    {
        public static DiscoveryBatch Parse(string text, Guid id, string requestedPath, bool lookup)
        {
            int lineNumber = 0;
            int responseLength = text?.Length ?? 0;
            DiscoveryProtocolException Reject(DiscoveryRejectionReason reason) => new DiscoveryProtocolException(reason, lineNumber, responseLength);
            try
            {
                if (string.IsNullOrEmpty(text)) throw Reject(DiscoveryRejectionReason.MissingResponse);
                if (text.Length > 2097152) throw Reject(DiscoveryRejectionReason.ResponseTooLarge);
                if (!text.EndsWith("\n", StringComparison.Ordinal)) throw Reject(DiscoveryRejectionReason.MissingFinalNewline);
                var lines = text.Split('\n');
                if (lines.Length < 4 || lines.Length > 10020) throw Reject(DiscoveryRejectionReason.InvalidLineCount);
                // Accept LF and Windows CRLF record delimiters. Do not Trim() fields or remove embedded CRs:
                // escaped database content and all other protocol checks must remain exact.
                for (int index = 0; index < lines.Length - 1; index++)
                {
                    lineNumber = index + 1;
                    if (lines[index].EndsWith("\r", StringComparison.Ordinal))
                        lines[index] = lines[index].Substring(0, lines[index].Length - 1);
                    if (lines[index].IndexOf('\r') >= 0) throw Reject(DiscoveryRejectionReason.InvalidLineEnding);
                }
                lineNumber = 1;
                if (lines[0] != "DOORS_DISCOVERY|1|" + id.ToString("N")) throw Reject(DiscoveryRejectionReason.HeaderMismatch);
                var batch = new DiscoveryBatch();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool warningsStarted = false;
                for (int index = 1; index < lines.Length - 2; index++)
                {
                    lineNumber = index + 1;
                    var fields = lines[index].Split('\t');
                    if (fields[0] == "W")
                    {
                        warningsStarted = true;
                        if (fields.Length != 2 || batch.Scope == null) throw Reject(DiscoveryRejectionReason.InvalidWarning);
                        DiscoveryIssueCode code;
                        if (!Enum.TryParse(fields[1], false, out code) || !Enum.IsDefined(typeof(DiscoveryIssueCode), code) || fields[1] != code.ToString() ||
                            code == DiscoveryIssueCode.MaxItemsReached || code == DiscoveryIssueCode.MaxDepthReached || code == DiscoveryIssueCode.MaxFoldersReached)
                            throw Reject(DiscoveryRejectionReason.InvalidWarning);
                        batch.Issues.Add(new DiscoveryIssue(requestedPath, code));
                        continue;
                    }
                    if (fields.Length != 5 || warningsStarted || (index == 1 ? fields[0] != "S" : fields[0] != "I")) throw Reject(DiscoveryRejectionReason.InvalidRecord);
                    var path = Decode(fields[1], Reject);
                    var name = Decode(fields[2], Reject);
                    var kind = ParseKind(fields[3], Reject);
                    DoorsAccessStatus access;
                    if (!Enum.TryParse(fields[4], false, out access) || !Enum.IsDefined(typeof(DoorsAccessStatus), access) || fields[4] != access.ToString()) throw Reject(DiscoveryRejectionReason.InvalidAccessStatus);
                    if (!seen.Add(path)) throw Reject(DiscoveryRejectionReason.DuplicatePath);
                    if ((kind == DoorsItemKind.DatabaseRoot && path != "/") ||
                        (kind == DoorsItemKind.Unknown && access == DoorsAccessStatus.Readable)) throw Reject(DiscoveryRejectionReason.InvalidItemMetadata);
                    var item = new DoorsDiscoveryItem(path, name, kind, access, kind != DoorsItemKind.Unknown);
                    if (index == 1)
                    {
                        if (!string.Equals(path, requestedPath, StringComparison.Ordinal)) throw Reject(DiscoveryRejectionReason.ScopeMismatch);
                        batch.Scope = item;
                    }
                    else
                    {
                        if (lookup || !DoorsItemPath.IsDirectChild(requestedPath, path) || batch.Scope.AccessStatus != DoorsAccessStatus.Readable ||
                            !IsContainer(batch.Scope.Type)) throw Reject(DiscoveryRejectionReason.UnexpectedChild);
                        batch.Children.Add(item);
                    }
                }
                lineNumber = lines.Length - 1;
                var footer = lines[lines.Length - 2].Split('\t');
                int count;
                if (batch.Scope == null || footer.Length != 3 || footer[0] != "END" ||
                    !int.TryParse(footer[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                    (footer[2] != "0" && footer[2] != "1") || (lookup && footer[2] != "0")) throw Reject(DiscoveryRejectionReason.InvalidFooter);
                if (count != batch.Children.Count) throw Reject(DiscoveryRejectionReason.CountMismatch);
                batch.IsTruncated = footer[2] == "1";
                if (batch.Scope.AccessStatus != DoorsAccessStatus.Readable && batch.Issues.Count == 0) throw Reject(DiscoveryRejectionReason.MissingAccessWarning);
                if (lookup && batch.Scope.AccessStatus == DoorsAccessStatus.Readable && batch.Scope.Type != DoorsItemKind.FormalModule &&
                    !batch.Issues.Any(issue => issue.Code == DiscoveryIssueCode.NotFormalModule)) throw Reject(DiscoveryRejectionReason.MissingTypeWarning);
                if (!lookup && batch.Scope.AccessStatus == DoorsAccessStatus.Readable && !IsContainer(batch.Scope.Type) &&
                    !batch.Issues.Any(issue => issue.Code == DiscoveryIssueCode.ScopeNotContainer)) throw Reject(DiscoveryRejectionReason.MissingTypeWarning);
                return batch;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IntegrationException)
            {
                if (ex is IntegrationException known && known.Code == IntegrationErrorCode.DiscoveryProtocolFailure) throw;
                throw Reject(DiscoveryRejectionReason.InvalidItemMetadata);
            }
        }

        public static bool IsContainer(DoorsItemKind kind) => kind == DoorsItemKind.Project || kind == DoorsItemKind.Folder || kind == DoorsItemKind.DatabaseRoot;

        private static DoorsItemKind ParseKind(string value, Func<DiscoveryRejectionReason, DiscoveryProtocolException> reject)
        {
            switch (value)
            {
                case "Unknown": return DoorsItemKind.Unknown;
                case "DatabaseRoot": return DoorsItemKind.DatabaseRoot;
                case "Project": return DoorsItemKind.Project;
                case "Folder": return DoorsItemKind.Folder;
                case "Formal": return DoorsItemKind.FormalModule;
                case "Link": return DoorsItemKind.LinkModule;
                case "Descriptive": return DoorsItemKind.DescriptiveModule;
                default: throw reject(DiscoveryRejectionReason.UnknownItemType);
            }
        }

        private static string Decode(string field, Func<DiscoveryRejectionReason, DiscoveryProtocolException> reject)
        {
            var output = new StringBuilder();
            for (int index = 0; index < field.Length; index++)
            {
                char value = field[index];
                if (char.IsControl(value)) throw reject(DiscoveryRejectionReason.InvalidEscape);
                if (value == '\\')
                {
                    if (++index == field.Length) throw reject(DiscoveryRejectionReason.InvalidEscape);
                    switch (field[index])
                    {
                        case '\\': value = '\\'; break;
                        case 't': value = '\t'; break;
                        case 'r': value = '\r'; break;
                        case 'n': value = '\n'; break;
                        default: throw reject(DiscoveryRejectionReason.InvalidEscape);
                    }
                }
                output.Append(value);
            }
            return output.ToString();
        }

    }
}
