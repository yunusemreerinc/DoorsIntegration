using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BaselineComparisonProtocolTests
    {
        private static string IdentityReply(Guid id, RequirementReadRequest request, string identity)
        {
            string reply = RequirementTestContext.Batch(id, request, RequirementTestContext.Object(1, 17));
            int metadataEnd = reply.IndexOf('\n', reply.IndexOf('\n') + 1);
            return reply.Insert(metadataEnd + 1, "Q\t" + RequirementTestContext.Escape(identity) + "\n").Replace("DOORS_REQUIREMENTS\t1\t", "DOORS_REQUIREMENTS\t2\t");
        }
        [TestMethod]
        public async Task NativeIdentityProtocolV2WorksThroughRealReaderAndBaselineServiceWithMockCom()
        {
            var request = BaselineFixture.Request();
            using (var context = new RequirementTestContext(request.BeforeRead))
            {
                int call = 0;
                context.Reply = id => IdentityReply(id, ++call == 1 ? request.BeforeRead : request.AfterRead, "DB-qualified-module");
                using (var result = await new BaselineComparisonService(context.Reader, context.Transport.Log).CompareAsync(request))
                {
                    Assert.AreEqual("DB-qualified-module", result.QualifiedModuleId);
                    Assert.AreEqual(17, result.ReadChanges().Single().Identity.AbsoluteNumber);
                    Assert.AreEqual(BaselineChangeStatus.Unchanged, result.ReadChanges().Single().Status);
                }
                Assert.AreEqual(2, call);
            }
        }
        [DataTestMethod]
        [DataRow("")] [DataRow("bad\tidentity")] [DataRow("bad\nidentity")]
        public async Task AvailableV2WithMissingOrControlIdentityIsRejected(string identity)
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => IdentityReply(id, context.Request, identity);
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request));
                Assert.AreEqual(IntegrationErrorCode.RequirementProtocolFailure, error.Code);
            }
        }
        [TestMethod]
        public async Task LegacyV1RemainsReadableButCannotBeUsedForIdentityVerifiedComparison()
        {
            var request = BaselineFixture.Request();
            using (var context = new RequirementTestContext(request.BeforeRead))
            {
                using (var snapshot = await context.Reader.ReadAsync(context.Request)) Assert.IsNull(snapshot.Summary.QualifiedModuleId);
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => new BaselineComparisonService(context.Reader, context.Transport.Log).CompareAsync(request));
                Assert.AreEqual(IntegrationErrorCode.BaselineComparisonFailed, error.Code);
            }
        }
        [TestMethod]
        public void BaselineScriptUsesExistingReadOnlyLoadAndNativeModuleIdentityWithoutWritePerms()
        {
            string source = File.ReadAllText(new RequirementScriptOptions().ScriptPath);
            StringAssert.Contains(source, "qualifiedUniqueID(moduleItem)");
            StringAssert.Contains(source, "qualifiedUniqueID(rqModule) != rqQualifiedId");
            StringAssert.Contains(source, "load(rqVersion, false)");
            StringAssert.Contains(source, "DOORS_REQUIREMENTS\\t2\\t");
            string code = Regex.Replace(source, @"//[^\r\n]*", "");
            Assert.IsFalse(Regex.IsMatch(code, @"\b(edit|share|save|undelete|create|deleteObject)\s*\(rqModule"));
        }
    }
}
