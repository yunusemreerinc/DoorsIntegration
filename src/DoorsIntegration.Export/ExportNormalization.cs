using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    internal sealed class ExportRowException : Exception
    {
        public ExportRowException(ExportIssueCode code, string column = null) { Code = code; Column = column; }
        public ExportIssueCode Code { get; }
        public string Column { get; }
    }
    internal static class ExportNormalization
    {
        internal const int ChunkCharacters = 4000;
        public static string[] Normalize(ExportRecord record, long id, ExportRequest request)
        {
            if (record == null || !Enum.IsDefined(typeof(ExportTableKind), record.Table) || record.Table == ExportTableKind.Errors || record.Table == ExportTableKind.ValueChunks)
                throw new ExportRowException(ExportIssueCode.InvalidRecord);
            var schema = ExportSchemas.Get(record.Table);
            var names = new HashSet<string>(schema.Columns.Skip(3).Select(column => column.Name), StringComparer.Ordinal);
            if (record.Fields.Keys.Any(key => !names.Contains(key))) throw new ExportRowException(ExportIssueCode.InvalidRecord);
            var row = new string[schema.Columns.Count]; row[0] = id.ToString(CultureInfo.InvariantCulture);
            for (int i = 3; i < row.Length; i++)
            {
                var column = schema.Columns[i]; record.Fields.TryGetValue(column.Name, out var value);
                if (value == null) { if (column.Required) throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name); continue; }
                switch (column.Type)
                {
                    case ExportColumnType.Text:
                        if (!(value is string text)) throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name);
                        if (text.Length > request.MaxFieldCharacters) throw new ExportRowException(ExportIssueCode.FieldTooLarge, column.Name);
                        if (column.Required && text.Length == 0) throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name);
                        ValidateUnicode(text, column.Name); row[i] = text; break;
                    case ExportColumnType.Integer:
                        if (!(value is byte || value is short || value is int || value is long || value is ushort || value is uint)) throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name);
                        row[i] = Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); break;
                    case ExportColumnType.Number:
                        if (!(value is decimal || value is double || value is float || value is long || value is int)) throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name);
                        if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)) || value is float single && (float.IsNaN(single) || float.IsInfinity(single)))
                            throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name);
                        row[i] = value is double floating ? floating.ToString("R", CultureInfo.InvariantCulture) : value is float real ? real.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture); break;
                    case ExportColumnType.Boolean:
                        if (!(value is bool boolean)) throw new ExportRowException(ExportIssueCode.InvalidRecord, column.Name);
                        row[i] = boolean ? "true" : "false"; break;
                    case ExportColumnType.DateTime:
                        DateTime utc;
                        if (value is DateTimeOffset offset) utc = offset.UtcDateTime;
                        else if (value is DateTime date && date.Kind == DateTimeKind.Utc) utc = date;
                        else throw new ExportRowException(ExportIssueCode.InvalidDate, column.Name);
                        row[i] = utc.ToString("O", CultureInfo.InvariantCulture); break;
                }
            }
            return row;
        }
        private static void ValidateUnicode(string value, string column)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c)) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new ExportRowException(ExportIssueCode.InvalidUnicode, column); }
                else if (char.IsLowSurrogate(c)) throw new ExportRowException(ExportIssueCode.InvalidUnicode, column);
            }
        }
        public static bool NeedsChunks(string value) => value != null && (value.Length > ChunkCharacters || value.Count(c => c == '\n') > 250);
        public static IEnumerable<string> Chunks(string value)
        {
            int start = 0;
            while (start < value.Length)
            {
                int end = Math.Min(start + ChunkCharacters, value.Length), lines = 0;
                for (int i = start; i < end; i++) if (value[i] == '\n' && ++lines > 250) { end = i; break; }
                if (end < value.Length && char.IsLowSurrogate(value[end])) end--;
                yield return value.Substring(start, end - start); start = end;
            }
        }
    }
}
