using System;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ModuleCreationProtocolTests
    {
        [DataTestMethod]
        [DataRow("\n")]
        [DataRow("\r\n")]
        public void PreviewProtocolAcceptsOnlyTheRequestedTargetAndSupportsWindowsLines(string newline)
        {
            var request = new FormalModuleCreateRequest("/Özel Proje", "Modül", "");
            var id = Guid.NewGuid();
            string payload = ModuleCreationTestContext.Batch(id, request.FullPath, ModuleCreationTestContext.Ready(), ModuleCreationOutcome.Preview).Replace("\n", newline);
            var result = ModuleCreationProtocol.Parse(payload, id, request, null);
            Assert.IsTrue(result.Checks.CanCreate);
            Assert.AreEqual(request.FullPath, result.FullPath);
        }

        [DataTestMethod]
        [DataRow("empty")]
        [DataRow("stale")]
        [DataRow("path")]
        [DataRow("numeric-enum")]
        [DataRow("unknown-enum")]
        [DataRow("missing-end")]
        [DataRow("extra-row")]
        [DataRow("missing-newline")]
        [DataRow("invalid-escape")]
        [DataRow("bare-cr")]
        [DataRow("preview-with-id")]
        [DataRow("preview-created")]
        [DataRow("oversized")]
        public void MalformedPreviewFailsWithoutLeakingUntrustedResponse(string fault)
        {
            var request = new FormalModuleCreateRequest("/Test", "PRIVATE_MODULE", "");
            var id = Guid.NewGuid();
            string payload = ModuleCreationTestContext.Batch(id, request.FullPath, ModuleCreationTestContext.Ready(), ModuleCreationOutcome.Preview);
            switch (fault)
            {
                case "empty": payload = ""; break;
                case "stale": payload = payload.Replace(id.ToString("N"), Guid.NewGuid().ToString("N")); break;
                case "path": payload = payload.Replace(request.FullPath, "/Other/PRIVATE_MODULE"); break;
                case "numeric-enum": payload = payload.Replace("Readable", "0"); break;
                case "unknown-enum": payload = payload.Replace("Readable", "PRIVATE_UNKNOWN"); break;
                case "missing-end": payload = payload.Replace("END\n", ""); break;
                case "extra-row": payload += "PRIVATE_EXTRA\n"; break;
                case "missing-newline": payload = payload.TrimEnd('\n'); break;
                case "invalid-escape": payload = payload.Replace("test-user", "PRIVATE\\x"); break;
                case "bare-cr": payload = payload.Replace("Readable", "Read\rable"); break;
                case "preview-with-id": payload = payload.Replace("R\tPreview\t\t\t", "R\tPreview\tPRIVATE_ID\tPRIVATE_QUALIFIED\t"); break;
                case "preview-created": payload = ModuleCreationTestContext.Batch(id, request.FullPath, ModuleCreationTestContext.Ready(), ModuleCreationOutcome.Created); break;
                case "oversized": payload = new string('x', 32769); break;
            }
            var failure = Assert.ThrowsException<IntegrationException>(() => ModuleCreationProtocol.Parse(payload, id, request, null));
            Assert.AreEqual(IntegrationErrorCode.ModuleCreationProtocolFailure, failure.Code);
            Assert.IsFalse(failure.ToString().Contains("PRIVATE_"));
        }

        [DataTestMethod]
        [DataRow("malformed")]
        [DataRow("different-context")]
        [DataRow("missing-identity")]
        [DataRow("invalid-cleanup")]
        [DataRow("readable-but-denied")]
        public async Task InvalidPostWriteResponseIsUncertainAndNeverRetried(string fault)
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                context.AlterResponse = payload =>
                {
                    switch (fault)
                    {
                        case "malformed": return payload.TrimEnd('\n');
                        case "different-context": return payload.Replace("folder-1", "PRIVATE_OTHER_FOLDER");
                        case "missing-identity": return payload.Replace("module-123\tdatabase-1:module-123", "\t");
                        case "invalid-cleanup": return payload.Replace("Closed", "NotRequired");
                        default: return payload.Replace("Allowed", "Denied");
                    }
                };
                var failure = await Assert.ThrowsExceptionAsync<ModuleCreationUncertainException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                var cause = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await failure.Completion);
                Assert.AreEqual(IntegrationErrorCode.ModuleCreationProtocolFailure, cause.Code);
                Assert.AreEqual(1, context.Modes.Count(mode => mode == "commit"));
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("PRIVATE_"));
            }
        }

        [TestMethod]
        public async Task NativeUncertaintyPreservesCandidateIdentityForInspection()
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                context.ForcedCommitOutcome = ModuleCreationOutcome.OutcomeUnknown;
                context.AlterResponse = payload => payload.Replace("R\tOutcomeUnknown\t\t\tNotRequired", "R\tOutcomeUnknown\tmodule-123\tdatabase-1:module-123\tFailed");
                var failure = await Assert.ThrowsExceptionAsync<ModuleCreationUncertainException>(async () => await context.Service.CreateFormalModuleAsync(preview));
                var result = await failure.Completion;
                Assert.AreEqual(ModuleCreationOutcome.OutcomeUnknown, result.Outcome);
                Assert.AreEqual("module-123", result.ModuleId);
                Assert.AreEqual(ModuleCreationCleanup.Failed, result.Cleanup);
                Assert.AreEqual(1, context.Modes.Count(mode => mode == "commit"));
            }
        }
    }
}
