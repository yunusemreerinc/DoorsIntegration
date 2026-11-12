using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    [TestCategory("DoorsLinkWriteIntegration")]
    public class LinkWriteIntegrationTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod]
        public async Task PreviewExactTestLink_DoesNotCommit()
        {
            RequireMode("link-preview"); var request = Request();
            using (var provider = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider())
            {
                var preview = await provider.GetRequiredService<ILinkService>().PreviewWriteAsync(request);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), preview.Inspection.DatabaseId);
                TestContext.WriteLine("Target=" + request.DisplayTarget + "; Action=" + request.Action + "; Fingerprint=" + preview.ApprovalFingerprint +
                    "; CanApply=" + preview.CanApply + "; Scope=" + preview.Inspection.TraceabilityScope);
                foreach (var issue in preview.Inspection.Issues) TestContext.WriteLine("Issue=" + issue);
                foreach (var edge in preview.Inspection.Before) TestContext.WriteLine("Before=" + edge.TargetModulePath + " #" + edge.TargetAbsoluteNumber +
                    "; ID=" + edge.TargetModuleId + "; Version=" + edge.VersionKind + " " + edge.Version);
            }
        }
        [TestMethod]
        public async Task CreateOneReviewedLinkInExplicitTestDatabase_LeavesLinkInPlace()
        { RequireMode("link-create"); await CommitAsync(LinkWriteAction.Create); }
        [TestMethod]
        public async Task RemoveOneReviewedLinkInExplicitTestDatabase_RequiresSeparateDeletionConsent()
        { RequireMode("link-remove"); await CommitAsync(LinkWriteAction.Remove); }
        private async Task CommitAsync(LinkWriteAction action)
        {
            var request = Request(); Assert.AreEqual(action, request.Action);
            string fingerprint = Required("DOORS_LINK_APPROVED_FINGERPRINT");
            Assert.AreEqual("APPLY " + fingerprint, Required("DOORS_LINK_WRITE_CONFIRMATION"));
            if (action == LinkWriteAction.Remove) Assert.AreEqual("DELETE " + request.DisplayTarget, Required("DOORS_LINK_REMOVAL_CONFIRMATION"));
            var services = new ServiceCollection(); services.AddSingleton<ILinkWriteConfirmation>(new ReviewedConfirmation(request.DisplayTarget, fingerprint));
            services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                var service = provider.GetRequiredService<ILinkService>(); var preview = await service.PreviewWriteAsync(request);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), preview.Inspection.DatabaseId);
                Assert.AreEqual(fingerprint, preview.ApprovalFingerprint, "State changed. Review a new dry-run; no mutation dispatched.");
                Assert.IsTrue(preview.CanApply); var result = await service.ApplyWriteAsync(preview);
                TestContext.WriteLine("Outcome=" + result.Outcome + "; Persistence=" + result.Persistence + "; Cleanup=" + result.Cleanup + "; Audit=" + result.AuditRecorded);
                Assert.IsTrue(result.Succeeded, "Inspect DOORS and audit; changes may already exist. Never retry automatically.");
                Assert.IsTrue(result.Comparison.Matches(request, result.TargetModuleId));
                using (var snapshot = await service.ReadAsync(new LinkReadRequest(request.SourceModulePath, request.LinkModulePath,
                    LinkDirection.Outgoing, target: request.ClientVersion)))
                {
                    Assert.AreEqual(RequirementModuleStatus.Available, snapshot.Summary.ModuleStatus);
                    Assert.IsFalse(snapshot.Summary.IsTruncated); Assert.IsFalse(snapshot.Summary.EnumerationFailed);
                    int count = snapshot.ReadLinks().Count(l => l.Source.AbsoluteNumber == request.SourceAbsoluteNumber &&
                        l.Target.ModulePath == request.TargetModulePath && l.Target.AbsoluteNumber == request.TargetAbsoluteNumber && l.Target.VersionKind == LinkVersionKind.Current);
                    Assert.AreEqual(action == LinkWriteAction.Create ? 1 : 0, count);
                }
                // No teardown mutation, restoration, linkset creation, force unlock or automatic retry.
            }
        }
        private static LinkWriteRequest Request()
        {
            string version = Required("DOORS_TEST_VERSION"), action = Required("DOORS_LINK_TEST_ACTION");
            Assert.IsTrue(version == "9.5" || version == "9.7"); Assert.IsTrue(action == "Create" || action == "Remove");
            return new LinkWriteRequest((LinkWriteAction)Enum.Parse(typeof(LinkWriteAction), action), Required("DOORS_LINK_TEST_SOURCE"),
                Positive("DOORS_LINK_TEST_SOURCE_ABSOLUTE"), Required("DOORS_LINK_TEST_TARGET"), Positive("DOORS_LINK_TEST_TARGET_ABSOLUTE"),
                Required("DOORS_LINK_TEST_MODULE"), version == "9.5" ? DoorsVersion.V95 : DoorsVersion.V97);
        }
        private static int Positive(string name)
        { Assert.IsTrue(int.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0); return value; }
        private static string Required(string name)
        { string value = Environment.GetEnvironmentVariable(name); Assert.IsFalse(string.IsNullOrWhiteSpace(value), "Missing setting: " + name); return value; }
        private static void RequireMode(string mode)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != mode)
                Assert.Inconclusive("Live link test disabled. See docs/LINK-WRITING.md for separate preview/write/removal consent.");
        }
        private sealed class ReviewedConfirmation : ILinkWriteConfirmation
        {
            private readonly string target, fingerprint;
            public ReviewedConfirmation(string target, string fingerprint) { this.target = target; this.fingerprint = fingerprint; }
            public Task<bool> ConfirmAsync(LinkWritePreview preview, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(preview.CanApply && preview.Request.DisplayTarget == target && preview.ApprovalFingerprint == fingerprint &&
                    preview.Inspection.DatabaseId == Required("DOORS_TEST_DATABASE_ID") && Required("DOORS_LINK_WRITE_CONFIRMATION") == "APPLY " + fingerprint);
            }
            public Task<bool> ConfirmRemovalAsync(LinkWritePreview preview, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(preview.Request.Action == LinkWriteAction.Remove && preview.Request.DisplayTarget == target &&
                    preview.ApprovalFingerprint == fingerprint && Required("DOORS_LINK_REMOVAL_CONFIRMATION") == "DELETE " + target);
            }
        }
    }
}
