using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ProcessingDoorsAdapterTests
    {
        [TestMethod]
        public async Task NativeAdapterRepreviewsEachRecordAndRetainsExplicitCoreApproval()
        {
            using (var native = new RequirementWriteTestContext())
            {
                var adapter = new DoorsBatchChangeAdapter(native.Service, new DoorsBatchAdapterOptions()); var change = Change(native.Request); var scope = new BatchOptions("db-1", "test-import");
                var inspection = await adapter.InspectAsync(change, scope, CancellationToken.None); Assert.IsTrue(inspection.CanApply); Assert.AreEqual(0, native.Confirmation.Calls);
                native.State = "changed-by-an-earlier-batch-row";
                native.Confirmation.BeforeReturn = fresh => Assert.AreNotEqual(inspection.ReviewStamp, fresh.ApprovalFingerprint);
                var denied = await adapter.ApplyAsync(change, inspection, scope, CancellationToken.None);
                Assert.AreEqual(BatchMutation.None, denied.Mutation); Assert.IsFalse(native.Modes.Contains("commit"));
                native.Confirmation.Approved = true;
                var result = await adapter.ApplyAsync(change, inspection, scope, CancellationToken.None);
                Assert.AreEqual(BatchMutation.Applied, result.Mutation); Assert.AreEqual("42", result.ResultingObjectId);
                Assert.AreEqual(2, native.Confirmation.Calls); Assert.AreEqual(1, native.Modes.Count(m => m == "commit"));
            }
        }
        [TestMethod]
        public async Task NativeAdapterRejectsWrongDatabaseAndActorDriftBeforeWriteApproval()
        {
            using (var native = new RequirementWriteTestContext())
            {
                var adapter = new DoorsBatchChangeAdapter(native.Service, new DoorsBatchAdapterOptions()); var change = Change(native.Request);
                Assert.IsFalse((await adapter.InspectAsync(change, new BatchOptions("wrong-db", "job"), CancellationToken.None)).CanApply);
                var scope = new BatchOptions("db-1", "job"); var inspection = await adapter.InspectAsync(change, scope, CancellationToken.None); native.Actor = "other-user";
                var result = await adapter.ApplyAsync(change, inspection, scope, CancellationToken.None);
                Assert.AreEqual("NativeContextChanged", result.Code); Assert.AreEqual(BatchMutation.None, result.Mutation); Assert.AreEqual(0, native.Confirmation.Calls);
            }
        }
        [TestMethod]
        public void SnapshotAdapterIsOfflinePreservesRichDataAndDoesNotGuessBaselineLabels()
        {
            using (var snapshot = new WpfWorkspaceRequirementSnapshot(new RequirementReadRequest("/P/M", baseline: new RequirementBaseline(1, 0)), 1))
            {
                var unknown = ProcessingSnapshotAdapter.Requirements(snapshot, "caller-db").Single();
                Assert.AreEqual(ArtifactVersion.Unknown, unknown.Identity.VersionKind); Assert.AreEqual("{\\b Heading}", unknown.Fields["Object Heading"].OpaqueRichText);
                var exact = ProcessingSnapshotAdapter.Nodes(snapshot, "caller-db", "1.0").Single(); Assert.AreEqual(ArtifactVersion.Baseline, exact.Identity.VersionKind); Assert.AreEqual("1.0", exact.Identity.Version);
            }
        }
        [TestMethod]
        public void ConsoleHostResolvesIndependentAnalysisAndFailClosedBatchConfirmation()
        {
            foreach (bool desktop in new[] { false })
            {
                var services = new ServiceCollection();
                DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                {
                    Assert.IsNotNull(provider.GetRequiredService<QualityEngine>()); Assert.IsNotNull(provider.GetRequiredService<TraceabilityCoverageService>());
                    Assert.IsNotNull(provider.GetRequiredService<BatchService>()); Assert.IsNotNull(provider.GetRequiredService<DoorsBatchServiceFactory>());
                    Assert.IsInstanceOfType(provider.GetRequiredService<IBatchConfirmation>(), typeof(RejectBatchConfirmation));
                }
            }
        }
        private static BatchChange Change(RequirementWriteRequest request) => new BatchChange("record-1", request.IsCreate ? BatchAction.Create : BatchAction.Update, request.Target.ModulePath,
            request.Changes.Select(a => new BatchAttribute(a.Name, a.Value, a.Format == RequirementWriteFormat.PlainText ? BatchValueFormat.PlainText : BatchValueFormat.OpaqueRichText, a.ExpectedPlainText)),
            request.AbsoluteNumber?.ToString(), (BatchPosition)Enum.Parse(typeof(BatchPosition), request.Position.Kind.ToString()), request.Position.AnchorAbsoluteNumber?.ToString(), request.RequiredAttributes);
    }
}
