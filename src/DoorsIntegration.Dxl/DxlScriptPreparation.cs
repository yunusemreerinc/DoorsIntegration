using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public static class DxlStringLiteral
    {
        public static string Encode(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var result = new StringBuilder("\"");
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
                            throw new ArgumentException("Desteklenmeyen kontrol karakteri.", nameof(value));
                        if (char.IsHighSurrogate(character))
                        {
                            if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                                throw new ArgumentException("Geçersiz UTF-16 karakteri.", nameof(value));
                            result.Append(character).Append(value[++index]);
                        }
                        else if (char.IsLowSurrogate(character)) throw new ArgumentException("Geçersiz UTF-16 karakteri.", nameof(value));
                        else result.Append(character);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        internal static void ValidateParameterName(string name)
        {
            if (name == null || !Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("Parametre adı ASCII harfle başlamalı; yalnızca harf/rakam/alt çizgi içermelidir.", nameof(name));
        }
    }

    public interface IDxlScriptLoader
    {
        string Load(string absolutePath, int maxBytes);
    }

    public sealed class Utf8DxlScriptLoader : IDxlScriptLoader
    {
        public string Load(string absolutePath, int maxBytes)
        {
            try
            {
                using (var input = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var bytes = new MemoryStream())
                {
                    if (input.Length > maxBytes) throw TooLarge();
                    var buffer = new byte[8192];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (bytes.Length + read > maxBytes) throw TooLarge();
                        bytes.Write(buffer, 0, read);
                    }
                    var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                    if (string.IsNullOrWhiteSpace(text) || text.IndexOf('\0') >= 0)
                        throw new IntegrationException(IntegrationErrorCode.DxlScriptLoadFailed, "Script boş veya geçerli bir UTF-8 DXL kaynağı değil.");
                    return text;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException ||
                ex is System.Security.SecurityException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new IntegrationException(IntegrationErrorCode.DxlScriptLoadFailed,
                    "DXL dosyası yüklenemedi. Yol, erişim izni ve UTF-8 kodlamasını kontrol edin.", ex);
            }
        }
        private static IntegrationException TooLarge() => new IntegrationException(
            IntegrationErrorCode.DxlScriptLoadFailed, "DXL kaynak dosyası boyut sınırını aşıyor.");
    }

    internal static class DxlExecutionProtocol
    {
        public static string Prefix(Guid id) => "DOORS_DXL|1|" + id.ToString("N") + "|";
        public static string Pending(Guid id) => Prefix(id) + "PENDING";

        public static string Compose(DxlExecutionRequest request, Guid id, string source, string outputPath, bool watchdogEnabled = true)
        {
            var script = new StringBuilder();
            // Must precede generated Unicode parameters as well as the external UTF-8 source.
            script.AppendLine("pragma encoding, \"UTF-8\"");
            script.AppendLine(DxlWatchdogPragma.Line(watchdogEnabled));
            script.AppendLine("// Generated execution context; di_ names are reserved.");
            script.Append("string di_requestId = ").Append(DxlStringLiteral.Encode(id.ToString("N"))).AppendLine(";");
            script.Append("string di_outputPath = ").Append(DxlStringLiteral.Encode(outputPath)).AppendLine(";");
            script.AppendLine("string di_error = \"\";");
            foreach (var item in request.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
                script.Append("string di_arg_").Append(item.Key).Append(" = ").Append(DxlStringLiteral.Encode(item.Value)).AppendLine(";");
            script.AppendLine("// External script snapshot begins.").AppendLine(source);
            script.AppendLine("// External script snapshot ends; completion marker must be reached.");
            script.AppendLine("if (di_error != \"\") {");
            script.Append("  oleSetResult(").Append(DxlStringLiteral.Encode(Prefix(id) + "ERROR|")).AppendLine(" di_error)");
            script.AppendLine("} else {");
            if (request.ResultMode == DxlResultMode.File)
                script.Append("  oleSetResult(").Append(DxlStringLiteral.Encode(Prefix(id) + "FILE")).AppendLine(")");
            else
            {
                script.Append("  if (oleGetResult() == ").Append(DxlStringLiteral.Encode(Pending(id))).AppendLine(") {");
                script.Append("    oleSetResult(").Append(DxlStringLiteral.Encode(Prefix(id) + "OK|")).AppendLine(")");
                script.AppendLine("  } else {");
                script.Append("    oleSetResult(").Append(DxlStringLiteral.Encode(Prefix(id) + "OK|")).AppendLine(" oleGetResult())");
                script.AppendLine("  }");
            }
            script.AppendLine("}");
            var result = script.ToString();
            if (Encoding.UTF8.GetByteCount(result) > request.MaxScriptBytes + 2 * 1024 * 1024)
                throw new IntegrationException(IntegrationErrorCode.DxlScriptLoadFailed, "Hazırlanmış DXL boyutu sınırı aşıldı.");
            return result;
        }

        public static string ValidateResponse(DxlExecutionRequest request, Guid id, string response)
        {
            var prefix = Prefix(id);
            if (response == null || !response.StartsWith(prefix, StringComparison.Ordinal)) throw ProtocolFailure();
            if ((long)response.Length > (long)prefix.Length + request.MaxInlineResultChars + 6) throw TooLarge();
            var body = response.Substring(prefix.Length);
            if (body.StartsWith("ERROR|", StringComparison.Ordinal))
            {
                if (body.Length - 6 > request.MaxInlineResultChars) throw TooLarge();
                throw new DxlScriptException(body.Substring(6));
            }
            if (request.ResultMode == DxlResultMode.File)
            {
                if (body != "FILE") throw ProtocolFailure();
                return null;
            }
            if (!body.StartsWith("OK|", StringComparison.Ordinal)) throw ProtocolFailure();
            if (body.Length - 3 > request.MaxInlineResultChars) throw TooLarge();
            return body.Substring(3);
        }

        private static IntegrationException ProtocolFailure() => new IntegrationException(IntegrationErrorCode.DxlProtocolFailure,
            "DXL tamamlanma yanıtı eksik veya geçersiz. Script derleme/çalışma hatası, halt veya başka bir COM istemcisi yanıtı olabilir.");
        private static IntegrationException TooLarge() => new IntegrationException(IntegrationErrorCode.DxlResultTooLarge,
            "Inline DXL sonucu sınırı aşıyor. Büyük sonuç için dosya modunu kullanın.");
    }

    internal static class DxlWatchdogPragma
    {
        public static string Line(bool enabled) => "pragma runLim, " +
            (enabled ? DxlWatchdogSettings.EnabledCycleLimit : 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
