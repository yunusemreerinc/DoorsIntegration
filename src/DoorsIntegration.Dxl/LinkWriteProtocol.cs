using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal static class LinkWriteProtocol
    {
        public static LinkWriteResult Parse(string text, Guid id, LinkWriteRequest request, LinkWritePreview approved)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || text.Length > 1048576 || !text.EndsWith("\n", StringComparison.Ordinal)) throw Failure();
                var lines = text.Replace("\r\n", "\n").Split('\n');
                if (lines.Length < 5 || lines.Length > 4200 || lines[0] != "DOORS_LINK_WRITE|1|" + id.ToString("N") || lines[lines.Length - 2] != "END") throw Failure();
                var r = lines[1].Split('\t');
                if (r.Length != 9 || r[0] != "R" || r[1] != (approved == null ? "preview" : "commit") || r[2] != request.Action.ToString() ||
                    Decode(r[3], 4096) != request.SourceModulePath || Number(r[4]) != request.SourceAbsoluteNumber ||
                    Decode(r[5], 4096) != request.TargetModulePath || Number(r[6]) != request.TargetAbsoluteNumber ||
                    Decode(r[7], 4096) != request.LinkModulePath || r[8] != (approved?.PlanId.ToString("N") ?? "")) throw Failure();
                var m = lines[2].Split('\t');
                if (m.Length != 14 || m[0] != "M") throw Failure();
                var outcome = Value<LinkWriteOutcome>(m[9]); var persistence = Value<LinkWritePersistence>(m[10]); var cleanup = Value<LinkWriteCleanup>(m[11]);
                bool verified = m[12] == "1" ? true : m[12] == "0" ? false : throw Failure();
                if (m[13] != "SelectedSourceObjectOutgoing") throw Failure();
                var before = new List<LinkTraceEdge>(); var after = new List<LinkTraceEdge>(); var issues = new List<LinkWriteIssue>();
                for (int i = 3; i < lines.Length - 2; i++)
                {
                    var row = lines[i].Split('\t');
                    if (row.Length == 2 && row[0] == "I")
                    { if (issues.Count >= 64) throw Failure(); issues.Add(Value<LinkWriteIssue>(row[1])); }
                    else if (row.Length == 6 && (row[0] == "B" || row[0] == "A"))
                    {
                        var list = row[0] == "B" ? before : after;
                        if (list.Count >= request.MaxTraceLinks) throw Failure();
                        list.Add(new LinkTraceEdge(Decode(row[1], 4096), Identity(row[2]), Value<LinkVersionKind>(row[3]), Identity(row[4]), Number(row[5])));
                    }
                    else throw Failure();
                }
                var result = new LinkWriteResult(id, Identity(m[1]), Identity(m[2]), Identity(m[3]), Identity(m[4]), Identity(m[5]),
                    Identity(m[6]), Identity(m[7]), Decode(m[8], 32768), outcome, persistence, cleanup, issues, before, after, verified);
                bool hasContext = result.DatabaseId.Length != 0 && result.UserName.Length != 0 && result.SourceModuleId.Length != 0 &&
                    result.TargetModuleId.Length != 0 && result.LinkModuleId.Length != 0 && result.SourceIdentifier.Length != 0 &&
                    result.TargetIdentifier.Length != 0 && result.BeforeState.Length != 0;
                int matches = before.Count(e => e.TargetModulePath == request.TargetModulePath && e.TargetModuleId == result.TargetModuleId &&
                    e.VersionKind == LinkVersionKind.Current && e.TargetAbsoluteNumber == request.TargetAbsoluteNumber);
                if (approved == null)
                {
                    if (outcome != LinkWriteOutcome.Preview || persistence != LinkWritePersistence.NotAttempted || verified || after.Count != 0 ||
                        cleanup == LinkWriteCleanup.LeftOpenForReview) throw Failure();
                    if (issues.Count == 0 && (!hasContext || cleanup != LinkWriteCleanup.Closed ||
                        (request.Action == LinkWriteAction.Create ? matches != 0 || before.Count >= request.MaxTraceLinks : matches != 1))) throw Failure();
                }
                else
                {
                    if (outcome == LinkWriteOutcome.Preview) throw Failure();
                    if (outcome == LinkWriteOutcome.Blocked || outcome == LinkWriteOutcome.ContextChanged)
                    { if (persistence != LinkWritePersistence.NotAttempted || verified || after.Count != 0 || issues.Count == 0) throw Failure(); }
                    if (outcome == LinkWriteOutcome.Applied)
                    {
                        var expected = approved.Inspection;
                        if (!result.Succeeded || !hasContext || !result.Comparison.Matches(request, result.TargetModuleId) ||
                            result.DatabaseId != expected.DatabaseId || result.UserName != expected.UserName || result.SourceModuleId != expected.SourceModuleId ||
                            result.TargetModuleId != expected.TargetModuleId || result.LinkModuleId != expected.LinkModuleId ||
                            result.SourceIdentifier != expected.SourceIdentifier || result.TargetIdentifier != expected.TargetIdentifier ||
                            result.BeforeState != expected.BeforeState || LinkTraceComparison.Compare(expected.Before, before).Added.Count != 0 ||
                            LinkTraceComparison.Compare(expected.Before, before).Removed.Count != 0 ||
                            (request.Action == LinkWriteAction.Create ? matches != 0 : matches != 1)) throw Failure();
                    }
                    if (outcome == LinkWriteOutcome.PartialFailure && issues.Count == 0) throw Failure();
                    if (persistence == LinkWritePersistence.Unknown && outcome != LinkWriteOutcome.OutcomeUnknown) throw Failure();
                }
                if (!verified && after.Count != 0) throw Failure();
                return result;
            }
            catch (IntegrationException ex) when (ex.Code != IntegrationErrorCode.LinkWriteProtocolFailure) { throw Failure(); }
            catch (ArgumentException) { throw Failure(); }
            catch (OverflowException) { throw Failure(); }
        }
        private static string Decode(string value, int limit) => RequirementWriteProtocol.Decode(value, limit);
        private static string Identity(string value)
        { string result = Decode(value, 8192); if (result.Any(char.IsControl)) throw Failure(); return result; }
        private static int Number(string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0 || number.ToString(CultureInfo.InvariantCulture) != value) throw Failure();
            return number;
        }
        private static T Value<T>(string value) where T : struct
        { if (!Enum.TryParse(value, false, out T result) || !Enum.IsDefined(typeof(T), result) || result.ToString() != value) throw Failure(); return result; }
        internal static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.LinkWriteProtocolFailure,
            "Link write response is incomplete, inconsistent or belongs to another request. Success was not accepted.");
    }
}
