using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class RuleTestSink : IRuleResultSink
    { internal readonly List<RuleResult> Results = new List<RuleResult>(); internal RuleRunSummary Summary; public void Write(RuleResult result) => Results.Add(result); public void Complete(RuleRunSummary summary) { Summary = summary; } }
    internal sealed class RuleTestTrace : IRuleTraceabilityProvider
    {
        internal Func<ArtifactIdentity, IEnumerable<RuleTraceabilityEvidence>> Build;
        internal int Calls;
        public IEnumerable<RuleTraceabilityEvidence> Read(ArtifactIdentity artifact, CancellationToken token) { Calls++; return Build(artifact); }
    }
    internal sealed class RuleClock : IProcessingClock
    { public DateTime UtcNow => RequirementsRuleEngineTests.Now; public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.CompletedTask; }
    [TestClass]
    public class RequirementsRuleEngineTests
    {
        internal static readonly DateTime Now = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);
        private static ArtifactIdentity Id(string number = "1", ArtifactVersion version = ArtifactVersion.Current, string label = "") => new ArtifactIdentity("repo", "/P/Requirements", number, version, label);
        private static QualityArtifact Artifact(string body = "The system shall report status.", params QualityField[] other) => new QualityArtifact(Id(), new[] { new QualityField("Object Text", QualityAvailability.Available, body) }.Concat(other));
        private static ConfiguredRule Rule(RuleKind kind, string[] fields = null, string[] terms = null, string[] evidence = null, int min = 1) =>
            new ConfiguredRule("rule", "1", kind, RuleSeverity.Warning, fields ?? new[] { "Object Text" }, terms, evidence, min, kind == RuleKind.Traceability ? "/P/Links" : "");
        private static RuleProfile Profile(Rule rule, bool approved = true)
        { var p = new RuleProfile("project-profile", "1", RuleStandard.DO178C, new[] { rule }, "project-A", "ORG-PLAN-1", false); return approved ? p.Approve(p.Fingerprint, "reviewer-A", "PROFILE-REVIEW-1", Now.AddMinutes(-1)) : p; }
        private static RuleRunContext Run(RuleExecutionMode mode = RuleExecutionMode.ApprovedProfileOnly, IRuleTraceabilityProvider trace = null, int max = 100000, int evidence = 32) =>
            new RuleRunContext("project-A", "read-only-snapshot", true, Now, mode, trace, maxArtifacts: max, maxEvidencePerResult: evidence);
        private static RuleTestSink Evaluate(Rule rule, QualityArtifact artifact = null, RuleRunContext run = null, bool approved = true)
        { var sink = new RuleTestSink(); new RequirementsQualityRuleEngine().Evaluate(new[] { artifact ?? Artifact() }, Profile(rule, approved), run ?? Run(), sink); return sink; }
        private static RuleTraceabilityEvidence Trace(ArtifactIdentity anchor, bool complete, params RuleTraceLink[] links) => new RuleTraceabilityEvidence(anchor, "/P/Links", CoverageDirection.Outgoing, complete, "link-snapshot-1", links);
        private static RuleTraceLink Link(string number = "2", RuleTraceResolution resolution = RuleTraceResolution.Confirmed, bool known = true) => new RuleTraceLink(Id(number), resolution, known);

        [DataTestMethod, DataRow(""), DataRow(" \r\n\t"), DataRow("\u200b\ufeff")]
        public void EmptyRequirementIncludesWhitespaceAndFormattingOnlyText(string text)
        { var result = Evaluate(Rule(RuleKind.EmptyRequirement), Artifact(text)).Results.Single(); Assert.AreEqual("EmptyRequirement", result.Code); Assert.AreEqual(RuleState.Finding, result.State); Assert.AreEqual(ReviewerDisposition.Pending, result.ReviewerDisposition); }
        [TestMethod]
        public void EmptyAnyCanUseHeadingWhileMissingBodyIsExplicitlyUnknown()
        {
            var r = Rule(RuleKind.EmptyRequirement, new[] { "Object Heading", "Object Text" });
            Assert.AreEqual(RuleState.Passed, Evaluate(r, Artifact("", new QualityField("Object Heading", QualityAvailability.Available, "Heading"))).Results.Single().State);
            var unknown = new QualityArtifact(Id(), new QualityField[0]); Assert.AreEqual(RuleState.NotEvaluated, Evaluate(r, unknown).Results.Single().State);
        }
        [TestMethod]
        public void AmbiguityUsesLiteralUnicodeWholeTermsWithExactOffsetsAndEvidence()
        {
            string text = "😀 breakfast FAST and as needed; hızlı a+b.";
            var r = Rule(RuleKind.AmbiguousWords, terms: new[] { "fast", "as needed", "hızlı", "a+b" }); var result = Evaluate(r, Artifact(text)).Results.Single();
            Assert.AreEqual(4, result.Evidence.Count); Assert.AreEqual(RuleState.Finding, result.State); Assert.IsTrue(result.Explanation.Length > 0 && result.Suggestion.Length > 0);
            foreach (var e in result.Evidence) Assert.AreEqual(e.Excerpt, text.Substring(e.Offset.Value, e.Length.Value)); Assert.IsFalse(result.Evidence.Any(e => e.Excerpt == "breakfast"));
        }
        [TestMethod]
        public void MultipleObligationsAreReviewCandidatesRatherThanSemanticProof()
        {
            var rule = Rule(RuleKind.MultipleObligations, terms: new[] { "shall", "shall not", "must" }, min: 2);
            Assert.AreEqual(RuleState.Passed, Evaluate(rule, Artifact("The system shall read and display the value.")).Results.Single().State);
            var found = Evaluate(rule, Artifact("The system shall not drop data and the monitor must report a fault.")).Results.Single();
            Assert.AreEqual(2, found.Evidence.Count); Assert.AreEqual("MultipleObligationCandidates", found.Code); StringAssert.Contains(found.Explanation, "anlamsal");
        }
        [TestMethod]
        public void EvidenceCapCannotTurnAnUnreachedObligationThresholdIntoPassed()
        {
            var result = Evaluate(Rule(RuleKind.MultipleObligations, terms: new[] { "shall" }, min: 5), Artifact("shall a; shall b; shall c; shall d; shall e;"), Run(evidence: 2)).Results.Single();
            Assert.AreEqual(RuleState.NotEvaluated, result.State); Assert.IsTrue(result.EvidenceTruncated);
        }
        [DataTestMethod, DataRow(QualityAvailability.Missing, RuleState.Finding), DataRow(QualityAvailability.Denied, RuleState.NotEvaluated), DataRow(QualityAvailability.NotRequested, RuleState.NotEvaluated), DataRow(QualityAvailability.Failed, RuleState.NotEvaluated)]
        public void RequiredAttributesDistinguishKnownAbsenceFromUnavailableEvidence(QualityAvailability availability, RuleState expected)
        { var result = Evaluate(Rule(RuleKind.RequiredAttributes, new[] { "Status" }), Artifact("text", new QualityField("Status", availability))).Results.Single(); Assert.AreEqual(expected, result.State); Assert.AreEqual(availability, result.Evidence.Single().Availability); }
        [TestMethod]
        public void MissingDictionaryEntryDoesNotPretendToProveAMissingDoorsAttribute()
        { Assert.AreEqual(RuleState.NotEvaluated, Evaluate(Rule(RuleKind.RequiredAttributes, new[] { "Status" })).Results.Single().State); }
        [TestMethod]
        public void KnownViolationAndDeniedOtherFieldRetainPartialEvidence()
        {
            var sink = Evaluate(Rule(RuleKind.RequiredAttributes, new[] { "Status", "Owner" }), Artifact("text", new QualityField("Status", QualityAvailability.Missing), new QualityField("Owner", QualityAvailability.Denied)));
            Assert.AreEqual(RuleState.Finding, sink.Results.Single().State); Assert.IsFalse(sink.Results.Single().EvidenceComplete); Assert.IsFalse(sink.Summary.FullyEvaluated);
        }
        [DataTestMethod, DataRow("", RuleState.Finding), DataRow("Test / accept under 20 ms", RuleState.Passed)]
        public void VerifiabilityChecksExplicitProjectEvidenceWithoutClaimingSemanticSufficiency(string value, RuleState expected)
        { var result = Evaluate(Rule(RuleKind.Verifiability, evidence: new[] { "Verification Method" }), Artifact("text", new QualityField("Verification Method", QualityAvailability.Available, value))).Results.Single(); Assert.AreEqual(expected, result.State); }
        [TestMethod]
        public void VerifiabilityCanRequireBothMethodAndAcceptanceCriteria()
        {
            var rule = Rule(RuleKind.Verifiability, evidence: new[] { "Method", "Criteria" }, min: 2);
            var result = Evaluate(rule, Artifact("text", new QualityField("Method", QualityAvailability.Available, "Test"), new QualityField("Criteria", QualityAvailability.Missing))).Results.Single();
            Assert.AreEqual(RuleState.Finding, result.State);
        }
        [DataTestMethod, DataRow(true, RuleState.Finding), DataRow(false, RuleState.NotEvaluated)]
        public void MissingTraceabilityRequiresACompleteExactScope(bool complete, RuleState expected)
        { var provider = new RuleTestTrace { Build = a => new[] { Trace(a, complete) } }; var sink = Evaluate(Rule(RuleKind.Traceability), run: Run(trace: provider)); Assert.AreEqual(expected, sink.Results.Single().State); Assert.AreEqual(1, provider.Calls); }
        [TestMethod]
        public void TraceabilityCountsDistinctResolvedCounterpartsAndPreservesUncertainty()
        {
            var provider = new RuleTestTrace { Build = a => new[] { Trace(a, true, Link(), Link()) } };
            Assert.AreEqual("TraceabilityMissing", Evaluate(Rule(RuleKind.Traceability, min: 2), run: Run(trace: provider)).Results.Single().Code);
            provider.Build = a => new[] { Trace(a, false, Link(), Link("3", RuleTraceResolution.Inaccessible)) };
            var present = Evaluate(Rule(RuleKind.Traceability), run: Run(trace: provider)); Assert.AreEqual(RuleState.Passed, present.Results.Single().State); Assert.IsFalse(present.Summary.FullyEvaluated);
        }
        [TestMethod]
        public void BaselineAndNativeLinkIdentityCannotBeGuessed()
        {
            var rule = new ConfiguredRule("trace", "1", RuleKind.Traceability, RuleSeverity.Error, linkModulePath: "/P/Links", requiredNativeLinkModuleId: "native-link-id");
            var provider = new RuleTestTrace { Build = a => new[] { Trace(a, true, Link()) } };
            Assert.AreEqual("NativeLinkIdentityUnavailable", Evaluate(rule, run: Run(trace: provider)).Results.Single().Code);
            var artifact = new QualityArtifact(Id(version: ArtifactVersion.Baseline, label: "1.0[A]"), new QualityField[0]);
            provider.Build = a => new[] { Trace(a, true, new RuleTraceLink(Id("2", ArtifactVersion.Baseline, "opaque"), RuleTraceResolution.Confirmed, false)) };
            Assert.AreEqual("TraceabilityUnknown", Evaluate(Rule(RuleKind.Traceability), artifact, Run(trace: provider)).Results.Single().Code);
        }
        [TestMethod]
        public void ForeignTraceEvidenceCannotBeAttachedToAnotherRequirement()
        { var provider = new RuleTestTrace { Build = a => new[] { Trace(Id("other"), true, Link()) } }; var sink = Evaluate(Rule(RuleKind.Traceability), run: Run(trace: provider)); Assert.IsTrue(sink.Summary.SourceFailed); Assert.AreEqual("TraceProviderFailed", sink.Results.Single().Code); }
        [TestMethod]
        public void UnapprovedAndForeignProfilesAreBlockedWhilePreviewIsExplicitlyAdvisory()
        {
            var r = Rule(RuleKind.EmptyRequirement); var blocked = Evaluate(r, approved: false); Assert.AreEqual("ProjectProfileApprovalRequired", blocked.Results.Single().Code);
            var preview = Evaluate(r, run: Run(RuleExecutionMode.DraftPreview), approved: false); Assert.AreEqual(RuleState.Passed, preview.Results.Single().State); Assert.IsFalse(preview.Summary.FullyEvaluated); Assert.IsFalse(preview.Summary.ComplianceAssessed); Assert.AreEqual("NotAssessed", preview.Summary.CertificationDecision);
            var sink = new RuleTestSink(); new RequirementsQualityRuleEngine().Evaluate(new[] { Artifact() }, Profile(r), new RuleRunContext("project-B", "snapshot", true, Now, RuleExecutionMode.DraftPreview), sink);
            Assert.AreEqual("ProfileProjectMismatch", sink.Results.Single().Code);
        }
        [DataTestMethod, DataRow(RuleStandard.DO178C), DataRow(RuleStandard.DO200B), DataRow(RuleStandard.DO330)]
        public void StandardTemplatesHaveSixReviewRulesAndNoAutomaticApproval(RuleStandard standard)
        {
            var template = BuiltinRuleProfiles.CreateTemplate(standard); Assert.AreEqual(6, template.Rules.Count); Assert.IsTrue(template.ExampleOnly); Assert.IsNull(template.Approval);
            Assert.IsFalse(template.IsApprovedFor("project-A", Now)); Assert.ThrowsException<InvalidOperationException>(() => template.Approve(template.Fingerprint, "R", "REF", Now));
            var project = template.ForProject("project-A", "PLAN", template.Rules); Assert.ThrowsException<InvalidOperationException>(() => project.Approve(project.Fingerprint, "R", "REF", Now));
        }
        [TestMethod]
        public void ProjectApprovalBindsExactConfigurationAndValidityWindow()
        {
            var profile = Profile(Rule(RuleKind.EmptyRequirement), false); Assert.ThrowsException<InvalidOperationException>(() => profile.Approve("wrong", "R", "REF", Now));
            profile = profile.Approve(profile.Fingerprint, "R", "REF", Now, Now.AddDays(1)); Assert.IsTrue(profile.IsApprovedFor("project-A", Now)); Assert.IsFalse(profile.IsApprovedFor("project-A", Now.AddDays(1))); Assert.IsFalse(profile.IsApprovedFor("project-A", Now.AddSeconds(-1)));
            Assert.IsNull(profile.ForProject("project-B", "PLAN", profile.Rules).Approval);
        }
        [TestMethod]
        public void JsonProfilesRoundTripDraftAndApprovedWithExactFingerprints()
        {
            using (var files = new ExecutorTestContext())
            {
                foreach (var profile in new[] { BuiltinRuleProfiles.CreateTemplate(RuleStandard.DO178C), Profile(Rule(RuleKind.EmptyRequirement)) })
                {
                    string path = Path.Combine(files.Root, Guid.NewGuid().ToString("N") + ".json"); profile.SaveJson(path); var loaded = RuleProfile.LoadJson(path);
                    Assert.AreEqual(profile.Fingerprint, loaded.Fingerprint); Assert.AreEqual(profile.Approval != null, loaded.Approval != null); Assert.ThrowsException<IOException>(() => loaded.SaveJson(path));
                }
            }
        }
        [DataTestMethod, DataRow("duplicate"), DataRow("unknown"), DataRow("schema"), DataRow("enum"), DataRow("type"), DataRow("approval")]
        public void JsonRejectsAmbiguityUnknownConfigurationAndStaleApproval(string mutation)
        {
            using (var files = new ExecutorTestContext())
            {
                string path = Path.Combine(files.Root, "profile.json"); Profile(Rule(RuleKind.EmptyRequirement)).SaveJson(path); string json = File.ReadAllText(path);
                switch (mutation) { case "duplicate": json = json.Insert(1, "\"id\":\"duplicate\","); break; case "unknown": json = json.Insert(1, "\"unexpected\":true,"); break;
                    case "schema": json = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"); break; case "enum": json = json.Replace("\"kind\":\"EmptyRequirement\"", "\"kind\":\"ExecuteDxl\""); break;
                    case "type": json = json.Replace("\"enabled\":true", "\"enabled\":\"true\""); break; case "approval": json = json.Replace("\"revision\":\"1\"", "\"revision\":\"2\""); break; }
                string bad = Path.Combine(files.Root, "bad.json"); File.WriteAllText(bad, json); Assert.ThrowsException<ArgumentException>(() => RuleProfile.LoadJson(bad));
            }
        }
        [TestMethod]
        public void ResultsPersistEvidenceAndDispositionWithoutRewritingAutomationState()
        {
            using (var files = new ExecutorTestContext())
            {
                var result = Evaluate(Rule(RuleKind.EmptyRequirement), Artifact("")).Results.Single(); var store = new FileRuleReviewStore(Path.Combine(files.Root, "reviews.jsonl"), new RuleClock());
                var accepted = store.Save(result, result.ResultFingerprint, ReviewerDisposition.Accepted, "reviewer", "Valid finding; change requested", "REVIEW-1");
                Assert.AreEqual(RuleState.Finding, accepted.State); Assert.AreEqual(ReviewerDisposition.Accepted, accepted.ReviewerDisposition); Assert.AreEqual(ReviewerDisposition.Pending, result.ReviewerDisposition);
                Assert.ThrowsException<ProcessingException>(() => store.Save(result, result.ResultFingerprint, ReviewerDisposition.Waived, "reviewer", "reason", "REF"));
                var waived = store.Save(result, result.ResultFingerprint, ReviewerDisposition.Waived, "reviewer-B", "Project-specific exception", "EXC-2", accepted.ReviewerDecision.RevisionHash);
                Assert.AreEqual(ReviewerDisposition.Waived, store.ApplyLatest(result).ReviewerDisposition); Assert.AreEqual(2, File.ReadAllLines(Path.Combine(files.Root, "reviews.jsonl")).Length);
                Assert.AreNotEqual(waived.ReviewerDecision.RevisionHash, accepted.ReviewerDecision.RevisionHash);
                var changed = Evaluate(Rule(RuleKind.EmptyRequirement), Artifact("changed")).Results.Single(); Assert.AreEqual(ReviewerDisposition.Pending, store.ApplyLatest(changed).ReviewerDisposition);
            }
        }
        [TestMethod]
        public void IncompleteReviewLedgerAndWrongAcknowledgementNeverOverwritePriorRecords()
        {
            using (var files = new ExecutorTestContext())
            {
                string path = Path.Combine(files.Root, "reviews.jsonl"); var store = new FileRuleReviewStore(path, new RuleClock()); var result = Evaluate(Rule(RuleKind.EmptyRequirement)).Results.Single();
                Assert.ThrowsException<ArgumentException>(() => store.Save(result, "wrong", ReviewerDisposition.Rejected, "R", "reason", "REF")); Assert.IsFalse(File.Exists(path));
                var reviewed = store.Save(result, result.ResultFingerprint, ReviewerDisposition.NeedsClarification, "R", "question", "REF"); File.AppendAllText(path, "{partial"); string before = File.ReadAllText(path);
                Assert.ThrowsException<ProcessingException>(() => store.Save(result, result.ResultFingerprint, ReviewerDisposition.Accepted, "R", "reason", "REF", reviewed.ReviewerDecision.RevisionHash)); Assert.AreEqual(before, File.ReadAllText(path));
            }
        }
        [TestMethod]
        public void StreamingReportHasEvidenceRecommendationsPendingDispositionAndNoCertificationDecision()
        {
            using (var files = new ExecutorTestContext())
            {
                string path = Path.Combine(files.Root, "report"); using (var report = new FileRuleReport(path)) new RequirementsQualityRuleEngine().Evaluate(new[] { Artifact("") }, Profile(Rule(RuleKind.EmptyRequirement)), Run(), report);
                string json = File.ReadAllText(Path.Combine(path, "results.jsonl")); StringAssert.Contains(json, "\"ReviewerDisposition\":\"Pending\""); StringAssert.Contains(json, "\"Evidence\""); StringAssert.Contains(json, "\"Suggestion\"");
                using (var input = new MemoryStream(Encoding.UTF8.GetBytes(json))) { var result = (RuleResult)new DataContractJsonSerializer(typeof(RuleResult)).ReadObject(input); Assert.AreEqual(RuleState.Finding, result.State); Assert.AreEqual(1, result.Evidence.Count); }
                StringAssert.Contains(File.ReadAllText(Path.Combine(path, "summary.json")), "NotAssessed");
            }
        }
        [TestMethod]
        public void FingerprintsBindRichTextTraceAndSourceContextAndEvidenceCaps()
        {
            var rule = Rule(RuleKind.RequiredAttributes, new[] { "Object Text" }); var first = Evaluate(rule).Results.Single();
            var changedRich = new QualityArtifact(Id(), new[] { new QualityField("Object Text", QualityAvailability.Available, "The system shall report status.", "{\\b rich}") });
            Assert.AreNotEqual(first.ResultFingerprint, Evaluate(rule, changedRich).Results.Single().ResultFingerprint);
            Assert.AreNotEqual(first.ResultFingerprint, Evaluate(rule, run: Run(evidence: 2)).Results.Single().ResultFingerprint);
        }
        [TestMethod]
        public void DisabledDeletedOversizedAndUnknownInputsAreExplicitRatherThanPassed()
        {
            var disabled = new ConfiguredRule("off", "1", RuleKind.EmptyRequirement, RuleSeverity.Warning, new[] { "Object Text" }, enabled: false);
            Assert.AreEqual(RuleState.Skipped, Evaluate(disabled, approved: false, run: Run(RuleExecutionMode.DraftPreview)).Results.Single().State);
            var deleted = new QualityArtifact(Id(), new QualityField[0], true); Assert.AreEqual(RuleState.Skipped, Evaluate(Rule(RuleKind.EmptyRequirement), deleted).Results.Single().State);
            Assert.AreEqual("FieldTooLarge", Evaluate(Rule(RuleKind.EmptyRequirement), Artifact(new string('a', 65537))).Results.Single().Code);
            Assert.AreEqual("UnknownArtifactVersion", Evaluate(Rule(RuleKind.EmptyRequirement), new QualityArtifact(Id(version: ArtifactVersion.Unknown), new QualityField[0])).Results.Single().Code);
        }
        [TestMethod]
        public void LimitsCancellationAndDuplicateIdentityAreReflectedInSummary()
        {
            var profile = Profile(Rule(RuleKind.EmptyRequirement)); var sink = new RuleTestSink();
            new RequirementsQualityRuleEngine().Evaluate(Enumerable.Range(1, 10).Select(i => new QualityArtifact(Id(i.ToString(CultureInfo.InvariantCulture)), new[] { new QualityField("Object Text", QualityAvailability.Available, "body") })), profile, Run(max: 3), sink);
            Assert.AreEqual(3L, sink.Summary.Artifacts); Assert.IsTrue(sink.Summary.Truncated);
            sink = new RuleTestSink(); new RequirementsQualityRuleEngine().Evaluate(new[] { Artifact(), Artifact() }, profile, Run(), sink); Assert.IsTrue(sink.Summary.SourceFailed); Assert.AreEqual("DuplicateArtifactIdentity", sink.Results.Last().Code);
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); sink = new RuleTestSink(); new RequirementsQualityRuleEngine().Evaluate(new[] { Artifact() }, profile, Run(), sink, cancel.Token); Assert.IsTrue(sink.Summary.Cancelled); Assert.AreEqual(0, sink.Results.Count); }
        }
        [TestMethod]
        public void CustomRuleFailureIsIsolatedAndUnboundResultsCannotBecomePassed()
        {
            var sink = Evaluate(new BrokenRule()); Assert.AreEqual(RuleState.RuleError, sink.Results.Single().State); Assert.IsFalse(sink.Summary.FullyEvaluated);
        }
        private sealed class BrokenRule : Rule
        {
            internal BrokenRule() : base("broken", "1", RuleSeverity.Error) { }
            public override string ConfigurationFingerprint => "trusted-custom-rule-v1";
            public override RuleResult Evaluate(RuleContext context, CancellationToken token = default(CancellationToken)) => throw new InvalidOperationException("Private plugin failure");
        }
        [TestMethod]
        public void TamperedSerializedResultCannotReuseAReviewerDisposition()
        {
            using (var files = new ExecutorTestContext())
            {
                var original = Evaluate(Rule(RuleKind.EmptyRequirement), Artifact("")).Results.Single(); string json;
                using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(RuleResult)).WriteObject(stream, original); json = Encoding.UTF8.GetString(stream.ToArray()); }
                json = json.Replace("EmptyRequirement", "AlteredEvidence");
                RuleResult changed; using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) changed = (RuleResult)new DataContractJsonSerializer(typeof(RuleResult)).ReadObject(stream);
                Assert.IsFalse(changed.VerifyIntegrity()); var store = new FileRuleReviewStore(Path.Combine(files.Root, "ledger.jsonl"), new RuleClock());
                Assert.ThrowsException<ArgumentException>(() => store.ApplyLatest(changed));
            }
        }
        [TestMethod]
        public void BundledJsonTemplatesMatchTheirBuiltinDefinitions()
        {
            foreach (var standard in new[] { RuleStandard.General, RuleStandard.DO178C, RuleStandard.DO200B, RuleStandard.DO330 })
            { string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "quality", standard.ToString().ToLowerInvariant() + "-rules.example.json"); Assert.AreEqual(BuiltinRuleProfiles.CreateTemplate(standard).Fingerprint, RuleProfile.LoadJson(file).Fingerprint); }
        }
        [TestMethod]
        public void SinkFailureDoesNotPublishACompletedSummaryOrLeakSourceEnumeration()
        {
            bool disposed = false;
            IEnumerable<QualityArtifact> Source() { try { yield return Artifact(); } finally { disposed = true; } }
            var sink = new BrokenSink(); Assert.ThrowsException<IOException>(() => new RequirementsQualityRuleEngine().Evaluate(Source(), Profile(Rule(RuleKind.EmptyRequirement)), Run(), sink)); Assert.IsTrue(disposed); Assert.IsFalse(sink.Completed);
        }
        private sealed class BrokenSink : IRuleResultSink { internal bool Completed; public void Write(RuleResult result) => throw new IOException("Failed result sink"); public void Complete(RuleRunSummary summary) { Completed = true; } }
        [TestMethod]
        public async Task SnapshotTraceProviderReadsRetainedLinksOnlyAndBindsExactIdentity()
        {
            var data = new GraphTestData(); data.Edge(1, 1); var req = new LinkReadRequest(GraphTestData.R, GraphTestData.L);
            using (var snapshot = await ((ILinkService)data).ReadAsync(req))
            {
                var provider = new SnapshotRuleTraceabilityProvider(snapshot, "repo"); var anchor = new ArtifactIdentity("repo", GraphTestData.R, "1");
                var evidence = provider.Read(anchor, CancellationToken.None).Single(e => e.Direction == CoverageDirection.Outgoing); Assert.AreEqual(1, evidence.Links.Count); Assert.AreEqual(1, data.LinkCalls);
                Assert.AreEqual(0, provider.Read(new ArtifactIdentity("other", GraphTestData.R, "1"), CancellationToken.None).Count());
            }
        }
    }
}
