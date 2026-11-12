using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal static class RequirementProtocol
    {
        public static RequirementReadSummary Validate(Stream stream, RequirementReadRequest request, Guid id, CancellationToken cancellationToken)
        {
            RequirementReadSummary summary = null;
            foreach (var record in Parse(stream, request, id, cancellationToken, value => summary = value)) { }
            return summary ?? throw Failure();
        }

        // One object at a time. The initial validation pass retains only a set of IDs, never object bodies.
        public static IEnumerable<RequirementRecord> Parse(Stream stream, RequirementReadRequest request, Guid id,
            CancellationToken cancellationToken, Action<RequirementReadSummary> completed)
        {
            using (var text = new StreamReader(stream, new UTF8Encoding(false, true), false, 16384, true))
            {
                // Two payloads, each at most doubled by escaping, plus escaped name/type/status columns.
                var lines = new BoundedLines(text, checked(request.MaxFieldCharacters * 4 + 12288));
                string header = lines.Read();
                if (header != null && header.StartsWith("\uFEFF", StringComparison.Ordinal)) header = header.Substring(1);
                bool identityProtocol = header == "DOORS_REQUIREMENTS\t2\t" + id.ToString("N");
                if (!identityProtocol && header != "DOORS_REQUIREMENTS\t1\t" + id.ToString("N")) throw Failure();
                string[] metadata = Columns(lines.Read(), 8, "M");
                string requestedMode = request.Baseline == null ? "Current" : "Baseline";
                if (Decode(metadata[1]) != request.ModulePath || metadata[2] != requestedMode ||
                    metadata[3] != (request.Baseline?.Major.ToString(CultureInfo.InvariantCulture) ?? "") ||
                    metadata[4] != (request.Baseline?.Minor.ToString(CultureInfo.InvariantCulture) ?? "") ||
                    Decode(metadata[5]) != (request.Baseline?.Suffix ?? "")) throw Failure();
                bool baselineObserved = Flag(metadata[6]);
                var ownership = EnumValue<RequirementModuleOwnership>(metadata[7]);
                string qualifiedModuleId = identityProtocol ? Decode(Columns(lines.Read(), 2, "Q")[1]) : null;
                if (qualifiedModuleId != null && (qualifiedModuleId.Length > 4096 || qualifiedModuleId.Any(char.IsControl))) throw Failure();
                var expectedNames = new[] { "Object Heading", "Object Text" }.Concat(request.CustomAttributes).ToArray();
                var ids = new HashSet<int>();
                int count = 0, previousOrder = 0;
                long fieldProblems = 0;
                string line;
                while ((line = lines.Read()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.StartsWith("END\t", StringComparison.Ordinal))
                    {
                        var footer = Columns(line, 10, "END");
                        var status = EnumValue<RequirementModuleStatus>(footer[1]);
                        int declaredCount = Number(footer[2], 0, request.MaxObjects);
                        int visited = Number(footer[3], 0, request.MaxObjects);
                        int denied = Number(footer[4], 0, visited);
                        int deleted = Number(footer[5], 0, visited);
                        int unavailable = Number(footer[6], 0, visited);
                        bool truncated = Flag(footer[7]), enumerationFailed = Flag(footer[8]);
                        var cleanup = EnumValue<RequirementCleanupStatus>(footer[9]);
                        if (count != declaredCount || previousOrder > visited || (long)count + denied + deleted + unavailable != visited ||
                            (request.DeletedObjects != DeletedObjectMode.Exclude && deleted != 0) ||
                            (ownership == RequirementModuleOwnership.OwnedReadOnly ? cleanup == RequirementCleanupStatus.NotOwned : cleanup != RequirementCleanupStatus.NotOwned) ||
                            (status == RequirementModuleStatus.Available && (ownership == RequirementModuleOwnership.NotLoaded || baselineObserved != (request.Baseline != null) || (identityProtocol && string.IsNullOrWhiteSpace(qualifiedModuleId)))) ||
                            (status != RequirementModuleStatus.Available && (count != 0 || visited != 0 || truncated || enumerationFailed)) ||
                            lines.Read() != null) throw Failure();
                        completed(new RequirementReadSummary(id, request, status, ownership, cleanup, count, visited, denied, deleted,
                            unavailable, fieldProblems, truncated, enumerationFailed, qualifiedModuleId));
                        yield break;
                    }
                    var objectFields = Columns(line, 10, "O");
                    int order = Number(objectFields[1], 1, request.MaxObjects);
                    int absolute = Number(objectFields[2], 1, int.MaxValue);
                    if (order <= previousOrder || !ids.Add(absolute)) throw Failure();
                    previousOrder = order;
                    string identifier = Decode(objectFields[3]);
                    string number = Decode(objectFields[4]);
                    var numberStatus = EnumValue<RequirementValueStatus>(objectFields[5]);
                    int level = Number(objectFields[6], 1, int.MaxValue);
                    int? parent = objectFields[7] == "" ? (int?)null : Number(objectFields[7], 1, int.MaxValue);
                    var parentStatus = EnumValue<RequirementParentStatus>(objectFields[8]);
                    bool isDeleted = Flag(objectFields[9]);
                    if (identifier.Length == 0 || identifier.Length > request.MaxFieldCharacters || number.Length > request.MaxFieldCharacters ||
                        (numberStatus != RequirementValueStatus.Readable && numberStatus != RequirementValueStatus.ReadFailed) ||
                        (numberStatus == RequirementValueStatus.Readable ? number.Length == 0 : number.Length != 0) ||
                        (parentStatus == RequirementParentStatus.Resolved ? !parent.HasValue : parent.HasValue) || parent == absolute ||
                        (parentStatus == RequirementParentStatus.Root && level != 1) ||
                        (isDeleted && request.DeletedObjects == DeletedObjectMode.Exclude)) throw Failure();
                    if (numberStatus != RequirementValueStatus.Readable || parentStatus == RequirementParentStatus.Unavailable) fieldProblems++;
                    var values = new List<RequirementValue>(expectedNames.Length);
                    long objectCharacters = 0;
                    foreach (string expectedName in expectedNames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var columns = Columns(lines.Read(), 7, "V");
                        string name = Decode(columns[1]), typeName = Decode(columns[2]);
                        var plainStatus = EnumValue<RequirementValueStatus>(columns[3]);
                        string plain = Decode(columns[4]);
                        var richStatus = EnumValue<RequirementValueStatus>(columns[5]);
                        string rtf = Decode(columns[6]);
                        bool metadataOnly = isDeleted && request.DeletedObjects == DeletedObjectMode.MetadataOnly;
                        if (name != expectedName || typeName.Length > 4096 || plain.Length > request.MaxFieldCharacters || rtf.Length > request.MaxFieldCharacters ||
                            (objectCharacters += (long)plain.Length + rtf.Length) > request.MaxObjectCharacters ||
                            (plainStatus != RequirementValueStatus.Readable && plain.Length != 0) ||
                            (richStatus != RequirementValueStatus.Readable && rtf.Length != 0) || plainStatus == RequirementValueStatus.NotApplicable ||
                            (metadataOnly ? plainStatus != RequirementValueStatus.NotRequested || richStatus != RequirementValueStatus.NotRequested :
                                plainStatus == RequirementValueStatus.NotRequested || richStatus == RequirementValueStatus.NotRequested) ||
                            (richStatus == RequirementValueStatus.NotApplicable && plainStatus != RequirementValueStatus.Readable)) throw Failure();
                        if ((plainStatus != RequirementValueStatus.Readable && plainStatus != RequirementValueStatus.NotRequested) ||
                            (richStatus != RequirementValueStatus.Readable && richStatus != RequirementValueStatus.NotRequested && richStatus != RequirementValueStatus.NotApplicable)) fieldProblems++;
                        values.Add(new RequirementValue(name, typeName, plainStatus, plain, richStatus, rtf));
                    }
                    if (lines.Read() != "E") throw Failure();
                    if (++count > request.MaxObjects) throw Failure();
                    yield return new RequirementRecord(order, absolute, identifier, number, numberStatus, level, parent, parentStatus, isDeleted, values);
                }
                throw Failure();
            }
        }

        private static string[] Columns(string line, int count, string tag)
        {
            if (line == null) throw Failure();
            var values = line.Split('\t');
            if (values.Length != count || values[0] != tag) throw Failure();
            return values;
        }
        private static int Number(string value, int minimum, int maximum)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) || result < minimum || result > maximum) throw Failure();
            return result;
        }
        private static bool Flag(string value) { if (value != "0" && value != "1") throw Failure(); return value == "1"; }
        private static T EnumValue<T>(string value) where T : struct
        {
            T result;
            if (!Enum.TryParse(value, false, out result) || !Enum.IsDefined(typeof(T), result) || result.ToString() != value) throw Failure();
            return result;
        }
        private static string Decode(string value)
        {
            var result = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (c == '\\')
                {
                    if (++index == value.Length) throw Failure();
                    switch (value[index])
                    {
                        case '\\': c = '\\'; break;
                        case 't': c = '\t'; break;
                        case 'r': c = '\r'; break;
                        case 'n': c = '\n'; break;
                        default: throw Failure();
                    }
                }
                result.Append(c);
            }
            return result.ToString();
        }
        internal static IntegrationException Failure(Exception cause = null) => new IntegrationException(IntegrationErrorCode.RequirementProtocolFailure,
            "Gereksinim aktarımı eksik, bozuk veya istenen modül/sürüm ile uyuşmuyor. Sonuç tam bir okuma olarak kabul edilmedi.", cause);

        private sealed class BoundedLines
        {
            private readonly TextReader reader;
            private readonly int maximum;
            private readonly char[] buffer = new char[8192];
            private int offset, length;
            public BoundedLines(TextReader reader, int maximum) { this.reader = reader; this.maximum = maximum; }
            public string Read()
            {
                var output = new StringBuilder();
                while (true)
                {
                    if (offset == length)
                    {
                        length = reader.Read(buffer, 0, buffer.Length);
                        offset = 0;
                        if (length == 0) { if (output.Length != 0) throw Failure(); return null; }
                    }
                    int newline = Array.IndexOf(buffer, '\n', offset, length - offset);
                    int size = (newline < 0 ? length : newline) - offset;
                    if ((long)output.Length + size > maximum) throw Failure();
                    output.Append(buffer, offset, size);
                    offset += size;
                    if (newline >= 0)
                    {
                        offset++;
                        if (output.Length > 0 && output[output.Length - 1] == '\r') output.Length--;
                        return output.ToString();
                    }
                }
            }
        }
    }
}
