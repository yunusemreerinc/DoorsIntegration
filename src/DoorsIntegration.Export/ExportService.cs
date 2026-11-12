using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class ExportService : IExportService
    {
        private readonly IReadOnlyDictionary<ExportFormat, IExportFormatWriter> writers;
        private readonly IIntegrationLogger logger;
        public ExportService(IEnumerable<IExportFormatWriter> writers, IIntegrationLogger logger)
        {
            if (writers == null) throw new ArgumentNullException(nameof(writers));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var map = new Dictionary<ExportFormat, IExportFormatWriter>();
            foreach (var writer in writers)
            {
                if (writer == null || map.ContainsKey(writer.Format)) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Her export biçimi için tek writer kaydedilmelidir.");
                map.Add(writer.Format, writer);
            }
            this.writers = map;
        }
        public Task<ExportResult> ExportAsync(ExportRequest request, IExportSource source, IProgress<ExportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!writers.TryGetValue(request.Format, out var writer)) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "İstenen export biçimi için writer bulunamadı.");
            // Do not pass the token to Task.Run: even pre-cancellation produces a report-only result.
            return Task.Run(() => new Job(request, source, writer, logger, progress, cancellationToken).Run());
        }
        private sealed class Job
        {
            private readonly ExportRequest request;
            private readonly IExportSource source;
            private readonly IExportFormatWriter writer;
            private readonly IIntegrationLogger logger;
            private readonly IProgress<ExportProgress> progress;
            private readonly CancellationToken token;
            private readonly Guid id = Guid.NewGuid();
            private readonly DateTime started = DateTime.UtcNow;
            private long read, accepted, skipped, issues, written;
            private long? estimated;
            private ExportPhase phase;
            private ExportWorkspace workspace;
            private ExportSpool spool;
            private ExportWriteContext context;
            public Job(ExportRequest request, IExportSource source, IExportFormatWriter writer, IIntegrationLogger logger, IProgress<ExportProgress> progress, CancellationToken token)
            { this.request = request; this.source = source; this.writer = writer; this.logger = logger; this.progress = progress; this.token = token; }
            public ExportResult Run()
            {
                bool published = false;
                try
                {
                    SetPhase(ExportPhase.Preparing); workspace = new ExportWorkspace(request.DestinationDirectory);
                    spool = new ExportSpool(workspace.SpoolDirectory, request);
                    context = new ExportWriteContext(workspace, spool, request, () => { if (++written % request.ProgressInterval == 0) Notify(); });
                    ExportStatus status;
                    try
                    {
                        token.ThrowIfCancellationRequested(); estimated = source.EstimatedRecordCount;
                        SetPhase(ExportPhase.Reading); Log("ExportStarted", "Format=" + request.Format);
                        foreach (var record in source.Read(token))
                        {
                            token.ThrowIfCancellationRequested();
                            if (read >= request.MaxRecords) throw ByteBudget.Limit();
                            read++;
                            try
                            {
                                var row = ExportNormalization.Normalize(record, read, request);
                                WriteRecord(record.Table, row); accepted++;
                                if (record.Table == ExportTableKind.Scopes)
                                {
                                    int index = ExportSchemas.Get(record.Table).Columns.ToList().FindIndex(column => column.Name == "isComplete");
                                    if (row[index] == "false") AddIssue(new ExportIssue(ExportIssueCode.IncompleteScope, "Warning", read, record.Table.ToString(), null));
                                }
                            }
                            catch (ExportRowException ex)
                            {
                                skipped++;
                                AddIssue(new ExportIssue(ex.Code, "Error", read, record?.Table.ToString(), ex.Column));
                                if (request.ErrorPolicy == ExportErrorPolicy.StopOnInvalidRow) throw new IntegrationException(IntegrationErrorCode.ExportFailed, "Geçersiz kaynak kaydı nedeniyle export durduruldu.", ex);
                            }
                            if (read % request.ProgressInterval == 0) Notify();
                        }
                        spool.Finish(); token.ThrowIfCancellationRequested(); SetPhase(ExportPhase.Writing);
                        writer.Write(context, token); token.ThrowIfCancellationRequested();
                        status = issues == 0 ? ExportStatus.Completed : ExportStatus.CompletedWithIssues;
                    }
                    catch (OperationCanceledException)
                    {
                        status = ExportStatus.Cancelled;
                        AddIssue(new ExportIssue(ExportIssueCode.Cancelled, "Error", null, null, null, (int)IntegrationErrorCode.DxlWaitCancelled), true);
                    }
                    catch (Exception ex) when (Recoverable(ex))
                    {
                        status = ExportStatus.Failed;
                        bool limit = ex is IntegrationException integration && integration.Code == IntegrationErrorCode.ExportLimitReached;
                        AddIssue(new ExportIssue(limit ? ExportIssueCode.LimitReached : phase == ExportPhase.Reading ? ExportIssueCode.SourceFailure : ExportIssueCode.WriteFailure,
                            "Error", null, null, null, ex is IntegrationException typed ? (int)typed.Code : (int)IntegrationErrorCode.ExportFailed, ex.GetType().FullName), true);
                        Log("ExportFailure", "ExceptionType=" + ex.GetType().FullName);
                    }
                    spool.Finish();
                    bool hasPayload = status == ExportStatus.Completed || status == ExportStatus.CompletedWithIssues;
                    if (!hasPayload) context.DeletePayload();
                    // Publication/report is the commit phase: cancellation no longer changes a completed artifact.
                    SetPhase(ExportPhase.Reporting); WriteReport(status, hasPayload);
                    var counts = spool.Counts; spool.Dispose(); workspace.DeleteSpools(); workspace.Publish(); published = true;
                    SetPhase(status == ExportStatus.Failed ? ExportPhase.Failed : status == ExportStatus.Cancelled ? ExportPhase.Cancelled : ExportPhase.Completed);
                    Log("ExportFinished", "Status=" + status + "; Records=" + accepted + "; Issues=" + issues);
                    return new ExportResult(id, status, request.DestinationDirectory, read, accepted, skipped, issues, context.ProtectedCells, counts);
                }
                catch (Exception ex) when (Recoverable(ex))
                {
                    Log("ExportPublicationFailure", "ExceptionType=" + ex.GetType().FullName);
                    if (ex is IntegrationException) throw;
                    throw new IntegrationException(IntegrationErrorCode.ExportFailed, "Export klasörü veya hata raporu yayımlanamadı. Hedef yolu ve dosya izinlerini kontrol edin.", ex);
                }
                finally
                {
                    if (!published)
                    {
                        try { spool?.Dispose(); } catch (Exception ex) when (Recoverable(ex)) { Log("ExportCleanupFailure", "ExceptionType=" + ex.GetType().FullName); }
                        try { workspace?.Discard(context?.Files ?? (IEnumerable<string>)Array.Empty<string>()); }
                        catch (Exception ex) when (Recoverable(ex)) { Log("ExportCleanupFailure", "ExceptionType=" + ex.GetType().FullName); }
                    }
                }
            }
            private void WriteRecord(ExportTableKind table, string[] values)
            {
                var schema = ExportSchemas.Get(table); var chunked = new List<int>();
                for (int i = 3; i < values.Length; i++) if (ExportNormalization.NeedsChunks(values[i])) chunked.Add(i);
                var main = (string[])values.Clone();
                main[1] = string.Join(",", chunked.Select(index => schema.Columns[index].Name));
                foreach (int index in chunked) main[index] = "[ValueChunks:" + values[0] + ":" + schema.Columns[index].Name + "]";
                spool.Write(table, main);
                foreach (int index in chunked)
                {
                    int part = 0;
                    foreach (var chunk in ExportNormalization.Chunks(values[index]))
                    {
                        token.ThrowIfCancellationRequested();
                        spool.Write(ExportTableKind.ValueChunks, new[] { (spool.Count(ExportTableKind.ValueChunks) + 1).ToString(CultureInfo.InvariantCulture), "", "", table.ToString(), values[0], schema.Columns[index].Name, (++part).ToString(CultureInfo.InvariantCulture), chunk });
                    }
                }
            }
            private void AddIssue(ExportIssue issue, bool fatal = false)
            {
                if (!fatal && issues >= request.MaxIssues) throw ByteBudget.Limit();
                issues++;
                spool.Write(ExportTableKind.Errors, new[] { issues.ToString(CultureInfo.InvariantCulture), "", "", issue.Code.ToString(), issue.Severity, issue.RecordId?.ToString(CultureInfo.InvariantCulture),
                    issue.Table, issue.Column, issue.ErrorCode?.ToString(CultureInfo.InvariantCulture), issue.ExceptionType, issue.Message });
            }
            private void WriteReport(ExportStatus status, bool hasPayload)
            {
                using (var stream = new BudgetStream(new FileStream(Path.Combine(workspace.Root, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None), new ByteBudget((long)(request.MaxIssues + 1) * 8192 + 1048576)))
                using (var output = new StreamWriter(stream, new UTF8Encoding(false, true), 16384))
                {
                    output.Write("{\"schemaVersion\":1,\"correlationId\":"); ExportJson.String(output, id.ToString("N"));
                    output.Write(",\"status\":"); ExportJson.String(output, status.ToString()); output.Write(",\"format\":"); ExportJson.String(output, request.Format.ToString());
                    output.Write(",\"startedAtUtc\":"); ExportJson.String(output, started.ToString("O", CultureInfo.InvariantCulture));
                    output.Write(",\"finishedAtUtc\":"); ExportJson.String(output, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    output.Write(",\"csvTextPrefix\":\"\\t\",\"csvPrefixDecoding\":\"RemoveExactlyOneLeadingTabFromTextFields\"");
                    output.Write(",\"hasPayload\":" + (hasPayload ? "true" : "false") + ",\"recordsRead\":" + read.ToString(CultureInfo.InvariantCulture) +
                        ",\"recordsAccepted\":" + accepted.ToString(CultureInfo.InvariantCulture) + ",\"recordsSkipped\":" + skipped.ToString(CultureInfo.InvariantCulture) + ",\"issueCount\":" + issues.ToString(CultureInfo.InvariantCulture) +
                        ",\"csvProtectedCellCount\":" + context.ProtectedCells.ToString(CultureInfo.InvariantCulture) + ",\"chunkCharacters\":4000,\"payloadFiles\":[");
                    bool first = true;
                    if (hasPayload) foreach (var file in context.Files.OrderBy(value => value, StringComparer.Ordinal))
                    { if (!first) output.Write(','); first = false; ExportJson.String(output, file); }
                    output.Write("],\"tables\":["); first = true;
                    foreach (var schema in spool.Schemas)
                    {
                        if (!first) output.Write(','); first = false; output.Write("{\"name\":"); ExportJson.String(output, schema.Kind.ToString());
                        output.Write(",\"stagedRows\":" + spool.Count(schema.Kind).ToString(CultureInfo.InvariantCulture) + ",\"columns\":[");
                        for (int i = 0; i < schema.Columns.Count; i++)
                        {
                            if (i > 0) output.Write(','); output.Write("{\"name\":"); ExportJson.String(output, schema.Columns[i].Name);
                            output.Write(",\"type\":"); ExportJson.String(output, schema.Columns[i].Type.ToString()); output.Write('}');
                        }
                        output.Write("]}");
                    }
                    output.Write("],\"issues\":["); first = true;
                    foreach (var row in spool.ReadRows(ExportTableKind.Errors, CancellationToken.None))
                    { if (!first) output.Write(','); first = false; ExportJson.Row(output, ExportSchemas.Get(ExportTableKind.Errors), row); }
                    output.Write("]}");
                }
            }
            private void SetPhase(ExportPhase value) { phase = value; Notify(); }
            private void Notify()
            {
                try { progress?.Report(new ExportProgress(id, phase, read, accepted, written, issues, estimated)); }
                catch (Exception ex) when (Recoverable(ex)) { Log("ExportProgressFailure", "ExceptionType=" + ex.GetType().FullName); }
            }
            private void Log(string name, string message)
            { try { logger.Write(IntegrationLogLevel.Information, name, id, message); } catch (Exception ex) when (Recoverable(ex)) { } }
            private static bool Recoverable(Exception ex) => !(ex is OutOfMemoryException || ex is StackOverflowException || ex is AccessViolationException || ex is ThreadAbortException);
        }
    }
}
