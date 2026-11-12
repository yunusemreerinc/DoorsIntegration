using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Tests
{
    internal sealed class ProcessingTestContext : IDisposable
    {
        public ProcessingTestContext()
        {
            Root = Path.Combine(Path.GetTempPath(), "DoorsIntegrationProcessingTests", Guid.NewGuid().ToString("N"));
            Storage = new ProcessingStorageOptions(Root); Journal = new FileBatchJournal(Storage); Service = NewService();
        }
        public string Root { get; }
        public ProcessingStorageOptions Storage { get; }
        public FileBatchJournal Journal { get; }
        public ProcessingTestAdapter Adapter { get; } = new ProcessingTestAdapter();
        public ProcessingTestAudit Audit { get; } = new ProcessingTestAudit();
        public ProcessingTestConfirmation Confirmation { get; } = new ProcessingTestConfirmation();
        public ProcessingTestClock Clock { get; } = new ProcessingTestClock();
        public BatchService Service { get; }
        private readonly List<BatchPreview> plans = new List<BatchPreview>();
        public BatchOptions Options(bool partial = false, int maxAttempts = 3, int maxRecords = 10000) => new BatchOptions("db", "import-2026", partial, maxAttempts, maxRecords: maxRecords);
        public BatchService NewService(IEnumerable<IBatchChangeValidator> validators = null) => new BatchService(Adapter, Journal, Audit, Confirmation, Storage, Clock, validators);
        public async Task<BatchPreview> Preview(params BatchChange[] changes) => Hold(await Service.PreviewAsync(new ProcessingArraySource(changes), Options()));
        public BatchPreview Hold(BatchPreview plan) { plans.Add(plan); return plan; }
        public static BatchChange Change(string key = "row-1", BatchAction action = BatchAction.Create, string objectId = null, string value = "private requirement") =>
            new BatchChange(key, action, "/P/M", new[] { new BatchAttribute("Object Text", value) }, objectId);
        public void Dispose()
        {
            foreach (var plan in plans) plan.Dispose();
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DoorsIntegrationProcessingTests")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    internal sealed class ProcessingArraySource : IBatchRecordSource
    {
        private readonly BatchChange[] changes;
        public ProcessingArraySource(params BatchChange[] changes) { this.changes = changes; }
        public IEnumerable<BatchInputRecord> Read(CancellationToken token)
        { for (int i = 0; i < changes.Length; i++) { token.ThrowIfCancellationRequested(); yield return new BatchInputRecord(i + 1, changes[i]); } }
    }
    internal sealed class ProcessingTestAdapter : IBatchChangeAdapter
    {
        public int Inspections, Calls;
        public List<BatchChange> Applied { get; } = new List<BatchChange>();
        public Func<BatchChange, int, BatchDispatchResult> Dispatch;
        public Func<BatchChange, BatchInspection> Inspect { get; set; }
        public Action OnDispatch { get; set; }
        public Task<BatchInspection> InspectAsync(BatchChange change, BatchOptions scope, CancellationToken token)
        { Inspections++; token.ThrowIfCancellationRequested(); return Task.FromResult(Inspect?.Invoke(change) ?? new BatchInspection(true, "db", "actor", "db:module", "review-" + change.PayloadHash)); }
        public Task<BatchDispatchResult> ApplyAsync(BatchChange change, BatchInspection reviewed, BatchOptions scope, CancellationToken token)
        { Calls++; Applied.Add(change); OnDispatch?.Invoke(); return Task.FromResult(Dispatch?.Invoke(change, Calls) ?? new BatchDispatchResult(BatchMutation.Applied, "Applied", resultingObjectId: Calls.ToString())); }
    }
    internal sealed class ProcessingTestClock : IProcessingClock
    {
        public DateTime Now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Now;
        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();
        public Task DelayAsync(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Delays.Add(delay); Now += delay; return Task.CompletedTask; }
    }
    internal sealed class ProcessingTestConfirmation : IBatchConfirmation
    {
        public bool Approved = true;
        public int Calls;
        public Action<BatchPreview> BeforeReturn;
        public Task<bool> ConfirmAsync(BatchPreview preview, CancellationToken token)
        { Calls++; BeforeReturn?.Invoke(preview); token.ThrowIfCancellationRequested(); return Task.FromResult(Approved); }
    }
    internal sealed class ProcessingTestAudit : IBatchAuditSink
    {
        public string FailPhase;
        public List<BatchAuditEntry> Entries { get; } = new List<BatchAuditEntry>();
        public void Record(BatchAuditEntry entry) { if (entry.Phase == FailPhase) throw new IOException("PRIVATE_AUDIT_FAILURE"); Entries.Add(entry); }
    }
    internal sealed class QualityTestSink : IQualityReportSink
    {
        public List<QualityObservation> Observations { get; } = new List<QualityObservation>();
        public QualitySummary Summary;
        public void Write(QualityObservation observation) => Observations.Add(observation);
        public void Complete(QualitySummary summary) => Summary = summary;
    }
    internal sealed class CoverageTestSink : ICoverageReportSink
    {
        public List<CoverageObservation> Observations { get; } = new List<CoverageObservation>();
        public CoverageSummary Summary;
        public void Write(CoverageObservation observation) => Observations.Add(observation);
        public void Complete(CoverageSummary summary) => Summary = summary;
    }
}
