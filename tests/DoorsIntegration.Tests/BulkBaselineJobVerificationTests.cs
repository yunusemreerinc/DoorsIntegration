using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BulkBaselineJobVerificationTests
    {
        [TestMethod]
        public async Task DefaultReadOnlyVerifierCanProveMockAbsenceWithIndependentVisibilityEvidence()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var verifier = new BaselineJobStateVerifier(f.Preflight.Metadata, f.Preflight.Reader, f.Preflight.Clock);
                var result = await verifier.VerifyAsync(job, job.Modules[0], CancellationToken.None);
                Assert.AreEqual(BaselineJobVerificationStatus.AbsentAuthoritatively, result.Status); Assert.AreEqual(BaselineMetadataSource.Mock, result.Source);
                Assert.AreEqual(job.Modules[0].ExpectedVersion, result.ExpectedVersion); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [DataTestMethod, DataRow("no-visibility"), DataRow("lock"), DataRow("dirty"), DataRow("no-revision"), DataRow("session"), DataRow("database")]
        public async Task NegativeBaselineExistsWithoutExactQuiescentEvidenceCannotAuthorizeRetry(string fault)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var state = f.Preflight.States[0];
                switch (fault)
                {
                    case "no-visibility": state.TargetAvailability = BaselineTargetAvailability.Unknown; break;
                    case "lock": PreflightTestFixture.LockOther(state); break;
                    case "dirty": state.UnsavedChanges = BaselineUnsavedState.CurrentSession; break;
                    case "no-revision": state.RevisionsReliable = BaselineCheckState.Unknown; break;
                    case "session": state.SessionValid = BaselineCheckState.Unknown; break;
                    default: state.DatabaseIdentity = "foreign-db"; break;
                }
                var result = await new BaselineJobStateVerifier(f.Preflight.Metadata, f.Preflight.Reader, f.Preflight.Clock).VerifyAsync(job, job.Modules[0], CancellationToken.None);
                Assert.AreEqual(BaselineJobVerificationStatus.Unknown, result.Status);
            }
        }
        [DataTestMethod, DataRow(false), DataRow(true)]
        public async Task ExistingBaselineChecksAnnotationCreatorAndStableIdentity(bool conflict)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var item = job.Modules[0]; var module = f.Preflight.States[0].Module;
                var entry = new BaselineHistoryEntry(new BaselineIdentity(module, item.ExpectedVersion), conflict ? "Other annotation" : item.Annotation, item.UserId, DateTime.UtcNow, "raw date");
                f.Preflight.Metadata.Modules[module.ModuleId] = new MockBaselineModuleState(module, new[] { entry }, item.ExpectedVersion, new BaselineVersion(4, 0), new BaselineVersion(3, 9));
                f.Preflight.States[0].TargetAvailability = BaselineTargetAvailability.Exists;
                var result = await new BaselineJobStateVerifier(f.Preflight.Metadata, f.Preflight.Reader, f.Preflight.Clock).VerifyAsync(job, item, CancellationToken.None);
                Assert.AreEqual(conflict ? BaselineJobVerificationStatus.ExistsConflicting : BaselineJobVerificationStatus.ExistsMatching, result.Status);
            }
        }
        [DataTestMethod, DataRow("source"), DataRow("module"), DataRow("version"), DataRow("annotation"), DataRow("stale"), DataRow("future")]
        public async Task RunnerRejectsForeignOrStaleVerificationEvidence(string fault)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Server.Override = (record, item) => new BaselineJobVerification(
                    fault == "module" ? new BaselineModuleReference(item.RepositoryId, "foreign-id", item.CanonicalFullPath) : item.Module,
                    fault == "version" ? new BaselineVersion(99, 0) : item.ExpectedVersion, fault == "annotation" ? "Other" : item.Annotation,
                    item.DatabaseIdentity, item.UserId, fault == "source" ? BaselineMetadataSource.NativeDxl : BaselineMetadataSource.Mock,
                    BaselineJobVerificationStatus.AbsentAuthoritatively, DateTime.UtcNow.AddMinutes(fault == "stale" ? -10 : fault == "future" ? 10 : 0), "Altered fixture evidence.");
                var result = await f.Runner().RunAsync(job.JobId); Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, result.Status); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task PermissionsChangedDuringApprovalBlockDispatch()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Confirmation.OnConfirm = _ => PreflightTestFixture.LockOther(f.Preflight.States[0]);
                var result = await f.Runner().RunAsync(job.JobId); Assert.AreEqual(BulkBaselineModuleStatus.Skipped, result.Modules[0].Status); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task RetryCapabilityIsBoundToPostApprovalFreshEvidence()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Adapter.Outcome = _ => BaselineWriteOutcome.NotRun; await f.Runner().RunAsync(job.JobId);
                f.Adapter.Outcome = null; f.Adapter.BeforeExecute = auth =>
                {
                    Assert.IsNotNull(auth.RetryVerification); Assert.IsTrue(auth.HasFreshAuthoritativeRetryVerification);
                    Assert.AreEqual(auth.Review.JobId, job.JobId); Assert.AreEqual(auth.Item.ExpectedVersion, auth.RetryVerification.ExpectedVersion);
                    return Task.CompletedTask;
                };
                int reads = f.Server.Reads; var result = await f.Runner().RetryAsync(job.JobId, job.Modules[0].ModuleId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, result.Status); Assert.IsTrue(f.Server.Reads >= reads + 2);
            }
        }
        [TestMethod]
        public async Task MetadataThatExpiresWhileStateIsReadCannotAuthorizeRetry()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Preflight.Reader.OnRead = (probe, call) =>
                { f.Preflight.Clock.Offset = TimeSpan.FromMinutes(3); return f.Preflight.States[0].ToFacts(); };
                var result = await new BaselineJobStateVerifier(f.Preflight.Metadata, f.Preflight.Reader, f.Preflight.Clock).VerifyAsync(job, job.Modules[0], CancellationToken.None);
                Assert.AreEqual(BaselineJobVerificationStatus.Unknown, result.Status); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
    }
}
