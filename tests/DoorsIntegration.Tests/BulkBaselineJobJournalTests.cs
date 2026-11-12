using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BulkBaselineJobJournalTests
    {
        [DataTestMethod, DataRow("truncate"), DataRow("identity"), DataRow("payload"), DataRow("schema"), DataRow("trailing")]
        public async Task CorruptJournalIsNeverResetOrUsedForWrites(string corruption)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var path = f.PathOf(job.JobId); string data = File.ReadAllText(path);
                switch (corruption)
                {
                    case "truncate": data = data.Substring(0, data.Length / 2); break;
                    case "identity": data = data.Replace("\"AuditHead\":\"", "\"AuditHead\":\"INVALID"); break;
                    case "payload": data = data.Replace("\"ProtectedPayload\":\"", "\"ProtectedPayload\":\"INVALID"); break;
                    case "schema": data = data.Replace("\"Schema\":2", "\"Schema\":99"); break;
                    default: data += "TRAILING_GARBAGE"; break;
                }
                File.WriteAllText(path, data);
                await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner().OpenAsync(job.JobId));
                Assert.AreEqual(data, File.ReadAllText(path)); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task RevisionConflictDoesNotReplaceCurrentCheckpoint()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); string before = File.ReadAllText(f.PathOf(job.JobId));
                using (var lease = f.Journal.Acquire(job.JobId)) Assert.ThrowsException<IntegrationException>(() => lease.Save(lease.Current));
                Assert.AreEqual(before, File.ReadAllText(f.PathOf(job.JobId))); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task BoundedAtomicWriteDoesNotPublishPartialInitialJournal()
        {
            using (var f = new JobTestFixture())
            {
                var tiny = new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(f.Root, 1024), new TestBaselineJournalProtector());
                var plan = await f.Preflight.Service.CreatePlanAsync(f.Preflight.Request());
                await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner(tiny).CreateAsync(plan));
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.json").Length); Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.tmp").Length); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task UnicodeAndEffectiveOverridesSurviveJournalRoundTrip()
        {
            using (var f = new JobTestFixture(2))
            {
                var modules = f.Preflight.States.Select((s, index) => new BaselineModuleSelection(s.Module, index == 0 ? "ÖZEL" : null, index == 0 ? "Özel\n注釈" : null));
                var plan = await f.Preflight.Service.CreatePlanAsync(new BulkBaselinePreflightRequest(new BulkBaselineRequest(modules, suffix: "SRR", annotation: "Job annotation")));
                var job = await f.Runner().CreateAsync(plan); var reopened = await f.Runner().OpenAsync(job.JobId);
                Assert.AreEqual("ÖZEL", reopened.Modules[0].Suffix); Assert.AreEqual("Özel\n注釈", reopened.Modules[0].Annotation);
                Assert.AreEqual("SRR", reopened.Modules[1].Suffix); Assert.AreEqual("Job annotation", reopened.Modules[1].Annotation);
                Assert.AreEqual(job.InputFingerprint, reopened.InputFingerprint); Assert.AreEqual(job.JobId, reopened.JobId); Assert.IsTrue(reopened.Revision > job.Revision);
                Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task MissingJobIsNotImplicitlyCreated()
        {
            using (var f = new JobTestFixture())
            {
                await Assert.ThrowsExceptionAsync<IntegrationException>(() => f.Runner().RunAsync(Guid.NewGuid()));
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.json").Length); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
    }
}
