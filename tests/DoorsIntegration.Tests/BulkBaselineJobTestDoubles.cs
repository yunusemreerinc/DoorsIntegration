using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class JobRetryConfirmation : IBulkBaselineRetryConfirmation
    {
        internal bool Approved = true; internal int Calls; internal Action OnConfirm;
        public Task<bool> ConfirmAsync(BaselineJobRetryReview review, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; OnConfirm?.Invoke(); Assert.AreEqual(BaselineJobVerificationStatus.AbsentAuthoritatively, review.Verification.Status); return Task.FromResult(Approved); }
    }
    internal sealed class JobMockServer : IBaselineJobStateVerifier
    {
        internal readonly Dictionary<string, BaselineJobVerificationStatus> Targets = new Dictionary<string, BaselineJobVerificationStatus>();
        internal readonly Dictionary<string, int> Creates = new Dictionary<string, int>();
        internal int Reads;
        internal Func<BulkBaselineJobRecord, BulkBaselineJobModule, BaselineJobVerification> Override { get; set; }
        internal Action<BulkBaselineJobModule, int> OnVerify { get; set; }
        public Task<BaselineJobVerification> VerifyAsync(BulkBaselineJobRecord job, BulkBaselineJobModule module, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++; OnVerify?.Invoke(module, Reads);
            var status = Targets.TryGetValue(module.ModuleId, out var value) ? value : BaselineJobVerificationStatus.AbsentAuthoritatively;
            return Task.FromResult(Override == null ? new BaselineJobVerification(module.Module, module.ExpectedVersion, module.Annotation,
                module.DatabaseIdentity, module.UserId, BaselineMetadataSource.Mock, status, DateTime.UtcNow, "Explicit external Mock server evidence; no native API.") : Override(job, module));
        }
    }
    internal sealed class JobMockAdapter : IBaselineExecutionAdapter
    {
        private readonly JobMockServer server;
        internal JobMockAdapter(JobMockServer server) { this.server = server; }
        public BaselineMetadataSource Source { get; set; } = BaselineMetadataSource.Mock;
        internal int Calls; internal int Concurrent; internal int MaximumConcurrent;
        internal Func<BaselineExecutionAuthorization, Task> BeforeExecute;
        internal Func<BaselineExecutionAuthorization, BaselineWriteOutcome> Outcome;
        internal Action<BaselineExecutionAuthorization> AfterCreate { get; set; }
        internal bool ReceivedCancelableToken;
        public async Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization auth, CancellationToken token)
        {
            ReceivedCancelableToken |= token.CanBeCanceled; Calls++; Concurrent++; MaximumConcurrent = Math.Max(MaximumConcurrent, Concurrent);
            try
            {
                Assert.IsTrue(auth.TryBeginWrite());
                if (BeforeExecute != null) await BeforeExecute(auth);
                var outcome = Outcome?.Invoke(auth) ?? BaselineWriteOutcome.Created;
                if (outcome == BaselineWriteOutcome.Created)
                {
                    if (server.Targets.TryGetValue(auth.Item.ModuleId, out var old) && old != BaselineJobVerificationStatus.AbsentAuthoritatively)
                        Assert.Fail("A duplicate create was dispatched by the runner.");
                    server.Targets[auth.Item.ModuleId] = BaselineJobVerificationStatus.ExistsMatching;
                    server.Creates[auth.Item.ModuleId] = server.Creates.TryGetValue(auth.Item.ModuleId, out int count) ? count + 1 : 1;
                    AfterCreate?.Invoke(auth);
                }
                var now = DateTime.UtcNow;
                return new BaselineModuleExecutionResult(auth.Review.JobId, auth.Item, outcome, Source,
                    outcome == BaselineWriteOutcome.Created || outcome == BaselineWriteOutcome.NeedsReconciliation, "Mock outcome=" + outcome, now, now, Guid.NewGuid());
            }
            finally { Concurrent--; }
        }
    }
    internal sealed class FaultingJobJournal : IBulkBaselineJobJournal
    {
        private readonly IBulkBaselineJobJournal inner;
        internal FaultingJobJournal(IBulkBaselineJobJournal inner) { this.inner = inner; }
        internal Func<BulkBaselineJobRecord, bool> BeforeSave;
        internal Func<BulkBaselineJobRecord, bool> AfterSave;
        public IBulkBaselineJobJournalLease Acquire(Guid jobId) => new Lease(inner.Acquire(jobId), this);
        private sealed class Lease : IBulkBaselineJobJournalLease
        {
            private readonly IBulkBaselineJobJournalLease inner; private readonly FaultingJobJournal parent;
            internal Lease(IBulkBaselineJobJournalLease inner, FaultingJobJournal parent) { this.inner = inner; this.parent = parent; }
            public BulkBaselineJobRecord Current => inner.Current;
            public void Save(BulkBaselineJobRecord record)
            {
                if (parent.BeforeSave?.Invoke(record) == true) throw new BulkBaselineJournalException("Simulated process crash before atomic checkpoint.");
                inner.Save(record);
                if (parent.AfterSave?.Invoke(record) == true) throw new BulkBaselineJournalException("Simulated process crash after atomic checkpoint.");
            }
            public void Dispose() => inner.Dispose();
        }
    }
    internal sealed class JobProgressObserver : IProgress<BulkBaselineJobProgress>
    {
        internal readonly List<BulkBaselineJobProgress> Events = new List<BulkBaselineJobProgress>();
        internal readonly TaskCompletionSource<bool> CancelSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Throw;
        public void Report(BulkBaselineJobProgress value)
        {
            lock (Events) Events.Add(value);
            if (value.CurrentModule?.Status == BulkBaselineModuleStatus.CancelRequested) CancelSeen.TrySetResult(true);
            if (Throw) throw new IOException("PRIVATE_PROGRESS_FAILURE");
        }
    }
    internal sealed class JobTestFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "DoorsIntegrationJobTests", Guid.NewGuid().ToString("N"));
        internal readonly PreflightTestFixture Preflight;
        internal readonly JobMockServer Server = new JobMockServer();
        internal readonly BaselineExecutionTestConfirmation Confirmation = new BaselineExecutionTestConfirmation { Approved = true };
        internal readonly JobRetryConfirmation RetryConfirmation = new JobRetryConfirmation();
        internal readonly JobMockAdapter Adapter;
        internal readonly FileBulkBaselineJobJournal Journal;
        internal JobTestFixture(int count = 3)
        { Preflight = new PreflightTestFixture(count); Adapter = new JobMockAdapter(Server); Journal = new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(Root), new TestBaselineJournalProtector()); }
        internal BulkBaselineJobRunner Runner(IBulkBaselineJobJournal journal = null, IBaselineExecutionAdapter adapter = null, IBaselineJobStateVerifier verifier = null) => new BulkBaselineJobRunner(
            journal ?? Journal, Preflight.Service, adapter ?? Adapter, Confirmation, RetryConfirmation, verifier ?? Server, Preflight.Clock, Preflight.Log);
        internal async Task<BulkBaselineJobRecord> Create(ExecutionPolicy policy = ExecutionPolicy.ContinueOnError) =>
            await Runner().CreateAsync(await Preflight.Service.CreatePlanAsync(Preflight.Request(policy, annotation: "İnceleme\n注釈")));
        internal string PathOf(Guid id) => Path.Combine(Root, id.ToString("N") + ".json");
        internal BulkBaselineJobRecord Raw(Guid id)
        { return Journal.ReadCachedSnapshot(id); }
        public void Dispose()
        {
            // Explicitly owned per-test temporary root only; no user journal is removed.
            string resolved = Path.GetFullPath(Root);
            string container = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DoorsIntegrationJobTests"));
            if (!string.Equals(Path.GetDirectoryName(resolved), container, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup target is outside its owned container.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
