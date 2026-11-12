using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace DoorsIntegration.Processing
{
    public sealed class ConfiguredQualityRule : IRequirementQualityRule
    {
        private readonly QualityRuleKind kind;
        private readonly string[] fields, allowed;
        private readonly Regex pattern;
        private readonly decimal? minimum, maximum;
        private readonly int? maxAgeDays;
        private readonly string configurationIssue;
        public ConfiguredQualityRule(QualityRuleDescriptor descriptor, QualityRuleKind kind, IEnumerable<string> fields,
            string pattern = null, IEnumerable<string> allowedValues = null, decimal? minimum = null, decimal? maximum = null, int? maxAgeDays = null)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            if (!Enum.IsDefined(typeof(QualityRuleKind), kind)) throw new ArgumentException();
            this.kind = kind; this.fields = (fields ?? Enumerable.Empty<string>()).Take(33).ToArray();
            if (this.fields.Length > 32 || this.fields.Distinct(StringComparer.Ordinal).Count() != this.fields.Length) throw new ArgumentException("Invalid rule fields.");
            foreach (string field in this.fields) ProcessingGuard.Text(field, 256);
            allowed = (allowedValues ?? Enumerable.Empty<string>()).Take(513).ToArray();
            if (allowed.Length > 512) throw new ArgumentException("Too many enum values.");
            foreach (string value in allowed) ProcessingGuard.Text(value, 1024, empty: true);
            this.minimum = minimum; this.maximum = maximum; this.maxAgeDays = maxAgeDays;
            configurationIssue = this.fields.Length == 0 ? "FieldMappingNotConfigured" : "";
            if (kind == QualityRuleKind.PatternAbsent)
            {
                if (string.IsNullOrEmpty(pattern) || pattern.Length > 512) configurationIssue = "PatternNotConfigured";
                else { try { this.pattern = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); } catch (ArgumentException) { configurationIssue = "InvalidPattern"; } }
            }
            if (kind == QualityRuleKind.Enumeration && allowed.Length == 0) configurationIssue = "EnumerationNotConfigured";
            if (kind == QualityRuleKind.NumericRange && (!minimum.HasValue || !maximum.HasValue || minimum > maximum)) configurationIssue = "RangeNotConfigured";
            if (kind == QualityRuleKind.Freshness && (!maxAgeDays.HasValue || maxAgeDays < 0 || maxAgeDays > 36500)) configurationIssue = "FreshnessNotConfigured";
            ConfigurationHash = ProcessingGuard.Hash(new[] { descriptor.Id, descriptor.Revision, descriptor.Severity.ToString(), descriptor.StandardContext, descriptor.ProjectReference,
                kind.ToString(), pattern ?? "", minimum?.ToString(CultureInfo.InvariantCulture) ?? "", maximum?.ToString(CultureInfo.InvariantCulture) ?? "", maxAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "", "fields", ProcessingGuard.Number(this.fields.Length) }
                .Concat(this.fields).Concat(new[] { "allowed", ProcessingGuard.Number(allowed.Length) }).Concat(allowed).ToArray());
        }
        public QualityRuleDescriptor Descriptor { get; }
        public string ConfigurationHash { get; }
        public string ConfigurationFingerprint => ConfigurationHash;
        public QualityRuleOutcome Evaluate(QualityArtifact artifact, QualityContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configurationIssue.Length != 0) return Outcome(QualityState.NotEvaluated, configurationIssue);
            if (artifact.IsDeleted) return Outcome(QualityState.NotEvaluated, "DeletedArtifact");
            var values = new List<string>(); bool unavailable = false;
            foreach (string field in fields)
            {
                if (!artifact.Fields.TryGetValue(field, out var value) || value.Availability != QualityAvailability.Available) { unavailable = true; continue; }
                if (value.Value.Length > context.MaxFieldCharacters) return Outcome(QualityState.NotEvaluated, "FieldTooLarge");
                values.Add(value.Value);
            }
            if (kind == QualityRuleKind.RequiredAny && values.Any(v => !string.IsNullOrWhiteSpace(v))) return Outcome(QualityState.Passed, "ContentPresent");
            if (unavailable) return Outcome(QualityState.NotEvaluated, "EvidenceUnavailable");
            if (kind == QualityRuleKind.RequiredAll || kind == QualityRuleKind.RequiredAny)
                return values.All(v => !string.IsNullOrWhiteSpace(v)) ? Outcome(QualityState.Passed, "RequiredValuesPresent") : Outcome(QualityState.Finding, "RequiredValueEmpty");
            foreach (string value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (kind == QualityRuleKind.PatternAbsent)
                {
                    try { if (pattern.IsMatch(value)) return Outcome(QualityState.Finding, "ConfiguredAmbiguityPatternFound"); }
                    catch (RegexMatchTimeoutException) { return Outcome(QualityState.RuleError, "PatternEvaluationTimeout"); }
                }
                else if (kind == QualityRuleKind.Enumeration)
                { if (!allowed.Contains(value, StringComparer.Ordinal)) return Outcome(QualityState.Finding, "ValueOutsideConfiguredEnumeration"); }
                else if (kind == QualityRuleKind.NumericRange)
                {
                    if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) || number < minimum.Value || number > maximum.Value)
                        return Outcome(QualityState.Finding, "ValueOutsideConfiguredRange");
                }
                else if (kind == QualityRuleKind.UtcDate || kind == QualityRuleKind.Freshness)
                {
                    if (!DateTimeOffset.TryParseExact(value, new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" }, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)) return Outcome(QualityState.Finding, "ExplicitUtcTimestampRequired");
                    if (kind == QualityRuleKind.Freshness && (date.UtcDateTime > context.UtcNow || context.UtcNow - date.UtcDateTime > TimeSpan.FromDays(maxAgeDays.Value)))
                        return Outcome(QualityState.Finding, "TimestampOutsideConfiguredFreshnessWindow");
                }
            }
            return Outcome(QualityState.Passed, "ConfiguredCheckPassed");
        }
        private static QualityRuleOutcome Outcome(QualityState state, string code) => new QualityRuleOutcome(state, code);
    }
    public sealed class QualityProfile
    {
        public QualityProfile(string id, string revision, IEnumerable<IRequirementQualityRule> rules, bool exampleOnly = true)
        {
            Id = ProcessingGuard.Text(id, 128); Revision = ProcessingGuard.Text(revision, 64); ExampleOnly = exampleOnly;
            var values = (rules ?? throw new ArgumentNullException(nameof(rules))).Take(129).ToArray();
            if (values.Length > 128 || values.Any(r => r == null || r.Descriptor == null || string.IsNullOrWhiteSpace(r.ConfigurationFingerprint)) || values.Select(r => r.Descriptor.Id).Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Invalid/duplicate rules.");
            Rules = Array.AsReadOnly(values);
            Fingerprint = ProcessingGuard.Hash(new[] { id, revision, exampleOnly.ToString() }.Concat(values.SelectMany(r => new[] { r.Descriptor.Id, r.Descriptor.Revision,
                r.Descriptor.StandardContext, r.Descriptor.ProjectReference, r.Descriptor.Severity.ToString(), r.ConfigurationFingerprint })).ToArray());
        }
        public string Id { get; }
        public string Revision { get; }
        public bool ExampleOnly { get; }
        public string Fingerprint { get; }
        public IReadOnlyList<IRequirementQualityRule> Rules { get; }
        public static QualityProfile LoadXml(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 };
            using (var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(input, settings))
            {
                var root = XElement.Load(reader); Check(root, "qualityProfile", new[] { "id", "revision", "exampleOnly" }, new[] { "rule" });
                var rules = new List<IRequirementQualityRule>();
                foreach (var element in root.Elements())
                {
                    if (rules.Count >= 128) throw new ArgumentException("Rule limit.");
                    Check(element, "rule", new[] { "id", "revision", "severity", "kind", "standardContext", "projectReference", "pattern", "minimum", "maximum", "maxAgeDays" }, new[] { "field", "allowed" });
                    T Value<T>(string name) where T : struct
                    { string text = (string)element.Attribute(name); if (!Enum.TryParse(text, false, out T value) || !Enum.IsDefined(typeof(T), value) || value.ToString() != text) throw new ArgumentException("Invalid rule enumeration."); return value; }
                    decimal? Number(string name) { string text = (string)element.Attribute(name); return text == null ? (decimal?)null : decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture); }
                    var descriptor = new QualityRuleDescriptor((string)element.Attribute("id"), (string)element.Attribute("revision"), Value<QualitySeverity>("severity"),
                        (string)element.Attribute("standardContext") ?? "", (string)element.Attribute("projectReference") ?? "");
                    string days = (string)element.Attribute("maxAgeDays");
                    foreach (var child in element.Elements()) if (child.HasElements || child.HasAttributes) throw new ArgumentException("Invalid field mapping.");
                    rules.Add(new ConfiguredQualityRule(descriptor, Value<QualityRuleKind>("kind"), element.Elements("field").Select(e => e.Value), (string)element.Attribute("pattern"),
                        element.Elements("allowed").Select(e => e.Value), Number("minimum"), Number("maximum"), days == null ? (int?)null : int.Parse(days, CultureInfo.InvariantCulture)));
                }
                if (!bool.TryParse((string)root.Attribute("exampleOnly"), out bool example)) throw new ArgumentException("Profile exampleOnly flag is required.");
                return new QualityProfile((string)root.Attribute("id"), (string)root.Attribute("revision"), rules, example);
            }
        }
        private static void Check(XElement element, string name, string[] attributes, string[] children)
        {
            if (element.Name != name || element.Attributes().Any(a => a.Name.NamespaceName.Length != 0 || !attributes.Contains(a.Name.LocalName)) ||
                element.Elements().Any(e => e.Name.NamespaceName.Length != 0 || !children.Contains(e.Name.LocalName))) throw new ArgumentException("Unknown quality profile element/attribute.");
        }
    }
}
