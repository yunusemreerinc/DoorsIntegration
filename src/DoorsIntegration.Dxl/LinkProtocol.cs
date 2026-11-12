using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal static class LinkProtocol
    {
        public static LinkReadSummary Validate(Stream stream, LinkReadRequest request, Guid id, CancellationToken token)
        {
            LinkReadSummary summary = null;
            foreach (var row in Parse(stream, request, id, token, value => summary = value)) { }
            return summary ?? throw Failure();
        }
        public static IEnumerable<LinkRecord> Parse(Stream stream, LinkReadRequest request, Guid id, CancellationToken token, Action<LinkReadSummary> completed)
        {
            using (var text = new StreamReader(stream, new UTF8Encoding(false, true), false, 16384, true))
            {
                var lines = new Lines(text);
                var header = lines.Read();
                if (header != null && header.StartsWith("\uFEFF", StringComparison.Ordinal)) header = header.Substring(1);
                if (header != "DOORS_LINKS\t1\t" + id.ToString("N")) throw Failure();
                var meta = Columns(lines.Read(), 11, "M");
                if (Decode(meta[1]) != request.ModulePath || Decode(meta[2]) != request.LinkModulePath || meta[3] != request.Direction.ToString() ||
                    meta[4] != (request.Baseline == null ? "Current" : "Baseline") ||
                    meta[5] != (request.Baseline?.Major.ToString(CultureInfo.InvariantCulture) ?? "") ||
                    meta[6] != (request.Baseline?.Minor.ToString(CultureInfo.InvariantCulture) ?? "") || Decode(meta[7]) != (request.Baseline?.Suffix ?? "") ||
                    Flag(meta[10]) != request.IncludeDeletedObjects) throw Failure();
                bool observedBaseline = Flag(meta[8]);
                var ownership = EnumValue<RequirementModuleOwnership>(meta[9]);
                int count = 0, unresolved = 0, gaps = 0, surrogateProblems = 0;
                string line;
                while ((line = lines.Read()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.StartsWith("END\t", StringComparison.Ordinal))
                    {
                        var footer = Columns(line, 13, "END");
                        var status = EnumValue<RequirementModuleStatus>(footer[1]);
                        var linkStatus = EnumValue<LinkModuleStatus>(footer[2]);
                        int records = Number(footer[3], 0, request.MaxLinks), visited = Number(footer[4], 0, request.MaxObjects);
                        int denied = Number(footer[5], 0, visited), deleted = Number(footer[6], 0, visited);
                        int reportedUnresolved = Number(footer[7], 0, records), reportedGaps = Number(footer[8], 0, records);
                        bool truncated = Flag(footer[9]), failed = Flag(footer[10]);
                        int opened = Number(footer[11], 0, request.MaxLinkedModules + 1), cleanup = Number(footer[12], 0, opened);
                        bool available = status == RequirementModuleStatus.Available && linkStatus == LinkModuleStatus.Available;
                        if (records != count || unresolved != reportedUnresolved || gaps != reportedGaps || (long)denied + deleted > visited ||
                            (request.IncludeDeletedObjects && deleted != 0) ||
                            (available && (ownership == RequirementModuleOwnership.NotLoaded || observedBaseline != (request.Baseline != null))) ||
                            (!available && (count != 0 || visited != 0 || truncated || failed)) || (count > 0 && visited == 0) ||
                            (ownership == RequirementModuleOwnership.OwnedReadOnly && opened == 0) ||
                            (ownership != RequirementModuleOwnership.OwnedReadOnly && opened > request.MaxLinkedModules) || lines.Read() != null) throw Failure();
                        completed(new LinkReadSummary(id, request, status, linkStatus, ownership, count, visited, denied, deleted,
                            unresolved, gaps, truncated, failed, opened, cleanup, surrogateProblems));
                        yield break;
                    }
                    var columns = Columns(line, 26, "L");
                    if (++count > request.MaxLinks || Number(columns[1], 1, request.MaxLinks) != count) throw Failure();
                    var direction = EnumValue<LinkDirection>(columns[2]);
                    var kind = EnumValue<LinkRecordKind>(columns[3]);
                    var resolution = EnumValue<LinkResolution>(columns[4]);
                    bool echoed = Flag(columns[5]);
                    var verification = EnumValue<LinkModuleResolution>(columns[6]);
                    string linkPath = PathValue(columns[7], false);
                    if (direction == LinkDirection.Both || (request.Direction != LinkDirection.Both && direction != request.Direction) || linkPath != request.LinkModulePath) throw Failure();
                    var source = Endpoint(columns, 8, request);
                    var target = Endpoint(columns, 17, request);
                    var anchor = direction == LinkDirection.Outgoing ? source : target;
                    if (anchor.ModulePath != request.ModulePath || !anchor.AbsoluteNumber.HasValue || !anchor.IsReadable ||
                        (!request.IncludeDeletedObjects && anchor.Resolution == LinkEndpointResolution.DeletedObject) ||
                        anchor.VersionKind != (request.Baseline == null ? LinkVersionKind.Current : LinkVersionKind.Baseline) ||
                        (request.Baseline != null && anchor.Version != BaselineVersion(request.Baseline))) throw Failure();
                    if (kind == LinkRecordKind.IncomingSourceReference)
                    {
                        if (direction != LinkDirection.Incoming || resolution != LinkResolution.SourceReferenceOnly || source.AbsoluteNumber.HasValue ||
                            source.VersionKind != LinkVersionKind.Unknown || source.IsReadable || echoed || verification != LinkModuleResolution.FilterOnly) throw Failure();
                        gaps++;
                    }
                    else
                    {
                        if (resolution == LinkResolution.SourceReferenceOnly ||
                            (resolution == LinkResolution.Resolved && (source.Resolution != LinkEndpointResolution.Resolved || target.Resolution != LinkEndpointResolution.Resolved || verification != LinkModuleResolution.Verified)) ||
                            (resolution == LinkResolution.IncomingReferenceOnly && (direction != LinkDirection.Incoming || verification != LinkModuleResolution.FilterOnly))) throw Failure();
                        if (resolution != LinkResolution.Resolved) unresolved++;
                    }
                    surrogateProblems += SurrogateProblems(source) + SurrogateProblems(target);
                    yield return new LinkRecord(count, direction, kind, resolution, echoed, linkPath, verification, source, target);
                }
                throw Failure();
            }
        }

        internal static string BaselineVersion(RequirementBaseline baseline) => baseline.Major.ToString(CultureInfo.InvariantCulture) + "." +
            baseline.Minor.ToString(CultureInfo.InvariantCulture) + (baseline.Suffix == "" ? "" : "(" + baseline.Suffix + ")");
        private static int SurrogateProblems(LinkEndpoint endpoint) => endpoint.SurrogateStatus == SurrogateStatus.Unavailable ||
            (endpoint.SurrogateStatus == SurrogateStatus.Matched && endpoint.ReferenceStatus != RequirementValueStatus.Readable && endpoint.ReferenceStatus != RequirementValueStatus.NotRequested) ? 1 : 0;
        private static LinkEndpoint Endpoint(string[] columns, int index, LinkReadRequest request)
        {
            string path = PathValue(columns[index], true);
            var kind = EnumValue<LinkVersionKind>(columns[index + 1]);
            string version = Decode(columns[index + 2]);
            int? absolute = columns[index + 3] == "" ? (int?)null : Number(columns[index + 3], 1, int.MaxValue);
            string identifier = Decode(columns[index + 4]);
            var status = EnumValue<LinkEndpointResolution>(columns[index + 5]);
            var surrogate = EnumValue<SurrogateStatus>(columns[index + 6]);
            string external = Decode(columns[index + 7]);
            var reference = EnumValue<RequirementValueStatus>(columns[index + 8]);
            bool readable = status == LinkEndpointResolution.Resolved || status == LinkEndpointResolution.DeletedObject;
            if (version.Length > 256 || identifier.Length > 8192 || external.Length > 8192 ||
                (kind == LinkVersionKind.Baseline ? version.Length == 0 : version.Length != 0) ||
                (readable ? path == "" || !absolute.HasValue || identifier == "" || kind == LinkVersionKind.Unknown : identifier != "") ||
                (request.SurrogateMapping == null ? surrogate != SurrogateStatus.NotConfigured : surrogate == SurrogateStatus.NotConfigured) ||
                (!readable && surrogate != SurrogateStatus.NotConfigured && surrogate != SurrogateStatus.Unavailable) ||
                (surrogate != SurrogateStatus.Matched && (external != "" || reference != RequirementValueStatus.NotRequested)) ||
                (reference != RequirementValueStatus.Readable && external != "") || reference == RequirementValueStatus.NotApplicable ||
                (surrogate == SurrogateStatus.Matched && request.SurrogateMapping?.ReferenceAttribute == null && reference != RequirementValueStatus.NotRequested)) throw Failure();
            return new LinkEndpoint(path, kind, version, absolute, identifier, status, surrogate, external, reference);
        }
        private static string PathValue(string encoded, bool emptyAllowed)
        {
            string value = Decode(encoded);
            if (emptyAllowed && value == "") return value;
            try { if (value == "/" || DoorsItemPath.Normalize(value) != value) throw Failure(); }
            catch (IntegrationException ex) when (ex.Code != IntegrationErrorCode.LinkProtocolFailure) { throw Failure(ex); }
            return value;
        }
        private static string[] Columns(string line, int count, string tag)
        {
            if (line == null) throw Failure();
            var result = line.Split('\t');
            if (result.Length != count || result[0] != tag) throw Failure();
            return result;
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
            var output = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    if (++i == value.Length) throw Failure();
                    switch (value[i])
                    {
                        case '\\': c = '\\'; break;
                        case 't': c = '\t'; break;
                        case 'r': c = '\r'; break;
                        case 'n': c = '\n'; break;
                        default: throw Failure();
                    }
                }
                output.Append(c);
            }
            return output.ToString();
        }
        internal static IntegrationException Failure(Exception cause = null) => new IntegrationException(IntegrationErrorCode.LinkProtocolFailure,
            "Link aktarımı eksik, bozuk veya istenen modül/sürüm/yön ile uyuşmuyor. Sonuç tam bağlantı matrisi olarak kabul edilmedi.", cause);
        private sealed class Lines
        {
            private readonly TextReader reader;
            private readonly char[] buffer = new char[8192];
            private int offset, length;
            public Lines(TextReader reader) { this.reader = reader; }
            public string Read()
            {
                var value = new StringBuilder();
                while (true)
                {
                    if (offset == length)
                    {
                        offset = 0; length = reader.Read(buffer, 0, buffer.Length);
                        if (length == 0) { if (value.Length != 0) throw Failure(); return null; }
                    }
                    int newline = Array.IndexOf(buffer, '\n', offset, length - offset);
                    int size = (newline < 0 ? length : newline) - offset;
                    if ((long)value.Length + size > 131072) throw Failure();
                    value.Append(buffer, offset, size); offset += size;
                    if (newline >= 0)
                    {
                        offset++;
                        if (value.Length > 0 && value[value.Length - 1] == '\r') value.Length--;
                        return value.ToString();
                    }
                }
            }
        }
    }
}
