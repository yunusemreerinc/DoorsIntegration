using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ModuleCreationTests
    {
        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(" ")]
        [DataRow(" trailing ")]
        [DataRow(".")]
        [DataRow("..")]
        [DataRow("A/B")]
        [DataRow("A\\B")]
        [DataRow("A\nB")]
        [DataRow("A\0B")]
        [DataRow("A\u2028B")]
        public void InvalidNameIsRejectedBeforePreview(string name)
        {
            Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration,
                Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", name, "")).Code);
        }

        [TestMethod]
        public void RequestRejectsInvalidDescriptionUnicodeScopeAndLimits()
        {
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", "N", null));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", "N", "\0"));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", "N", "\uD800"));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", "N", new string('a', 65537)));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", new string('a', 256), ""));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("Test", "N", ""));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", "N", "", timeout: TimeSpan.Zero));
            Assert.ThrowsException<IntegrationException>(() => new FormalModuleCreateRequest("/Test", "N", "", connectionMode: (DoorsConnectionMode)99));
            Assert.AreEqual("/N", new FormalModuleCreateRequest("/", "N", "Unicode 👋\r\nA\tB").FullPath);
        }

        [TestMethod]
        public async Task PreviewIsReadOnlyAndCannotBeSentDirectlyToCommit()
        {
            using (var context = new ModuleCreationTestContext())
            {
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                Assert.IsTrue(preview.CanCreate);
                Assert.AreEqual(context.Request, preview.Request);
                Assert.IsTrue(preview.ExpiresAtUtc > DateTime.UtcNow);
                Assert.AreEqual(64, preview.ApprovalFingerprint.Length);
                Assert.AreEqual(0, context.Confirmation.Calls);
                Assert.AreEqual("preview", context.Modes.Single());
                Assert.AreEqual("", DiscoveryTestContext.Parameter(context.Transport.Sessions.Session.Script, "approvedPlan"));
                Assert.AreEqual(DoorsConnectionMode.AttachExisting, context.Transport.Sessions.Mode);
                var rejected = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Gateway.CommitAsync(preview, CancellationToken.None));
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationApprovalRequired, rejected.Code);
                Assert.AreEqual(1, context.Modes.Count);
            }
        }

        [TestMethod]
        public async Task DeclinedOrMissingConfirmationNeverDispatchesAWrite()
        {
            using (var context = new ModuleCreationTestContext())
            {
                var service = new ModuleService(new RecordingHierarchyReader(), context.Gateway, new RejectModuleCreationConfirmation());
                var preview = await service.PreviewFormalModuleCreationAsync(context.Request);
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await service.CreateFormalModuleAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationApprovalRequired, failure.Code);
                Assert.AreEqual("preview", context.Modes.Single());
                Assert.ThrowsException<IntegrationException>(() => new ModuleService(new RecordingHierarchyReader()).PreviewFormalModuleCreationAsync(context.Request).GetAwaiter().GetResult());
            }
        }

        [DataTestMethod]
        [DataRow("missing-folder")]
        [DataRow("not-container")]
        [DataRow("folder-unavailable")]
        [DataRow("permission-denied")]
        [DataRow("permission-unknown")]
        [DataRow("invalid-name")]
        [DataRow("invalid-description")]
        [DataRow("name-conflict")]
        [DataRow("name-unknown")]
        [DataRow("locked")]
        [DataRow("lock-unknown")]
        [DataRow("missing-identity")]
        public async Task FailedOrUnknownPreflightBlocksBeforeConfirmation(string fault)
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Checks = Fault(fault);
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                Assert.IsFalse(preview.CanCreate);
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationBlocked, failure.Code);
                StringAssert.Contains(failure.Message, preview.Checks.Summary);
                Assert.AreEqual(0, context.Confirmation.Calls);
                Assert.AreEqual("preview", context.Modes.Single());
            }
        }

        [DataTestMethod]
        [DataRow(ModuleCreationCleanup.Closed)]
        [DataRow(ModuleCreationCleanup.Failed)]
        public async Task ApprovalCreatesOnceAndReturnsVerifiedIdentitiesAndCleanup(ModuleCreationCleanup cleanup)
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Cleanup = cleanup;
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                var result = await context.Service.CreateFormalModuleAsync(preview);
                Assert.AreEqual(ModuleCreationOutcome.Created, result.Outcome);
                Assert.AreEqual(context.Request.FullPath, result.FullPath);
                Assert.AreEqual("module-123", result.ModuleId);
                Assert.AreEqual("database-1:module-123", result.QualifiedModuleId);
                Assert.AreEqual(cleanup, result.Cleanup);
                CollectionAssert.AreEqual(new[] { "preview", "commit" }, context.Modes);
                Assert.AreEqual(1, context.Confirmation.Calls);
                StringAssert.Contains(context.Transport.Sessions.Session.Script, "string di_arg_approvedPlan = \"" + preview.PlanId.ToString("N") + "\"");
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Gateway.CommitAsync(preview, CancellationToken.None));
                Assert.AreEqual(2, context.Modes.Count);
            }
        }

        [DataTestMethod]
        [DataRow("permission-denied")]
        [DataRow("name-conflict")]
        [DataRow("locked")]
        [DataRow("lock-unknown")]
        [DataRow("context")]
        [DataRow("database")]
        [DataRow("user")]
        public async Task CommitRechecksChangesAfterApprovalWithoutAutomaticRetry(string change)
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                context.Confirmation.OnConfirm = () => context.CommitChecks = change == "context" ? ModuleCreationTestContext.Ready(folderId: "replaced-folder") :
                    change == "database" ? ModuleCreationTestContext.Ready(databaseId: "other-database") :
                    change == "user" ? ModuleCreationTestContext.Ready(user: "other-user") : Fault(change);
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationBlocked, failure.Code);
                Assert.AreEqual(2, context.Modes.Count);
                Assert.IsFalse(context.Transport.Gate.IsBusy);
            }
        }

        [TestMethod]
        public async Task PreviewFromAnotherServiceAndConcurrentReplayAreRejected()
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                var other = new ModuleService(new RecordingHierarchyReader(), context.Gateway, context.Confirmation);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await other.CreateFormalModuleAsync(preview));
                var first = context.Service.CreateFormalModuleAsync(preview);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                Assert.AreEqual(ModuleCreationOutcome.Created, (await first).Outcome);
                Assert.AreEqual(1, context.Modes.Count(mode => mode == "commit"));
            }
        }

        [TestMethod]
        public async Task FingerprintBindsParametersAndContextButNotRandomPlanId()
        {
            using (var context = new ModuleCreationTestContext())
            {
                var first = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                var second = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                Assert.AreNotEqual(first.PlanId, second.PlanId);
                Assert.AreEqual(first.ApprovalFingerprint, second.ApprovalFingerprint);
                var changed = await context.Service.PreviewFormalModuleCreationAsync(new FormalModuleCreateRequest("/Test", "NewModule", "Other description"));
                Assert.AreNotEqual(first.ApprovalFingerprint, changed.ApprovalFingerprint);
                context.Checks = ModuleCreationTestContext.Ready(databaseId: "other-database");
                changed = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                Assert.AreNotEqual(first.ApprovalFingerprint, changed.ApprovalFingerprint);
            }
        }

        [TestMethod]
        public async Task UnicodeEscapingAndDiagnosticsDoNotExposeCreationContent()
        {
            using (var context = new ModuleCreationTestContext())
            {
                var request = new FormalModuleCreateRequest("/Özel PRIVATE_FOLDER", "PRIVATE_NAME", "Özel 👋 \"; halt; //\r\nPRIVATE_DESCRIPTION\\x");
                var preview = await context.Service.PreviewFormalModuleCreationAsync(request);
                Assert.AreEqual(request.Description, DiscoveryTestContext.Parameter(context.Transport.Sessions.Session.Script, "description"));
                Assert.AreEqual(request.FullPath, preview.Request.FullPath);
                Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("PRIVATE_"));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TimeoutOrCancellationRetainsComAndExposesLateCreationOutcome(bool cancel)
        {
            using (var context = new ModuleCreationTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                context.Confirmation.Approved = true;
                var request = new FormalModuleCreateRequest("/Test", "NewModule", "", timeout: TimeSpan.FromMilliseconds(cancel ? 5000 : 200));
                var preview = await context.Service.PreviewFormalModuleCreationAsync(request);
                context.Transport.Sessions.Session.BlockRun = true;
                context.Transport.Sessions.Session.EnteredRun.Reset();
                var pending = context.Service.CreateFormalModuleAsync(preview, cancellation.Token);
                Assert.IsTrue(context.Transport.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                if (cancel) cancellation.Cancel();
                var failure = await Assert.ThrowsExceptionAsync<ModuleCreationUncertainException>(async () => await pending);
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationOutcomeUnknown, failure.Code);
                Assert.AreEqual(preview.PlanId, failure.PlanId);
                Assert.IsTrue(context.Transport.Gate.IsBusy);
                Assert.IsFalse(failure.Completion.IsCompleted);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                context.Transport.Sessions.Session.ReleaseRun.Set();
                var late = await failure.Completion;
                Assert.AreEqual(ModuleCreationOutcome.Created, late.Outcome);
                Assert.AreEqual("module-123", late.ModuleId);
                Assert.AreEqual(failure.OperationId, late.CorrelationId);
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.AreEqual(1, context.Modes.Count(mode => mode == "commit"));
            }
        }

        [TestMethod]
        public async Task CancellationBeforeConfirmationNeverWrites()
        {
            using (var context = new ModuleCreationTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                cancellation.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await context.Service.CreateFormalModuleAsync(preview, cancellation.Token));
                Assert.AreEqual(0, context.Confirmation.Calls);
                Assert.AreEqual("preview", context.Modes.Single());
            }
        }

        [TestMethod]
        public async Task CancellationDuringConfirmationNeverDispatchesCommit()
        {
            using (var context = new ModuleCreationTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                context.Confirmation.Approved = true;
                context.Confirmation.OnConfirm = cancellation.Cancel;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await context.Service.CreateFormalModuleAsync(preview, cancellation.Token));
                Assert.AreEqual("preview", context.Modes.Single());
            }
        }

        [TestMethod]
        public async Task FailureBeforeGatewayConsumesApprovalRevokesTheCapability()
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                var service = new ModuleService(new RecordingHierarchyReader(), new FailingCommitGateway(context.Gateway), context.Confirmation);
                var preview = await service.PreviewFormalModuleCreationAsync(context.Request);
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await service.CreateFormalModuleAsync(preview));
                var rejected = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Gateway.CommitAsync(preview, CancellationToken.None));
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationApprovalRequired, rejected.Code);
                Assert.AreEqual("preview", context.Modes.Single());
            }
        }

        [TestMethod]
        public async Task ComFailureAfterCommitDispatchIsUncertainAndRetainsItsCompletion()
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                context.Transport.Sessions.Session.RunFailure = new System.Runtime.InteropServices.COMException("PRIVATE_SERVER_MESSAGE", unchecked((int)0x80010105));
                var failure = await Assert.ThrowsExceptionAsync<ModuleCreationUncertainException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await failure.Completion);
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.AreEqual(1, context.Modes.Count(mode => mode == "commit"));
                Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("PRIVATE_"));
            }
        }

        [TestMethod]
        public async Task MissingScriptFailsBeforeComAndDefaultDiDeniesWrites()
        {
            using (var context = new ModuleCreationTestContext())
            {
                var gateway = new DxlModuleCreationGateway(context.Transport.Executor,
                    new ModuleCreationScriptOptions(Path.Combine(context.Transport.Root, "missing.dxl")), context.Transport.Log);
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await gateway.PreviewAsync(context.Request, CancellationToken.None));
                Assert.AreEqual(IntegrationErrorCode.DxlScriptLoadFailed, failure.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
            var services = new ServiceCollection();
            DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
            using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
            {
                Assert.IsInstanceOfType(provider.GetRequiredService<IModuleCreationConfirmation>(), typeof(RejectModuleCreationConfirmation));
                Assert.IsInstanceOfType(provider.GetRequiredService<IFormalModuleCreationGateway>(), typeof(DxlModuleCreationGateway));
            }
        }

        private static FormalModuleCreationChecks Fault(string fault)
        {
            switch (fault)
            {
                case "missing-folder": return ModuleCreationTestContext.Ready(folder: CreationFolderStatus.DeniedOrNotFound);
                case "not-container": return ModuleCreationTestContext.Ready(folder: CreationFolderStatus.NotContainer);
                case "folder-unavailable": return ModuleCreationTestContext.Ready(folder: CreationFolderStatus.Unavailable);
                case "permission-denied": return ModuleCreationTestContext.Ready(permission: CreationPermissionStatus.Denied);
                case "permission-unknown": return ModuleCreationTestContext.Ready(permission: CreationPermissionStatus.Unknown);
                case "invalid-name": return ModuleCreationTestContext.Ready(name: CreationInputStatus.Invalid);
                case "invalid-description": return ModuleCreationTestContext.Ready(description: CreationInputStatus.Invalid);
                case "name-conflict": return ModuleCreationTestContext.Ready(collision: CreationNameStatus.Conflict);
                case "name-unknown": return ModuleCreationTestContext.Ready(collision: CreationNameStatus.Unknown);
                case "locked": return ModuleCreationTestContext.Ready(locks: CreationLockStatus.Locked);
                case "lock-unknown": return ModuleCreationTestContext.Ready(locks: CreationLockStatus.Unknown);
                default: return ModuleCreationTestContext.Ready(databaseId: "");
            }
        }

        private sealed class FailingCommitGateway : IFormalModuleCreationGateway
        {
            private readonly IFormalModuleCreationGateway reader;
            public FailingCommitGateway(IFormalModuleCreationGateway reader) { this.reader = reader; }
            public Task<FormalModuleCreationChecks> PreviewAsync(FormalModuleCreateRequest request, CancellationToken cancellationToken) => reader.PreviewAsync(request, cancellationToken);
            public Task<FormalModuleCreationResult> CommitAsync(FormalModuleCreationPreview approvedPreview, CancellationToken cancellationToken) => throw new OperationCanceledException();
        }
    }
}
