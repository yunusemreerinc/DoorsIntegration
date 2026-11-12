using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DoorsIntegration.Processing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ProcessingCoverageTests
    {
        [TestMethod]
        public void CompleteScopeDistinguishesCoveredAndUncoveredAndDeduplicatesPhysicalObservations()
        {
            var a = Id("1"); var b = Id("2"); var target = Id("42", "/P/Tests"); var relation = new TraceRelation(a, target, "verifies", true); var sink = new CoverageTestSink();
            var summary = new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(a), new CoverageNode(b) }, new[] { relation, relation },
                new[] { Policy() }, Context(), sink);
            Assert.AreEqual(1L, summary.Covered); Assert.AreEqual(1L, summary.Uncovered); Assert.AreEqual(0L, summary.Unknown);
            Assert.AreEqual(1L, summary.DuplicateObservations); Assert.AreEqual(50m, summary.CoveragePercent); Assert.IsFalse(summary.ComplianceAssessed);
        }
        [TestMethod]
        public void PartialScopesNeverInventUncoveredCountsOrACompletePercentage()
        {
            var a = Id("1"); var b = Id("2"); var target = Id("42", "/P/Tests"); var sink = new CoverageTestSink();
            var summary = new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(a), new CoverageNode(b) }, new[] { new TraceRelation(a, target, "verifies", true) },
                new[] { Policy() }, Context(false), sink);
            Assert.AreEqual(1L, summary.Covered); Assert.AreEqual(1L, summary.Unknown); Assert.AreEqual(0L, summary.Uncovered); Assert.IsNull(summary.CoveragePercent);
        }
        [DataTestMethod]
        [DataRow("unresolved")]
        [DataRow("echoed")]
        [DataRow("deleted")]
        public void UnusableEdgesDoNotCountAsCoverage(string mode)
        {
            var a = Id("1"); var target = Id("42", "/P/Tests"); var sink = new CoverageTestSink();
            var edge = new TraceRelation(a, mode == "unresolved" ? null : target, "verifies", mode != "unresolved", mode == "echoed", mode == "deleted");
            var summary = new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(a) }, new[] { edge }, new[] { Policy() }, Context(), sink);
            Assert.AreEqual(0L, summary.Covered); Assert.AreEqual(1L, summary.Unknown); Assert.IsNull(summary.CoveragePercent);
        }
        [TestMethod]
        public void VersionIdentityDirectionAndContainerPolicyAreExact()
        {
            var current = Id("1"); var baseline = Id("1", "/P/M", ArtifactVersion.Baseline, "1.0"); var tests = Id("42", "/P/Tests");
            var sink = new CoverageTestSink();
            new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(current), new CoverageNode(baseline) }, new[] { new TraceRelation(tests, current, "verifies", true) },
                new[] { new CoveragePolicy("incoming", "verifies", CoverageDirection.Incoming, targetContainers: new[] { "/P/Tests" }) }, Context(), sink);
            Assert.AreEqual("Covered", sink.Observations.First().State); Assert.AreEqual("Uncovered", sink.Observations.Last().State);
            sink = new CoverageTestSink();
            new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(current) }, new[] { new TraceRelation(current, tests, "other-module", true) }, new[] { Policy() }, Context(), sink);
            Assert.AreEqual("Uncovered", sink.Observations.Single().State);
        }
        [TestMethod]
        public void MinimumCountsDoNotCountDuplicateLinksAndDeletedNodesAreExcluded()
        {
            var a = Id("1"); var target = Id("42", "/P/Tests"); var edge = new TraceRelation(a, target, "verifies", true); var sink = new CoverageTestSink();
            var summary = new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(a), new CoverageNode(Id("2"), true) }, new[] { edge, edge },
                new[] { new CoveragePolicy("two-tests", "verifies", CoverageDirection.Outgoing, 2) }, Context(), sink);
            Assert.AreEqual(0L, summary.Covered); Assert.AreEqual(1L, summary.Uncovered); Assert.AreEqual(1L, summary.Excluded);
            Assert.AreEqual(1, sink.Observations.First().ObservedLinks);
        }
        [TestMethod]
        public void BoundsCancellationEmptyPoliciesAndSourceFailuresCannotProduce100Percent()
        {
            var a = Id("1"); var nodes = new[] { new CoverageNode(a), new CoverageNode(Id("2")) }; var service = new TraceabilityCoverageService();
            var limited = service.Analyze(nodes, new TraceRelation[0], new[] { Policy() }, new CoverageContext("db", true, "bounded", maxNodes: 1), new CoverageTestSink());
            Assert.IsTrue(limited.Truncated); Assert.IsNull(limited.CoveragePercent); Assert.AreEqual(1L, limited.Unknown);
            Assert.IsNull(service.Analyze(new CoverageNode[0], new TraceRelation[0], new[] { Policy() }, Context(), new CoverageTestSink()).CoveragePercent);
            Assert.IsFalse(service.Analyze(nodes, new TraceRelation[0], new CoveragePolicy[0], Context(), new CoverageTestSink()).ScopeComplete);
            var failed = service.Analyze(BrokenNodes(), new TraceRelation[0], new[] { Policy() }, Context(), new CoverageTestSink()); Assert.IsTrue(failed.SourceFailed); Assert.IsNull(failed.CoveragePercent);
            using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); Assert.IsTrue(service.Analyze(nodes, new TraceRelation[0], new[] { Policy() }, Context(), new CoverageTestSink(), cancellation.Token).Cancelled); }
        }
        [TestMethod]
        public void FileReportPersistsExplicitScopeAndDisclosure()
        {
            using (var c = new ProcessingTestContext())
            using (var sink = new FileCoverageReport(Path.Combine(c.Root, "coverage")))
            {
                new TraceabilityCoverageService().Analyze(new[] { new CoverageNode(Id("1")) }, new TraceRelation[0], new[] { Policy() }, Context(false), sink);
                string summary = File.ReadAllText(Path.Combine(sink.DirectoryPath, "summary.json"));
                StringAssert.Contains(summary, "\"CoveragePercent\":null"); StringAssert.Contains(summary, "\"ComplianceAssessed\":false");
                StringAssert.Contains(File.ReadAllText(Path.Combine(sink.DirectoryPath, "coverage.jsonl")), "Unknown");
            }
        }
        private static IEnumerable<CoverageNode> BrokenNodes() { yield return new CoverageNode(Id("1")); throw new IOException("PRIVATE"); }
        private static ArtifactIdentity Id(string value, string container = "/P/M", ArtifactVersion kind = ArtifactVersion.Current, string version = "") => new ArtifactIdentity("db", container, value, kind, version);
        private static CoveragePolicy Policy() => new CoveragePolicy("tests", "verifies", CoverageDirection.Outgoing, targetContainers: new[] { "/P/Tests" });
        private static CoverageContext Context(bool complete = true) => new CoverageContext("db", complete, "test snapshot");
    }
}
