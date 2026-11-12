using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class LinkWriteScriptOptions
    {
        public LinkWriteScriptOptions() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "link-write.dxl")) { }
        public LinkWriteScriptOptions(string path) { ScriptPath = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); }
        public string ScriptPath { get; }
    }
    public sealed class DxlLinkWriteGateway : ILinkWriteGateway
    {
        private readonly IDxlExecutor executor;
        private readonly LinkWriteScriptOptions scripts;
        private readonly IIntegrationLogger logger;
        private readonly ILinkWriteAuditSink audit;
        public DxlLinkWriteGateway(IDxlExecutor executor, LinkWriteScriptOptions scripts, IIntegrationLogger logger, ILinkWriteAuditSink audit)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }
        public Task<LinkWriteResult> PreviewAsync(LinkWriteRequest request, CancellationToken cancellationToken)
        { if (request == null) throw new ArgumentNullException(nameof(request)); return ExecuteAsync(request, null, cancellationToken); }
        public Task<LinkWriteResult> CommitAsync(LinkWritePreview preview, CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            cancellationToken.ThrowIfCancellationRequested();
            if (!preview.CanApply || !preview.TryBeginCommit()) throw new IntegrationException(IntegrationErrorCode.LinkWriteApprovalRequired,
                "Direct link write rejected. A fresh dry-run and all explicit approvals are required.");
            try { audit.Record(new LinkWriteAuditEntry("CommitDispatchRequested", preview)); }
            catch (Exception ex) { throw new IntegrationException(IntegrationErrorCode.LinkWriteAuditFailed, "Audit failed; COM write was not dispatched.", ex); }
            return ExecuteAsync(preview.Request, preview, cancellationToken);
        }
        private async Task<LinkWriteResult> ExecuteAsync(LinkWriteRequest request, LinkWritePreview approved, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var p = new Dictionary<string, string>
            {
                ["mode"] = approved == null ? "preview" : "commit", ["action"] = request.Action.ToString(),
                ["sourcePath"] = request.SourceModulePath, ["sourceAbs"] = request.SourceAbsoluteNumber.ToString(CultureInfo.InvariantCulture),
                ["targetPath"] = request.TargetModulePath, ["targetAbs"] = request.TargetAbsoluteNumber.ToString(CultureInfo.InvariantCulture),
                ["linkPath"] = request.LinkModulePath, ["maxLinks"] = request.MaxTraceLinks.ToString(CultureInfo.InvariantCulture),
                ["approvedPlan"] = approved?.PlanId.ToString("N") ?? "", ["expectedState"] = approved?.Inspection.BeforeState ?? ""
            };
            var execution = new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunFile, DxlResultMode.Inline, p,
                request.Timeout, request.ConnectionMode, request.ClientVersion, maxInlineResultChars: 1048576);
            // Start failures are definite pre-dispatch failures. Once started, cancellation cannot prove no mutation.
            var operation = executor.Start(execution);
            var completion = ParseCompletionAsync(operation, request, approved);
            _ = completion.ContinueWith(t => { var observed = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            DxlLog.Write(logger, IntegrationLogLevel.Information, "LinkWriteStarted", operation.CorrelationId,
                "Mode=" + p["mode"] + "; Action=" + request.Action + "; PlanId=" + (approved?.PlanId.ToString("N") ?? "None") + "; AutomaticRetry=False");
            try { await operation.WaitAsync(token).ConfigureAwait(false); return await completion.ConfigureAwait(false); }
            catch (Exception ex) when (approved != null)
            {
                DxlLog.Write(logger, IntegrationLogLevel.Error, "LinkWriteOutcomeUnknown", operation.CorrelationId, "AutomaticRetry=False");
                try { audit.Record(new LinkWriteAuditEntry("OutcomeUnknown", approved, operationId: operation.CorrelationId)); } catch { /* Retain original uncertainty. */ }
                throw new LinkWriteUncertainException(approved.PlanId, operation.CorrelationId, completion, ex);
            }
        }
        private async Task<LinkWriteResult> ParseCompletionAsync(DxlExecutionOperation operation, LinkWriteRequest request, LinkWritePreview approved)
        {
            using (var response = await operation.Completion.ConfigureAwait(false))
            {
                if (response.IsFile) throw LinkWriteProtocol.Failure();
                var parsed = LinkWriteProtocol.Parse(response.Text, operation.CorrelationId, request, approved);
                if (approved != null)
                {
                    try { audit.Record(new LinkWriteAuditEntry("CommitCompleted", approved, parsed)); }
                    catch { parsed = parsed.WithAuditFailure(); }
                }
                DxlLog.Write(logger, IntegrationLogLevel.Information, "LinkWriteCompleted", operation.CorrelationId,
                    "Outcome=" + parsed.Outcome + "; Persistence=" + parsed.Persistence + "; Cleanup=" + parsed.Cleanup + "; AuditRecorded=" + parsed.AuditRecorded);
                return parsed;
            }
        }
    }
}
