using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    // Only fake COM transport runs here. NativeDxl is a protocol fixture, not live evidence.
    internal sealed class TestBaselineCompatibility : IBaselineExecutionCompatibility
    {
        public string ClientVersion => "9.7.2.0";
        public bool IsValidated(BaselineExecutionAuthorization authorization, BaselineExecutionScriptOptions scripts) => true;
    }
    internal sealed class BaselineExecutionTransportFixture : IDisposable
    {
        internal readonly ExecutorTestContext Files = new ExecutorTestContext();
        internal readonly PreflightTestFixture Preflight = new PreflightTestFixture();
        internal readonly DxlExecutor Executor;
        internal readonly BaselineExecutionScriptOptions Scripts = new BaselineExecutionScriptOptions();
        internal DxlBaselineExecutionAdapter Adapter;
        internal BaselineExecutionAuthorization Authorization;
        internal Func<string, string> Alter;
        internal BaselineExecutionTransportFixture()
        {
            Executor = Queued(Files);
            Adapter = new DxlBaselineExecutionAdapter(Executor, Scripts, new TestBaselineCompatibility(), Files.Log);
            Files.Sessions.Session.OnRun = session =>
            {
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N"); var payload = Batch(id, Authorization);
                session.Payload = Alter == null ? payload : Alter(payload);
            };
        }
        internal static DxlExecutor Queued(ExecutorTestContext f) => new DxlExecutor(f.Sessions, new Utf8DxlScriptLoader(), f.Workspaces, f.Log, f.Gate,
            new NoDxlRunLimitAutomation(), new DxlWatchdogSettings(), DoorsStaExecutionQueue.Shared);
        internal async Task Prepare(BaselineType type = BaselineType.Minor, TimeSpan? timeout = null, DoorsVersion target = DoorsVersion.V97,
            string suffix = "SRR", string annotation = "Review\nİnceleme 注釈")
        {
            var request = new BulkBaselinePreflightRequest(new BulkBaselineRequest(Preflight.States.Select(s => new BaselineModuleSelection(s.Module)), type,
                suffix: suffix, annotation: annotation), timeout: timeout, target: target);
            var plan = await Preflight.Service.CreatePlanAsync(request); var original = plan.Items.Single();
            Assert.AreEqual(BulkBaselineItemStatus.Ready, original.Status);
            var p = BaselineCheckState.Passed;
            var facts = new BaselinePreflightFacts(original.Module, p, DoorsItemKind.FormalModule, p, p, p,
                BaselineEditState.Closed, BaselineLockState.Unlocked, p, BaselineUnsavedState.None, p,
                "database-1", "test-user", "1700000000", "connection-1", "content-1", "acl-1", "lock-1", p, p, p,
                BaselineTargetAvailability.Free, BaselineMetadataSource.NativeDxl, "FAKE transport fixture; not native validation.");
            // Reflection is confined to fake-transport tests. Core deliberately grants no friend
            // assembly access and no public authorization constructor to application callers.
            var condition = Internal<BaselinePrecondition>("fixture", plan.RequestFingerprint, facts, "metadata", DateTime.UtcNow);
            var item = Internal<BulkBaselineDryRunItem>(original.Probe, original.Status, original.ReasonCode, original.Reason, original.RecommendedResolution,
                original.Warning, original.CurrentVersion, original.ExpectedVersion, condition, false);
            Authorization = Internal<BaselineExecutionAuthorization>(Internal<BaselineExecutionReview>(plan), item, Preflight.Clock);
        }
        internal static T Internal<T>(params object[] args) => (T)Activator.CreateInstance(typeof(T),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, args, System.Globalization.CultureInfo.InvariantCulture);
        internal static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        internal static string Batch(Guid id, BaselineExecutionAuthorization authorization)
        {
            var item = authorization.Item; var f = item.Precondition.Facts;
            return "DOORS_BASELINE_EXECUTE\t1\t" + id.ToString("N") + "\nM\t" + Escape(item.Module.CanonicalFullPath) + "\t" + Escape(item.Module.ModuleId) +
                "\t" + Escape(f.DatabaseIdentity) + "\t" + Escape(f.CurrentUserId) + "\nT\t" + item.ExpectedVersion.Major + "\t" + item.ExpectedVersion.Minor + "\t" +
                Escape(item.Probe.Suffix) + "\t" + Escape(item.Probe.Annotation) + "\nR\tCreated\t1\tVerified\tClosed\nEND\n";
        }
        public void Dispose() => Files.Dispose();
    }
    [TestClass, DoNotParallelize]
    public class BaselineExecutionProtocolTests
    {
        [DataTestMethod, DataRow(DoorsVersion.V95), DataRow(DoorsVersion.V97)]
        public async Task UnvalidatedReleaseNeverOpensComEvenWithCapability(DoorsVersion version)
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(target: version);
                var adapter = new DxlBaselineExecutionAdapter(f.Executor, f.Scripts, new UnvalidatedBaselineExecutionCompatibility(), f.Files.Log);
                var result = await adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, result.Outcome); Assert.IsFalse(result.WriteAttempted); Assert.AreEqual(0, f.Files.Sessions.OpenCalls);
            }
        }
        [DataTestMethod, DataRow(BaselineType.Major), DataRow(BaselineType.Minor)]
        public async Task ReviewedNativeProtocolRunsOnceOnSharedStaAndReplaysAreRejected(BaselineType type)
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(type); var result = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.Created, result.Outcome); Assert.IsTrue(result.WriteAttempted);
                Assert.AreEqual(ApartmentState.STA, f.Files.Sessions.OpenApartment);
                Assert.AreEqual(1, f.Files.Sessions.Session.ThreadIds.Distinct().Count());
                var replay = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, replay.Outcome); Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
                StringAssert.Contains(f.Files.Sessions.Session.Script, "di_arg_type = \"" + type + "\"");
                Assert.AreEqual(result.Module.ModuleId, f.Authorization.Item.Module.ModuleId);
            }
        }
        [TestMethod]
        public async Task UnqueuedLegacyExecutorCannotRunNativeBaselineWrite()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); var adapter = new DxlBaselineExecutionAdapter(f.Files.Executor, f.Scripts, new TestBaselineCompatibility(), f.Files.Log);
                var result = await adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, result.Outcome); Assert.AreEqual(0, f.Files.Sessions.OpenCalls);
            }
        }
        [DataTestMethod, DataRow("truncated"), DataRow("wrong-id"), DataRow("wrong-module"), DataRow("wrong-database"), DataRow("wrong-version"),
            DataRow("wrong-annotation"), DataRow("no-attempt"), DataRow("bad-cleanup"), DataRow("bad-escape"), DataRow("extra-row")]
        public async Task InvalidPostWriteResponseIsUnknownNotDefiniteFailureAndNeverRetried(string fault)
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); f.Alter = text =>
                {
                    switch (fault)
                    {
                        case "truncated": return text.TrimEnd('\n');
                        case "wrong-id": return text.Replace("\t1\t", "\t2\t");
                        case "wrong-module": return text.Replace("native-1", "PRIVATE");
                        case "wrong-database": return text.Replace("database-1", "PRIVATE");
                        case "wrong-version": return text.Replace("T\t3\t8", "T\t3\t9");
                        case "wrong-annotation": return text.Replace("Review", "PRIVATE");
                        case "no-attempt": return text.Replace("Created\t1", "Created\t0");
                        case "bad-cleanup": return text.Replace("Verified\tClosed", "Verified\tNotOpened");
                        case "bad-escape": return text.Replace("test-user", "bad\\q");
                        default: return text + "PRIVATE\n";
                    }
                };
                var result = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, result.Outcome); Assert.IsFalse(result.AutomaticRetryAllowed);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await result.LateCompletion);
                Assert.AreEqual(1, f.Files.Sessions.OpenCalls); Assert.IsFalse(string.Join(" ", f.Files.Log.Entries).Contains("PRIVATE"));
            }
        }
        [DataTestMethod, DataRow("TargetExists"), DataRow("UnsavedChanges"), DataRow("AlreadyOpenOrLocked"), DataRow("NumberingChanged"), DataRow("SessionChanged")]
        public async Task NativeGuardBlocksWithoutCreateAttempt(string reason)
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); f.Alter = t => t.Replace("Created\t1\tVerified\tClosed", "Blocked\t0\t" + reason + "\tNotOpened");
                var result = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.Blocked, result.Outcome); Assert.IsFalse(result.WriteAttempted); StringAssert.Contains(result.Reason, reason);
            }
        }
        [TestMethod]
        public async Task DirtyOwnedModuleLeftOpenRequiresInspectionAndQuarantinesSession()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); f.Alter = t => t.Replace("Created\t1\tVerified\tClosed", "Blocked\t0\tUnsavedChanges\tNeedsInspection");
                var result = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, result.Outcome); Assert.IsFalse(result.WriteAttempted);
                await f.Prepare(); var blocked = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, blocked.Outcome); Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task TimeoutKeepsGateAndLateCompletionAndRequiresExplicitReconciliation()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(timeout: TimeSpan.FromMilliseconds(80)); f.Files.Sessions.Session.BlockRun = true;
                var pending = f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.IsTrue(f.Files.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                var uncertain = await pending;
                Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, uncertain.Outcome); Assert.IsTrue(f.Executor.IsBusy);
                Assert.IsFalse(uncertain.LateCompletion.IsCompleted); Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
                f.Files.Sessions.Session.ReleaseRun.Set(); var late = await uncertain.LateCompletion;
                Assert.AreEqual(BaselineWriteOutcome.Created, late.Outcome); Assert.IsFalse(f.Executor.IsBusy);
                await f.Prepare(); var denied = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, denied.Outcome); Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
                f.Files.Sessions.Session.BlockRun = false;
                f.Files.Sessions.Session.OnRun = s => s.Payload = "DOORS_BASELINE_RECONCILE\t1\t" + s.Prefix.Split('|')[2] + "\tVERIFIED\n";
                Assert.AreEqual(BaselineReconciliationStatus.Verified, await f.Adapter.ReconcileAsync(uncertain));
                Assert.AreEqual(2, f.Files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task QueueDelayCannotDispatchExpiredApproval()
        {
            using (var blocker = new ExecutorTestContext()) using (var f = new BaselineExecutionTransportFixture())
            {
                blocker.Sessions.Session.BlockRun = true;
                var first = BaselineExecutionTransportFixture.Queued(blocker).Start(new DxlExecutionRequest(blocker.SourcePath));
                Assert.IsTrue(blocker.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                await f.Prepare(); var pending = f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                f.Preflight.Clock.Offset = TimeSpan.FromMinutes(3); blocker.Sessions.Session.ReleaseRun.Set(); using (await first.Completion) { }
                var result = await pending;
                Assert.AreEqual(0, f.Files.Sessions.OpenCalls); Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, result.Outcome);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await result.LateCompletion);
            }
        }
        [TestMethod]
        public async Task CancelingWaitDoesNotCancelNativeWriteOrPermitAnotherDispatch()
        {
            using (var f = new BaselineExecutionTransportFixture()) using (var token = new CancellationTokenSource())
            {
                await f.Prepare(); f.Files.Sessions.Session.BlockRun = true;
                var pending = f.Adapter.ExecuteBaselineAsync(f.Authorization, token.Token);
                Assert.IsTrue(f.Files.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5))); token.Cancel();
                var result = await pending; Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, result.Outcome);
                Assert.IsTrue(f.Executor.IsBusy); Assert.IsFalse(result.LateCompletion.IsCompleted);
                f.Files.Sessions.Session.ReleaseRun.Set(); Assert.AreEqual(BaselineWriteOutcome.Created, (await result.LateCompletion).Outcome);
                Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
            }
        }
        [DataTestMethod, DataRow("NOT_FOUND_OR_UNREADABLE", BaselineReconciliationStatus.NotFoundOrUnreadable),
            DataRow("CONFLICT", BaselineReconciliationStatus.Conflict), DataRow("UNKNOWN", BaselineReconciliationStatus.Unknown)]
        public async Task NegativeReconciliationNeverAuthorizesRetry(string response, BaselineReconciliationStatus expected)
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); f.Alter = text => text.Replace("Created\t1\tVerified", "NeedsReconciliation\t1\tCreateOrVerificationUncertain");
                var uncertain = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, uncertain.Outcome);
                f.Files.Sessions.Session.OnRun = s => s.Payload = "DOORS_BASELINE_RECONCILE\t1\t" + s.Prefix.Split('|')[2] + "\t" + response + "\n";
                Assert.AreEqual(expected, await f.Adapter.ReconcileAsync(uncertain));
                await f.Prepare(); var blocked = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, blocked.Outcome); Assert.AreEqual(2, f.Files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task BusyGateIsKnownNotDispatchedAndDoesNotScheduleRetry()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); using (f.Files.Gate.Enter())
                {
                    var result = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                    Assert.AreEqual(BaselineWriteOutcome.NotRun, result.Outcome); Assert.IsFalse(result.WriteAttempted);
                    Assert.AreEqual(0, f.Files.Sessions.OpenCalls);
                }
                Assert.IsFalse(f.Authorization.TryBeginWrite());
            }
        }
        [TestMethod]
        public async Task ParametersAreEscapedByExistingTransportNotConcatenatedIntoDxl()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                string text = "\"; print 99; //\nç 注釈 \\"; await f.Prepare(annotation: text);
                var result = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.Created, result.Outcome);
                StringAssert.Contains(f.Files.Sessions.Session.Script, "di_arg_annotation = " + DxlStringLiteral.Encode(text));
                Assert.AreEqual(text, result.Annotation);
            }
        }
        [TestMethod]
        public async Task ReviewedCompatibilityPinsExactScriptHashesDatabaseUserAndVersion()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
                var profile = new ReviewedBaselineExecutionCompatibility(DoorsVersion.V97, "database-1", "test-user", Hash(f.Scripts.ExecutePath), Hash(f.Scripts.ReconcilePath), "Reviewed 9.7 manual sections", "Fake unit-test evidence only", "9.7.2.0");
                Assert.IsTrue(profile.IsValidated(f.Authorization, f.Scripts));
                var other = new ReviewedBaselineExecutionCompatibility(DoorsVersion.V97, "other-db", "test-user", Hash(f.Scripts.ExecutePath), Hash(f.Scripts.ReconcilePath), "review", "test", "9.7.2.0");
                Assert.IsFalse(other.IsValidated(f.Authorization, f.Scripts));
                string original = f.Scripts.ExecutePath; string copy = Path.Combine(f.Files.Root, "changed.dxl"); File.WriteAllText(copy, "changed"); f.Scripts.ExecutePath = copy;
                Assert.IsFalse(profile.IsValidated(f.Authorization, f.Scripts)); f.Scripts.ExecutePath = original;
                Assert.ThrowsException<ArgumentException>(() => new ReviewedBaselineExecutionCompatibility(DoorsVersion.V95, "db", "user", new string('0', 64), new string('0', 64), "review", "test", "9.7.2.0"));
            }
        }
        [TestMethod]
        public async Task MissingDeploymentScriptIsKnownNotRunBeforeCom()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); f.Scripts.ExecutePath = Path.Combine(f.Files.Root, "missing.dxl");
                var profile = new ReviewedBaselineExecutionCompatibility(DoorsVersion.V97, "database-1", "test-user", new string('0', 64), new string('0', 64), "manual", "unit only", "9.7.2.0");
                var result = await new DxlBaselineExecutionAdapter(f.Executor, f.Scripts, profile, f.Files.Log).ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, result.Outcome); Assert.IsFalse(result.WriteAttempted); Assert.AreEqual(0, f.Files.Sessions.OpenCalls);
            }
        }
        [DataTestMethod, DataRow("exact", true), DataRow("foreign-job", false), DataRow("stale", false), DataRow("foreign-source", false)]
        public async Task DurableRunnerRetryCanRearmOnlySameDrainedTargetWithFreshApprovedEvidence(string mode, bool allowed)
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(); f.Alter = text => text.Replace("Created\t1\tVerified", "NeedsReconciliation\t1\tCreateOrVerificationUncertain");
                var uncertain = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NeedsReconciliation, uncertain.Outcome);
                var previous = f.Authorization; var item = previous.Item; var facts = item.Precondition.Facts;
                var proof = new BaselineJobVerification(item.Module, item.ExpectedVersion, item.Annotation, facts.DatabaseIdentity, facts.CurrentUserId,
                    mode == "foreign-source" ? BaselineMetadataSource.Mock : BaselineMetadataSource.NativeDxl, BaselineJobVerificationStatus.AbsentAuthoritatively,
                    DateTime.UtcNow.AddMinutes(mode == "stale" ? -10 : 0), "FAKE approved durable-runner recovery proof; not live validation.");
                var review = BaselineExecutionTransportFixture.Internal<BaselineExecutionReview>(previous.Review.Plan, mode == "foreign-job" ? Guid.NewGuid() : previous.Review.JobId);
                f.Authorization = BaselineExecutionTransportFixture.Internal<BaselineExecutionAuthorization>(review, item, f.Preflight.Clock, proof);
                f.Alter = null; var retried = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(allowed ? BaselineWriteOutcome.Created : BaselineWriteOutcome.NotRun, retried.Outcome);
                Assert.AreEqual(allowed ? 2 : 1, f.Files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task ApprovedRetryEvidenceCannotRearmAStillRunningComCall()
        {
            using (var f = new BaselineExecutionTransportFixture())
            {
                await f.Prepare(timeout: TimeSpan.FromMilliseconds(80)); f.Files.Sessions.Session.BlockRun = true;
                var pending = f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.IsTrue(f.Files.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5))); var uncertain = await pending;
                var old = f.Authorization; var item = old.Item; var facts = item.Precondition.Facts;
                var proof = new BaselineJobVerification(item.Module, item.ExpectedVersion, item.Annotation, facts.DatabaseIdentity, facts.CurrentUserId,
                    BaselineMetadataSource.NativeDxl, BaselineJobVerificationStatus.AbsentAuthoritatively, DateTime.UtcNow, "Fake proof cannot override the actual active COM gate.");
                f.Authorization = BaselineExecutionTransportFixture.Internal<BaselineExecutionAuthorization>(old.Review, item, f.Preflight.Clock, proof);
                var denied = await f.Adapter.ExecuteBaselineAsync(f.Authorization, CancellationToken.None);
                Assert.AreEqual(BaselineWriteOutcome.NotRun, denied.Outcome); Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
                f.Files.Sessions.Session.ReleaseRun.Set(); Assert.AreEqual(BaselineWriteOutcome.Created, (await uncertain.LateCompletion).Outcome);
            }
        }
    }
}
