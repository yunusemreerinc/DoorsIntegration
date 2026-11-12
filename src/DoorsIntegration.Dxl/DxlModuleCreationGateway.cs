using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class ModuleCreationScriptOptions
    {
        public ModuleCreationScriptOptions() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "module-create.dxl")) { }
        public ModuleCreationScriptOptions(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Script path is required.", nameof(path));
            ScriptPath = Path.GetFullPath(path);
        }
        public string ScriptPath { get; }
    }

    public sealed class DxlModuleCreationGateway : IFormalModuleCreationGateway
    {
        private readonly IDxlExecutor executor;
        private readonly ModuleCreationScriptOptions scripts;
        private readonly IIntegrationLogger logger;
        public DxlModuleCreationGateway(IDxlExecutor executor, ModuleCreationScriptOptions scripts, IIntegrationLogger logger)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<FormalModuleCreationChecks> PreviewAsync(FormalModuleCreateRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return (await ExecuteAsync(request, null, cancellationToken).ConfigureAwait(false)).Checks;
        }

        public Task<FormalModuleCreationResult> CommitAsync(FormalModuleCreationPreview approvedPreview, CancellationToken cancellationToken)
        {
            if (approvedPreview == null) throw new ArgumentNullException(nameof(approvedPreview));
            cancellationToken.ThrowIfCancellationRequested();
            if (!approvedPreview.CanCreate || !approvedPreview.TryBeginCommit())
                throw new IntegrationException(IntegrationErrorCode.ModuleCreationApprovalRequired, "Yazma için yeni önizleme ve kullanıcı onayı gerekir; COM çağrısı başlatılmadı.");
            return ExecuteAsync(approvedPreview.Request, approvedPreview, cancellationToken);
        }

        private async Task<FormalModuleCreationResult> ExecuteAsync(FormalModuleCreateRequest request, FormalModuleCreationPreview approved, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = new Dictionary<string, string>
            {
                ["folder"] = request.TargetFolder, ["name"] = request.ModuleName, ["description"] = request.Description,
                ["mode"] = approved == null ? "preview" : "commit", ["approvedPlan"] = approved?.PlanId.ToString("N") ?? "",
                ["expectedDatabase"] = approved?.Checks.DatabaseId ?? "", ["expectedUser"] = approved?.Checks.UserName ?? "",
                ["expectedFolderId"] = approved?.Checks.FolderId ?? ""
            };
            var execution = new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunString, DxlResultMode.Inline,
                parameters, request.Timeout, request.ConnectionMode, request.Target, maxInlineResultChars: 32768);
            // Start may fail before dispatch (e.g. busy gate); that is a definite failure, not a write outcome.
            var operation = executor.Start(execution);
            var completion = ParseCompletionAsync(operation, request, approved);
            _ = completion.ContinueWith(task => { var observed = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            DxlLog.Write(logger, IntegrationLogLevel.Information, "ModuleCreationStarted", operation.CorrelationId,
                "Mode=" + parameters["mode"] + "; PlanId=" + (approved?.PlanId.ToString("N") ?? "None"));
            try
            {
                // ParseCompletionAsync owns and disposes the result, including any late completion.
                await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (approved != null)
            {
                DxlLog.Write(logger, IntegrationLogLevel.Error, "ModuleCreationOutcomeUnknown", operation.CorrelationId,
                    "PlanId=" + approved.PlanId.ToString("N") + "; AutomaticRetry=False");
                throw new ModuleCreationUncertainException(approved.PlanId, operation.CorrelationId, completion, ex);
            }
        }

        private async Task<FormalModuleCreationResult> ParseCompletionAsync(DxlExecutionOperation operation, FormalModuleCreateRequest request, FormalModuleCreationPreview approved)
        {
            using (var result = await operation.Completion.ConfigureAwait(false))
            {
                if (result.IsFile) throw ModuleCreationProtocol.Failure();
                var parsed = ModuleCreationProtocol.Parse(result.Text, result.CorrelationId, request, approved);
                DxlLog.Write(logger, IntegrationLogLevel.Information, "ModuleCreationCompleted", operation.CorrelationId,
                    "Outcome=" + parsed.Outcome + "; Cleanup=" + parsed.Cleanup + "; " + parsed.Checks.Summary);
                return parsed;
            }
        }
    }
}
