using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal static class RequirementWriteProtocol
    {
        public static RequirementWriteResult Parse(string text, Guid id, RequirementWriteTarget target,
            RequirementWriteRequest request, RequirementWritePreview approved, string mode)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || text.Length > 1048576 || !text.EndsWith("\n", StringComparison.Ordinal)) throw Failure();
                var lines = text.Split('\n');
                if (lines.Length < 4 || lines.Length > 20000) throw Failure();
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    if (lines[i].EndsWith("\r", StringComparison.Ordinal)) lines[i] = lines[i].Substring(0, lines[i].Length - 1);
                    if (lines[i].IndexOf('\r') >= 0) throw Failure();
                }
                if (lines[0] != "DOORS_REQUIREMENT_WRITE|1|" + id.ToString("N") || lines[lines.Length - 2] != "END") throw Failure();
                var m = lines[1].Split('\t');
                if (m.Length != 15 || m[0] != "M" || m[1] != mode || Decode(m[2], 8192) != target.ModulePath) throw Failure();
                string database = Identity(m[3]), user = Identity(m[4]), module = Identity(m[5]), qualified = Identity(m[6]);
                if ((module.Length == 0) != (qualified.Length == 0)) throw Failure();
                string state = Decode(m[7], 32768);
                var outcome = ReadEnum<RequirementWriteOutcome>(m[8]);
                var persistence = ReadEnum<RequirementWritePersistence>(m[9]);
                var cleanup = ReadEnum<RequirementWriteCleanup>(m[10]);
                int absolute = Number(m[11]), parent = Number(m[13]), level = Number(m[14]);
                string identifier = Identity(m[12]);
                var attributes = new List<RequirementAttributeSchema>();
                var enums = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var definitions = new List<string[]>();
                var issues = new List<RequirementWriteIssue>();
                var steps = new List<RequirementWriteStep>();
                var beforeValues = new List<RequirementAttributeBeforeValue>();
                for (int i = 2; i < lines.Length - 2; i++)
                {
                    var row = lines[i].Split('\t');
                    if (row[0] == "A" && row.Length == 10)
                    {
                        if (definitions.Count >= 256) throw Failure();
                        string name = Identity(row[1]);
                        RequirementWriteTargetForProtocol(name);
                        if (enums.ContainsKey(name)) throw Failure();
                        enums.Add(name, new List<string>());
                        row[1] = name; definitions.Add(row);
                    }
                    else if (row[0] == "E" && row.Length == 3)
                    {
                        string name = Identity(row[1]);
                        if (!enums.TryGetValue(name, out var values) || values.Count >= 512) throw Failure();
                        string value = Decode(row[2], 1024);
                        if (values.Contains(value, StringComparer.Ordinal) || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) throw Failure();
                        values.Add(value);
                    }
                    else if (row[0] == "B" && row.Length == 4)
                    {
                        string name = Identity(row[1]);
                        if (request == null || request.IsCreate || !request.Changes.Any(c => c.Name == name) || beforeValues.Any(v => v.Name == name)) throw Failure();
                        beforeValues.Add(new RequirementAttributeBeforeValue(name, Decode(row[2], 32768), Decode(row[3], 32768)));
                    }
                    else if (row[0] == "I" && row.Length == 4)
                    {
                        if (issues.Count >= 256) throw Failure();
                        issues.Add(new RequirementWriteIssue(ReadEnum<RequirementWriteIssueSeverity>(row[1]),
                            ReadEnum<RequirementWriteIssueCode>(row[2]), Identity(row[3])));
                    }
                    else if (row[0] == "S" && row.Length == 5)
                    {
                        if (steps.Count >= 36) throw Failure();
                        var status = ReadEnum<RequirementWriteStepStatus>(row[3]);
                        RequirementWriteIssueCode? error = row[4].Length == 0 ? (RequirementWriteIssueCode?)null : ReadEnum<RequirementWriteIssueCode>(row[4]);
                        if ((status == RequirementWriteStepStatus.Failed) != error.HasValue) throw Failure();
                        steps.Add(new RequirementWriteStep(ReadEnum<RequirementWriteStepKind>(row[1]), Identity(row[2]), status, error));
                    }
                    else throw Failure();
                }
                foreach (var row in definitions)
                {
                    string baseType = Identity(row[3]);
                    if (baseType != "Enumeration" && enums[row[1]].Count != 0) throw Failure();
                    attributes.Add(new RequirementAttributeSchema(row[1], Identity(row[2]), baseType, Flag(row[4]), Flag(row[5]),
                        Flag(row[6]), Flag(row[7]), Flag(row[8]), Flag(row[9]), enums[row[1]]));
                }
                var schema = new RequirementModuleSchema(id, target.ModulePath, database, user, module, qualified, attributes, issues);
                if (request != null && !request.IsCreate && schema.IsAvailable &&
                    (outcome == RequirementWriteOutcome.Preview || outcome == RequirementWriteOutcome.Applied) && beforeValues.Count != request.Changes.Count) throw Failure();
                var expectedSteps = Plan(request);
                if (steps.Count != expectedSteps.Count || steps.Where((s, i) => s.Kind != expectedSteps[i].Kind || s.AttributeName != expectedSteps[i].AttributeName).Any()) throw Failure();
                if (mode != "commit")
                {
                    if (outcome != (mode == "schema" ? RequirementWriteOutcome.Schema : RequirementWriteOutcome.Preview) ||
                        persistence != RequirementWritePersistence.NotAttempted || steps.Any(s => s.Status != RequirementWriteStepStatus.Skipped) ||
                        cleanup == RequirementWriteCleanup.LeftOpenForReview) throw Failure();
                }
                else
                {
                    if (approved == null || outcome == RequirementWriteOutcome.Schema || outcome == RequirementWriteOutcome.Preview) throw Failure();
                    if (outcome == RequirementWriteOutcome.Applied && (!schema.IsAvailable || module != approved.Inspection.Schema.ModuleId ||
                        qualified != approved.Inspection.Schema.QualifiedModuleId || database != approved.Inspection.Schema.DatabaseId ||
                        user != approved.Inspection.Schema.UserName || state != approved.Inspection.BeforeState ||
                        persistence != RequirementWritePersistence.Saved || cleanup != RequirementWriteCleanup.Closed ||
                        absolute == 0 || identifier.Length == 0 || level < 1 || steps.Any(s => s.Status != RequirementWriteStepStatus.Applied))) throw Failure();
                    if (outcome == RequirementWriteOutcome.Applied && (beforeValues.Count != approved.Inspection.BeforeValues.Count ||
                        beforeValues.Where((v, i) => v.Name != approved.Inspection.BeforeValues[i].Name || v.PlainText != approved.Inspection.BeforeValues[i].PlainText ||
                            v.RichTextWithOle != approved.Inspection.BeforeValues[i].RichTextWithOle).Any())) throw Failure();
                    if (outcome == RequirementWriteOutcome.Blocked || outcome == RequirementWriteOutcome.ContextChanged)
                    {
                        if (persistence != RequirementWritePersistence.NotAttempted || steps.Any(s => s.Status != RequirementWriteStepStatus.Skipped)) throw Failure();
                        if (outcome == RequirementWriteOutcome.Blocked && schema.IsAvailable) throw Failure();
                    }
                    bool halted = false;
                    foreach (var step in steps)
                    {
                        if (halted && step.Status != RequirementWriteStepStatus.Skipped) throw Failure();
                        if (step.Status != RequirementWriteStepStatus.Applied) halted = true;
                    }
                    if (request.AbsoluteNumber.HasValue && absolute != 0 && absolute != request.AbsoluteNumber.Value) throw Failure();
                }
                if (absolute == 0 && (identifier.Length != 0 || parent != 0 || level != 0)) throw Failure();
                if (absolute != 0 && (identifier.Length == 0 || level < 1 || parent == absolute)) throw Failure();
                return new RequirementWriteResult(schema, state, outcome, persistence, cleanup, absolute == 0 ? (int?)null : absolute,
                    identifier, parent == 0 ? (int?)null : parent, level, steps, beforeValues: beforeValues);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OverflowException || ex is IntegrationException)
            { throw Failure(); }
        }

        internal static IReadOnlyList<RequirementWriteStep> Plan(RequirementWriteRequest request)
        {
            var steps = new List<RequirementWriteStep>();
            if (request == null) return steps;
            if (request.IsCreate)
            {
                steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Create, "", RequirementWriteStepStatus.Skipped));
                steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Position, "", RequirementWriteStepStatus.Skipped));
            }
            foreach (var change in request.Changes) steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Attribute, change.Name, RequirementWriteStepStatus.Skipped));
            if (!request.IsCreate && request.Position.Kind != RequirementPositionKind.Keep)
                steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Position, "", RequirementWriteStepStatus.Skipped));
            steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Save, "", RequirementWriteStepStatus.Skipped));
            steps.Add(new RequirementWriteStep(RequirementWriteStepKind.Verify, "", RequirementWriteStepStatus.Skipped));
            return steps;
        }
        private static bool Flag(string value) => value == "1" ? true : value == "0" ? false : throw Failure();
        private static int Number(string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 0 ||
                number.ToString(CultureInfo.InvariantCulture) != value) throw Failure();
            return number;
        }
        private static T ReadEnum<T>(string value) where T : struct
        {
            if (!Enum.TryParse(value, false, out T result) || !Enum.IsDefined(typeof(T), result) || result.ToString() != value) throw Failure();
            return result;
        }
        private static void RequirementWriteTargetForProtocol(string name)
        { if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name != name.Trim()) throw Failure(); }
        private static string Identity(string value)
        {
            string decoded = Decode(value, 1024);
            if (decoded.Any(char.IsControl)) throw Failure();
            return decoded;
        }
        internal static string Decode(string value, int limit)
        {
            var b = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c)) throw Failure();
                if (c == '\\')
                {
                    if (++i >= value.Length) throw Failure();
                    switch (value[i])
                    {
                        case '\\': c = '\\'; break;
                        case 't': c = '\t'; break;
                        case 'r': c = '\r'; break;
                        case 'n': c = '\n'; break;
                        default: throw Failure();
                    }
                }
                b.Append(c);
                if (b.Length > limit) throw Failure();
            }
            var result = b.ToString();
            for (int i = 0; i < result.Length; i++)
            {
                if (char.IsHighSurrogate(result[i])) { if (++i >= result.Length || !char.IsLowSurrogate(result[i])) throw Failure(); }
                else if (char.IsLowSurrogate(result[i])) throw Failure();
            }
            return result;
        }
        internal static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.RequirementWriteProtocolFailure,
            "Gereksinim yazma yanıtı eksik, tutarsız veya başka isteğe ait; başarı kabul edilmedi.");
    }
}
