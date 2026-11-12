using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DoorsSmokeProbe : IDoorsSmokeProbe
    {
        private readonly IDxlSessionFactory sessions;
        private readonly IDxlProbeProtocol protocol;
        private readonly IDxlWorkspaceFactory workspaces;
        private readonly IDoorsOperationGate gate;
        private readonly ModuleService modules;
        private readonly ILinkReader links;
        public DoorsSmokeProbe(IDxlSessionFactory sessions, IDxlProbeProtocol protocol, IDxlWorkspaceFactory workspaces,
            IDoorsOperationGate gate, IDoorsHierarchyReader hierarchy, ILinkReader links)
        { this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions)); this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces)); this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
            modules = new ModuleService(hierarchy ?? throw new ArgumentNullException(nameof(hierarchy))); this.links = links ?? throw new ArgumentNullException(nameof(links)); }
        public SmokeOperationSemantics Semantics => SmokeOperationSemantics.IdempotentReadOnly;
        public SmokeProbeOperation Start(SmokeTest test, DoorsSmokeRequest request, Guid correlationId)
        {
            if (test <= SmokeTest.ArchitectureCompatibility || !Enum.IsDefined(typeof(SmokeTest), test)) throw new ArgumentOutOfRangeException(nameof(test));
            var stop = new CancellationTokenSource();
            var completion = new TaskCompletionSource<SmokeProbeEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = new SmokeProbeOperation(completion.Task, () => { if (!completion.Task.IsCompleted) stop.Cancel(); });
            if (test == SmokeTest.ModuleListing || test == SmokeTest.LinkRead)
            {
                _ = Task.Run(async () =>
                {
                    try { completion.SetResult(await ReadAsync(test, request, stop.Token, operation).ConfigureAwait(false)); }
                    catch (Exception ex) { completion.SetException(ex); }
                    finally { stop.Dispose(); }
                });
            }
            else
            {
                var thread = new Thread(() =>
                {
                    IDxlExecutionSession session = null; IDxlWorkspace workspace = null; IDisposable lease = null; Exception failure = null;
                    var data = new Dictionary<string, string> { ["Apartment"] = Thread.CurrentThread.GetApartmentState().ToString(), ["ConnectionMode"] = "AttachExisting",
                        ["DatabaseMutation"] = "None", ["AutomaticRetries"] = "0" };
                    try
                    {
                        stop.Token.ThrowIfCancellationRequested(); lease = gate.Enter();
                        session = Measure(operation, "COM_CONNECT", () => { stop.Token.ThrowIfCancellationRequested(); return sessions.Open(DoorsConnectionMode.AttachExisting); });
                        stop.Token.ThrowIfCancellationRequested();
                        if (test != SmokeTest.ComSession)
                        {
                            string marker = "SMOKE_PENDING|" + correlationId.ToString("N");
                            Measure(operation, "RESULT_SET", () => { session.SetResult(marker); return true; });
                            stop.Token.ThrowIfCancellationRequested();
                            if (test == SmokeTest.Result)
                            {
                                string stored = Measure(operation, "RESULT_PROPERTY_ROUNDTRIP", session.ReadResult);
                                if (!string.Equals(stored, marker, StringComparison.Ordinal)) throw new IntegrationException(IntegrationErrorCode.DxlProtocolFailure, "Result property did not return this test's marker.");
                            }
                            stop.Token.ThrowIfCancellationRequested(); string script = protocol.CreateScript(request.Target, correlationId);
                            if (test == SmokeTest.RunFile)
                            {
                                workspace = Measure(operation, "UTF8_FILE_PREPARE", () => workspaces.Create(correlationId));
                                workspace.WriteScript(script); stop.Token.ThrowIfCancellationRequested();
                                Measure(operation, "RUNFILE", () => { session.RunFile(workspace.ScriptPath); return true; });
                            }
                            else Measure(operation, "RUNSTR", () => { session.RunString(script); return true; });
                            stop.Token.ThrowIfCancellationRequested();
                            string response = Measure(operation, "RESULT_GET", session.ReadResult);
                            Measure(operation, "OLESETRESULT_NONCE_VALIDATE", () => { protocol.ValidateResponse(correlationId, response); return true; });
                            data["FreshNonceMatched"] = "True";
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        try { if (session != null) Measure(operation, "COM_CLEANUP", () => { session.Dispose(); return true; }); }
                        catch (Exception ex) { failure = new IntegrationException(IntegrationErrorCode.ComFailure, "Original COM reference cleanup failed; session requires review.", ex); }
                        try { if (workspace != null) Measure(operation, "LOCAL_FILE_CLEANUP", () => { workspace.Dispose(); return true; }); }
                        catch (Exception ex) { if (failure == null) failure = new IntegrationException(IntegrationErrorCode.DxlResultFileFailed, "Owned local smoke workspace cleanup failed.", ex); }
                        try { lease?.Dispose(); } catch (Exception ex) { failure = new IntegrationException(IntegrationErrorCode.ComFailure, "Native gate cleanup failed.", ex); }
                        stop.Dispose();
                    }
                    if (failure == null) completion.SetResult(new SmokeProbeEvidence(data)); else completion.SetException(failure);
                }) { IsBackground = true, Name = "DOORS smoke STA " + correlationId.ToString("N") };
                thread.SetApartmentState(ApartmentState.STA);
                try { thread.Start(); } catch (Exception ex) { stop.Dispose(); completion.SetException(ex); }
            }
            return operation;
        }
        private async Task<SmokeProbeEvidence> ReadAsync(SmokeTest test, DoorsSmokeRequest request, CancellationToken token, SmokeProbeOperation operation)
        {
            var timer = Stopwatch.StartNew(); operation.Record(new SmokeLogEntry("READ_ONLY_SCOPE", "STARTED", 0, test.ToString()));
            try
            {
                token.ThrowIfCancellationRequested();
                if (test == SmokeTest.ModuleListing)
                {
                    var result = await modules.DiscoverFormalModulesAsync(new DiscoveryScope(request.RootPath, maxItems: request.MaxItems,
                        timeout: request.Timeout, target: request.Target, connectionMode: DoorsConnectionMode.AttachExisting), token).ConfigureAwait(false);
                    if (result.Issues.Count != 0 || result.IsTruncated || !result.IsCompleteForVisibleScope)
                        throw new SmokeProbeFailure("ModuleListingIncomplete_ReviewBoundsAndAccess", new SmokeProbeEvidence(new Dictionary<string,string> {
                            ["FormalModuleCount"] = result.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Truncated"] = result.IsTruncated.ToString(),
                            ["IssueCodes"] = string.Join(",", System.Linq.Enumerable.Select(result.Issues, row => row.Code.ToString())), ["DxlRequestId"] = result.CorrelationId.ToString("N") }));
                    return new SmokeProbeEvidence(new Dictionary<string, string> { ["FormalModuleCount"] = result.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["IsCompleteForVisibleScope"] = result.IsCompleteForVisibleScope.ToString(), ["DxlRequestId"] = result.CorrelationId.ToString("N"), ["ModuleOpenedForEdit"] = "False" });
                }
                using (var result = await links.ReadAsync(new LinkReadRequest(request.ModulePath, request.LinkModulePath, maxLinks: request.MaxLinks,
                    timeout: request.Timeout, target: request.Target, connectionMode: DoorsConnectionMode.AttachExisting), token).ConfigureAwait(false))
                {
                    if (result.Summary.ModuleStatus != RequirementModuleStatus.Available || result.Summary.LinkModuleStatus != LinkModuleStatus.Available ||
                        result.Summary.IsTruncated || result.Summary.EnumerationFailed || result.Summary.CleanupFailureCount != 0)
                        throw new SmokeProbeFailure("LinkReadIncomplete_ReviewModuleAccessBoundsAndCleanup", new SmokeProbeEvidence(new Dictionary<string,string> {
                            ["ModuleStatus"] = result.Summary.ModuleStatus.ToString(), ["LinkModuleStatus"] = result.Summary.LinkModuleStatus.ToString(),
                            ["Truncated"] = result.Summary.IsTruncated.ToString(), ["EnumerationFailed"] = result.Summary.EnumerationFailed.ToString(),
                            ["CleanupFailures"] = result.Summary.CleanupFailureCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                    int count = 0; foreach (var item in result.ReadLinks()) { token.ThrowIfCancellationRequested(); count++; }
                    if (count != result.Summary.RecordCount) throw new SmokeProbeFailure("LinkRecordCountMismatch", new SmokeProbeEvidence());
                    if (!result.Summary.IsCompleteForReadableScope) operation.Record(new SmokeLogEntry("LINK_VISIBILITY", "PARTIAL", timer.Elapsed.TotalMilliseconds,
                        "Transport read completed; endpoint/reference/permission visibility is incomplete. No coverage claim."));
                    return new SmokeProbeEvidence(new Dictionary<string, string> { ["RecordsRead"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["IsCompleteForReadableScope"] = result.Summary.IsCompleteForReadableScope.ToString(), ["UnresolvedLinks"] = result.Summary.UnresolvedLinkCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["IncomingReferences"] = result.Summary.SourceReferenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture), ["DeniedObjects"] = result.Summary.DeniedObjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["DxlRequestId"] = result.Summary.CorrelationId.ToString("N"), ["CoverageAssessed"] = "False" });
                }
            }
            catch (DxlExecutionWaitException ex)
            {
                operation.Record(new SmokeLogEntry("ORIGINAL_COMPLETION", "WAITING", timer.Elapsed.TotalMilliseconds, "Deadline/cancellation is not native cancellation; no redispatch."));
                // Existing readers own late result-file disposal. Observe the original worker, never submit again.
                try
                {
                    await ex.Operation.Completion.ConfigureAwait(false);
                    operation.Record(new SmokeLogEntry("ORIGINAL_NATIVE_COMPLETION", "PASS", timer.Elapsed.TotalMilliseconds, "Native worker and cleanup completed; original wait result remains expired/cancelled."));
                }
                catch (Exception late)
                { operation.Record(new SmokeLogEntry("ORIGINAL_NATIVE_COMPLETION", "FAIL", timer.Elapsed.TotalMilliseconds, "ExceptionType=" + late.GetType().FullName + "; HResult=0x" + late.GetBaseException().HResult.ToString("X8"))); }
                throw;
            }
            catch (Exception ex)
            { operation.Record(new SmokeLogEntry("READ_ONLY_SCOPE", "FAIL", timer.Elapsed.TotalMilliseconds, "ExceptionType=" + ex.GetType().FullName)); throw; }
            finally { operation.Record(new SmokeLogEntry("READ_ONLY_SCOPE_END", "OBSERVED", timer.Elapsed.TotalMilliseconds, "Original read/cleanup observation finished.")); }
        }
        private static T Measure<T>(SmokeProbeOperation operation, string phase, Func<T> action)
        {
            var timer = Stopwatch.StartNew(); operation.Record(new SmokeLogEntry(phase, "STARTED", 0, "Owned STA; no automatic retry."));
            try { T result = action(); operation.Record(new SmokeLogEntry(phase, "PASS", timer.Elapsed.TotalMilliseconds, "Call returned.")); return result; }
            catch (Exception ex) { operation.Record(new SmokeLogEntry(phase, "FAIL", timer.Elapsed.TotalMilliseconds, "ExceptionType=" + ex.GetType().FullName + "; HResult=0x" + ex.GetBaseException().HResult.ToString("X8"))); throw; }
        }
    }
}
