using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.AcceptanceHost
{
    // Test-only subprocess. There is no Real/COM mode, activation, or user-database path.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 3 || (args[0] != "crash" && args[0] != "recover") ||
                    (args[2] != "before-checkpoint" && args[2] != "after-checkpoint")) return 2;
                string root = Path.GetFullPath(args[1]);
                string container = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DoorsIntegrationJobTests")) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(container, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(root.Substring(container.Length), "N", out var fixtureId) || fixtureId == Guid.Empty) return 3;
                return Run(args[0], root, args[2]).GetAwaiter().GetResult();
            }
            catch (Exception error) { Console.Error.WriteLine("Mock worker failed: " + error.GetType().FullName); return 4; }
        }
        private static async Task<int> Run(string mode, string root, string phase)
        {
            string marker = Path.Combine(root, "mock-acceptance.marker");
            if (mode == "crash")
            {
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) return 5;
                Directory.CreateDirectory(root); File.WriteAllText(marker, "Task27 Mock-only fixture v1");
            }
            else if (!File.Exists(marker) || File.ReadAllText(marker) != "Task27 Mock-only fixture v1") return 6;
            var catalog = new MockModuleSelectionAdapter();
            var store = new MockBaselineManagerStore(catalog, Path.Combine(root, "mock-server"));
            var clock = new BaselinePreflightClock(); var logger = new QuietLog();
            var preflight = new BulkBaselinePreflightService(store, store, clock, logger);
            var journal = new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(Path.Combine(root, "jobs")), new MockOnlyProtector());
            var writer = new Writer(store, root, mode == "crash" && phase == "before-checkpoint");
            IBulkBaselineJobJournal checkpoints = mode == "crash" && phase == "after-checkpoint" ? (IBulkBaselineJobJournal)new CrashJournal(journal, root) : journal;
            var consent = new MockConsent();
            var runner = new BulkBaselineJobRunner(checkpoints, preflight, writer, consent, consent, new BaselineJobStateVerifier(store, store, clock), clock, logger);
            if (mode == "crash")
            {
                var modules = new System.Collections.Generic.List<BaselineModuleSelection>();
                foreach (string name in new[] { "001", "002" })
                {
                    var item = await catalog.ReadAsync(MockDoorsAdapter.BaselineRepositoryId, "/Bulk/P01/F01/Requirement module " + name,
                        DoorsVersion.V97, DoorsConnectionMode.AttachExisting, TimeSpan.FromSeconds(10), CancellationToken.None);
                    modules.Add(new BaselineModuleSelection(item.Module));
                }
                var plan = await preflight.CreatePlanAsync(new BulkBaselinePreflightRequest(new BulkBaselineRequest(modules, BaselineType.Minor, suffix: "ACCEPT", annotation: "Mock process crash acceptance")));
                if (plan.ReadyCount != 2) return 7;
                var job = await runner.CreateAsync(plan);
                Save(Path.Combine(root, "job.json"), new Receipt { JobId = job.JobId, Source = "Mock", Phase = phase });
                await runner.RunAsync(job.JobId); return 8; // A missing forced termination is a failing fixture.
            }
            var original = Read(Path.Combine(root, "job.json"));
            string statusBefore;
            using (var lease = journal.Acquire(original.JobId)) statusBefore = lease.Current.Modules[0].Status.ToString();
            var opened = await runner.OpenAsync(original.JobId);
            int writesDuringReconciliation = writer.Calls;
            var completed = await runner.RunAsync(original.JobId);
            int writesAfterResume = writer.Calls;
            var repeated = await runner.RunAsync(original.JobId);
            Save(Path.Combine(root, "recovered.json"), new Receipt { JobId = original.JobId, Source = "Mock", Phase = phase,
                StatusBeforeRecovery = statusBefore, FirstStatusAfterOpen = opened.Modules[0].Status.ToString(),
                Status = repeated.Status.ToString(), WritesDuringReconciliation = writesDuringReconciliation, WritesAfterResume = writesAfterResume,
                WritesAfterRepeat = writer.Calls, BaselineCount = Directory.GetFiles(Path.Combine(root, "mock-server"), "*.json").Length,
                ModuleCount = completed.Modules.Count, Attempts = completed.Modules.Select(m => m.Attempts).ToArray() });
            return completed.Status == BulkBaselineModuleStatus.Succeeded && repeated.Status == BulkBaselineModuleStatus.Succeeded ? 0 : 9;
        }
        private static void Terminate(string root, string phase)
        {
            using (var output = new FileStream(Path.Combine(root, "crash-proof.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = Encoding.UTF8.GetBytes("MOCK only; durable baseline exists; phase=" + phase); output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            // Kill only this explicitly launched test child; no graceful disposal/checkpoint/finally is assumed.
            Process.GetCurrentProcess().Kill(); Thread.Sleep(Timeout.Infinite);
        }
        private sealed class Writer : IBaselineExecutionAdapter
        {
            private readonly MockBaselineManagerStore inner; private readonly string root; private readonly bool crash;
            internal Writer(MockBaselineManagerStore inner, string root, bool crash) { this.inner = inner; this.root = root; this.crash = crash; }
            public BaselineMetadataSource Source => BaselineMetadataSource.Mock;
            internal int Calls;
            public async Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization auth, CancellationToken token)
            { Calls++; var result = await inner.ExecuteBaselineAsync(auth, token); if (crash && result.Outcome == BaselineWriteOutcome.Created) Terminate(root, "before-checkpoint"); return result; }
        }
        private sealed class CrashJournal : IBulkBaselineJobJournal
        {
            private readonly IBulkBaselineJobJournal inner; private readonly string root;
            internal CrashJournal(IBulkBaselineJobJournal inner, string root) { this.inner = inner; this.root = root; }
            public IBulkBaselineJobJournalLease Acquire(Guid id) => new Lease(inner.Acquire(id), root);
            private sealed class Lease : IBulkBaselineJobJournalLease
            {
                private readonly IBulkBaselineJobJournalLease inner; private readonly string root;
                internal Lease(IBulkBaselineJobJournalLease inner, string root) { this.inner = inner; this.root = root; }
                public BulkBaselineJobRecord Current => inner.Current;
                public void Save(BulkBaselineJobRecord job) { inner.Save(job); if (job.Modules[0].Status == BulkBaselineModuleStatus.Succeeded) Terminate(root, "after-checkpoint"); }
                public void Dispose() => inner.Dispose();
            }
        }
        private sealed class MockConsent : IBaselineExecutionConfirmation, IBulkBaselineRetryConfirmation
        {
            public Task<bool> ConfirmAsync(BaselineExecutionReview review, CancellationToken token) => Task.FromResult(review.Plan.Items.All(i => i.Precondition.Facts.Source == BaselineMetadataSource.Mock));
            public Task<bool> ConfirmAsync(BaselineJobRetryReview review, CancellationToken token) => Task.FromResult(false);
        }
        private sealed class QuietLog : IIntegrationLogger { public void Write(IntegrationLogLevel level, string name, Guid id, string message) { } }
        [DataContract] internal sealed class Receipt
        {
            [DataMember] public Guid JobId;
            [DataMember] public string Source;
            [DataMember] public string Phase;
            [DataMember] public string StatusBeforeRecovery;
            [DataMember] public string FirstStatusAfterOpen;
            [DataMember] public string Status;
            [DataMember] public int WritesDuringReconciliation;
            [DataMember] public int WritesAfterResume;
            [DataMember] public int WritesAfterRepeat;
            [DataMember] public int BaselineCount;
            [DataMember] public int ModuleCount;
            [DataMember] public int[] Attempts;
        }
        private static Receipt Read(string path) { using (var input = File.OpenRead(path)) return (Receipt)new DataContractJsonSerializer(typeof(Receipt)).ReadObject(input); }
        private static void Save(string path, Receipt value)
        { using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { new DataContractJsonSerializer(typeof(Receipt)).WriteObject(output, value); output.Flush(true); } }
        // Known test key permits separate MOCK worker processes to reopen synthetic fixtures.
        // Not a security feature, DPAPI validation, application default, or a protector for user data.
        private sealed class MockOnlyProtector : IBaselineJournalProtector
        {
            private static readonly byte[] Key = MakeKey();
            private static byte[] MakeKey() { using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes("Task27-MOCK-FIXTURE-ONLY-NOT-FOR-USER-DATA")); }
            public byte[] Protect(byte[] bytes)
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = Key; aes.GenerateIV(); using (var transform = aes.CreateEncryptor())
                    { var data = aes.IV.Concat(transform.TransformFinalBlock(bytes, 0, bytes.Length)).ToArray(); using (var hmac = new HMACSHA256(Key)) return hmac.ComputeHash(data).Concat(data).ToArray(); }
                }
            }
            public byte[] Unprotect(byte[] bytes)
            {
                if (bytes.Length < 64) throw new CryptographicException(); var data = bytes.Skip(32).ToArray();
                using (var hmac = new HMACSHA256(Key)) if (!hmac.ComputeHash(data).SequenceEqual(bytes.Take(32))) throw new CryptographicException();
                using (var aes = Aes.Create()) { aes.Key = Key; aes.IV = data.Take(16).ToArray(); using (var transform = aes.CreateDecryptor()) return transform.TransformFinalBlock(data, 16, data.Length - 16); }
            }
        }
    }
}
