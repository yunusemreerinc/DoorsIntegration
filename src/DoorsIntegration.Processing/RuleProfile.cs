using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DoorsIntegration.Processing
{
    public sealed class RuleProfileApproval
    {
        public RuleProfileApproval(string projectId, string profileFingerprint, string reviewer, string reference, DateTime approvedAtUtc, DateTime? expiresAtUtc = null)
        {
            ProjectId = ProcessingGuard.Text(projectId, 256); ProfileFingerprint = ProcessingGuard.Text(profileFingerprint, 128);
            Reviewer = ProcessingGuard.Text(reviewer, 256); Reference = ProcessingGuard.Text(reference, 1024);
            if (approvedAtUtc.Kind != DateTimeKind.Utc || (expiresAtUtc.HasValue && (expiresAtUtc.Value.Kind != DateTimeKind.Utc || expiresAtUtc <= approvedAtUtc))) throw new ArgumentException("Invalid approval UTC dates.");
            ApprovedAtUtc = approvedAtUtc; ExpiresAtUtc = expiresAtUtc;
        }
        public string ProjectId { get; }
        public string ProfileFingerprint { get; }
        public string Reviewer { get; }
        public string Reference { get; }
        public DateTime ApprovedAtUtc { get; }
        public DateTime? ExpiresAtUtc { get; }
    }
    public sealed class RuleProfile
    {
        public RuleProfile(string id, string revision, RuleStandard standard, IEnumerable<Rule> rules, string projectId = "", string projectReference = "", bool exampleOnly = true, RuleProfileApproval approval = null)
        {
            Id = ProcessingGuard.Text(id, 128); Revision = ProcessingGuard.Text(revision, 64);
            ProjectId = ProcessingGuard.Text(projectId, 256, empty: true); ProjectReference = ProcessingGuard.Text(projectReference, 1024, empty: true);
            if (!Enum.IsDefined(typeof(RuleStandard), standard) || (!exampleOnly && (ProjectId.Length == 0 || ProjectReference.Length == 0))) throw new ArgumentException("Project profiles require an explicit project and review-plan reference.");
            var values = (rules ?? throw new ArgumentNullException(nameof(rules))).Take(129).ToArray();
            if (values.Length == 0 || values.Length > 128 || values.Any(r => r == null || string.IsNullOrWhiteSpace(r.ConfigurationFingerprint)) || values.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Invalid profile rules.");
            Rules = Array.AsReadOnly(values); Standard = standard; ExampleOnly = exampleOnly;
            Fingerprint = RuleGuard.Hash(new[] { "RuleProfile-v1", Id, Revision, standard.ToString(), ProjectId, ProjectReference, exampleOnly.ToString() }.Concat(values.Select(r => r.ConfigurationFingerprint)));
            if (approval != null && (approval.ProjectId != ProjectId || approval.ProfileFingerprint != Fingerprint || exampleOnly || !values.Any(r => r.Enabled) || values.Any(r => r.Enabled && r.ConfigurationIssue.Length > 0)))
                throw new ArgumentException("Stale, foreign or unconfigured profile approval.");
            Approval = approval;
        }
        public string Id { get; }
        public string Revision { get; }
        public RuleStandard Standard { get; }
        public string ProjectId { get; }
        public string ProjectReference { get; }
        public bool ExampleOnly { get; }
        public IReadOnlyList<Rule> Rules { get; }
        public string Fingerprint { get; }
        public RuleProfileApproval Approval { get; }
        public bool CanApprove => !ExampleOnly && Rules.Any(r => r.Enabled) && Rules.All(r => !r.Enabled || r.ConfigurationIssue.Length == 0);
        public string Disclosure => ProcessingDisclosure.Statement;
        public bool IsApprovedFor(string projectId, DateTime utcNow) => utcNow.Kind == DateTimeKind.Utc && !ExampleOnly && Approval != null &&
            ProjectId == projectId && Approval.ProjectId == projectId && Approval.ProfileFingerprint == Fingerprint && Approval.ApprovedAtUtc <= utcNow && (!Approval.ExpiresAtUtc.HasValue || utcNow < Approval.ExpiresAtUtc.Value);
        public RuleProfile ForProject(string projectId, string projectReference, IEnumerable<Rule> configuredRules) => new RuleProfile(Id, Revision, Standard, configuredRules, projectId, projectReference, false);
        public RuleProfile Approve(string acknowledgedFingerprint, string reviewer, string reference, DateTime approvedAtUtc, DateTime? expiresAtUtc = null)
        {
            if (acknowledgedFingerprint != Fingerprint || !CanApprove)
                throw new InvalidOperationException("Review and configure a project-bound profile before acknowledging its exact fingerprint.");
            return new RuleProfile(Id, Revision, Standard, Rules, ProjectId, ProjectReference, false, new RuleProfileApproval(ProjectId, Fingerprint, reviewer, reference, approvedAtUtc, expiresAtUtc));
        }
        public static RuleProfile LoadJson(string path) => RuleProfileJson.Load(path);
        public static RuleProfile ParseJson(string json) => RuleProfileJson.Read(json);
        public void SaveJson(string newPath) => RuleProfileJson.Save(this, newPath);
    }

    public static class BuiltinRuleProfiles
    {
        public static RuleProfile CreateTemplate(RuleStandard standard)
        {
            if (!Enum.IsDefined(typeof(RuleStandard), standard)) throw new ArgumentException();
            string[] verification = standard == RuleStandard.DO200B ? new[] { "Validation Method", "Acceptance Criteria" } : new[] { "Verification Method", "Acceptance Criteria" };
            string[] required = standard == RuleStandard.DO330 ? new[] { "Status", "Tool Requirement Reference" } : standard == RuleStandard.DO200B ? new[] { "Status", "Data Source" } : new[] { "Status" };
            return new RuleProfile("example-" + standard.ToString().ToLowerInvariant() + "-quality", "1", standard, new Rule[] {
                new ConfiguredRule("empty", "1", RuleKind.EmptyRequirement, RuleSeverity.Error, new[] { "Object Text" }),
                new ConfiguredRule("ambiguity", "1", RuleKind.AmbiguousWords, RuleSeverity.Warning, new[] { "Object Text" }, new[] { "TBD", "TBC", "appropriate", "as needed", "etc", "fast", "uygun", "gerektiğinde", "hızlı" }),
                new ConfiguredRule("obligations", "1", RuleKind.MultipleObligations, RuleSeverity.Warning, new[] { "Object Text" }, new[] { "shall", "must", "zorundadır", "olmalıdır", "yapmalıdır" }, minimumCount: 2),
                new ConfiguredRule("verifiability", "1", RuleKind.Verifiability, RuleSeverity.Warning, evidenceFields: verification, minimumCount: 2),
                new ConfiguredRule("required", "1", RuleKind.RequiredAttributes, RuleSeverity.Error, required),
                new ConfiguredRule("trace", "1", RuleKind.Traceability, RuleSeverity.Warning, linkModulePath: "") });
        }
    }

    internal static class RuleProfileJson
    {
        private const int MaxBytes = 1048576;
        internal static RuleProfile Load(string path)
        {
            string text;
            using (var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            { if (stream.Length > MaxBytes) throw new ArgumentException("Profile exceeds 1 MiB."); using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) text = reader.ReadToEnd(); }
            return Read(text);
        }
        internal static RuleProfile Read(string text)
        {
            if (text == null || text.Length > MaxBytes) throw new ArgumentException("Profile limit.");
            Validate(text); var dto = ProcessingJson.Decode<ProfileDto>(text);
            if (dto.SchemaVersion != 1 || dto.Rules == null) throw new ArgumentException("Unsupported rule profile schema.");
            var rules = dto.Rules.Select(r => (Rule)new ConfiguredRule(r.Id, r.Revision, EnumValue<RuleKind>(r.Kind), EnumValue<RuleSeverity>(r.Severity), r.Fields, r.Terms, r.EvidenceFields,
                r.MinimumCount ?? 1, r.LinkModulePath ?? "", r.Direction == null ? CoverageDirection.Outgoing : EnumValue<CoverageDirection>(r.Direction), r.TargetModules,
                r.VersionPolicy == null ? CoverageVersionPolicy.SameVersion : EnumValue<CoverageVersionPolicy>(r.VersionPolicy), r.RequiredNativeLinkModuleId, r.Enabled, r.Suggestion ?? "")).ToArray();
            var approval = dto.Approval == null ? null : new RuleProfileApproval(dto.Approval.ProjectId, dto.Approval.ProfileFingerprint, dto.Approval.Reviewer, dto.Approval.Reference,
                Utc(dto.Approval.ApprovedAtUtc), dto.Approval.ExpiresAtUtc == null ? (DateTime?)null : Utc(dto.Approval.ExpiresAtUtc));
            return new RuleProfile(dto.Id, dto.Revision, EnumValue<RuleStandard>(dto.Standard), rules, dto.ProjectId, dto.ProjectReference, dto.ExampleOnly, approval);
        }
        internal static void Save(RuleProfile profile, string path)
        {
            var rules = new List<RuleDto>();
            foreach (var rule in profile.Rules)
            {
                var r = rule as ConfiguredRule ?? throw new NotSupportedException("Executable custom rules cannot be loaded from JSON; register them in trusted host code.");
                rules.Add(new RuleDto { Id = r.Id, Revision = r.Revision, Kind = r.Kind.ToString(), Severity = r.Severity.ToString(), Enabled = r.Enabled, Fields = r.Fields.ToArray(), Terms = r.Terms.ToArray(), EvidenceFields = r.EvidenceFields.ToArray(),
                    MinimumCount = r.MinimumCount, LinkModulePath = r.LinkModulePath, Direction = r.Direction.ToString(), TargetModules = r.TargetModules.ToArray(), VersionPolicy = r.VersionPolicy.ToString(), RequiredNativeLinkModuleId = r.RequiredNativeLinkModuleId, Suggestion = r.Suggestion });
            }
            var dto = new ProfileDto { SchemaVersion = 1, Id = profile.Id, Revision = profile.Revision, Standard = profile.Standard.ToString(), ProjectId = profile.ProjectId, ProjectReference = profile.ProjectReference, ExampleOnly = profile.ExampleOnly, Rules = rules.ToArray() };
            if (profile.Approval != null) dto.Approval = new ApprovalDto { ProjectId = profile.Approval.ProjectId, ProfileFingerprint = profile.Approval.ProfileFingerprint, Reviewer = profile.Approval.Reviewer,
                Reference = profile.Approval.Reference, ApprovedAtUtc = profile.Approval.ApprovedAtUtc.ToString("O"), ExpiresAtUtc = profile.Approval.ExpiresAtUtc?.ToString("O") };
            string json = ProcessingJson.Encode(dto); if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new ArgumentException("Profile exceeds 1 MiB.");
            using (var stream = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true))) { writer.Write(json); writer.Flush(); stream.Flush(true); }
        }
        private static T EnumValue<T>(string value) where T : struct
        { if (!Enum.TryParse(value, false, out T parsed) || !Enum.IsDefined(typeof(T), parsed) || parsed.ToString() != value) throw new ArgumentException("Unknown rule profile enum."); return parsed; }
        private static DateTime Utc(string value)
        { if (!DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) || date.Kind != DateTimeKind.Utc) throw new ArgumentException("Explicit round-trip UTC date required."); return date; }
        private static void Validate(string text)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(new UTF8Encoding(false, true).GetBytes(text), new XmlDictionaryReaderQuotas { MaxDepth = 16, MaxArrayLength = 1024, MaxStringContentLength = MaxBytes, MaxNameTableCharCount = 65536, MaxBytesPerRead = 4096 }))
            {
                var root = XElement.Load(reader);
                Object(root, new[] { "schemaVersion", "id", "revision", "standard", "projectId", "projectReference", "exampleOnly", "rules", "approval" }, new[] { "schemaVersion", "id", "revision", "standard", "projectId", "projectReference", "exampleOnly", "rules" });
                foreach (var element in root.Elements())
                {
                    string name = element.Name.LocalName;
                    if (name == "rules")
                    {
                        Type(element, "array"); if (element.Elements().Count() > 128) throw new ArgumentException("Rule limit.");
                        foreach (var rule in element.Elements())
                        {
                            if (rule.Name != "item") throw new ArgumentException();
                            Object(rule, new[] { "id", "revision", "kind", "severity", "enabled", "fields", "terms", "evidenceFields", "minimumCount", "linkModulePath", "direction", "targetModules", "versionPolicy", "requiredNativeLinkModuleId", "suggestion" }, new[] { "id", "revision", "kind", "severity", "enabled" });
                            foreach (var property in rule.Elements())
                            { string key = property.Name.LocalName;
                                if (new[] { "fields", "terms", "evidenceFields", "targetModules" }.Contains(key)) { if ((string)property.Attribute("type") == "null") continue; Type(property, "array"); foreach (var item in property.Elements()) { if (item.Name != "item") throw new ArgumentException(); Type(item, "string"); } }
                                else Type(property, key == "enabled" ? "boolean" : key == "minimumCount" ? "number" : "string", key == "requiredNativeLinkModuleId"); }
                        }
                    }
                    else if (name == "approval")
                    {
                        if ((string)element.Attribute("type") == "null") continue;
                        Object(element, new[] { "projectId", "profileFingerprint", "reviewer", "reference", "approvedAtUtc", "expiresAtUtc" }, new[] { "projectId", "profileFingerprint", "reviewer", "reference", "approvedAtUtc" });
                        foreach (var property in element.Elements()) Type(property, "string", property.Name == "expiresAtUtc");
                    }
                    else Type(element, name == "schemaVersion" ? "number" : name == "exampleOnly" ? "boolean" : "string");
                }
            }
        }
        private static void Object(XElement element, string[] allowed, string[] required)
        { Type(element, "object"); var values = element.Elements().ToArray(); if (values.Any(v => v.Name.NamespaceName.Length != 0 || !allowed.Contains(v.Name.LocalName)) || values.GroupBy(v => v.Name).Any(g => g.Count() != 1) || required.Any(k => !values.Any(v => v.Name == k))) throw new ArgumentException("Unknown, duplicate or missing JSON profile property."); }
        private static void Type(XElement element, string type, bool nullable = false)
        { if (element.Attributes().Any(a => a.Name != "type") || ((string)element.Attribute("type") != type && !(nullable && (string)element.Attribute("type") == "null"))) throw new ArgumentException("Invalid JSON profile value type."); if (type != "object" && type != "array" && element.HasElements) throw new ArgumentException(); }

        [DataContract] private sealed class ProfileDto
        {
            [DataMember(Name="schemaVersion")] public int SchemaVersion;
            [DataMember(Name="id")] public string Id;
            [DataMember(Name="revision")] public string Revision;
            [DataMember(Name="standard")] public string Standard;
            [DataMember(Name="projectId")] public string ProjectId;
            [DataMember(Name="projectReference")] public string ProjectReference;
            [DataMember(Name="exampleOnly")] public bool ExampleOnly;
            [DataMember(Name="rules")] public RuleDto[] Rules;
            [DataMember(Name="approval")] public ApprovalDto Approval;
        }
        [DataContract] private sealed class RuleDto
        {
            [DataMember(Name="id")] public string Id;
            [DataMember(Name="revision")] public string Revision;
            [DataMember(Name="kind")] public string Kind;
            [DataMember(Name="severity")] public string Severity;
            [DataMember(Name="enabled")] public bool Enabled;
            [DataMember(Name="fields")] public string[] Fields;
            [DataMember(Name="terms")] public string[] Terms;
            [DataMember(Name="evidenceFields")] public string[] EvidenceFields;
            [DataMember(Name="minimumCount")] public int? MinimumCount;
            [DataMember(Name="linkModulePath")] public string LinkModulePath;
            [DataMember(Name="direction")] public string Direction;
            [DataMember(Name="targetModules")] public string[] TargetModules;
            [DataMember(Name="versionPolicy")] public string VersionPolicy;
            [DataMember(Name="requiredNativeLinkModuleId")] public string RequiredNativeLinkModuleId;
            [DataMember(Name="suggestion")] public string Suggestion;
        }
        [DataContract] private sealed class ApprovalDto
        {
            [DataMember(Name="projectId")] public string ProjectId;
            [DataMember(Name="profileFingerprint")] public string ProfileFingerprint;
            [DataMember(Name="reviewer")] public string Reviewer;
            [DataMember(Name="reference")] public string Reference;
            [DataMember(Name="approvedAtUtc")] public string ApprovedAtUtc;
            [DataMember(Name="expiresAtUtc")] public string ExpiresAtUtc;
        }
    }
}
