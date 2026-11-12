using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public interface IRuleResultSink
    { void Write(RuleResult result); void Complete(RuleRunSummary summary); }
    [DataContract]
    public sealed class RuleRunSummary
    {
        internal RuleRunSummary(Guid id, RuleProfile profile, RuleRunContext context, long artifacts, long evaluations, long findings, long unknown, long errors, long skipped, long incomplete, bool cancelled, bool truncated, bool failed)
        {
            RunId = id.ToString("N"); ProfileId = profile.Id; ProfileFingerprint = profile.Fingerprint; ProjectId = context.ProjectId; Standard = profile.Standard.ToString();
            Mode = context.Mode.ToString(); ProfileApprovalValid = profile.IsApprovedFor(context.ProjectId, context.UtcNow); ExampleOnly = profile.ExampleOnly;
            SourceDescription = context.SourceDescription; SourceComplete = context.SourceComplete; TimestampUtc = context.UtcNow.ToString("O");
            Artifacts = artifacts; Evaluations = evaluations; Findings = findings; NotEvaluated = unknown; RuleErrors = errors; Skipped = skipped;
            Cancelled = cancelled; Truncated = truncated; SourceFailed = failed; EnabledRules = profile.Rules.Count(r => r.Enabled); IncompleteEvidence = incomplete;
            FullyEvaluated = ProfileApprovalValid && context.Mode == RuleExecutionMode.ApprovedProfileOnly && context.SourceComplete && artifacts > 0 && EnabledRules > 0 &&
                !cancelled && !truncated && !failed && unknown == 0 && errors == 0 && incomplete == 0;
            Disclosure = ProcessingDisclosure.Statement; CertificationDecision = "NotAssessed";
        }
        [DataMember] public string RunId { get; private set; }
        [DataMember] public string ProfileId { get; private set; }
        [DataMember] public string ProfileFingerprint { get; private set; }
        [DataMember] public string ProjectId { get; private set; }
        [DataMember] public string Standard { get; private set; }
        [DataMember] public string Mode { get; private set; }
        [DataMember] public bool ProfileApprovalValid { get; private set; }
        [DataMember] public bool ExampleOnly { get; private set; }
        [DataMember] public string SourceDescription { get; private set; }
        [DataMember] public bool SourceComplete { get; private set; }
        [DataMember] public string TimestampUtc { get; private set; }
        [DataMember] public long Artifacts { get; private set; }
        [DataMember] public long Evaluations { get; private set; }
        [DataMember] public long Findings { get; private set; }
        [DataMember] public long NotEvaluated { get; private set; }
        [DataMember] public long RuleErrors { get; private set; }
        [DataMember] public long Skipped { get; private set; }
        [DataMember] public long IncompleteEvidence { get; private set; }
        [DataMember] public int EnabledRules { get; private set; }
        [DataMember] public bool Cancelled { get; private set; }
        [DataMember] public bool Truncated { get; private set; }
        [DataMember] public bool SourceFailed { get; private set; }
        [DataMember] public bool FullyEvaluated { get; private set; }
        [DataMember] public bool ComplianceAssessed { get; private set; }
        [DataMember] public bool ToolQualified { get; private set; }
        [DataMember] public string CertificationDecision { get; private set; }
        [DataMember] public string Disclosure { get; private set; }
    }
    public sealed class RequirementsQualityRuleEngine
    {
        public RuleRunSummary Evaluate(IEnumerable<QualityArtifact> source, RuleProfile profile, RuleRunContext run, IRuleResultSink sink,
            CancellationToken token = default(CancellationToken), IProgress<BatchProgress> progress = null)
        {
            if (source == null || profile == null || run == null || sink == null) throw new ArgumentNullException();
            Guid id = Guid.NewGuid(); long artifacts = 0, evaluations = 0, findings = 0, unknown = 0, errors = 0, skipped = 0, incomplete = 0;
            bool cancelled = false, truncated = false, failed = false; var identities = new HashSet<string>(StringComparer.Ordinal);
            using (var input = source.GetEnumerator())
            {
                while (true)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    QualityArtifact artifact;
                    try { if (!input.MoveNext()) break; artifact = input.Current ?? throw new ArgumentException(); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; break; }
                    catch { failed = true; break; }
                    if (artifacts >= run.MaxArtifacts) { truncated = true; break; }
                    artifacts++; bool duplicate = !identities.Add(artifact.Identity.Key); failed |= duplicate;
                    RuleContext context; bool traceFailed = false;
                    try { context = new RuleContext(artifact, profile, run, profile.Rules.Any(r => r.Enabled && r.RequiresTraceability) ? run.Traceability?.Read(artifact.Identity, token) : null); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; break; }
                    catch { traceFailed = failed = true; context = new RuleContext(artifact, profile, run); }
                    foreach (var rule in profile.Rules)
                    {
                        if (token.IsCancellationRequested) { cancelled = true; break; }
                        RuleResult result;
                        try
                        {
                            string gate = !rule.Enabled ? "RuleDisabled" : artifact.IsDeleted ? "DeletedArtifact" : duplicate ? "DuplicateArtifactIdentity" :
                                artifact.Identity.VersionKind == ArtifactVersion.Unknown ? "UnknownArtifactVersion" :
                                profile.ProjectId.Length > 0 && profile.ProjectId != run.ProjectId ? "ProfileProjectMismatch" :
                                run.Mode == RuleExecutionMode.ApprovedProfileOnly && !profile.IsApprovedFor(run.ProjectId, run.UtcNow) ? "ProjectProfileApprovalRequired" :
                                traceFailed && rule is ConfiguredRule configured && configured.Kind == RuleKind.Traceability ? "TraceProviderFailed" : "";
                            if (gate.Length > 0) result = RuleResult.Create(context, rule, gate == "RuleDisabled" || gate == "DeletedArtifact" ? RuleState.Skipped : RuleState.NotEvaluated,
                                gate, "Kural çalıştırılmadı: " + gate + ".", "Profil onayını, proje/sürüm bağlamını ve kanıt kapsamını inceleyin.", evidenceComplete: false);
                            else result = rule.Evaluate(context, token);
                            if (result == null || result.ArtifactKey != artifact.Identity.Key || result.ProfileFingerprint != profile.Fingerprint || result.ContextFingerprint != context.ContextFingerprint ||
                                result.InputFingerprint != context.InputFingerprint || result.RuleFingerprint != rule.ConfigurationFingerprint || result.ReviewerDecision != null || !result.VerifyIntegrity())
                                throw new InvalidOperationException("Rule returned unbound evidence.");
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; break; }
                        catch { result = RuleResult.Create(context, rule, RuleState.RuleError, "RuleExecutionFailed", "Kural yürütülemedi; başarılı kabul edilmedi.", "Kural ve bağlamı inceleyin.", evidenceComplete: false); }
                        if (result.State == RuleState.Finding) findings++; if (result.State == RuleState.NotEvaluated) unknown++; if (result.State == RuleState.RuleError) errors++; if (result.State == RuleState.Skipped) skipped++;
                        if (result.State != RuleState.Skipped && !result.EvidenceComplete) incomplete++;
                        sink.Write(result); evaluations++;
                    }
                    if (cancelled) break; ProcessingGuard.Progress(progress, new BatchProgress(id, "RequirementRules", artifacts, evaluations));
                }
            }
            var summary = new RuleRunSummary(id, profile, run, artifacts, evaluations, findings, unknown, errors, skipped, incomplete, cancelled, truncated, failed); sink.Complete(summary); return summary;
        }
    }
    public sealed class FileRuleReport : IRuleResultSink, IDisposable
    {
        private StreamWriter output;
        private readonly long maxBytes;
        private long bytes;
        public FileRuleReport(string newDirectory, long maxBytes = 268435456)
        {
            if (maxBytes < 65536 || maxBytes > 1073741824) throw new ArgumentException("Invalid report budget.");
            this.maxBytes = maxBytes; DirectoryPath = Path.GetFullPath(newDirectory ?? throw new ArgumentNullException(nameof(newDirectory)));
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("New rule report directory required.");
            Directory.CreateDirectory(DirectoryPath); output = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "results.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false, true)) { NewLine = "\n" };
        }
        public string DirectoryPath { get; }
        public void Write(RuleResult result)
        {
            if (output == null) throw new ObjectDisposedException(nameof(FileRuleReport)); string line = ProcessingJson.Encode(result); bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > maxBytes) throw new ProcessingException("RuleReportLimit", "Rule report budget exceeded; no summary published."); output.WriteLine(line);
        }
        public void Complete(RuleRunSummary summary)
        { if (output == null) throw new ObjectDisposedException(nameof(FileRuleReport)); output.Flush(); Dispose(); using (var stream = new FileStream(Path.Combine(DirectoryPath, "summary.json"), FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true))) writer.Write(ProcessingJson.Encode(summary)); }
        public void Dispose() { output?.Dispose(); output = null; }
    }
}
