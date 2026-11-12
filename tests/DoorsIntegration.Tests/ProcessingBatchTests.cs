using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Processing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ProcessingBatchTests
    {
        [TestMethod]
        public void HashesSeparateAttributeStructureFromRequiredFieldLists()
        {
            var first = new BatchAttribute("A", "value"); var second = new BatchAttribute("B", "{x}", BatchValueFormat.OpaqueRichText, "plain");
            var one = new BatchChange("key", BatchAction.Create, "/P/M", new[] { first }, requiredAttributes: new[] { second.Name, second.Value, second.Format.ToString(), second.ExpectedPlainText });
            var two = new BatchChange("key", BatchAction.Create, "/P/M", new[] { first, second });
            Assert.AreNotEqual(one.PayloadHash, two.PayloadHash);
        }
        [TestMethod]
        public async Task InspectionTransportFailureHaltsFurtherAdapterReadsAndPreventsExecution()
        {
            using (var c = new ProcessingTestContext())
            {
                c.Adapter.Inspect = change => throw new IOException("PRIVATE");
                var plan = c.Hold(await c.Service.PreviewAsync(new ProcessingArraySource(ProcessingTestContext.Change("a"), ProcessingTestContext.Change("b")), c.Options(true)));
                Assert.AreEqual(1, c.Adapter.Inspections); Assert.IsFalse(plan.IsComplete); Assert.IsFalse(plan.CanApply);
                Assert.AreEqual("InspectionHalted", plan.ReadRows().Last().Code);
            }
        }
        [TestMethod]
        public async Task PreviewIsImmutableReadOnlyAndDuplicateKeysBlockAllMatchingRows()
        {
            using (var c = new ProcessingTestContext())
            {
                var change = ProcessingTestContext.Change(); var plan = await c.Preview(change);
                Assert.IsTrue(plan.CanApply); Assert.AreEqual(0, c.Adapter.Calls); Assert.AreEqual(0, c.Confirmation.Calls);
                Assert.ThrowsException<IOException>(() => File.WriteAllText(plan.FilePath, "tampered"));
                var duplicated = c.Hold(await c.Service.PreviewAsync(new ProcessingArraySource(change, change), c.Options(true)));
                Assert.IsFalse(duplicated.CanApply); Assert.IsTrue(duplicated.ReadRows().All(r => r.Status == BatchRowStatus.Conflict));
                Assert.AreEqual(1, c.Adapter.Inspections);
            }
        }
        [TestMethod]
        public async Task OverlappingUpdatesWithDifferentKeysAreBothBlocked()
        {
            using (var c = new ProcessingTestContext())
            {
                var plan = await c.Preview(ProcessingTestContext.Change("a", BatchAction.Update, "5"), ProcessingTestContext.Change("b", BatchAction.Update, "5"));
                Assert.AreEqual(2L, plan.BlockedCount); Assert.AreEqual(0, c.Adapter.Inspections); Assert.IsFalse(plan.CanApply);
            }
        }
        [DataTestMethod]
        [DataRow("denied")]
        [DataRow("expired")]
        [DataRow("expires-in-review")]
        [DataRow("foreign")]
        [DataRow("disposed")]
        public async Task MissingFreshApprovalNeverDispatches(string mode)
        {
            using (var c = new ProcessingTestContext())
            {
                var plan = await c.Preview(ProcessingTestContext.Change()); var service = c.Service;
                if (mode == "denied") c.Confirmation.Approved = false;
                if (mode == "expired") c.Clock.Now += TimeSpan.FromHours(1);
                if (mode == "expires-in-review") c.Confirmation.BeforeReturn = _ => c.Clock.Now += TimeSpan.FromHours(1);
                if (mode == "foreign") service = c.NewService();
                if (mode == "disposed") plan.Dispose();
                await Assert.ThrowsExceptionAsync<ProcessingException>(async () => await service.ApplyAsync(plan));
                Assert.AreEqual(0, c.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task SuccessfulKeysSurviveAServiceRestartAndPayloadConflictsNeverOverwrite()
        {
            using (var c = new ProcessingTestContext())
            {
                var change = ProcessingTestContext.Change(); var plan = await c.Preview(change);
                var report = await c.Service.ApplyAsync(plan); Assert.AreEqual(BatchRowStatus.Applied, report.ReadRows().Single().Status);
                await Assert.ThrowsExceptionAsync<ProcessingException>(async () => await c.Service.ApplyAsync(plan));
                var restarted = c.NewService(); var next = c.Hold(await restarted.PreviewAsync(new ProcessingArraySource(change), c.Options()));
                Assert.AreEqual(BatchRowStatus.AlreadyApplied, next.ReadRows().Single().Status);
                var replay = await restarted.ApplyAsync(next); Assert.AreEqual(BatchRowStatus.AlreadyApplied, replay.ReadRows().Single().Status); Assert.AreEqual(1, c.Adapter.Calls);
                var conflict = c.Hold(await restarted.PreviewAsync(new ProcessingArraySource(ProcessingTestContext.Change(value: "changed")), c.Options()));
                Assert.AreEqual(BatchRowStatus.Conflict, conflict.ReadRows().Single().Status); Assert.IsFalse(conflict.CanApply);
                Assert.AreEqual(1, c.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task OnlyExplicitNoMutationTransientFailuresAreAutomaticallyRetriedWithBoundedBackoff()
        {
            using (var c = new ProcessingTestContext())
            {
                c.Adapter.Dispatch = (change, calls) => calls < 3 ? new BatchDispatchResult(BatchMutation.None, "Busy", true) : new BatchDispatchResult(BatchMutation.Applied, "Done", resultingObjectId: "42");
                var plan = await c.Preview(ProcessingTestContext.Change()); var report = await c.Service.ApplyAsync(plan);
                Assert.AreEqual(3, c.Adapter.Calls); Assert.AreEqual(3, report.ReadRows().Single().Attempts); Assert.AreEqual("42", report.ReadRows().Single().ObjectId);
                CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400) }, c.Clock.Delays);
                Assert.ThrowsException<ArgumentException>(() => new BatchDispatchResult(BatchMutation.Unknown, "bad", true));
            }
        }
        [TestMethod]
        public async Task LifetimeAttemptLimitSurvivesRestart()
        {
            using (var c = new ProcessingTestContext())
            {
                c.Adapter.Dispatch = (change, calls) => new BatchDispatchResult(BatchMutation.None, "Busy", true);
                var request = ProcessingTestContext.Change(); var report = await c.Service.ApplyAsync(await c.Preview(request));
                Assert.AreEqual(3, c.Adapter.Calls); Assert.IsFalse(report.ReadRows().Single().CanRepreview);
                var service = c.NewService(); var next = c.Hold(await service.PreviewAsync(new ProcessingArraySource(request), c.Options()));
                Assert.AreEqual("AttemptLimit", next.ReadRows().Single().Code); Assert.IsFalse(next.CanApply);
            }
        }
        [DataTestMethod]
        [DataRow(BatchMutation.Unknown)]
        [DataRow(BatchMutation.Partial)]
        public async Task UnknownOrPartialRowsAreHeldAndStopRemainingWritesEvenWithPartialProcessing(BatchMutation mutation)
        {
            using (var c = new ProcessingTestContext())
            {
                c.Adapter.Dispatch = (change, calls) => new BatchDispatchResult(mutation, "ReviewRequired");
                var plan = c.Hold(await c.Service.PreviewAsync(new ProcessingArraySource(ProcessingTestContext.Change("a"), ProcessingTestContext.Change("b")), c.Options(true)));
                var report = await c.Service.ApplyAsync(plan); var rows = report.ReadRows().ToArray();
                Assert.AreEqual(1, c.Adapter.Calls); Assert.IsFalse(rows[0].CanRepreview); Assert.AreEqual(BatchRowStatus.NotAttempted, rows[1].Status);
                Assert.IsTrue(File.Exists(report.FailuresPath));
                var service = c.NewService(); var next = c.Hold(await service.PreviewAsync(new ProcessingArraySource(ProcessingTestContext.Change("a")), c.Options()));
                Assert.AreEqual("ManualReconciliationRequired", next.ReadRows().Single().Code); Assert.IsFalse(next.CanApply);
            }
        }
        [TestMethod]
        public async Task LateCompletionIsObservedWithoutResubmissionAndHoldsTheDurableLease()
        {
            using (var c = new ProcessingTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var completion = new TaskCompletionSource<BatchDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                c.Adapter.Dispatch = (change, calls) => new BatchDispatchResult(BatchMutation.Unknown, "Timeout", completion: completion.Task);
                var first = ProcessingTestContext.Change("a"); var plan = c.Hold(await c.Service.PreviewAsync(new ProcessingArraySource(first, ProcessingTestContext.Change("b")), c.Options(true)));
                var pending = c.Service.ApplyAsync(plan, cancellation.Token);
                Assert.AreEqual(1, c.Adapter.Calls); Assert.IsFalse(pending.IsCompleted); cancellation.Cancel();
                var competing = c.NewService(); var busy = c.Hold(await competing.PreviewAsync(new ProcessingArraySource(first), c.Options()));
                Assert.AreEqual("IdempotencyBusy", busy.ReadRows().Single().Code);
                completion.SetResult(new BatchDispatchResult(BatchMutation.Applied, "Verified", resultingObjectId: "42"));
                var report = await pending; var rows = report.ReadRows().ToArray();
                Assert.AreEqual(BatchRowStatus.Applied, rows[0].Status); Assert.AreEqual("42", rows[0].ObjectId);
                Assert.AreEqual(BatchRowStatus.NotAttempted, rows[1].Status); Assert.AreEqual(1, c.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task CrashMarkerAndIncompleteJournalTailAreNeverReplayedOrRepairedAutomatically()
        {
            using (var c = new ProcessingTestContext())
            {
                var change = ProcessingTestContext.Change(); string key = c.Options().IdempotencyKey(change);
                using (var lease = c.Journal.Acquire(key)) lease.Append(new BatchJournalEntry(key, change.PayloadHash, BatchJournalState.Started, 1));
                var plan = await c.Preview(change); Assert.IsFalse(plan.CanApply); Assert.AreEqual(0, c.Adapter.Calls);
                string path = Path.Combine(c.Root, "journal", key + ".jsonl"); File.AppendAllText(path, "{incomplete"); string before = File.ReadAllText(path);
                var blocked = await c.Preview(change); Assert.IsFalse(blocked.CanApply); Assert.AreEqual("JournalCorrupt", blocked.ReadRows().Single().Code);
                Assert.AreEqual(before, File.ReadAllText(path)); Assert.AreEqual(0, c.Adapter.Calls);
            }
        }
        [DataTestMethod]
        [DataRow("PreviewCompleted")]
        [DataRow("ApprovalRequested")]
        [DataRow("ApprovalGranted")]
        public async Task AuditFailureBeforeJobDispatchNeverWrites(string phase)
        {
            using (var c = new ProcessingTestContext())
            {
                c.Audit.FailPhase = phase;
                await Assert.ThrowsExceptionAsync<ProcessingException>(async () => { var p = await c.Preview(ProcessingTestContext.Change()); await c.Service.ApplyAsync(p); });
                Assert.AreEqual(0, c.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task AuditFailureAfterPersistenceCannotCauseDuplicateRetry()
        {
            using (var c = new ProcessingTestContext())
            {
                var change = ProcessingTestContext.Change(); var plan = await c.Preview(change); c.Audit.FailPhase = "AttemptCompleted";
                var result = (await c.Service.ApplyAsync(plan)).ReadRows().Single();
                Assert.AreEqual(BatchRowStatus.AppliedWithProblems, result.Status); Assert.IsFalse(result.AuditRecorded); Assert.IsFalse(result.CanRepreview);
                c.Audit.FailPhase = null; var next = await c.Preview(change);
                Assert.AreEqual(BatchRowStatus.AlreadyApplied, next.ReadRows().Single().Status); await c.Service.ApplyAsync(next); Assert.AreEqual(1, c.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task RetryPreviewContainsOnlySafeFailedRowsAndRequiresNewApproval()
        {
            using (var c = new ProcessingTestContext())
            {
                c.Adapter.Dispatch = (change, calls) => change.Key == "b" ? new BatchDispatchResult(BatchMutation.None, "PermissionDenied") : new BatchDispatchResult(BatchMutation.Applied, "Done");
                var plan = c.Hold(await c.Service.PreviewAsync(new ProcessingArraySource(ProcessingTestContext.Change("a"), ProcessingTestContext.Change("b")), c.Options(true)));
                var report = await c.Service.ApplyAsync(plan); Assert.AreEqual(1L, report.FailedCount);
                var retry = c.Hold(await c.Service.PreviewRetryAsync(plan, report)); Assert.AreEqual("b", retry.ReadRows().Single().Change.Key);
                Assert.AreEqual(1, c.Confirmation.Calls); c.Adapter.Dispatch = null; await c.Service.ApplyAsync(retry);
                Assert.AreEqual(2, c.Confirmation.Calls); Assert.AreEqual(3, c.Adapter.Calls); Assert.AreEqual(1, c.Adapter.Applied.Count(r => r.Key == "a"));
            }
        }
        [TestMethod]
        public async Task FailureCsvProtectsFormulaLikeKeysAndUtf8()
        {
            using (var c = new ProcessingTestContext())
            {
                c.Adapter.Dispatch = (change, calls) => new BatchDispatchResult(BatchMutation.None, "Blocked");
                var report = await c.Service.ApplyAsync(await c.Preview(ProcessingTestContext.Change(" =HYPERLINK(\"x\") Ş")));
                string path = Path.Combine(c.Root, "failed.csv"); BatchReportExporter.ExportFailuresCsv(report, path);
                StringAssert.Contains(File.ReadAllText(path), "' =HYPERLINK"); StringAssert.Contains(File.ReadAllText(path), "Ş");
                Assert.ThrowsException<IOException>(() => BatchReportExporter.ExportFailuresCsv(report, path));
            }
        }
    }
}
