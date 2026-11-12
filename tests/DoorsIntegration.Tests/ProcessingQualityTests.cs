using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DoorsIntegration.Processing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ProcessingQualityTests
    {
        [TestMethod]
        public void ProfileFingerprintSeparatesFieldMappingsFromAllowedValues()
        {
            var a = new ConfiguredQualityRule(Descriptor(), QualityRuleKind.Enumeration, new[] { "Text", "Other" }, allowedValues: new[] { "A" });
            var b = new ConfiguredQualityRule(Descriptor(), QualityRuleKind.Enumeration, new[] { "Text" }, allowedValues: new[] { "Other", "A" });
            Assert.AreNotEqual(a.ConfigurationFingerprint, b.ConfigurationFingerprint);
        }
        [DataTestMethod]
        [DataRow("requirements-quality.example.xml")]
        [DataRow("do178c-review.example.xml")]
        [DataRow("do200b-review.example.xml")]
        [DataRow("do330-review.example.xml")]
        public void DistributedProfilesAreExplicitExamplesWithVersionedRules(string file)
        {
            var profile = QualityProfile.LoadXml(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "quality", file));
            Assert.IsTrue(profile.ExampleOnly); Assert.IsTrue(profile.Rules.Count > 0); Assert.AreEqual(64, profile.Fingerprint.Length);
            Assert.IsTrue(profile.Rules.All(r => r.Descriptor.Revision.Length > 0 && r.Descriptor.ProjectReference.StartsWith("EXAMPLE", StringComparison.Ordinal)));
        }
        [TestMethod]
        public void ProcessingAssemblyHasNoDoorsCoreComOrDxlDependency()
        {
            var references = typeof(QualityEngine).Assembly.GetReferencedAssemblies();
            Assert.IsFalse(references.Any(r => r.Name.StartsWith("DoorsIntegration.", StringComparison.Ordinal)));
        }
        [DataTestMethod]
        [DataRow(QualityAvailability.Missing)]
        [DataRow(QualityAvailability.Denied)]
        [DataRow(QualityAvailability.NotRequested)]
        [DataRow(QualityAvailability.TooLarge)]
        [DataRow(QualityAvailability.Failed)]
        public void UnavailableEvidenceNeverBecomesAPass(QualityAvailability availability)
        {
            var sink = new QualityTestSink(); var rule = Rule(QualityRuleKind.RequiredAll);
            var summary = Run(new QualityArtifact(Id(), new[] { new QualityField("Text", availability) }), rule, sink);
            Assert.AreEqual("NotEvaluated", sink.Observations.Single().State); Assert.IsFalse(summary.FullyEvaluated); Assert.IsFalse(summary.ComplianceAssessed); Assert.IsFalse(summary.ToolQualified);
        }
        [DataTestMethod]
        [DataRow(QualityRuleKind.RequiredAll, "", "RequiredValueEmpty")]
        [DataRow(QualityRuleKind.PatternAbsent, "The system should do TBD", "ConfiguredAmbiguityPatternFound")]
        [DataRow(QualityRuleKind.Enumeration, "unknown", "ValueOutsideConfiguredEnumeration")]
        [DataRow(QualityRuleKind.NumericRange, "1,2", "ValueOutsideConfiguredRange")]
        [DataRow(QualityRuleKind.UtcDate, "2026-10-18", "ExplicitUtcTimestampRequired")]
        [DataRow(QualityRuleKind.Freshness, "2020-01-01T00:00:00Z", "TimestampOutsideConfiguredFreshnessWindow")]
        public void ConfiguredChecksProduceReviewFindingsWithoutClaimingCompliance(QualityRuleKind kind, string value, string code)
        {
            var sink = new QualityTestSink(); var summary = Run(Artifact(value), Rule(kind), sink);
            Assert.AreEqual(code, sink.Observations.Single().Code); Assert.AreEqual(1L, summary.Findings);
            Assert.IsTrue(summary.FullyEvaluated); Assert.IsFalse(summary.ComplianceAssessed); StringAssert.Contains(summary.Disclosure, "Review assistance only");
        }
        [TestMethod]
        public void MissingMappingDeletedObjectsAndFieldLimitsRemainNotEvaluated()
        {
            var context = Context(maxField: 3); var sink = new QualityTestSink();
            var profile = new QualityProfile("p", "1", new[] { new ConfiguredQualityRule(Descriptor(), QualityRuleKind.RequiredAny, new string[0]) });
            var summary = new QualityEngine().Evaluate(new[] { Artifact("text") }, profile, context, sink);
            Assert.AreEqual("FieldMappingNotConfigured", sink.Observations.Single().Code); Assert.IsFalse(summary.FullyEvaluated);
            sink = new QualityTestSink(); Run(new QualityArtifact(Id(), new[] { new QualityField("Text", QualityAvailability.Available, "x") }, true), Rule(QualityRuleKind.RequiredAll), sink);
            Assert.AreEqual("DeletedArtifact", sink.Observations.Single().Code);
            sink = new QualityTestSink(); new QualityEngine().Evaluate(new[] { Artifact("long") }, new QualityProfile("p", "1", new[] { Rule(QualityRuleKind.RequiredAll) }), context, sink);
            Assert.AreEqual("FieldTooLarge", sink.Observations.Single().Code);
        }
        [TestMethod]
        public void CustomRuleFailureIsIsolatedAndNeverLogsRequirementOrExceptionText()
        {
            var sink = new QualityTestSink(); var summary = new QualityEngine().Evaluate(new[] { Artifact("PRIVATE_REQUIREMENT") },
                new QualityProfile("p", "1", new IRequirementQualityRule[] { new ThrowingRule(), Rule(QualityRuleKind.RequiredAll) }), Context(), sink);
            Assert.AreEqual(2L, summary.Evaluations); Assert.AreEqual(1L, summary.RuleErrors); Assert.IsFalse(summary.FullyEvaluated);
            Assert.IsFalse(sink.Observations.Any(o => o.Code.Contains("PRIVATE") || o.Code.Contains("SECRET")));
        }
        [TestMethod]
        public void FileSummaryAlwaysDisclosesLimitsAndDoesNotSerializeContent()
        {
            using (var c = new ProcessingTestContext())
            using (var sink = new FileQualityReport(Path.Combine(c.Root, "quality")))
            {
                new QualityEngine().Evaluate(new[] { Artifact("PRIVATE_REQUIREMENT") }, new QualityProfile("p", "1", new[] { Rule(QualityRuleKind.RequiredAll) }), Context(), sink);
                string summary = File.ReadAllText(Path.Combine(sink.DirectoryPath, "summary.json"));
                StringAssert.Contains(summary, "\"ComplianceAssessed\":false"); StringAssert.Contains(summary, "\"ToolQualified\":false");
                Assert.IsFalse(File.ReadAllText(Path.Combine(sink.DirectoryPath, "observations.jsonl")).Contains("PRIVATE_REQUIREMENT"));
            }
        }
        [TestMethod]
        public void XmlProfilesRejectDtdAndUnknownSettingsAndFingerprintActualConfiguration()
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string path = Path.Combine(c.Root, "rules.xml");
                File.WriteAllText(path, "<qualityProfile id=\"org\" revision=\"1\" exampleOnly=\"true\"><rule id=\"R\" revision=\"1\" severity=\"Error\" kind=\"RequiredAll\" standardContext=\"DO-178C\" projectReference=\"ORG-REQ-1\"><field>Text</field></rule></qualityProfile>");
                var profile = QualityProfile.LoadXml(path); Assert.AreEqual("DO-178C", profile.Rules.Single().Descriptor.StandardContext);
                File.WriteAllText(path, File.ReadAllText(path).Replace("Text</field>", "Other</field>")); Assert.AreNotEqual(profile.Fingerprint, QualityProfile.LoadXml(path).Fingerprint);
                File.WriteAllText(path, "<!DOCTYPE p [<!ENTITY x SYSTEM 'file:///invalid'>]><qualityProfile id=\"p\" revision=\"1\" exampleOnly=\"true\" />");
                Assert.ThrowsException<XmlException>(() => QualityProfile.LoadXml(path));
                File.WriteAllText(path, "<qualityProfile id=\"p\" revision=\"1\" exampleOnly=\"true\" claimsCompliance=\"true\" />");
                Assert.ThrowsException<ArgumentException>(() => QualityProfile.LoadXml(path));
            }
        }
        [TestMethod]
        public async Task OptionalQualityGateBlocksImportWithoutContactingAnyAdapter()
        {
            using (var c = new ProcessingTestContext())
            {
                var profile = new QualityProfile("gate", "1", new[] { new ConfiguredQualityRule(new QualityRuleDescriptor("required", "1", QualitySeverity.Error), QualityRuleKind.RequiredAll, new[] { "Object Text" }) });
                var service = c.NewService(new[] { new QualityBatchValidator(profile, c.Clock) });
                var plan = c.Hold(await service.PreviewAsync(new ProcessingArraySource(ProcessingTestContext.Change(value: "")), c.Options()));
                Assert.IsFalse(plan.CanApply); Assert.AreEqual(0, c.Adapter.Inspections); Assert.AreEqual(0, c.Adapter.Calls);
            }
        }
        [TestMethod]
        public void EmptyPartialCancelledAndLimitedRunsNeverLookFullyEvaluated()
        {
            var profile = new QualityProfile("p", "1", new[] { Rule(QualityRuleKind.RequiredAll) });
            Assert.IsFalse(new QualityEngine().Evaluate(new QualityArtifact[0], profile, Context(), new QualityTestSink()).FullyEvaluated);
            Assert.IsFalse(new QualityEngine().Evaluate(new[] { Artifact("x") }, profile, new QualityContext("partial", false, DateTime.UtcNow), new QualityTestSink()).FullyEvaluated);
            var limited = new QualityEngine().Evaluate(new[] { Artifact("x"), Artifact("y") }, profile, Context(maxArtifacts: 1), new QualityTestSink()); Assert.IsTrue(limited.Truncated);
            using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Assert.IsTrue(new QualityEngine().Evaluate(new[] { Artifact("x") }, profile, Context(), new QualityTestSink(), cancelled.Token).Cancelled); }
        }
        private sealed class ThrowingRule : IRequirementQualityRule
        {
            public QualityRuleDescriptor Descriptor { get; } = new QualityRuleDescriptor("custom", "1", QualitySeverity.Error);
            public string ConfigurationFingerprint => "custom-config-v1";
            public QualityRuleOutcome Evaluate(QualityArtifact artifact, QualityContext context, CancellationToken token) => throw new InvalidOperationException("SECRET_EXCEPTION");
        }
        internal static ArtifactIdentity Id(string objectId = "1", ArtifactVersion kind = ArtifactVersion.Current, string version = "") => new ArtifactIdentity("db", "/P/M", objectId, kind, version);
        private static QualityArtifact Artifact(string text) => new QualityArtifact(Id(), new[] { new QualityField("Text", QualityAvailability.Available, text) });
        private static QualityRuleDescriptor Descriptor() => new QualityRuleDescriptor("org-quality", "1", QualitySeverity.Warning, "DO-178C", "ORG-CHECKLIST");
        private static ConfiguredQualityRule Rule(QualityRuleKind kind) => new ConfiguredQualityRule(Descriptor(), kind, new[] { "Text" }, "\\b(TBD|should)\\b", new[] { "approved", "draft" }, 0m, 10m, 30);
        private static QualityContext Context(int maxField = 65536, int maxArtifacts = 100000) => new QualityContext("test evidence", true, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), maxArtifacts, maxField);
        private static QualitySummary Run(QualityArtifact artifact, IRequirementQualityRule rule, QualityTestSink sink) => new QualityEngine().Evaluate(new[] { artifact }, new QualityProfile("p", "1", new[] { rule }), Context(), sink);
    }
}
