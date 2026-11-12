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
    public sealed class RequirementScriptOptions
    {
        public RequirementScriptOptions() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "requirements.dxl")) { }
        public RequirementScriptOptions(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Script path is required.", nameof(path));
            ScriptPath = Path.GetFullPath(path);
        }
        public string ScriptPath { get; }
    }

    public sealed class DxlRequirementReader : IRequirementReader
    {
        private readonly IDxlExecutor executor;
        private readonly RequirementScriptOptions scripts;
        private readonly IIntegrationLogger logger;
        public DxlRequirementReader(IDxlExecutor executor, RequirementScriptOptions scripts, IIntegrationLogger logger)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public async Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (cancellationToken.IsCancellationRequested) throw Cancelled();
            var parameters = new Dictionary<string, string>
            {
                ["path"] = request.ModulePath, ["attributes"] = string.Join("\n", request.CustomAttributes) + (request.CustomAttributes.Count == 0 ? "" : "\n"),
                ["version"] = request.Baseline == null ? "Current" : "Baseline", ["major"] = request.Baseline?.Major.ToString(CultureInfo.InvariantCulture) ?? "",
                ["minor"] = request.Baseline?.Minor.ToString(CultureInfo.InvariantCulture) ?? "", ["suffix"] = request.Baseline?.Suffix ?? "",
                ["deleted"] = request.DeletedObjects.ToString(), ["allowDxl"] = request.AllowAttributeDxl ? "1" : "0",
                ["open"] = request.OpenIfNeeded ? "1" : "0", ["maxObjects"] = request.MaxObjects.ToString(CultureInfo.InvariantCulture),
                ["maxField"] = request.MaxFieldCharacters.ToString(CultureInfo.InvariantCulture), ["maxObject"] = request.MaxObjectCharacters.ToString(CultureInfo.InvariantCulture),
                ["maxFile"] = request.MaxFileBytes.ToString(CultureInfo.InvariantCulture)
            };
            var execution = new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunFile, DxlResultMode.File,
                parameters, request.Timeout, request.ConnectionMode, request.Target, maxResultFileBytes: request.MaxFileBytes);
            var operation = executor.Start(execution);
            DxlLog.Write(logger, IntegrationLogLevel.Information, "RequirementsStarted", operation.CorrelationId,
                "Version=" + parameters["version"] + "; Attributes=" + request.CustomAttributes.Count + "; Deleted=" + request.DeletedObjects);
            DxlExecutionResult result = null;
            try
            {
                result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsFile) throw RequirementProtocol.Failure();
                RequirementReadSummary summary;
                using (var stream = result.OpenResultStream()) summary = RequirementProtocol.Validate(stream, request, result.CorrelationId, cancellationToken);
                DxlLog.Write(logger, IntegrationLogLevel.Information, "RequirementsCompleted", result.CorrelationId,
                    "Status=" + summary.ModuleStatus + "; Objects=" + summary.ObjectCount + "; FieldProblems=" + summary.FieldProblemCount + "; Truncated=" + summary.IsTruncated);
                var snapshot = new RequirementSnapshot(result, summary);
                result = null; // Transfer local-file ownership only after the entire file has been validated.
                return snapshot;
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
            catch (DecoderFallbackException ex) { throw RequirementProtocol.Failure(ex); }
            catch (OperationCanceledException ex) { throw Cancelled(ex); }
            catch (IOException ex) { throw DxlWorkspaceFactory.FileFailure(ex); }
            finally { if (result != null) Cleanup(result, operation.CorrelationId); }
        }

        private void Cleanup(DxlExecutionResult result, Guid id)
        {
            try { result.Dispose(); }
            catch (Exception ex) { DxlLog.Write(logger, IntegrationLogLevel.Error, "RequirementCleanupFailed", id, "ExceptionType=" + ex.GetType().FullName); }
        }
        private static IntegrationException Cancelled(Exception cause = null) => new IntegrationException(IntegrationErrorCode.DxlWaitCancelled,
            "Gereksinim okuma beklemesi iptal edildi. Başlamış COM çağrısı zorla kesilmedi.", cause);
    }
}
