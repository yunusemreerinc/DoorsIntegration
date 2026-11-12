using System;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal static class BaselineExecutionProtocol
    {
        internal static BaselineModuleExecutionResult Parse(string text, Guid operationId, BaselineExecutionAuthorization authorization, DateTime start)
        {
            try
            {
                if (text == null || text.Length > 131072 || !text.EndsWith("\n", StringComparison.Ordinal)) throw Failure();
                string[] lines = text.Split('\n');
                if (lines.Length != 6 || lines[0] != "DOORS_BASELINE_EXECUTE\t1\t" + operationId.ToString("N") || lines[4] != "END" || lines[5] != "") throw Failure();
                string[] m = lines[1].Split('\t'), t = lines[2].Split('\t'), r = lines[3].Split('\t');
                var item = authorization.Item; var values = DxlBaselineExecutionAdapter.Parameters(item);
                if (m.Length != 5 || m[0] != "M" || Decode(m[1]) != item.Module.CanonicalFullPath || t.Length != 5 || t[0] != "T" ||
                    t[1] != values["major"] || t[2] != values["minor"] || Decode(t[3]) != item.Probe.Suffix || Decode(t[4]) != item.Probe.Annotation ||
                    r.Length != 5 || r[0] != "R" || (r[2] != "0" && r[2] != "1")) throw Failure();
                bool attempted = r[2] == "1";
                BaselineWriteOutcome outcome;
                if (r[1] == "Created") outcome = BaselineWriteOutcome.Created;
                else if (r[1] == "Blocked") outcome = BaselineWriteOutcome.Blocked;
                else if (r[1] == "NeedsReconciliation") outcome = BaselineWriteOutcome.NeedsReconciliation;
                else throw Failure();
                if ((outcome == BaselineWriteOutcome.Blocked) == attempted || (r[4] != "NotOpened" && r[4] != "NeedsInspection" && r[4] != "CreateClosesModule" && r[4] != "Closed")) throw Failure();
                string moduleId = Decode(m[2]), database = Decode(m[3]), user = Decode(m[4]);
                if (attempted && (moduleId != item.Module.ModuleId || database != values["database"] || user != values["user"])) throw Failure();
                if (outcome == BaselineWriteOutcome.Created && (r[3] != "Verified" || r[4] != "Closed")) throw Failure();
                if (r[3].Length == 0 || r[3].Length > 128) throw Failure();
                foreach (char c in r[3]) if (!char.IsLetter(c)) throw Failure();
                // A dirty or unclosed owned module needs inspection even if no create was attempted.
                if (r[4] == "NeedsInspection") outcome = BaselineWriteOutcome.NeedsReconciliation;
                return new BaselineModuleExecutionResult(authorization.Review.JobId, item, outcome, BaselineMetadataSource.NativeDxl,
                    attempted, "NativeReason=" + r[3] + "; Cleanup=" + r[4] + "; AutomaticRetry=False", start, DateTime.UtcNow, operationId);
            }
            catch (IntegrationException) { throw; }
            catch (Exception) { throw Failure(); }
        }
        private static string Decode(string value)
        {
            var b = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i]; if (char.IsControl(c)) throw Failure();
                if (c == '\\')
                {
                    if (++i >= value.Length) throw Failure();
                    switch (value[i]) { case '\\': c = '\\'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break; default: throw Failure(); }
                }
                if (char.IsHighSurrogate(c)) { if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) throw Failure(); b.Append(c).Append(value[++i]); }
                else if (char.IsLowSurrogate(c)) throw Failure(); else b.Append(c);
            }
            return b.ToString();
        }
        internal static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.BaselineExecutionFailed,
            "Baseline execution response is malformed or inconsistent; creation was not assumed and no retry was scheduled.");
    }
}
