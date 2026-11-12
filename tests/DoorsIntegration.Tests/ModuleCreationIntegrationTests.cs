using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    [TestCategory("DoorsWriteIntegration")]
    public class ModuleCreationIntegrationTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public async Task PreviewFormalModuleInExplicitTestFolder_DoesNotWrite()
        {
            RequireMode("preview");
            var request = RequestFromEnvironment();
            using (var provider = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider())
            {
                var preview = await provider.GetRequiredService<IModuleService>().PreviewFormalModuleCreationAsync(request);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), preview.Checks.DatabaseId, "Unexpected database. No write was attempted.");
                TestContext.WriteLine("Target: " + request.FullPath);
                TestContext.WriteLine("Description: " + request.Description);
                TestContext.WriteLine("Database: " + preview.Checks.DatabaseId + "; User: " + preview.Checks.UserName + "; Folder ID: " + preview.Checks.FolderId);
                TestContext.WriteLine(preview.Checks.Summary);
                TestContext.WriteLine("CanCreate: " + preview.CanCreate);
                TestContext.WriteLine("Approval fingerprint: " + preview.ApprovalFingerprint);
                TestContext.WriteLine("Review these values before explicitly enabling the separate create test. No module was created by this test.");
            }
        }

        [TestMethod]
        public async Task CreateFormalModuleInExplicitTestFolder_RequiresReviewedFingerprintAndConsent()
        {
            RequireMode("create");
            Assert.AreEqual("I_CONFIRM_CREATE_FORMAL_MODULE", Required("DOORS_TEST_WRITE_CONSENT"), "Explicit write consent is required.");
            var request = RequestFromEnvironment();
            Assert.AreEqual(request.FullPath, Required("DOORS_TEST_CONFIRM_TARGET"), "Consent must name the exact target.");
            string fingerprint = Required("DOORS_TEST_APPROVED_FINGERPRINT");
            Assert.AreEqual(64, fingerprint.Length, "Run and review the preview test first.");
            var services = new ServiceCollection();
            services.AddSingleton<IModuleCreationConfirmation>(new ReviewedTestConfirmation(request.FullPath, fingerprint));
            services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                var service = provider.GetRequiredService<IModuleService>();
                var preview = await service.PreviewFormalModuleCreationAsync(request);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), preview.Checks.DatabaseId, "Unexpected database; write blocked.");
                Assert.AreEqual(fingerprint, preview.ApprovalFingerprint, "Preview changed; obtain a new dry-run and approval.");
                Assert.IsTrue(preview.CanCreate, preview.Checks.Summary);
                var result = await service.CreateFormalModuleAsync(preview);
                TestContext.WriteLine("CREATED: " + result.FullPath + "; ID=" + result.ModuleId + "; Qualified ID=" + result.QualifiedModuleId + "; Cleanup=" + result.Cleanup);
                Assert.AreEqual(ModuleCreationOutcome.Created, result.Outcome);
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.ModuleId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.QualifiedModuleId));
                // A separate read-only DXL query verifies IDs, formal type, path and persisted description.
                var verification = new DxlExecutionRequest(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "verify-created-module.dxl"),
                    parameters: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["path"] = result.FullPath, ["moduleId"] = result.ModuleId,
                        ["qualifiedId"] = result.QualifiedModuleId, ["description"] = request.Description
                    }, target: request.Target, connectionMode: DoorsConnectionMode.AttachExisting);
                using (var check = await provider.GetRequiredService<IDxlExecutor>().ExecuteAsync(verification))
                    Assert.AreEqual("VERIFIED", check.Text, "Created module could not be independently verified. Inspect it; do not automatically retry.");
                Assert.AreEqual(ModuleCreationCleanup.Closed, result.Cleanup, "Module was created, but cleanup requires inspection.");
                // Intentionally retained: cleanup does not delete, purge, rename or overwrite the created module.
            }
        }

        private static void RequireMode(string expected)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != expected)
                Assert.Inconclusive("Live DOORS test disabled. Explicitly configure DOORS_TEST_MODE=" + expected + " and a reviewed test target; see docs/MODULE-CREATION.md.");
        }
        private static string Required(string key)
        {
            string value = Environment.GetEnvironmentVariable(key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), "Missing explicit test configuration: " + key);
            return value;
        }
        private static FormalModuleCreateRequest RequestFromEnvironment()
        {
            string version = Required("DOORS_TEST_VERSION");
            Assert.IsTrue(version == "9.5" || version == "9.7", "DOORS_TEST_VERSION must be 9.5 or 9.7.");
            string folder = Required("DOORS_TEST_FOLDER");
            Assert.AreNotEqual("/", folder, "Use an explicitly designated test project/folder, never the database root.");
            string description = Required("DOORS_TEST_DESCRIPTION");
            return new FormalModuleCreateRequest(folder, Required("DOORS_TEST_MODULE_NAME"), description,
                version == "9.5" ? DoorsVersion.V95 : DoorsVersion.V97, DoorsConnectionMode.AttachExisting);
        }

        private sealed class ReviewedTestConfirmation : IModuleCreationConfirmation
        {
            private readonly string path;
            private readonly string fingerprint;
            public ReviewedTestConfirmation(string path, string fingerprint) { this.path = path; this.fingerprint = fingerprint; }
            public Task<bool> ConfirmAsync(FormalModuleCreationPreview preview, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(preview.Request.FullPath == path && preview.ApprovalFingerprint == fingerprint && preview.CanCreate &&
                    Environment.GetEnvironmentVariable("DOORS_TEST_WRITE_CONSENT") == "I_CONFIRM_CREATE_FORMAL_MODULE" &&
                    Environment.GetEnvironmentVariable("DOORS_TEST_CONFIRM_TARGET") == path);
            }
        }
    }
}
