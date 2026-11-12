using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DxlExecutor : IDxlExecutor
    {
        private readonly IDxlSessionFactory sessions;
        private readonly IDxlScriptLoader scripts;
        private readonly IDxlWorkspaceFactory workspaces;
        private readonly IIntegrationLogger logger;
        private readonly IDoorsOperationGate gate;
        private readonly IDxlRunLimitAutomation runLimitAutomation;
        private readonly DxlWatchdogSettings watchdogSettings;
        private readonly IDoorsStaExecutionQueue staQueue;
        private DxlExecutionOperation activeOperation;

        public DxlExecutor(IDxlSessionFactory sessions, IDxlScriptLoader scripts, IDxlWorkspaceFactory workspaces,
            IIntegrationLogger logger, IDoorsOperationGate gate)
            : this(sessions, scripts, workspaces, logger, gate, new NoDxlRunLimitAutomation()) { }

        public DxlExecutor(IDxlSessionFactory sessions, IDxlScriptLoader scripts, IDxlWorkspaceFactory workspaces,
            IIntegrationLogger logger, IDoorsOperationGate gate, IDxlRunLimitAutomation runLimitAutomation)
            : this(sessions, scripts, workspaces, logger, gate, runLimitAutomation, new DxlWatchdogSettings()) { }

        public DxlExecutor(IDxlSessionFactory sessions, IDxlScriptLoader scripts, IDxlWorkspaceFactory workspaces,
            IIntegrationLogger logger, IDoorsOperationGate gate, IDxlRunLimitAutomation runLimitAutomation,
            DxlWatchdogSettings watchdogSettings)
            : this(sessions, scripts, workspaces, logger, gate, runLimitAutomation, watchdogSettings, null) { }

        public DxlExecutor(IDxlSessionFactory sessions, IDxlScriptLoader scripts, IDxlWorkspaceFactory workspaces,
            IIntegrationLogger logger, IDoorsOperationGate gate, IDxlRunLimitAutomation runLimitAutomation,
            DxlWatchdogSettings watchdogSettings, IDoorsStaExecutionQueue staQueue)
        {
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
            this.runLimitAutomation = runLimitAutomation ?? throw new ArgumentNullException(nameof(runLimitAutomation));
            this.watchdogSettings = watchdogSettings ?? throw new ArgumentNullException(nameof(watchdogSettings));
            this.staQueue = staQueue;
        }
        public DxlExecutionOperation ActiveOperation => Volatile.Read(ref activeOperation);
        public bool IsBusy => gate.IsBusy;
        public bool UsesStaQueue => staQueue != null;

        private static void CheckDispatchPrecondition(DxlExecutionRequest request)
        {
            if (request.DispatchPrecondition != null && !request.DispatchPrecondition())
                throw new IntegrationException(IntegrationErrorCode.BaselineExecutionFailed, "Dispatch approval/evidence expired or changed; native script was not invoked.");
        }

        public DxlExecutionOperation Start(DxlExecutionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var lease = gate.Enter();
            var operation = new DxlExecutionOperation(Guid.NewGuid(), logger, request.Timeout, watchdogSettings.Enabled);
            Volatile.Write(ref activeOperation, operation);
            try
            {
                if (staQueue != null)
                {
                    staQueue.Enqueue(() => ExecuteWorker(request, operation, lease));
                    return operation;
                }
                var worker = new Thread(() => ExecuteWorker(request, operation, lease))
                {
                    IsBackground = true, Name = "DOORS DXL executor STA"
                };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref activeOperation, null);
                lease.Dispose();
                operation.Fail(Normalize(ex));
            }
            return operation;
        }

        public async Task<DxlExecutionResult> ExecuteAsync(DxlExecutionRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = Start(request);
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void ExecuteWorker(DxlExecutionRequest request, DxlExecutionOperation operation, IDisposable lease)
        {
            var elapsed = Stopwatch.StartNew();
            IDxlExecutionSession session = null;
            IDxlWorkspace workspace = null;
            DxlExecutionResult result = null;
            IntegrationException failure = null;
            DxlLog.Write(logger, IntegrationLogLevel.Information, "DxlStarted", operation.CorrelationId,
                "Method=" + request.Method + "; ResultMode=" + request.ResultMode + "; ConnectionMode=" + request.ConnectionMode +
                "; TargetProfile=" + DoorsVersions.Format(request.Target));
            DxlLog.Write(logger, IntegrationLogLevel.Information, "DxlTimeoutConfigured", operation.CorrelationId,
                "TimeoutMs=" + operation.EffectiveTimeout.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DxlLog.Write(logger, IntegrationLogLevel.Information, "DxlWatchdogConfigured", operation.CorrelationId,
                "Enabled=" + operation.WatchdogEnabled + "; CycleLimit=" + (operation.WatchdogEnabled ? DxlWatchdogSettings.EnabledCycleLimit : 0));
            try
            {
                var source = scripts.Load(request.ScriptPath, request.MaxScriptBytes);
                CheckDispatchPrecondition(request);
                operation.CheckBeforeDispatch();
                workspace = workspaces.Create(operation.CorrelationId);
                operation.SetWorkspace(workspace.DirectoryPath);
                var script = DxlExecutionProtocol.Compose(request, operation.CorrelationId, source, workspace.ResultPath, operation.WatchdogEnabled);
                request.DebugCapture?.Prepare(request, operation.CorrelationId, source, script);
                if (request.Method == DxlInvocationMethod.RunFile) workspace.WriteScript(script);
                operation.CheckBeforeDispatch();
                session = sessions.Open(request.ConnectionMode);
                CheckDispatchPrecondition(request);
                operation.CheckBeforeDispatch();
                session.SetResult(DxlExecutionProtocol.Pending(operation.CorrelationId));
                operation.CheckBeforeDispatch();
                var automation = operation.WatchdogEnabled ? runLimitAutomation : new NoDxlRunLimitAutomation();
                using (automation.Watch(operation.CorrelationId, () => !operation.PromptAutomationCancelled))
                {
                    operation.CheckBeforeDispatch();
                    CheckDispatchPrecondition(request);
                    if (request.Method == DxlInvocationMethod.RunString) session.RunString(script);
                    else session.RunFile(workspace.ScriptPath);
                }
                var rawResponse = session.ReadResult();
                request.DebugCapture?.ComResponse(rawResponse);
                var text = DxlExecutionProtocol.ValidateResponse(request, operation.CorrelationId, rawResponse);
                if (request.ResultMode == DxlResultMode.File)
                {
                    var fileLease = workspace.OpenResult(request.MaxResultFileBytes);
                    try { result = new DxlExecutionResult(operation.CorrelationId, workspace, fileLease, request.MaxResultFileBytes); }
                    catch { fileLease.Dispose(); throw; }
                }
                else result = new DxlExecutionResult(operation.CorrelationId, text);
            }
            catch (Exception ex)
            {
                request.DebugCapture?.Failed(ex, "DxlExecutionOrTransportFailure");
                failure = Normalize(ex);
            }
            finally
            {
                try { session?.Dispose(); }
                catch (Exception ex) { RecordCleanupFailure(ex, operation, ref failure); }

                if (result != null && result.IsFile)
                {
                    // Transfer workspace ownership only after COM cleanup succeeds.
                    if (failure != null)
                    {
                        try { result.Dispose(); }
                        catch (Exception ex) { RecordCleanupFailure(ex, operation, ref failure); }
                    }
                    workspace = null;
                }
                try { workspace?.Dispose(); }
                catch (Exception ex) { RecordCleanupFailure(ex, operation, ref failure); }
                Volatile.Write(ref activeOperation, null);
                try { lease.Dispose(); }
                catch (Exception ex) { RecordCleanupFailure(ex, operation, ref failure); }
            }

            if (failure != null)
            {
                try { result?.Dispose(); }
                catch (Exception ex) { RecordCleanupFailure(ex, operation, ref failure); }
                DxlLog.Write(logger, IntegrationLogLevel.Error, "DxlFailed", operation.CorrelationId,
                    "Code=" + failure.Code + "; ExceptionType=" + failure.GetBaseException().GetType().FullName +
                    "; HResult=0x" + failure.GetBaseException().HResult.ToString("X8") + "; ElapsedMs=" + elapsed.ElapsedMilliseconds);
                operation.Fail(failure);
            }
            else
            {
                DxlLog.Write(logger, IntegrationLogLevel.Information, "DxlCompleted", operation.CorrelationId,
                    "Status=OK; ElapsedMs=" + elapsed.ElapsedMilliseconds);
                operation.Complete(result);
            }
        }

        private void RecordCleanupFailure(Exception exception, DxlExecutionOperation operation, ref IntegrationException failure)
        {
            if (failure == null) failure = Normalize(exception);
            DxlLog.Write(logger, IntegrationLogLevel.Error, "DxlCleanupFailed", operation.CorrelationId,
                "ExceptionType=" + exception.GetType().FullName + "; HResult=0x" + exception.HResult.ToString("X8"));
        }

        private static IntegrationException Normalize(Exception exception) => exception as IntegrationException ??
            new IntegrationException(IntegrationErrorCode.Unexpected, "Beklenmeyen DXL yürütme hatası. İşlem kaydını kontrol edin.", exception);
    }
}
