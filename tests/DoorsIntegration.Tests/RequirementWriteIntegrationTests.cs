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
    [TestCategory("DoorsWriteIntegration")]
    public class RequirementWriteIntegrationTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public async Task PreviewRequirementWriteInExplicitTestModule_DoesNotCommit()
        {
            RequireMode("requirement-preview");
            var request = Request();
            using (var provider = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider())
            {
                var service = provider.GetRequiredService<IRequirementService>();
                var schema = await service.GetAttributeSchemaAsync(request.Target);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), schema.DatabaseId);
                foreach (var definition in schema.Attributes)
                    TestContext.WriteLine(definition.Name + ": " + definition.TypeName + "; Base=" + definition.BaseType +
                        "; Object=" + definition.IsObjectAttribute + "; DXL=" + definition.IsGeneratedByDxl +
                        "; Enum=[" + string.Join(", ", definition.EnumerationValues) + "]");
                var preview = await service.PreviewWriteAsync(request);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), preview.Inspection.Schema.DatabaseId);
                TestContext.WriteLine("Module: " + request.Target.ModulePath + "; Current object: " + request.AbsoluteNumber +
                    "; Position: " + request.Position.Kind + "; Anchor: " + request.Position.AnchorAbsoluteNumber);
                foreach (var value in preview.Inspection.BeforeValues) TestContext.WriteLine("BEFORE " + value.Name + ": " + value.PlainText);
                foreach (var value in request.Changes) TestContext.WriteLine("REQUESTED " + value.Name + ": " + value.Value);
                foreach (var issue in preview.Inspection.Schema.Issues) TestContext.WriteLine(issue.Severity + ": " + issue.Code + " " + issue.AttributeName);
                TestContext.WriteLine("CanApply=" + preview.CanApply + "; Actor=" + preview.Inspection.Schema.UserName);
                TestContext.WriteLine("Approval fingerprint: " + preview.ApprovalFingerprint);
                TestContext.WriteLine("No commit was requested. Review the complete request and warnings before enabling the separate write test.");
            }
        }

        [TestMethod]
        public async Task ApplyRequirementWriteInExplicitTestModule_RequiresReviewedFingerprintAndConsent()
        {
            RequireMode("requirement-write");
            var request = Request();
            Assert.AreEqual("I_CONFIRM_REQUIREMENT_WRITE", Required("DOORS_REQ_TEST_CONSENT"));
            Assert.AreEqual(request.Target.ModulePath, Required("DOORS_REQ_TEST_CONFIRM_MODULE"));
            string fingerprint = Required("DOORS_REQ_TEST_APPROVED_FINGERPRINT");
            Assert.AreEqual(64, fingerprint.Length);
            var services = new ServiceCollection();
            services.AddSingleton<IRequirementWriteConfirmation>(new ReviewedConfirmation(request.Target.ModulePath, fingerprint));
            services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                var service = provider.GetRequiredService<IRequirementService>();
                var preview = await service.PreviewWriteAsync(request);
                Assert.AreEqual(Required("DOORS_TEST_DATABASE_ID"), preview.Inspection.Schema.DatabaseId);
                Assert.AreEqual(fingerprint, preview.ApprovalFingerprint, "Preview changed. Obtain and review a new preview; no write was requested.");
                Assert.IsTrue(preview.CanApply);
                var result = await service.ApplyWriteAsync(preview);
                TestContext.WriteLine("Outcome=" + result.Outcome + "; Persistence=" + result.Persistence + "; Cleanup=" + result.Cleanup +
                    "; Absolute=" + result.AbsoluteNumber + "; Identifier=" + result.ObjectIdentifier + "; Audit=" + result.AuditRecorded);
                foreach (var step in result.Steps) TestContext.WriteLine(step.Kind + ":" + step.AttributeName + "=" + step.Status + "; Error=" + step.Error);
                Assert.IsTrue(result.Succeeded, "Inspect module and audit. Changes might already exist; never retry this test automatically.");
                using (var snapshot = await service.ReadAsync(new RequirementReadRequest(request.Target.ModulePath, target: request.Target.ClientVersion)))
                {
                    var persisted = snapshot.ReadObjects().Single(o => o.AbsoluteNumber == result.AbsoluteNumber);
                    Assert.AreEqual(result.ObjectIdentifier, persisted.ObjectIdentifier);
                    Assert.AreEqual(result.ParentAbsoluteNumber, persisted.ParentAbsoluteNumber);
                    foreach (var change in request.Changes)
                        Assert.AreEqual(change.Value.Replace("\r\n", "\n"), persisted.Values[change.Name].PlainText.Replace("\r\n", "\n"));
                }
                // Created objects and all test edits are intentionally retained. No delete or rollback is attempted.
            }
        }

        private static RequirementWriteRequest Request()
        {
            string version = Required("DOORS_TEST_VERSION");
            Assert.IsTrue(version == "9.5" || version == "9.7");
            var target = new RequirementWriteTarget(Required("DOORS_REQ_TEST_MODULE"), version == "9.5" ? DoorsVersion.V95 : DoorsVersion.V97);
            string action = Required("DOORS_REQ_TEST_ACTION");
            Assert.IsTrue(action == "create" || action == "update" || action == "move");
            int? absolute = action == "create" ? (int?)null : Positive("DOORS_REQ_TEST_ABSOLUTE");
            RequirementPosition position = null;
            if (action == "move")
            {
                string kind = Required("DOORS_REQ_TEST_POSITION");
                Assert.IsTrue(kind == "After" || kind == "FirstChild" || kind == "LastChild");
                position = new RequirementPosition((RequirementPositionKind)Enum.Parse(typeof(RequirementPositionKind), kind), Positive("DOORS_REQ_TEST_ANCHOR"));
            }
            var values = action == "move" ? new RequirementAttributeChange[0] : new[]
            {
                new RequirementAttributeChange("Object Heading", Required("DOORS_REQ_TEST_HEADING")),
                new RequirementAttributeChange("Object Text", Required("DOORS_REQ_TEST_TEXT"))
            };
            string required = Environment.GetEnvironmentVariable("DOORS_REQ_TEST_REQUIRED") ?? "";
            return new RequirementWriteRequest(target, values, absolute, position,
                required.Length == 0 ? null : required.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        }
        private static int Positive(string name)
        {
            Assert.IsTrue(int.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result > 0);
            return result;
        }
        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), "Required test setting missing: " + name);
            return value;
        }
        private static void RequireMode(string mode)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != mode)
                Assert.Inconclusive("Live requirement write test disabled. Explicitly configure the reviewed test module; see docs/REQUIREMENT-WRITING.md.");
        }
        private sealed class ReviewedConfirmation : IRequirementWriteConfirmation
        {
            private readonly string path;
            private readonly string fingerprint;
            public ReviewedConfirmation(string path, string fingerprint) { this.path = path; this.fingerprint = fingerprint; }
            public Task<bool> ConfirmAsync(RequirementWritePreview preview, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(preview.CanApply && preview.Request.Target.ModulePath == path && preview.ApprovalFingerprint == fingerprint &&
                    preview.Inspection.Schema.DatabaseId == Required("DOORS_TEST_DATABASE_ID") &&
                    Environment.GetEnvironmentVariable("DOORS_REQ_TEST_CONSENT") == "I_CONFIRM_REQUIREMENT_WRITE" &&
                    Environment.GetEnvironmentVariable("DOORS_REQ_TEST_CONFIRM_MODULE") == path);
            }
        }
    }
}
