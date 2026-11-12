using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class RequirementWriteScriptOptions
    {
        public RequirementWriteScriptOptions() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "requirement-write.dxl")) { }
        public RequirementWriteScriptOptions(string path)
        { ScriptPath = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); }
        public string ScriptPath { get; }
    }

    public sealed class DxlRequirementWriteGateway : IRequirementWriteGateway
    {
        private readonly IDxlExecutor executor;
        private readonly RequirementWriteScriptOptions scripts;
        private readonly IIntegrationLogger logger;
        private readonly IRequirementWriteAuditSink audit;
        public DxlRequirementWriteGateway(IDxlExecutor executor, RequirementWriteScriptOptions scripts,
            IIntegrationLogger logger, IRequirementWriteAuditSink audit)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }
        public async Task<RequirementModuleSchema> GetAttributeSchemaAsync(RequirementWriteTarget target, CancellationToken cancellationToken)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            return (await ExecuteAsync(target, null, null, cancellationToken).ConfigureAwait(false)).Schema;
        }
        public Task<RequirementWriteResult> PreviewAsync(RequirementWriteRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return ExecuteAsync(request.Target, request, null, cancellationToken);
        }
        public Task<RequirementWriteResult> CommitAsync(RequirementWritePreview preview, CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            cancellationToken.ThrowIfCancellationRequested();
            if (!preview.CanApply || !preview.TryBeginCommit())
                throw new IntegrationException(IntegrationErrorCode.RequirementWriteApprovalRequired, "Doğrudan yazma reddedildi; yeni önizleme ve açık onay gerekir.");
            var schema = preview.Inspection.Schema;
            try
            {
                audit.Record(new RequirementWriteAuditEntry("CommitDispatchRequested", preview.PlanId, Guid.Empty, preview.ApprovalFingerprint,
                    schema.ModulePath, schema.UserName, schema.DatabaseId, schema.QualifiedModuleId, request: preview.Request, inspection: preview.Inspection));
            }
            catch (Exception ex) { throw new IntegrationException(IntegrationErrorCode.RequirementWriteAuditFailed, "Audit kaydı başarısız; COM yazma çağrısı başlatılmadı.", ex); }
            return ExecuteAsync(preview.Request.Target, preview.Request, preview, cancellationToken);
        }

        private async Task<RequirementWriteResult> ExecuteAsync(RequirementWriteTarget target, RequirementWriteRequest request,
            RequirementWritePreview approved, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string mode = request == null ? "schema" : approved == null ? "preview" : "commit";
            var p = new Dictionary<string, string>
            {
                ["path"] = target.ModulePath, ["mode"] = mode, ["absolute"] = request?.AbsoluteNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
                ["position"] = request?.Position.Kind.ToString() ?? "Keep", ["anchor"] = request?.Position.AnchorAbsoluteNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
                ["attributes"] = request?.AttributePayload ?? "", ["required"] = request == null ? "" : string.Join("\n", request.RequiredAttributes),
                ["approvedPlan"] = approved?.PlanId.ToString("N") ?? "", ["expectedState"] = approved?.Inspection.BeforeState ?? ""
            };
            var execution = new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunFile, DxlResultMode.Inline,
                p, target.Timeout, target.ConnectionMode, target.ClientVersion, maxInlineResultChars: 1048576);
            var operation = executor.Start(execution);
            var completion = ParseCompletionAsync(operation, target, request, approved, mode);
            _ = completion.ContinueWith(t => { var observed = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            DxlLog.Write(logger, IntegrationLogLevel.Information, "RequirementWriteStarted", operation.CorrelationId,
                "Mode=" + mode + "; PlanId=" + (approved?.PlanId.ToString("N") ?? "None") + "; AutomaticRetry=False");
            try
            {
                await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (approved != null)
            {
                DxlLog.Write(logger, IntegrationLogLevel.Error, "RequirementWriteOutcomeUnknown", operation.CorrelationId,
                    "PlanId=" + approved.PlanId.ToString("N") + "; AutomaticRetry=False");
                try { audit.Record(new RequirementWriteAuditEntry("OutcomeUnknown", approved.PlanId, operation.CorrelationId,
                    approved.ApprovalFingerprint, target.ModulePath, approved.Inspection.Schema.UserName,
                    approved.Inspection.Schema.DatabaseId, approved.Inspection.Schema.QualifiedModuleId,
                    request: approved.Request, inspection: approved.Inspection)); }
                catch { /* Original uncertainty and its late completion must stay available. */ }
                throw new RequirementWriteUncertainException(approved.PlanId, operation.CorrelationId, completion, ex);
            }
        }

        private async Task<RequirementWriteResult> ParseCompletionAsync(DxlExecutionOperation operation, RequirementWriteTarget target,
            RequirementWriteRequest request, RequirementWritePreview approved, string mode)
        {
            using (var response = await operation.Completion.ConfigureAwait(false))
            {
                if (response.IsFile) throw RequirementWriteProtocol.Failure();
                var parsed = RequirementWriteProtocol.Parse(response.Text, operation.CorrelationId, target, request, approved, mode);
                if (approved != null)
                {
                    try
                    {
                        var schema = parsed.Schema;
                        audit.Record(new RequirementWriteAuditEntry("CommitCompleted", approved.PlanId, operation.CorrelationId,
                            approved.ApprovalFingerprint, target.ModulePath, schema.UserName, schema.DatabaseId, schema.QualifiedModuleId, parsed, approved.Request));
                    }
                    catch
                    {
                        parsed = parsed.WithAuditFailure();
                        DxlLog.Write(logger, IntegrationLogLevel.Error, "RequirementWriteAuditFailed", operation.CorrelationId,
                            "CommitMayHaveSucceeded=True; AutomaticRetry=False");
                    }
                }
                DxlLog.Write(logger, IntegrationLogLevel.Information, "RequirementWriteCompleted", operation.CorrelationId,
                    "Outcome=" + parsed.Outcome + "; Persistence=" + parsed.Persistence + "; Cleanup=" + parsed.Cleanup + "; AuditRecorded=" + parsed.AuditRecorded);
                return parsed;
            }
        }
    }
}
