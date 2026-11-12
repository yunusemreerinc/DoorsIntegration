using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public enum ArtifactVersion { Unknown, Current, Baseline }
    public enum QualityAvailability { Available, Missing, Denied, NotRequested, TooLarge, Failed }
    public enum QualitySeverity { Information, Warning, Error }
    public enum QualityState { Passed, Finding, NotEvaluated, RuleError }
    public enum QualityRuleKind { RequiredAny, RequiredAll, PatternAbsent, Enumeration, UtcDate, Freshness, NumericRange }

    public sealed class ArtifactIdentity
    {
        public ArtifactIdentity(string repositoryId, string containerId, string objectId, ArtifactVersion versionKind = ArtifactVersion.Current, string version = "")
        {
            RepositoryId = ProcessingGuard.Text(repositoryId, 1024); ContainerId = ProcessingGuard.Text(containerId, 4096); ObjectId = ProcessingGuard.Text(objectId, 256);
            Version = ProcessingGuard.Text(version, 256, empty: true);
            if (!Enum.IsDefined(typeof(ArtifactVersion), versionKind) || (versionKind == ArtifactVersion.Baseline ? Version.Length == 0 : Version.Length != 0)) throw new ArgumentException("Invalid version identity.");
            VersionKind = versionKind; Key = ProcessingGuard.Hash(RepositoryId, ContainerId, ObjectId, VersionKind.ToString(), Version);
        }
        public string RepositoryId { get; }
        public string ContainerId { get; }
        public string ObjectId { get; }
        public ArtifactVersion VersionKind { get; }
        public string Version { get; }
        public string Key { get; }
        public string Display => ContainerId + " #" + ObjectId + " [" + VersionKind + " " + Version + "]";
    }
    public sealed class QualityField
    {
        public QualityField(string name, QualityAvailability availability, string value = null, string opaqueRichText = null)
        {
            Name = ProcessingGuard.Text(name, 256);
            if (!Enum.IsDefined(typeof(QualityAvailability), availability) || (availability == QualityAvailability.Available && value == null)) throw new ArgumentException("Invalid field availability.");
            Availability = availability; Value = value; OpaqueRichText = opaqueRichText;
        }
        public string Name { get; }
        public QualityAvailability Availability { get; }
        public string Value { get; }
        public string OpaqueRichText { get; }
    }
    public sealed class QualityArtifact
    {
        public QualityArtifact(ArtifactIdentity identity, IEnumerable<QualityField> fields, bool deleted = false)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            var values = (fields ?? throw new ArgumentNullException(nameof(fields))).Take(129).ToArray();
            if (values.Length > 128 || values.Any(v => v == null)) throw new ArgumentException("Invalid fields.");
            Fields = new ReadOnlyDictionary<string, QualityField>(values.ToDictionary(v => v.Name, StringComparer.Ordinal)); IsDeleted = deleted;
        }
        public ArtifactIdentity Identity { get; }
        public IReadOnlyDictionary<string, QualityField> Fields { get; }
        public bool IsDeleted { get; }
        public string Fingerprint => ProcessingGuard.Hash(new[] { Identity.Key, IsDeleted.ToString() }.Concat(Fields.Values.OrderBy(v => v.Name, StringComparer.Ordinal)
            .SelectMany(v => new[] { v.Name, v.Availability.ToString(), v.Value ?? "", v.OpaqueRichText ?? "" })).ToArray());
    }
    public sealed class QualityContext
    {
        public QualityContext(string sourceDescription, bool sourceComplete, DateTime utcNow, int maxArtifacts = 100000, int maxFieldCharacters = 65536)
        {
            SourceDescription = ProcessingGuard.Text(sourceDescription, 4096);
            if (utcNow.Kind != DateTimeKind.Utc || maxArtifacts < 1 || maxArtifacts > 1000000 || maxFieldCharacters < 1 || maxFieldCharacters > 16777216) throw new ArgumentException("Invalid quality limits/UTC clock.");
            SourceComplete = sourceComplete; UtcNow = utcNow; MaxArtifacts = maxArtifacts; MaxFieldCharacters = maxFieldCharacters;
        }
        public string SourceDescription { get; }
        public bool SourceComplete { get; }
        public DateTime UtcNow { get; }
        public int MaxArtifacts { get; }
        public int MaxFieldCharacters { get; }
    }
    public sealed class QualityRuleDescriptor
    {
        public QualityRuleDescriptor(string id, string revision, QualitySeverity severity, string standardContext = "", string projectReference = "")
        {
            Id = ProcessingGuard.Text(id, 128); Revision = ProcessingGuard.Text(revision, 64);
            if (!Enum.IsDefined(typeof(QualitySeverity), severity)) throw new ArgumentException("Invalid severity.");
            Severity = severity; StandardContext = ProcessingGuard.Text(standardContext, 128, empty: true); ProjectReference = ProcessingGuard.Text(projectReference, 512, empty: true);
        }
        public string Id { get; }
        public string Revision { get; }
        public QualitySeverity Severity { get; }
        public string StandardContext { get; }
        public string ProjectReference { get; }
    }
    public sealed class QualityRuleOutcome
    {
        public QualityRuleOutcome(QualityState state, string code)
        { if (!Enum.IsDefined(typeof(QualityState), state)) throw new ArgumentException(); State = state; Code = ProcessingGuard.Text(code, 256); }
        public QualityState State { get; }
        public string Code { get; }
    }
    public interface IRequirementQualityRule
    {
        QualityRuleDescriptor Descriptor { get; }
        string ConfigurationFingerprint { get; }
        QualityRuleOutcome Evaluate(QualityArtifact artifact, QualityContext context, CancellationToken cancellationToken);
    }
    [DataContract]
    public sealed class QualityObservation
    {
        internal QualityObservation(QualityArtifact artifact, QualityRuleDescriptor rule, QualityRuleOutcome outcome, string inputHash)
        {
            ArtifactKey = artifact.Identity.Key; Artifact = artifact.Identity.Display; RepositoryId = artifact.Identity.RepositoryId;
            RuleId = rule.Id; RuleRevision = rule.Revision; Severity = rule.Severity.ToString(); State = outcome.State.ToString(); Code = outcome.Code;
            StandardContext = rule.StandardContext; ProjectReference = rule.ProjectReference; InputHash = inputHash;
        }
        [DataMember] public string ArtifactKey { get; private set; }
        [DataMember] public string Artifact { get; private set; }
        [DataMember] public string RepositoryId { get; private set; }
        [DataMember] public string RuleId { get; private set; }
        [DataMember] public string RuleRevision { get; private set; }
        [DataMember] public string Severity { get; private set; }
        [DataMember] public string State { get; private set; }
        [DataMember] public string Code { get; private set; }
        [DataMember] public string StandardContext { get; private set; }
        [DataMember] public string ProjectReference { get; private set; }
        [DataMember] public string InputHash { get; private set; }
    }
    [DataContract]
    public sealed class QualitySummary
    {
        internal QualitySummary(Guid id, QualityContext context, long artifacts, long evaluations, long findings, long notEvaluated,
            long ruleErrors, bool cancelled, bool truncated, bool sourceFailed, int rules, string profileHash)
        {
            RunId = id.ToString("N"); SourceDescription = context.SourceDescription; SourceComplete = context.SourceComplete;
            Artifacts = artifacts; Evaluations = evaluations; Findings = findings; NotEvaluated = notEvaluated; RuleErrors = ruleErrors;
            Cancelled = cancelled; Truncated = truncated; SourceFailed = sourceFailed; RuleCount = rules; ProfileHash = profileHash;
            TimestampUtc = context.UtcNow.ToString("O");
            FullyEvaluated = SourceComplete && artifacts > 0 && !Cancelled && !Truncated && !SourceFailed && NotEvaluated == 0 && RuleErrors == 0 && RuleCount > 0;
            Disclosure = ProcessingDisclosure.Statement;
        }
        [DataMember] public string RunId { get; private set; }
        [DataMember] public string SourceDescription { get; private set; }
        [DataMember] public bool SourceComplete { get; private set; }
        [DataMember] public long Artifacts { get; private set; }
        [DataMember] public long Evaluations { get; private set; }
        [DataMember] public long Findings { get; private set; }
        [DataMember] public long NotEvaluated { get; private set; }
        [DataMember] public long RuleErrors { get; private set; }
        [DataMember] public int RuleCount { get; private set; }
        [DataMember] public string ProfileHash { get; private set; }
        [DataMember] public string TimestampUtc { get; private set; }
        [DataMember] public bool Cancelled { get; private set; }
        [DataMember] public bool Truncated { get; private set; }
        [DataMember] public bool SourceFailed { get; private set; }
        [DataMember] public bool FullyEvaluated { get; private set; }
        [DataMember] public bool ComplianceAssessed { get; private set; }
        [DataMember] public bool ToolQualified { get; private set; }
        [DataMember] public string Disclosure { get; private set; }
    }
    public interface IQualityReportSink
    {
        void Write(QualityObservation observation);
        void Complete(QualitySummary summary);
    }
}
