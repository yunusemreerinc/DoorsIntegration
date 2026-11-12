using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class SetTestConfirmation : IBaselineSetConfirmation
    {
        internal bool Approved = true; internal int Calls; internal Action OnConfirm;
        public Task<bool> ConfirmAsync(BaselineSetPreflight plan, bool second, CancellationToken token) { Calls++; OnConfirm?.Invoke(); return Task.FromResult(Approved); }
    }
    internal sealed class SetFixture : IDisposable
    {
        internal readonly JobTestFixture Files = new JobTestFixture(1);
        internal readonly SetTestConfirmation Consent = new SetTestConfirmation();
        internal readonly TestBaselineJournalProtector Protector = new TestBaselineJournalProtector();
        internal readonly BaselineModuleReference[] Modules = new[] { new BaselineModuleReference("repo", "A", "/Project/Folder1/A"), new BaselineModuleReference("repo", "B", "/Other/Folder2/B") };
        internal MockNativeBaselineSetAdapter Adapter;
        internal FileBaselineSetJobRepository Repository;
        internal NativeBaselineSetService Service;
        internal bool Allowed = true;
        internal SetFixture() { Restart(); }
        internal void Restart()
        {
            Adapter = new MockNativeBaselineSetAdapter(Path.Combine(Files.Root, "mock-state.bin"), Protector);
            Repository = new FileBaselineSetJobRepository(new BulkBaselineJournalOptions(Path.Combine(Files.Root, "set-jobs")), Protector);
            Service = new NativeBaselineSetService(Adapter, Adapter, Repository, Consent, new BaselineTestLog(), () => Allowed);
        }
        internal BaselineSetRequest Request(BaselineSetAction action, string definition = "", string set = "", BaselineModuleReference[] modules = null, string suffix = "REVIEW") => new BaselineSetRequest(action, "/Project", definition, set, "Definition", "Description 注釈", modules, suffix, "Annotation Türkçe", DoorsVersion.V97);
        internal async Task<BaselineSetJob> Run(BaselineSetRequest request)
        { var plan = await Service.PreflightAsync(request); Assert.IsTrue(plan.Ready, string.Join("\n", plan.Problems)); return await Service.ExecuteAsync(plan); }
        internal Task<BaselineSetJob> Definition() => Run(Request(BaselineSetAction.CreateDefinition, modules: Modules));
        public void Dispose() => Files.Dispose();
    }
    [TestClass, TestCategory("NativeBaselineSetMock")]
    public class NativeBaselineSetTests
    {
        [TestMethod]
        public async Task DefinitionCrudUsesMultiFolderStableIdsAndDeduplicates()
        {
            using (var f = new SetFixture())
            {
                var created = await f.Run(f.Request(BaselineSetAction.CreateDefinition, modules: new[] { f.Modules[0], f.Modules[0] }));
                Assert.AreEqual(BaselineSetJobStatus.Succeeded, created.Status);
                await f.Run(f.Request(BaselineSetAction.AddDefinitionModules, created.DefinitionId, modules: f.Modules));
                Assert.AreEqual(2, (await f.Service.ListDefinitionModules("/Project", created.DefinitionId, DoorsVersion.V97)).Count);
                var crossRepository = await f.Service.PreflightAsync(f.Request(BaselineSetAction.AddDefinitionModules, created.DefinitionId,
                    modules: new[] { new BaselineModuleReference("another-repository", "C", "/Other/C") }));
                Assert.IsFalse(crossRepository.Ready, "Different folders/projects may share a definition; different database scopes may not.");
                await f.Run(new BaselineSetRequest(BaselineSetAction.UpdateDefinition, "/Project", created.DefinitionId, name: "Renamed", description: "Changed"));
                Assert.AreEqual("Renamed", (await f.Service.GetBaselineSetDefinition("/Project", created.DefinitionId, DoorsVersion.V97)).Name);
                await f.Run(f.Request(BaselineSetAction.RemoveDefinitionModules, created.DefinitionId, modules: new[] { f.Modules[1] }));
                Assert.AreEqual(1, (await f.Service.ListDefinitionModules("/Project", created.DefinitionId, DoorsVersion.V97)).Count);
                Assert.AreEqual(1, (await f.Service.ListBaselineSetDefinitions("/Project", DoorsVersion.V97)).Count);
            }
        }
        [DataTestMethod, DataRow(BaselineSetAction.CreateMajorSet, 1, 0), DataRow(BaselineSetAction.CreateMinorSet, 0, 1)]
        public async Task CompleteWorkflowSeparatesSetVersionFromModuleVersion(BaselineSetAction action, int major, int minor)
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); var opened = await f.Run(f.Request(action, d.DefinitionId));
                var set = await f.Service.GetOpenBaselineSet("/Project", d.DefinitionId, DoorsVersion.V97);
                Assert.AreEqual(new NativeBaselineSetVersion(major, minor, "REVIEW"), set.Version);
                var populated = await f.Run(f.Request(BaselineSetAction.AddModuleBaselines, d.DefinitionId, opened.SetId, f.Modules));
                Assert.AreEqual(BaselineSetJobStatus.Succeeded, populated.Status); Assert.IsTrue(populated.LinkVerification.Verified); Assert.AreEqual(0, populated.MissingModules.Count);
                set = await f.Service.GetBaselineSetDetails("/Project", d.DefinitionId, opened.SetId, DoorsVersion.V97);
                Assert.IsTrue(set.IsOpen); Assert.IsTrue(set.Members.All(m => m.ModuleBaselineVersion == "1.5[REVIEW]"));
                var closed = await f.Run(f.Request(BaselineSetAction.CloseSet, d.DefinitionId, opened.SetId));
                Assert.AreEqual(BaselineSetJobStatus.Succeeded, closed.Status); Assert.IsTrue(closed.LinkVerification.Verified);
                Assert.IsNull(await f.Service.GetOpenBaselineSet("/Project", d.DefinitionId, DoorsVersion.V97));
            }
        }
        [DataTestMethod, DataRow(BaselineSetAction.CreateMajorSet), DataRow(BaselineSetAction.CreateMinorSet), DataRow(BaselineSetAction.UpdateDefinition), DataRow(BaselineSetAction.AddDefinitionModules), DataRow(BaselineSetAction.RemoveDefinitionModules)]
        public async Task OpenSetBlocksSecondSetAndDefinitionChanges(BaselineSetAction action)
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); await f.Run(f.Request(BaselineSetAction.CreateMajorSet, d.DefinitionId)); int writes = f.Adapter.Writes;
                var p = await f.Service.PreflightAsync(f.Request(action, d.DefinitionId, modules: f.Modules));
                Assert.IsFalse(p.Ready); Assert.IsTrue(p.Problems.Any(x => x.Contains("open set"))); Assert.AreEqual(writes, f.Adapter.Writes);
            }
        }
        [TestMethod]
        public async Task PartialPopulationStaysOpenAndReportsMissingWithoutAutomaticClose()
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); var s = await f.Run(f.Request(BaselineSetAction.CreateMajorSet, d.DefinitionId)); f.Adapter.FailModuleId = "B";
                var result = await f.Run(f.Request(BaselineSetAction.AddModuleBaselines, d.DefinitionId, s.SetId, f.Modules));
                Assert.AreEqual(BaselineSetJobStatus.Partial, result.Status); CollectionAssert.AreEqual(new[] { "B" }, result.MissingModules.ToArray());
                Assert.IsTrue((await f.Service.GetOpenBaselineSet("/Project", d.DefinitionId, DoorsVersion.V97)).IsOpen);
                Assert.IsFalse((await f.Service.PreflightAsync(f.Request(BaselineSetAction.CloseSet, d.DefinitionId, s.SetId))).Ready);
                f.Adapter.FailModuleId = null;
                var completed = await f.Run(f.Request(BaselineSetAction.AddModuleBaselines, d.DefinitionId, s.SetId, new[] { f.Modules[1] })); Assert.AreEqual(0, completed.MissingModules.Count);
                Assert.AreEqual(2, completed.After.Sets.Single().Members.Count);
            }
        }
        [TestMethod]
        public async Task UnknownCommitSurvivesRestartAndReconcilesWithoutRepeatingCreate()
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); f.Adapter.ReturnUnknownAfterCommit = true;
                var unknown = await f.Run(f.Request(BaselineSetAction.CreateMajorSet, d.DefinitionId)); Assert.AreEqual(BaselineSetJobStatus.Unknown, unknown.Status);
                f.Restart(); var result = await f.Service.ReconcileAsync(unknown.JobId);
                Assert.AreEqual(BaselineSetJobStatus.Succeeded, result.Status); Assert.AreEqual(0, f.Adapter.Writes); Assert.AreEqual(1, (await f.Service.ListBaselineSets("/Project", d.DefinitionId, DoorsVersion.V97)).Count);
                await f.Service.ReconcileAsync(unknown.JobId); Assert.AreEqual(0, f.Adapter.Writes);
            }
        }
        [DataTestMethod, DataRow("permission"), DataRow("lock"), DataRow("dirty"), DataRow("shareable"), DataRow("unknown-shareable"), DataRow("shared-mode")]
        public async Task PreflightBlocksUnsafeOrUnknownConfiguration(string problem)
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition();
                if (problem == "permission") f.Adapter.CanModify = false;
                else if (problem == "lock") f.Adapter.LockedByOther = true;
                else if (problem == "dirty") f.Adapter.DirtyDefinition = true;
                else f.Adapter.ModulePolicies["A"] = new MockBaselineSetModulePolicy { ShareableConfigured = problem == "shareable" ? BaselineCheckState.Passed : problem == "unknown-shareable" ? BaselineCheckState.Unknown : BaselineCheckState.Failed, EditState = problem == "shared-mode" ? BaselineEditState.SharedOtherSession : BaselineEditState.ExclusiveCurrentSession };
                Assert.IsFalse((await f.Service.PreflightAsync(f.Request(BaselineSetAction.CreateMinorSet, d.DefinitionId))).Ready); Assert.AreEqual(1, f.Adapter.Writes);
            }
        }
        [TestMethod]
        public async Task LinkLossAfterCloseIsReportedAndNeverRolledBack()
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); var s = await f.Run(f.Request(BaselineSetAction.CreateMajorSet, d.DefinitionId)); await f.Run(f.Request(BaselineSetAction.AddModuleBaselines, d.DefinitionId, s.SetId, f.Modules));
                f.Adapter.LoseLinkAfterClose = true; var closed = await f.Run(f.Request(BaselineSetAction.CloseSet, d.DefinitionId, s.SetId));
                Assert.AreEqual(BaselineSetJobStatus.NeedsReconciliation, closed.Status); Assert.AreEqual(1, closed.LinkVerification.Missing.Count);
                Assert.IsFalse((await f.Service.GetBaselineSetDetails("/Project", d.DefinitionId, s.SetId, DoorsVersion.V97)).IsOpen);
            }
        }
        [TestMethod]
        public async Task ApprovalReadonlyTamperAndFreshnessGatesPreventDispatch()
        {
            using (var f = new SetFixture())
            {
                var p = await f.Service.PreflightAsync(f.Request(BaselineSetAction.CreateDefinition, modules: f.Modules)); f.Consent.Approved = false;
                Assert.AreEqual(BaselineSetJobStatus.Declined, (await f.Service.ExecuteAsync(p)).Status); Assert.AreEqual(0, f.Adapter.Writes);
                f.Consent.Approved = true; f.Allowed = false; await Assert.ThrowsExceptionAsync<IntegrationException>(() => f.Service.ExecuteAsync(p)); f.Allowed = true;
                f.Consent.OnConfirm = () => f.Adapter.LockedByOther = true; Assert.AreEqual(BaselineSetJobStatus.Blocked, (await f.Service.ExecuteAsync(p)).Status);
                f.Adapter.LockedByOther = false; p.Request.Modules.Clear(); await Assert.ThrowsExceptionAsync<IntegrationException>(() => f.Service.ExecuteAsync(p)); Assert.AreEqual(0, f.Adapter.Writes);
            }
        }
        [TestMethod]
        public async Task CompletedJournalFramesAreAppendOnlyAndCorruptionFailsClosed()
        {
            using (var f = new SetFixture())
            {
                var job = await f.Definition(); string root = Path.Combine(f.Files.Root, "set-jobs", "set-" + job.JobId.ToString("N")); var frames = Directory.GetFiles(root, "*.json").OrderBy(x => x).ToArray(); var first = File.ReadAllBytes(frames[0]);
                await f.Service.ReconcileAsync(job.JobId); CollectionAssert.AreEqual(first, File.ReadAllBytes(frames[0]));
                File.WriteAllText(frames[0], "invalid"); Assert.ThrowsException<BulkBaselineJournalException>(() => f.Service.GetJob(job.JobId));
            }
        }
    }
}
