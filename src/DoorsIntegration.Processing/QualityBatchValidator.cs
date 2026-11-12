using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Processing
{
    // Optional import gate. No repository access; update payloads contain only the supplied fields.
    public sealed class QualityBatchValidator : IBatchChangeValidator
    {
        private readonly QualityProfile profile; private readonly IProcessingClock clock; private readonly bool blockNotEvaluated;
        public QualityBatchValidator(QualityProfile profile, IProcessingClock clock, bool blockNotEvaluated = true)
        { this.profile = profile ?? throw new ArgumentNullException(nameof(profile)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.blockNotEvaluated = blockNotEvaluated; }
        public string ConfigurationFingerprint => ProcessingGuard.Hash(profile.Fingerprint, blockNotEvaluated.ToString());
        public IEnumerable<string> Validate(BatchChange change, CancellationToken cancellationToken)
        {
            var sink = new GateSink(blockNotEvaluated);
            var artifact = new QualityArtifact(new ArtifactIdentity("batch-input", change.Target, change.Key), change.Attributes.Select(a =>
                new QualityField(a.Name, QualityAvailability.Available, a.ExpectedPlainText ?? a.Value, a.Format == BatchValueFormat.OpaqueRichText ? a.Value : null)));
            new QualityEngine().Evaluate(new[] { artifact }, profile, new QualityContext("Captured input fields only", false, clock.UtcNow, 1), sink, cancellationToken);
            if (profile.Rules.Count == 0) sink.Errors.Add("QualityGate:NoRulesConfigured");
            return sink.Errors;
        }
        private sealed class GateSink : IQualityReportSink
        {
            private readonly bool blockIncomplete;
            public GateSink(bool blockIncomplete) { this.blockIncomplete = blockIncomplete; }
            public List<string> Errors { get; } = new List<string>();
            public void Write(QualityObservation observation)
            {
                if (observation.State == QualityState.RuleError.ToString() ||
                    (blockIncomplete && observation.State == QualityState.NotEvaluated.ToString()) ||
                    (observation.State == QualityState.Finding.ToString() && observation.Severity == QualitySeverity.Error.ToString()))
                    Errors.Add(observation.RuleId + ":" + observation.Code);
            }
            public void Complete(QualitySummary summary) { if (summary.Cancelled || summary.SourceFailed || summary.Truncated) Errors.Add("QualityGate:IncompleteRun"); }
        }
    }
}
