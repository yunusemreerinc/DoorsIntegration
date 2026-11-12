using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public sealed class QualityEngine
    {
        public QualitySummary Evaluate(IEnumerable<QualityArtifact> source, QualityProfile profile, QualityContext context,
            IQualityReportSink sink, CancellationToken token = default(CancellationToken), IProgress<BatchProgress> progress = null)
        {
            if (source == null || profile == null || context == null || sink == null) throw new ArgumentNullException();
            Guid id = Guid.NewGuid(); long artifacts = 0, evaluations = 0, findings = 0, notEvaluated = 0, errors = 0;
            bool cancelled = false, truncated = false, sourceFailed = false;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            using (var input = source.GetEnumerator())
            {
                while (true)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    QualityArtifact artifact;
                    try { if (!input.MoveNext()) break; artifact = input.Current; if (artifact == null) throw new ArgumentException(); }
                    catch { sourceFailed = true; break; }
                    if (artifacts >= context.MaxArtifacts) { truncated = true; break; }
                    if (!identities.Add(artifact.Identity.Key) || artifact.Identity.VersionKind == ArtifactVersion.Unknown) sourceFailed = true;
                    artifacts++; string hash = artifact.Fingerprint;
                    foreach (var rule in profile.Rules)
                    {
                        if (token.IsCancellationRequested) { cancelled = true; break; }
                        QualityRuleOutcome outcome;
                        try { outcome = rule.Evaluate(artifact, context, token) ?? new QualityRuleOutcome(QualityState.RuleError, "MissingRuleOutcome"); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; break; }
                        catch { outcome = new QualityRuleOutcome(QualityState.RuleError, "RuleExecutionFailed"); }
                        if (outcome.State == QualityState.Finding) findings++;
                        if (outcome.State == QualityState.NotEvaluated) notEvaluated++;
                        if (outcome.State == QualityState.RuleError) errors++;
                        sink.Write(new QualityObservation(artifact, rule.Descriptor, outcome, hash)); evaluations++;
                    }
                    if (cancelled) break;
                    ProcessingGuard.Progress(progress, new BatchProgress(id, "Quality", artifacts, evaluations));
                }
            }
            var summary = new QualitySummary(id, context, artifacts, evaluations, findings, notEvaluated, errors, cancelled, truncated, sourceFailed, profile.Rules.Count, profile.Fingerprint);
            sink.Complete(summary); return summary;
        }
    }
    public sealed class FileQualityReport : IQualityReportSink, IDisposable
    {
        private StreamWriter output;
        private readonly long maxBytes;
        private long bytes;
        public FileQualityReport(string newDirectory, long maxBytes = 268435456)
        {
            if (maxBytes < 65536 || maxBytes > 1073741824) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            this.maxBytes = maxBytes;
            DirectoryPath = Path.GetFullPath(newDirectory ?? throw new ArgumentNullException(nameof(newDirectory)));
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("Analysis output directory already exists.");
            Directory.CreateDirectory(DirectoryPath); output = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "observations.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false, true)) { NewLine = "\n" };
        }
        public string DirectoryPath { get; }
        public void Write(QualityObservation observation)
        {
            if (output == null) throw new ObjectDisposedException(nameof(FileQualityReport)); string line = ProcessingJson.Encode(observation);
            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > maxBytes) throw new ProcessingException("ReportLimit", "Quality report exceeded its disk limit; no completed summary was published.");
            output.WriteLine(line);
        }
        public void Complete(QualitySummary summary)
        {
            if (output == null) throw new ObjectDisposedException(nameof(FileQualityReport)); Dispose();
            File.WriteAllText(Path.Combine(DirectoryPath, "summary.json"), ProcessingJson.Encode(summary), new UTF8Encoding(false, true));
        }
        public void Dispose() { output?.Dispose(); output = null; }
    }
}
