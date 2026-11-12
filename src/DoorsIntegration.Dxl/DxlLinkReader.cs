using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class LinkScriptOptions
    {
        public LinkScriptOptions() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "links.dxl")) { }
        public LinkScriptOptions(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Script path is required.", nameof(path));
            ScriptPath = Path.GetFullPath(path);
        }
        public string ScriptPath { get; }
    }
    public sealed class DxlLinkReader : ILinkReader
    {
        private readonly IDxlExecutor executor;
        private readonly LinkScriptOptions scripts;
        private readonly IIntegrationLogger logger;
        public DxlLinkReader(IDxlExecutor executor, LinkScriptOptions scripts, IIntegrationLogger logger)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public async Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (cancellationToken.IsCancellationRequested) throw Cancelled();
            var parameters = new Dictionary<string, string>
            {
                ["path"] = request.ModulePath, ["linkPath"] = request.LinkModulePath, ["direction"] = request.Direction.ToString(),
                ["version"] = request.Baseline == null ? "Current" : "Baseline", ["major"] = request.Baseline?.Major.ToString(CultureInfo.InvariantCulture) ?? "",
                ["minor"] = request.Baseline?.Minor.ToString(CultureInfo.InvariantCulture) ?? "", ["suffix"] = request.Baseline?.Suffix ?? "",
                ["open"] = request.OpenIfNeeded ? "1" : "0", ["resolve"] = request.ResolveEndpoints ? "1" : "0", ["deleted"] = request.IncludeDeletedObjects ? "1" : "0",
                ["marker"] = request.SurrogateMapping?.MarkerAttribute ?? "", ["markerValue"] = request.SurrogateMapping?.MarkerValue ?? "",
                ["reference"] = request.SurrogateMapping?.ReferenceAttribute ?? "", ["maxObjects"] = request.MaxObjects.ToString(CultureInfo.InvariantCulture),
                ["maxLinks"] = request.MaxLinks.ToString(CultureInfo.InvariantCulture), ["maxModules"] = request.MaxLinkedModules.ToString(CultureInfo.InvariantCulture),
                ["maxFile"] = request.MaxFileBytes.ToString(CultureInfo.InvariantCulture)
            };
            var operation = executor.Start(new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunFile, DxlResultMode.File,
                parameters, request.Timeout, request.ConnectionMode, request.Target, maxResultFileBytes: request.MaxFileBytes));
            DxlLog.Write(logger, IntegrationLogLevel.Information, "LinksStarted", operation.CorrelationId, "Direction=" + request.Direction);
            DxlExecutionResult result = null;
            try
            {
                result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsFile) throw LinkProtocol.Failure();
                LinkReadSummary summary;
                using (var stream = result.OpenResultStream()) summary = LinkProtocol.Validate(stream, request, result.CorrelationId, cancellationToken);
                DxlLog.Write(logger, IntegrationLogLevel.Information, "LinksCompleted", result.CorrelationId,
                    "Status=" + summary.ModuleStatus + "; Records=" + summary.RecordCount + "; Unresolved=" + summary.UnresolvedLinkCount + "; SourceReferences=" + summary.SourceReferenceCount + "; Truncated=" + summary.IsTruncated);
                var snapshot = new LinkSnapshot(result, summary); result = null; return snapshot;
            }
            catch (DxlExecutionWaitException)
            {
                _ = operation.Completion.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) Cleanup(task.Result, operation.CorrelationId);
                    else { var observed = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (DecoderFallbackException ex) { throw LinkProtocol.Failure(ex); }
            catch (OperationCanceledException ex) { throw Cancelled(ex); }
            catch (IOException ex) { throw DxlWorkspaceFactory.FileFailure(ex); }
            finally { if (result != null) Cleanup(result, operation.CorrelationId); }
        }
        private void Cleanup(DxlExecutionResult result, Guid id)
        {
            try { result.Dispose(); }
            catch (Exception ex) { DxlLog.Write(logger, IntegrationLogLevel.Error, "LinkCleanupFailed", id, "ExceptionType=" + ex.GetType().FullName); }
        }
        private static IntegrationException Cancelled(Exception cause = null) => new IntegrationException(IntegrationErrorCode.DxlWaitCancelled,
            "Link okuma beklemesi iptal edildi. Başlamış COM çağrısı zorla kesilmedi.", cause);
    }
    internal sealed class LinkSnapshot : ILinkSnapshot
    {
        private readonly DxlFileLease lease;
        public LinkSnapshot(DxlExecutionResult result, LinkReadSummary summary) { lease = new DxlFileLease(result); Summary = summary; }
        public LinkReadSummary Summary { get; }
        public Stream OpenRawStream() => lease.Open();
        public void Dispose() => lease.Dispose();
        public IEnumerable<LinkRecord> ReadLinks()
        {
            using (var stream = OpenRawStream())
            using (var iterator = LinkProtocol.Parse(stream, Summary.Request, Summary.CorrelationId, CancellationToken.None, ignored => { }).GetEnumerator())
            {
                while (true)
                {
                    bool next;
                    try { next = iterator.MoveNext(); }
                    catch (DecoderFallbackException ex) { throw LinkProtocol.Failure(ex); }
                    catch (IOException ex) { throw DxlWorkspaceFactory.FileFailure(ex); }
                    if (!next) yield break;
                    yield return iterator.Current;
                }
            }
        }
    }
}
