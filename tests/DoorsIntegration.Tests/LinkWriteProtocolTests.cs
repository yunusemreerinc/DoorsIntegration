using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class LinkWriteProtocolTests
    {
        [DataTestMethod]
        [DataRow("request-id")]
        [DataRow("request-path")]
        [DataRow("action")]
        [DataRow("terminator")]
        [DataRow("numeric-enum")]
        [DataRow("bad-escape")]
        [DataRow("unknown-row")]
        [DataRow("unknown-version")]
        [DataRow("missing-identity")]
        [DataRow("preview-saved")]
        [DataRow("preview-after")]
        [DataRow("raw-cr")]
        public async Task MalformedPreviewIsRejectedWithoutLeakingResponseOrAuthorizingWrite(string fault)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Alter = text =>
                {
                    switch (fault)
                    {
                        case "request-id": return text.Replace("DOORS_LINK_WRITE|1|", "DOORS_LINK_WRITE|1|PRIVATE_RESPONSE");
                        case "request-path": return text.Replace("/P/Source", "/Wrong");
                        case "action": return text.Replace("\tCreate\t", "\tRemove\t");
                        case "terminator": return text.Replace("END\n", "PRIVATE_RESPONSE");
                        case "numeric-enum": return text.Replace("\tPreview\t", "\t0\t");
                        case "bad-escape": return text.Replace("\tSRC-1\t", "\t\\xPRIVATE_RESPONSE\t");
                        case "unknown-row": return text.Replace("END\n", "PRIVATE_RESPONSE\nEND\n");
                        case "unknown-version": return text.Replace("\tBaseline\t", "\tUnknown\t");
                        case "missing-identity": return text.Replace("\tSRC-1\t", "\t\t");
                        case "preview-saved": return text.Replace("\tNotAttempted\t", "\tSaved\t");
                        case "preview-after": return text.Replace("B\t/P/Target", "A\t/P/Target");
                        case "raw-cr": return text.Replace("\tSRC-1\t", "\tSRC\r-1\t");
                        default: throw new InvalidOperationException();
                    }
                };
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.PreviewWriteAsync(c.Request));
                Assert.AreEqual(IntegrationErrorCode.LinkWriteProtocolFailure, ex.Code);
                Assert.IsFalse(ex.Message.Contains("PRIVATE_RESPONSE")); Assert.IsFalse(c.Modes.Contains("commit"));
            }
        }
        [DataTestMethod]
        [DataRow("wrong-plan")]
        [DataRow("wrong-db")]
        [DataRow("wrong-actor")]
        [DataRow("wrong-source")]
        [DataRow("wrong-state")]
        [DataRow("not-saved")]
        [DataRow("not-closed")]
        [DataRow("not-verified")]
        [DataRow("extra-edge")]
        [DataRow("missing-added-edge")]
        [DataRow("baseline-deleted")]
        [DataRow("duplicate-after")]
        [DataRow("before-changed")]
        [DataRow("issue-on-success")]
        public async Task FalseSuccessOrUnrelatedTraceDeltaIsUncertainAndNeverRetried(string fault)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Confirmation.Approved = true; var preview = await c.Service.PreviewWriteAsync(c.Request);
                c.Alter = text =>
                {
                    switch (fault)
                    {
                        case "wrong-plan": return text.Replace(preview.PlanId.ToString("N"), Guid.NewGuid().ToString("N"));
                        case "wrong-db": return text.Replace("\t" + c.Database + "\t", "\twrong-db\t");
                        case "wrong-actor": return text.Replace("\t" + c.Actor + "\t", "\twrong-actor\t");
                        case "wrong-source": return text.Replace("\tdb:source\t", "\tdb:other\t");
                        case "wrong-state": return text.Replace("state-v1", "state-v2");
                        case "not-saved": return text.Replace("\tSaved\t", "\tNotAttempted\t");
                        case "not-closed": return text.Replace("\tClosed\t", "\tLeftOpenForReview\t");
                        case "not-verified": return text.Replace("\t1\tSelectedSourceObjectOutgoing", "\t0\tSelectedSourceObjectOutgoing");
                        case "extra-edge": return text.Replace("END\n", "A\t/P/Other\tdb:other\tCurrent\t\t55\nEND\n");
                        case "missing-added-edge": return text.Replace("A\t/P/Target\tdb:target\tCurrent\t\t2\n", "");
                        case "baseline-deleted": return text.Replace("A\t/P/Target\tdb:target\tBaseline\t1.0\t2\n", "");
                        case "duplicate-after": return text.Replace("END\n", "A\t/P/Target\tdb:target\tCurrent\t\t2\nEND\n");
                        case "before-changed": return text.Replace("B\t/P/Target\tdb:target\tCurrent\t\t8\n", "B\t/P/Target\tdb:target\tCurrent\t\t9\n");
                        case "issue-on-success": return text.Replace("END\n", "I\tPermissionDenied\nEND\n");
                        default: throw new InvalidOperationException();
                    }
                };
                var ex = await Assert.ThrowsExceptionAsync<LinkWriteUncertainException>(async () => await c.Service.ApplyWriteAsync(preview));
                var cause = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await ex.Completion);
                Assert.AreEqual(IntegrationErrorCode.LinkWriteProtocolFailure, cause.Code);
                Assert.AreEqual(1, c.Modes.Count(m => m == "commit")); Assert.IsFalse(c.Transport.Gate.IsBusy);
            }
        }
        [TestMethod]
        public void TraceComparisonPreservesMultiplicityVersionAndModuleIdentity()
        {
            var current = new LinkTraceEdge("/P/T", "db:t", LinkVersionKind.Current, "", 2);
            var baseline = new LinkTraceEdge("/P/T", "db:t", LinkVersionKind.Baseline, "1.0", 2);
            var other = new LinkTraceEdge("/P/T", "db:replacement", LinkVersionKind.Current, "", 2);
            var diff = LinkTraceComparison.Compare(new[] { current, current, baseline }, new[] { baseline, current, other });
            Assert.AreEqual(1, diff.Removed.Count); Assert.AreEqual(current.Key, diff.Removed[0].Key);
            Assert.AreEqual(1, diff.Added.Count); Assert.AreEqual(other.Key, diff.Added[0].Key);
        }
        [TestMethod]
        public void FullBoundedTraceInReverseOrderHasNoFalseDelta()
        {
            var edges = Enumerable.Range(1, 2048).Select(i => new LinkTraceEdge("/P/T", "db:t", LinkVersionKind.Current, "", i)).ToArray();
            var diff = LinkTraceComparison.Compare(edges, edges.Reverse());
            Assert.AreEqual(0, diff.Added.Count); Assert.AreEqual(0, diff.Removed.Count);
        }
        [TestMethod]
        public async Task BoundsRejectOversizedResponseAndExcessiveEdges()
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Request = LinkWriteTests.Request(LinkWriteAction.Create, max: 1);
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.PreviewWriteAsync(c.Request));
                Assert.AreEqual(IntegrationErrorCode.LinkWriteProtocolFailure, ex.Code);
            }
            using (var c = new LinkWriteTestContext())
            {
                c.Alter = text => text.Replace("END\n", new string('x', 1048577) + "\nEND\n");
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.PreviewWriteAsync(c.Request));
                Assert.IsFalse(c.Modes.Contains("commit"));
            }
        }
        [TestMethod]
        public async Task QuotesBackslashesAndUnicodePathsAreDataAndRoundTripThroughTransport()
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Request = new LinkWriteRequest(LinkWriteAction.Create, "/Proje/Şartlar \"kaynak\"", 1, "/Proje/Hedef \\ α", 2, "/Proje/Bağlantılar");
                c.Before.Clear(); var preview = await c.Service.PreviewWriteAsync(c.Request);
                Assert.IsTrue(preview.CanApply);
                Assert.AreEqual(c.Request.SourceModulePath, DiscoveryTestContext.Parameter(c.Transport.Sessions.Session.Script, "sourcePath"));
                c.Confirmation.Approved = true; var result = await c.Service.ApplyWriteAsync(preview);
                Assert.IsTrue(result.Succeeded); Assert.AreEqual(c.Request.TargetModulePath, result.Comparison.Added.Single().TargetModulePath);
            }
        }
    }
}
