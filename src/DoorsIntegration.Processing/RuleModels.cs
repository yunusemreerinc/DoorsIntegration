using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public enum RuleSeverity { Information, Warning, Error }
    public enum RuleState { Passed, Finding, NotEvaluated, RuleError, Skipped }
    public enum RuleKind { EmptyRequirement, AmbiguousWords, MultipleObligations, Verifiability, RequiredAttributes, Traceability }
    public enum RuleStandard { General, DO178C, DO200B, DO330 }
    public enum RuleExecutionMode { ApprovedProfileOnly, DraftPreview }
    public enum ReviewerDisposition { Pending, Accepted, Rejected, Waived, NeedsClarification, NotApplicable }
    public enum RuleTraceResolution { Confirmed, Unresolved, Inaccessible, ReferenceOnly, DeletedEndpoint }

    public sealed class RuleTraceLink
    {
        public RuleTraceLink(ArtifactIdentity counterpart, RuleTraceResolution resolution, bool versionContextKnown = true)
        { if (!Enum.IsDefined(typeof(RuleTraceResolution), resolution) || (resolution == RuleTraceResolution.Confirmed && counterpart == null)) throw new ArgumentException("Invalid trace evidence."); Counterpart = counterpart; Resolution = resolution; VersionContextKnown = versionContextKnown; }
        public ArtifactIdentity Counterpart { get; }
        public RuleTraceResolution Resolution { get; }
        public bool VersionContextKnown { get; }
    }
    public sealed class RuleTraceabilityEvidence
    {
        public RuleTraceabilityEvidence(ArtifactIdentity anchor, string linkModulePath, CoverageDirection direction, bool scopeComplete, string evidenceReference,
            IEnumerable<RuleTraceLink> links, string nativeLinkModuleId = null)
        {
            Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor)); LinkModulePath = RuleGuard.Path(linkModulePath);
            if (!Enum.IsDefined(typeof(CoverageDirection), direction)) throw new ArgumentException();
            var values = (links ?? throw new ArgumentNullException(nameof(links))).Take(100001).ToArray();
            if (values.Length > 100000 || values.Any(v => v == null)) throw new ArgumentException("Trace evidence limit.");
            Links = Array.AsReadOnly(values); Direction = direction; ScopeComplete = scopeComplete; EvidenceReference = ProcessingGuard.Text(evidenceReference, 1024);
            NativeLinkModuleId = nativeLinkModuleId == null ? null : ProcessingGuard.Text(nativeLinkModuleId, 1024);
            Fingerprint = RuleGuard.Hash(new[] { Anchor.Key, LinkModulePath, direction.ToString(), scopeComplete.ToString(), EvidenceReference, nativeLinkModuleId ?? "" }
                .Concat(values.SelectMany(v => new[] { v.Counterpart?.Key ?? "", v.Resolution.ToString(), v.VersionContextKnown.ToString() })));
        }
        public ArtifactIdentity Anchor { get; }
        public string LinkModulePath { get; }
        public CoverageDirection Direction { get; }
        public bool ScopeComplete { get; }
        public string EvidenceReference { get; }
        public string NativeLinkModuleId { get; }
        public IReadOnlyList<RuleTraceLink> Links { get; }
        public string Fingerprint { get; }
    }
    public interface IRuleTraceabilityProvider
    { IEnumerable<RuleTraceabilityEvidence> Read(ArtifactIdentity artifact, CancellationToken token); }

    public sealed class RuleRunContext
    {
        public RuleRunContext(string projectId, string sourceDescription, bool sourceComplete, DateTime utcNow,
            RuleExecutionMode mode = RuleExecutionMode.ApprovedProfileOnly, IRuleTraceabilityProvider traceability = null,
            int maxArtifacts = 100000, int maxFieldCharacters = 65536, int maxEvidencePerResult = 32, int maxExcerptCharacters = 256)
        {
            ProjectId = ProcessingGuard.Text(projectId, 256); SourceDescription = ProcessingGuard.Text(sourceDescription, 4096);
            if (utcNow.Kind != DateTimeKind.Utc || !Enum.IsDefined(typeof(RuleExecutionMode), mode) || maxArtifacts < 1 || maxArtifacts > 1000000 ||
                maxFieldCharacters < 1 || maxFieldCharacters > 16777216 || maxEvidencePerResult < 2 || maxEvidencePerResult > 64 || maxExcerptCharacters < 0 || maxExcerptCharacters > 1024) throw new ArgumentException("Invalid rule context.");
            SourceComplete = sourceComplete; UtcNow = utcNow; Mode = mode; Traceability = traceability; MaxArtifacts = maxArtifacts;
            MaxFieldCharacters = maxFieldCharacters; MaxEvidencePerResult = maxEvidencePerResult; MaxExcerptCharacters = maxExcerptCharacters;
        }
        public string ProjectId { get; }
        public string SourceDescription { get; }
        public bool SourceComplete { get; }
        public DateTime UtcNow { get; }
        public RuleExecutionMode Mode { get; }
        public IRuleTraceabilityProvider Traceability { get; }
        public int MaxArtifacts { get; }
        public int MaxFieldCharacters { get; }
        public int MaxEvidencePerResult { get; }
        public int MaxExcerptCharacters { get; }
    }
    public sealed class RuleContext
    {
        private readonly Dictionary<string, string> valueHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        public RuleContext(QualityArtifact artifact, RuleProfile profile, RuleRunContext run, IEnumerable<RuleTraceabilityEvidence> traceability = null)
        {
            Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact)); Profile = profile ?? throw new ArgumentNullException(nameof(profile)); Run = run ?? throw new ArgumentNullException(nameof(run));
            var traces = (traceability ?? Enumerable.Empty<RuleTraceabilityEvidence>()).Take(65).ToArray();
            if (traces.Length > 64 || traces.Any(t => t == null || t.Anchor.Key != artifact.Identity.Key) || traces.Select(t => t.LinkModulePath + "\n" + t.Direction).Distinct(StringComparer.Ordinal).Count() != traces.Length)
                throw new ArgumentException("Trace context must bind uniquely to this exact artifact/version.");
            Traceability = Array.AsReadOnly(traces);
            InputFingerprint = RuleGuard.Hash(new[] { artifact.Identity.Key, artifact.IsDeleted.ToString() }.Concat(artifact.Fields.Values.OrderBy(f => f.Name, StringComparer.Ordinal)
                .SelectMany(f => new[] { f.Name, f.Availability.ToString(), f.Value ?? "", f.OpaqueRichText ?? "" })));
            ContextFingerprint = RuleGuard.Hash(new[] { run.ProjectId, profile.Fingerprint, run.SourceDescription, run.SourceComplete.ToString(), run.Mode.ToString(),
                ProcessingGuard.Number(run.MaxFieldCharacters), ProcessingGuard.Number(run.MaxEvidencePerResult), ProcessingGuard.Number(run.MaxExcerptCharacters) }.Concat(traces.OrderBy(t => t.Fingerprint, StringComparer.Ordinal).Select(t => t.Fingerprint)));
        }
        public QualityArtifact Artifact { get; }
        public RuleProfile Profile { get; }
        public RuleRunContext Run { get; }
        public IReadOnlyList<RuleTraceabilityEvidence> Traceability { get; }
        public string InputFingerprint { get; }
        public string ContextFingerprint { get; }
        public QualityField Field(string name) => Artifact.Fields.TryGetValue(name, out var field) ? field : new QualityField(name, QualityAvailability.NotRequested);
        public RuleEvidence Evidence(string name, string code, int? offset = null, int? length = null)
        {
            var field = Field(name); string value = field.Value ?? ""; int start = Math.Min(offset ?? 0, value.Length);
            if (start < 0 || length < 0 || offset > value.Length || (start < value.Length && char.IsLowSurrogate(value[start]))) throw new ArgumentException("Invalid evidence offset.");
            int count = Math.Min(value.Length - start, Math.Min(length ?? value.Length, Run.MaxExcerptCharacters));
            if (count > 0 && char.IsHighSurrogate(value[start + count - 1])) count--;
            string fingerprint;
            lock (valueHashes) { if (!valueHashes.TryGetValue(name, out fingerprint)) { fingerprint = RuleGuard.Hash(new[] { value, field.OpaqueRichText ?? "" }); valueHashes.Add(name, fingerprint); } }
            return new RuleEvidence(name, field.Availability, code, offset, length, value.Substring(start, count), fingerprint);
        }
    }
    [DataContract]
    public sealed class RuleEvidence
    {
        public RuleEvidence(string field, QualityAvailability availability, string code, int? offset = null, int? length = null, string excerpt = "", string valueFingerprint = "")
        {
            Field = ProcessingGuard.Text(field, 256, empty: true); Code = ProcessingGuard.Text(code, 256); AvailabilityName = availability.ToString();
            if (!Enum.IsDefined(typeof(QualityAvailability), availability) || offset < 0 || length < 0) throw new ArgumentException();
            if (excerpt == null || excerpt.Length > 1024) throw new ArgumentException("Excerpt limit."); new UTF8Encoding(false, true).GetByteCount(excerpt);
            Offset = offset; Length = length; Excerpt = excerpt; ValueFingerprint = ProcessingGuard.Text(valueFingerprint, 128, empty: true);
        }
        [DataMember] public string Field { get; private set; }
        [DataMember] public string AvailabilityName { get; private set; }
        [DataMember] public string Code { get; private set; }
        [DataMember] public int? Offset { get; private set; }
        [DataMember] public int? Length { get; private set; }
        [DataMember] public string Excerpt { get; private set; }
        [DataMember] public string ValueFingerprint { get; private set; }
        public QualityAvailability Availability => (QualityAvailability)Enum.Parse(typeof(QualityAvailability), AvailabilityName);
    }
    [DataContract]
    public sealed class RuleReviewDecision
    {
        internal RuleReviewDecision(ReviewerDisposition disposition, string reviewer, string reason, string reference, DateTime at, string revisionHash)
        { DispositionName = disposition.ToString(); Reviewer = reviewer; Reason = reason; Reference = reference; AtUtc = at.ToString("O"); RevisionHash = revisionHash; }
        [DataMember] public string DispositionName { get; private set; }
        [DataMember] public string Reviewer { get; private set; }
        [DataMember] public string Reason { get; private set; }
        [DataMember] public string Reference { get; private set; }
        [DataMember] public string AtUtc { get; private set; }
        [DataMember] public string RevisionHash { get; private set; }
        public ReviewerDisposition Disposition => (ReviewerDisposition)Enum.Parse(typeof(ReviewerDisposition), DispositionName);
    }
    [DataContract]
    public sealed class RuleResult
    {
        [DataMember(Name="Evidence")] private RuleEvidence[] evidence;
        private RuleResult() { }
        public static RuleResult Create(RuleContext context, Rule rule, RuleState state, string code, string explanation, string suggestion,
            IEnumerable<RuleEvidence> evidence = null, bool evidenceComplete = true, bool evidenceTruncated = false)
        {
            if (context == null || rule == null || !Enum.IsDefined(typeof(RuleState), state)) throw new ArgumentException();
            var values = (evidence ?? Enumerable.Empty<RuleEvidence>()).Take(context.Run.MaxEvidencePerResult + 1).ToArray();
            if (values.Length > context.Run.MaxEvidencePerResult || values.Any(e => e == null)) throw new ArgumentException("Evidence limit.");
            var result = new RuleResult { ArtifactKey = context.Artifact.Identity.Key, Artifact = context.Artifact.Identity.Display, ProjectId = context.Run.ProjectId,
                ProfileId = context.Profile.Id, ProfileFingerprint = context.Profile.Fingerprint, RuleId = rule.Id, RuleRevision = rule.Revision, RuleFingerprint = rule.ConfigurationFingerprint,
                SeverityName = rule.Severity.ToString(), StateName = state.ToString(), Code = ProcessingGuard.Text(code, 256), Explanation = ProcessingGuard.Text(explanation, 4096, true),
                Suggestion = ProcessingGuard.Text(suggestion, 4096, true), InputFingerprint = context.InputFingerprint, ContextFingerprint = context.ContextFingerprint,
                StandardContext = context.Profile.Standard.ToString(), evidence = values, EvidenceComplete = evidenceComplete, EvidenceTruncated = evidenceTruncated,
                EvaluationMode = context.Run.Mode.ToString(), ProfileApprovalValid = context.Profile.IsApprovedFor(context.Run.ProjectId, context.Run.UtcNow), EvaluatedAtUtc = context.Run.UtcNow.ToString("O"), ReviewerDispositionName = ReviewerDisposition.Pending.ToString() };
            result.ResultFingerprint = result.ComputeFingerprint();
            return result;
        }
        private string ComputeFingerprint() => RuleGuard.Hash(new[] { "RuleResult-v1", ArtifactKey, Artifact, ProjectId, ProfileId, ProfileFingerprint, RuleId, RuleRevision, RuleFingerprint, SeverityName,
            InputFingerprint, ContextFingerprint, StandardContext, EvaluationMode, EvaluatedAtUtc, StateName, Code, Explanation, Suggestion, EvidenceComplete.ToString(), EvidenceTruncated.ToString(), ProfileApprovalValid.ToString(), ComplianceAssessed.ToString(), Disclosure }
            .Concat(Evidence.SelectMany(e => new[] { e.Field, e.AvailabilityName, e.Code, e.Offset?.ToString(CultureInfo.InvariantCulture) ?? "", e.Length?.ToString(CultureInfo.InvariantCulture) ?? "", e.Excerpt, e.ValueFingerprint })));
        public bool VerifyIntegrity() { try { return !ComplianceAssessed && Disclosure == ProcessingDisclosure.Statement && ResultFingerprint != null && ResultFingerprint == ComputeFingerprint(); } catch { return false; } }
        internal RuleResult WithDecision(RuleReviewDecision decision)
        { var clone = (RuleResult)MemberwiseClone(); clone.ReviewerDecision = decision; clone.ReviewerDispositionName = decision.DispositionName; return clone; }
        [DataMember] public string ArtifactKey { get; private set; }
        [DataMember] public string Artifact { get; private set; }
        [DataMember] public string ProjectId { get; private set; }
        [DataMember] public string ProfileId { get; private set; }
        [DataMember] public string ProfileFingerprint { get; private set; }
        [DataMember] public string RuleId { get; private set; }
        [DataMember] public string RuleRevision { get; private set; }
        [DataMember] public string RuleFingerprint { get; private set; }
        [DataMember] public string SeverityName { get; private set; }
        [DataMember] public string StateName { get; private set; }
        [DataMember] public string Code { get; private set; }
        [DataMember] public string Explanation { get; private set; }
        [DataMember] public string Suggestion { get; private set; }
        public IReadOnlyList<RuleEvidence> Evidence => Array.AsReadOnly(evidence ?? new RuleEvidence[0]);
        [DataMember] public bool EvidenceComplete { get; private set; }
        [DataMember] public bool EvidenceTruncated { get; private set; }
        [DataMember] public string InputFingerprint { get; private set; }
        [DataMember] public string ContextFingerprint { get; private set; }
        [DataMember] public string ResultFingerprint { get; private set; }
        [DataMember] public string StandardContext { get; private set; }
        [DataMember] public string EvaluationMode { get; private set; }
        [DataMember] public bool ProfileApprovalValid { get; private set; }
        [DataMember] public string EvaluatedAtUtc { get; private set; }
        [DataMember] public RuleReviewDecision ReviewerDecision { get; private set; }
        [DataMember(Name="ReviewerDisposition")] public string ReviewerDispositionName { get; private set; }
        [DataMember] public bool ComplianceAssessed { get; private set; }
        [DataMember] public string Disclosure { get; private set; } = ProcessingDisclosure.Statement;
        public RuleState State => (RuleState)Enum.Parse(typeof(RuleState), StateName);
        public RuleSeverity Severity => (RuleSeverity)Enum.Parse(typeof(RuleSeverity), SeverityName);
        public ReviewerDisposition ReviewerDisposition => ReviewerDecision?.Disposition ?? ReviewerDisposition.Pending;
    }
    internal static class RuleGuard
    {
        internal static string Path(string path)
        { ProcessingGuard.Text(path, 4096); if (path[0] != '/' || path == "/" || path.EndsWith("/", StringComparison.Ordinal) || path.Contains("//") || path.IndexOfAny(new[] { '*', '?' }) >= 0 || path.Split('/').Any(p => p == "." || p == "..")) throw new ArgumentException("Exact link-module path required."); return path; }
        internal static IReadOnlyList<string> Strings(IEnumerable<string> source, int max, int chars)
        { var values = (source ?? Enumerable.Empty<string>()).Take(max + 1).ToArray(); if (values.Length > max || values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Invalid rule collection."); foreach (var v in values) ProcessingGuard.Text(v, chars); return Array.AsReadOnly(values); }
        internal static string Hash(IEnumerable<string> values)
        {
            using (var sha = SHA256.Create()) { using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write)) { using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true)) { foreach (var value in values) writer.Write(value ?? ""); writer.Flush(); } stream.FlushFinalBlock(); }
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant(); }
        }
    }
}
