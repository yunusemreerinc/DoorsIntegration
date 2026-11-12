using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class LinkWriteTests
    {
        [DataTestMethod]
        [DataRow("/")]
        [DataRow("/P/*")]
        [DataRow("/P/?")]
        [DataRow("relative")]
        [DataRow("/P/../M")]
        [DataRow("/P/\nM")]
        public void NonExactPathsAreRejectedBeforeCom(string path)
        {
            Assert.ThrowsException<IntegrationException>(() => new LinkWriteRequest(LinkWriteAction.Create, path, 1, "/P/T", 2, "/P/L"));
            Assert.ThrowsException<IntegrationException>(() => new LinkWriteRequest(LinkWriteAction.Remove, "/P/S", 1, path, 2, "/P/L"));
            Assert.ThrowsException<IntegrationException>(() => new LinkWriteRequest(LinkWriteAction.Create, "/P/S", 1, "/P/T", 2, path));
        }
        [TestMethod]
        public void InvalidObjectsProfilesModesAndLimitsAreRejected()
        {
            Assert.ThrowsException<IntegrationException>(() => Request(LinkWriteAction.Create, absolute: 0));
            Assert.ThrowsException<IntegrationException>(() => Request((LinkWriteAction)99));
            Assert.ThrowsException<IntegrationException>(() => Request(LinkWriteAction.Create, max: 2049));
            Assert.ThrowsException<IntegrationException>(() => Request(LinkWriteAction.Create, timeout: TimeSpan.FromHours(1)));
            Assert.ThrowsException<IntegrationException>(() => new LinkWriteRequest(LinkWriteAction.Create, "/P/S", 1, "/P/T", 2, "/P/S"));
            Assert.ThrowsException<IntegrationException>(() => new LinkWriteRequest(LinkWriteAction.Create, "/P/S", 1, "/P/T", 2, "/P/L", connectionMode: (DoorsConnectionMode)99));
        }
        [TestMethod]
        public async Task ReadOnlyServiceDoesNotAcquireAWriteCapability()
        {
            var service = new LinkService(new UnusedLinkReader());
            var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await service.PreviewWriteAsync(Request(LinkWriteAction.Create)));
            Assert.AreEqual(IntegrationErrorCode.LinkWriteApprovalRequired, ex.Code);
        }
        [TestMethod]
        public async Task DryRunCapturesBaselinesAndDoesNotAuthorizeADirectCommit()
        {
            using (var c = new LinkWriteTestContext())
            {
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                Assert.IsTrue(preview.CanApply); Assert.AreEqual(64, preview.ApprovalFingerprint.Length);
                Assert.AreEqual(LinkVersionKind.Baseline, preview.Inspection.Before[1].VersionKind);
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Gateway.CommitAsync(preview, CancellationToken.None));
                Assert.AreEqual(IntegrationErrorCode.LinkWriteApprovalRequired, ex.Code);
                CollectionAssert.AreEqual(new[] { "preview" }, c.Modes); Assert.AreEqual(0, c.Confirmation.Calls);
                Assert.IsFalse(preview.TryBeginCommit());
            }
        }
        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task RemovalRequiresBothIndependentApprovals(bool general, bool removal)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Request = Request(LinkWriteAction.Remove); c.Before.Add(c.Selected());
                c.Confirmation.Approved = general; c.Confirmation.RemovalApproved = removal;
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                Assert.IsTrue(preview.CanApply);
                if (general && removal)
                {
                    var result = await c.Service.ApplyWriteAsync(preview);
                    Assert.IsTrue(result.Succeeded); Assert.AreEqual(1, result.Comparison.Removed.Count); Assert.AreEqual(0, result.Comparison.Added.Count);
                    Assert.IsTrue(result.After.Any(e => e.VersionKind == LinkVersionKind.Baseline));
                    CollectionAssert.AreEqual(new[] { "preview", "commit" }, c.Modes);
                }
                else
                {
                    var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
                    Assert.AreEqual(IntegrationErrorCode.LinkWriteApprovalRequired, ex.Code); Assert.IsFalse(c.Modes.Contains("commit"));
                }
                Assert.AreEqual(general ? 1 : 0, c.Confirmation.RemovalCalls);
                Assert.IsFalse(preview.TryBeginCommit());
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
            }
        }
        [TestMethod]
        public async Task CreateRequiresApprovalComparesOneDeltaAndCannotReplay()
        {
            using (var c = new LinkWriteTestContext())
            {
                var rejected = await c.Service.PreviewWriteAsync(c.Request);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(rejected));
                c.Confirmation.Approved = true;
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                var result = await c.Service.ApplyWriteAsync(preview);
                Assert.IsTrue(result.Succeeded); Assert.IsTrue(result.Comparison.Matches(c.Request, "db:target"));
                Assert.AreEqual(1, result.Comparison.Added.Count); Assert.AreEqual(0, result.Comparison.Removed.Count);
                Assert.AreEqual(0, c.Confirmation.RemovalCalls);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(1, c.Modes.Count(m => m == "commit"));
            }
        }
        [DataTestMethod]
        [DataRow(LinkWriteIssue.ModuleUnavailable)]
        [DataRow(LinkWriteIssue.NotFormalModule)]
        [DataRow(LinkWriteIssue.NotLinkModule)]
        [DataRow(LinkWriteIssue.AlreadyOpen)]
        [DataRow(LinkWriteIssue.Locked)]
        [DataRow(LinkWriteIssue.PermissionDenied)]
        [DataRow(LinkWriteIssue.BaselineNotWritable)]
        [DataRow(LinkWriteIssue.ObjectUnavailable)]
        [DataRow(LinkWriteIssue.LinksetMissing)]
        [DataRow(LinkWriteIssue.LinksetAmbiguous)]
        [DataRow(LinkWriteIssue.TraceabilityIncomplete)]
        [DataRow(LinkWriteIssue.LimitExceeded)]
        public async Task FailedPreconditionsBlockBeforeAnyApprovalOrCommit(LinkWriteIssue issue)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Issues.Add(issue); c.Confirmation.Approved = true;
                var preview = await c.Service.PreviewWriteAsync(c.Request); Assert.IsFalse(preview.CanApply);
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.LinkWriteBlocked, ex.Code); Assert.AreEqual(0, c.Confirmation.Calls);
                CollectionAssert.AreEqual(new[] { "preview" }, c.Modes);
            }
        }
        [DataTestMethod]
        [DataRow(LinkWriteAction.Create, 1, LinkWriteIssue.DuplicateLink)]
        [DataRow(LinkWriteAction.Create, 2, LinkWriteIssue.DuplicateLink)]
        [DataRow(LinkWriteAction.Remove, 0, LinkWriteIssue.LinkMissing)]
        [DataRow(LinkWriteAction.Remove, 2, LinkWriteIssue.AmbiguousLink)]
        public async Task ExistingAndAmbiguousLinksAreNeverSilentlyCreatedOrBulkDeleted(LinkWriteAction action, int count, LinkWriteIssue issue)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Request = Request(action); for (int i = 0; i < count; i++) c.Before.Add(c.Selected());
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                Assert.IsFalse(preview.CanApply); CollectionAssert.Contains(preview.Inspection.Issues.ToArray(), issue);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
                Assert.IsFalse(c.Modes.Contains("commit"));
            }
        }
        [DataTestMethod]
        [DataRow("state")]
        [DataRow("database")]
        [DataRow("actor")]
        [DataRow("source")]
        [DataRow("target")]
        [DataRow("linkModule")]
        [DataRow("duplicate")]
        [DataRow("lock")]
        public async Task ChangedContextAfterApprovalIsRecheckedAndNeverRetried(string change)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Confirmation.Approved = true;
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                c.Confirmation.BeforeReturn = _ =>
                {
                    switch (change)
                    {
                        case "state": c.StateSalt = "changed"; break;
                        case "database": c.Database = "other"; break;
                        case "actor": c.Actor = "other"; break;
                        case "source": c.SourceId = "other"; break;
                        case "target": c.TargetId = "other"; break;
                        case "linkModule": c.LinkId = "other"; break;
                        case "duplicate": c.Before.Add(c.Selected()); break;
                        default: c.Issues.Add(LinkWriteIssue.Locked); break;
                    }
                };
                var result = await c.Service.ApplyWriteAsync(preview);
                Assert.IsFalse(result.Succeeded); Assert.AreEqual(LinkWritePersistence.NotAttempted, result.Persistence);
                Assert.IsTrue(result.Outcome == LinkWriteOutcome.Blocked || result.Outcome == LinkWriteOutcome.ContextChanged);
                Assert.AreEqual(1, c.Modes.Count(m => m == "commit"));
            }
        }
        [DataTestMethod]
        [DataRow("before")]
        [DataRow("general")]
        [DataRow("removal")]
        public async Task ExpiryAtEitherApprovalStageAndForeignPlansAreRejected(string when)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Request = Request(LinkWriteAction.Remove); c.Before.Add(c.Selected()); c.Confirmation.Approved = c.Confirmation.RemovalApproved = true;
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.NewService().ApplyWriteAsync(preview));
                Action expire = () => typeof(LinkWritePreview).GetField("<ExpiresAtUtc>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(preview, DateTime.UtcNow.AddSeconds(-1));
                if (when == "before") expire(); else if (when == "general") c.Confirmation.BeforeReturn = _ => expire(); else c.Confirmation.BeforeRemovalReturn = _ => expire();
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
                Assert.IsFalse(c.Modes.Contains("commit"));
            }
        }
        [TestMethod]
        public async Task CancelDuringRemovalApprovalNeverDispatches()
        {
            using (var c = new LinkWriteTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                c.Request = Request(LinkWriteAction.Remove); c.Before.Add(c.Selected()); c.Confirmation.Approved = c.Confirmation.RemovalApproved = true;
                var preview = await c.Service.PreviewWriteAsync(c.Request);
                c.Confirmation.BeforeRemovalReturn = _ => cancellation.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await c.Service.ApplyWriteAsync(preview, cancellation.Token));
                Assert.IsFalse(c.Modes.Contains("commit")); Assert.IsFalse(preview.TryBeginCommit());
            }
        }
        [DataTestMethod]
        [DataRow("PreviewCompleted")]
        [DataRow("ApprovalRequested")]
        [DataRow("CommitAuthorized")]
        [DataRow("CommitDispatchRequested")]
        [DataRow("RemovalApprovalRequested")]
        [DataRow("RemovalApprovalGranted")]
        public async Task AuditFailureBeforeDispatchBlocksMutation(string phase)
        {
            using (var c = new LinkWriteTestContext())
            {
                if (phase.StartsWith("Removal", StringComparison.Ordinal)) { c.Request = Request(LinkWriteAction.Remove); c.Before.Add(c.Selected()); }
                c.Audit.FailPhase = phase; c.Confirmation.Approved = c.Confirmation.RemovalApproved = true;
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () =>
                { var preview = await c.Service.PreviewWriteAsync(c.Request); await c.Service.ApplyWriteAsync(preview); });
                Assert.AreEqual(IntegrationErrorCode.LinkWriteAuditFailed, ex.Code); Assert.IsFalse(c.Modes.Contains("commit"));
            }
        }
        [TestMethod]
        public async Task AuditFailureAfterCommitPreservesSavedResultAndBlocksSuccessClaim()
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Confirmation.Approved = true; var preview = await c.Service.PreviewWriteAsync(c.Request); c.Audit.FailPhase = "CommitCompleted";
                var result = await c.Service.ApplyWriteAsync(preview);
                Assert.AreEqual(LinkWritePersistence.Saved, result.Persistence); Assert.IsTrue(result.AfterVerified);
                Assert.IsFalse(result.AuditRecorded); Assert.IsFalse(result.Succeeded);
            }
        }
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DeadlineOrCancellationRetainsLateResultAndOperationGateUntilNativeReturn(bool cancel)
        {
            using (var c = new LinkWriteTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                c.Request = Request(LinkWriteAction.Create, timeout: TimeSpan.FromMilliseconds(cancel ? 2000 : 100)); c.Confirmation.Approved = true;
                var preview = await c.Service.PreviewWriteAsync(c.Request); var session = c.Transport.Sessions.Session;
                session.EnteredRun.Reset(); session.BlockRun = true;
                var pending = c.Service.ApplyWriteAsync(preview, cancellation.Token);
                Assert.IsTrue(session.EnteredRun.Wait(TimeSpan.FromSeconds(3)));
                if (cancel) cancellation.Cancel();
                var ex = await Assert.ThrowsExceptionAsync<LinkWriteUncertainException>(async () => await pending);
                Assert.IsTrue(c.Transport.Gate.IsBusy); Assert.IsFalse(ex.Completion.IsCompleted);
                Assert.AreEqual(ex.OperationId, c.Audit.Entries.Single(e => e.Phase == "OutcomeUnknown").OperationId);
                session.ReleaseRun.Set(); var result = await ex.Completion;
                Assert.IsTrue(result.Succeeded); Assert.IsFalse(c.Transport.Gate.IsBusy);
                Assert.AreEqual(1, c.Modes.Count(m => m == "commit")); Assert.IsFalse(preview.TryBeginCommit());
            }
        }
        [TestMethod]
        public async Task BusyGateFailsBeforeDispatchWhileComFailureIsUncertain()
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Confirmation.Approved = true; var preview = await c.Service.PreviewWriteAsync(c.Request);
                using (c.Transport.Gate.Enter())
                {
                    var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await c.Service.ApplyWriteAsync(preview));
                    Assert.AreEqual(IntegrationErrorCode.DoorsOperationBusy, ex.Code); Assert.IsFalse(ex is LinkWriteUncertainException);
                }
                var next = await c.Service.PreviewWriteAsync(c.Request); c.Transport.Sessions.Session.RunFailure = new COMException("PRIVATE_COM_ERROR");
                var unknown = await Assert.ThrowsExceptionAsync<LinkWriteUncertainException>(async () => await c.Service.ApplyWriteAsync(next));
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await unknown.Completion);
                Assert.AreEqual(1, c.Modes.Count(m => m == "commit"));
                Assert.IsFalse(string.Join("\n", c.Transport.Log.Entries).Contains("PRIVATE_COM_ERROR"));
            }
        }
        [DataTestMethod]
        [DataRow(LinkWriteOutcome.PartialFailure)]
        [DataRow(LinkWriteOutcome.OutcomeUnknown)]
        public async Task PartialOrUnknownResultsNeverImplyRollbackOrSuccess(LinkWriteOutcome outcome)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Confirmation.Approved = true; var preview = await c.Service.PreviewWriteAsync(c.Request); c.ForcedOutcome = outcome;
                var result = await c.Service.ApplyWriteAsync(preview);
                Assert.IsFalse(result.Succeeded); Assert.AreEqual(LinkWriteCleanup.LeftOpenForReview, result.Cleanup); Assert.IsNull(result.Comparison);
            }
        }
        [TestMethod]
        public async Task FileAuditIsAppendOnlyAndIncompleteTailBlocksWithoutOverwrite()
        {
            using (var c = new LinkWriteTestContext())
            {
                var preview = await c.Service.PreviewWriteAsync(c.Request); string path = Path.Combine(c.Transport.Root, "link-audit.jsonl");
                var sink = new FileLinkWriteAuditSink(new LinkWriteAuditOptions(path));
                sink.Record(new LinkWriteAuditEntry("ApprovalRequested", preview)); sink.Record(new LinkWriteAuditEntry("ApprovalDeclined", preview));
                Assert.AreEqual(2, File.ReadAllLines(path).Length); StringAssert.Contains(File.ReadAllText(path), "sourceModuleId");
                File.AppendAllText(path, "{partial"); string before = File.ReadAllText(path);
                Assert.ThrowsException<InvalidDataException>(() => sink.Record(new LinkWriteAuditEntry("CommitAuthorized", preview)));
                Assert.AreEqual(before, File.ReadAllText(path));
            }
        }
        [TestMethod]
        public void BothHostsComposeLinkWritingAndConsoleDefaultsToDeny()
        {
            foreach (bool desktop in new[] { false, true })
            {
                var services = new ServiceCollection();
                if (desktop) DoorsIntegration.Desktop.ServiceCollectionExtensions.AddDoorsDesktop(services);
                else DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                {
                    Assert.IsInstanceOfType(provider.GetRequiredService<ILinkWriteGateway>(), typeof(DxlLinkWriteGateway));
                    Assert.IsInstanceOfType(provider.GetRequiredService<ILinkWriteConfirmation>(), desktop ? typeof(DoorsIntegration.Desktop.DesktopWriteConfirmation) : typeof(RejectLinkWriteConfirmation));
                }
            }
        }
        internal static LinkWriteRequest Request(LinkWriteAction action, int absolute = 1, int max = 2048, TimeSpan? timeout = null) =>
            new LinkWriteRequest(action, "/P/Source", absolute, "/P/Target", 2, "/P/Links", timeout: timeout, maxTraceLinks: max);
    }
}
