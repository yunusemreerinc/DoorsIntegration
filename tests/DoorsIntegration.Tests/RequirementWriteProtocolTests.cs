using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class RequirementWriteProtocolTests
    {
        [DataTestMethod]
        [DataRow("stale")]
        [DataRow("header")]
        [DataRow("no-footer")]
        [DataRow("no-newline")]
        [DataRow("raw-cr")]
        [DataRow("wrong-path")]
        [DataRow("wrong-mode")]
        [DataRow("numeric-outcome")]
        [DataRow("unknown-row")]
        [DataRow("duplicate-attribute")]
        [DataRow("duplicate-enum")]
        [DataRow("enum-before-definition")]
        [DataRow("bad-flag")]
        [DataRow("invalid-escape")]
        [DataRow("bad-surrogate")]
        [DataRow("missing-step")]
        [DataRow("wrong-step")]
        [DataRow("preview-write")]
        [DataRow("preview-saved")]
        [DataRow("missing-old-values")]
        public async Task MalformedOrInconsistentPreviewsFailWithoutExposingRawContent(string fault)
        {
            using (var context = new RequirementWriteTestContext())
            {
                if (fault == "missing-old-values") context.Request = new RequirementWriteRequest(context.Request.Target, context.Request.Changes, 42);
                context.Alter = text =>
                {
                    switch (fault)
                    {
                        case "stale": return text.Replace("DOORS_REQUIREMENT_WRITE|1|", "DOORS_REQUIREMENT_WRITE|1|bad");
                        case "header": return text.Replace("WRITE|1|", "WRITE|2|");
                        case "no-footer": return text.Replace("END\n", "");
                        case "no-newline": return text.TrimEnd('\n');
                        case "raw-cr": return text.Replace("Text\tText", "Te\rxt\tText");
                        case "wrong-path": return text.Replace("/P/M", "/PRIVATE_CONTENT");
                        case "wrong-mode": return text.Replace("M\tpreview", "M\tcommit");
                        case "numeric-outcome": return text.Replace("\tPreview\t", "\t1\t");
                        case "unknown-row": return text.Replace("END\n", "X\tPRIVATE_CONTENT\nEND\n");
                        case "duplicate-attribute": return text.Replace("END\n", "A\tObject Text\tText\tText\t1\t0\t1\t0\t1\t1\nEND\n");
                        case "duplicate-enum": return text.Replace("E\tPriority\tLow\n", "E\tPriority\tLow\nE\tPriority\tLow\n");
                        case "enum-before-definition": return text.Replace("A\tObject Heading", "E\tMissing\tValue\nA\tObject Heading");
                        case "bad-flag": return text.Replace("\t1\t0\t1\t0\t1\t1\n", "\t2\t0\t1\t0\t1\t1\n");
                        case "invalid-escape": return text.Replace("\tObject Heading\t", "\tObject\\qHeading\t");
                        case "bad-surrogate": return text.Replace("\tPriority Type\t", "\t\uD800\t");
                        case "missing-step": return text.Replace("S\tVerify\t\tSkipped\t\n", "");
                        case "wrong-step": return text.Replace("S\tSave\t", "S\tCreate\t");
                        case "preview-write": return text.Replace("S\tCreate\t\tSkipped\t", "S\tCreate\t\tApplied\t");
                        case "preview-saved": return text.Replace("\tNotAttempted\t", "\tSaved\t");
                        case "missing-old-values": return string.Join("\n", text.Split('\n').Where(line => !line.StartsWith("B\t")));
                        default: throw new InvalidOperationException();
                    }
                };
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.PreviewWriteAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteProtocolFailure, ex.Code);
                Assert.IsFalse(ex.Message.Contains("PRIVATE_CONTENT"));
                Assert.IsFalse(context.Modes.Contains("commit"));
            }
        }

        [DataTestMethod]
        [DataRow("wrong-identity")]
        [DataRow("missing-object")]
        [DataRow("unsaved-success")]
        [DataRow("open-success")]
        [DataRow("skipped-success")]
        [DataRow("failure-followed-by-success")]
        [DataRow("changed-before-state")]
        public async Task InconsistentCommitResponsesAreUncertainAndNeverRetried(string fault)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                context.Alter = text =>
                {
                    switch (fault)
                    {
                        case "wrong-identity": return text.Replace("module-1", "other-module");
                        case "missing-object": return text.Replace("\t42\tREQ-42\t0\t1\n", "\t0\t\t0\t0\n");
                        case "unsaved-success": return text.Replace("\tSaved\t", "\tNotAttempted\t");
                        case "open-success": return text.Replace("\tClosed\t", "\tLeftOpenForReview\t");
                        case "skipped-success": return text.Replace("S\tSave\t\tApplied\t", "S\tSave\t\tSkipped\t");
                        case "failure-followed-by-success": return text.Replace("S\tCreate\t\tApplied\t\n", "S\tCreate\t\tFailed\tNativeFailure\n");
                        case "changed-before-state": return text.Replace("state-v1", "different-state");
                        default: throw new InvalidOperationException();
                    }
                };
                var ex = await Assert.ThrowsExceptionAsync<RequirementWriteUncertainException>(async () => await context.Service.ApplyWriteAsync(preview));
                var parse = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await ex.Completion);
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteProtocolFailure, parse.Code);
                Assert.AreEqual(1, context.Modes.Count(m => m == "commit"));
                Assert.IsFalse(context.Transport.Gate.IsBusy);
            }
        }

        [TestMethod]
        public async Task CrLfRecordsAndOldRichTextArePreservedInTheReview()
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Request = new RequirementWriteRequest(context.Request.Target, context.Request.Changes, 42);
                context.Alter = s => s.Replace("\n", "\r\n");
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Assert.AreEqual(context.Request.Changes.Count, preview.Inspection.BeforeValues.Count);
                Assert.AreEqual("old plain", preview.Inspection.BeforeValues[0].PlainText);
                Assert.AreEqual("{\\b old plain}", preview.Inspection.BeforeValues[0].RichTextWithOle);
            }
        }
    }
}
