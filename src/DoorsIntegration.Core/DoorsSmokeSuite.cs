using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class DoorsSmokeSuite : IDoorsSmokeSuite
    {
        private readonly IDoorsSmokeEnvironmentInspector inspector;
        private readonly IDoorsSmokeProbe probe;
        private readonly IIntegrationLogger logger;
        public DoorsSmokeSuite(IDoorsSmokeEnvironmentInspector inspector, IDoorsSmokeProbe probe, IIntegrationLogger logger)
        { this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector)); this.probe = probe ?? throw new ArgumentNullException(nameof(probe)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }

        public async Task<DoorsSmokeRun> RunAsync(DoorsSmokeRequest request, IProgress<SmokeTestResult> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var id = Guid.NewGuid(); var started = DateTime.UtcNow; var clock = Stopwatch.StartNew();
            var results = new List<SmokeTestResult>(); var settlements = new List<Task<SmokeSettlement>>(); bool uncertain = false;
            DoorsSmokeEnvironment environment; Exception inspectionError = null;
            try
            {
                environment = await inspector.InspectAsync(request.Target, cancellationToken).ConfigureAwait(false);
                if (environment == null || environment.Compatibility.Target != request.Target ||
                    environment.Compatibility.Registrations.GroupBy(row => row.Architecture).Any(group => group.Count() > 1))
                    throw new InvalidOperationException("Environment observation is missing or inconsistent.");
            }
            catch (Exception ex)
            {
                inspectionError = ex;
                environment = new DoorsSmokeEnvironment(new DoorsCompatibilityReport(request.Target, Environment.Is64BitOperatingSystem, Environment.Is64BitProcess,
                    "Unavailable", Array.Empty<ComRegistrationSnapshot>()), Array.Empty<SmokeClientProcess>(), false, -1);
            }
            var current = environment.Compatibility.Registrations.SingleOrDefault(row => row.Architecture == (environment.Compatibility.Is64BitProcess ? ComArchitecture.X64 : ComArchitecture.X86));
            bool eligible = false; string blocked = inspectionError == null ? null : "EnvironmentInspectionFailed";
            foreach (SmokeTest test in Enum.GetValues(typeof(SmokeTest)))
            {
                var caseId = Guid.NewGuid(); var caseStart = DateTime.UtcNow; var timer = Stopwatch.StartNew();
                if (inspectionError == null) caseStart = test == SmokeTest.ComRegistrationX86 ? environment.X86StartedAtUtc :
                    test == SmokeTest.ComRegistrationX64 ? environment.X64StartedAtUtc : test == SmokeTest.ClientProcessSession ? environment.ProcessStartedAtUtc : caseStart;
                SmokeStatus status; string reason; int attempts = 0; IntegrationErrorCode? code = null; string exceptionType = null; int? hresult = null;
                SmokeProbeEvidence evidence = null; var log = new List<SmokeLogEntry> { new SmokeLogEntry("TEST_START", "STARTED", 0, "ReadOnly=True; MaximumAttempts=1; AutomaticRetryAllowed=False", caseStart) };
                Log("SmokeTestStarted", caseId, "Test=" + test + "; Target=" + DoorsVersions.Format(request.Target));
                if (inspectionError != null)
                {
                    status = test == SmokeTest.ComRegistrationX86 ? (inspectionError is OperationCanceledException ? SmokeStatus.CANCELLED : SmokeStatus.FAIL) : SmokeStatus.NOT_RUN;
                    reason = blocked; if (status == SmokeStatus.FAIL) code = IntegrationErrorCode.SmokeFailed;
                    exceptionType = inspectionError.GetType().FullName; hresult = inspectionError.HResult;
                }
                else if (test <= SmokeTest.ArchitectureCompatibility)
                {
                    var verdict = Evaluate(test, request, environment, current);
                    status = verdict.Status; reason = verdict.Reason; evidence = verdict.Evidence; code = verdict.Code;
                    if (test == SmokeTest.ArchitectureCompatibility)
                    {
                        eligible = status == SmokeStatus.PASS && results.Where(row => row.Required).All(row => row.Status == SmokeStatus.PASS);
                        if (!eligible) blocked = status == SmokeStatus.PASS ? "EarlierEnvironmentPrerequisiteFailed" : reason;
                    }
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    status = results.Any(row => row.Status == SmokeStatus.CANCELLED || row.Status == SmokeStatus.TIMEOUT) ? SmokeStatus.NOT_RUN : SmokeStatus.CANCELLED;
                    reason = "CancellationRequestedBeforeTest"; if (status == SmokeStatus.CANCELLED) code = IntegrationErrorCode.DxlWaitCancelled;
                }
                else if (blocked != null || !eligible) { status = SmokeStatus.NOT_RUN; reason = blocked ?? "EnvironmentUnavailable"; }
                else if (!request.Live) { status = SmokeStatus.NOT_RUN; reason = "LiveNotRequested"; }
                else if (probe.Semantics != SmokeOperationSemantics.IdempotentReadOnly)
                { status = SmokeStatus.FAIL; reason = "NonReadOnlyOrUnknownAdapterRejected"; code = IntegrationErrorCode.SmokeFailed; blocked = reason; }
                else if (test == SmokeTest.LinkRead && request.ModulePath == null) { status = SmokeStatus.NOT_RUN; reason = "LinkTestPathsNotConfigured"; }
                else
                {
                    SmokeProbeOperation operation = null;
                    try
                    {
                        attempts = 1; operation = probe.Start(test, request, caseId);
                        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (var timeoutStop = new CancellationTokenSource())
                        using (cancellationToken.Register(() => stop.TrySetResult(true)))
                        {
                            try
                            {
                                await Task.WhenAny(operation.Completion, Task.Delay(request.Timeout, timeoutStop.Token), stop.Task).ConfigureAwait(false);
                                if (operation.Completion.IsCompleted)
                                {
                                    evidence = await operation.Completion.ConfigureAwait(false); status = SmokeStatus.PASS; reason = "ReadOnlyProbeCompleted";
                                }
                                else
                                {
                                    status = cancellationToken.IsCancellationRequested ? SmokeStatus.CANCELLED : SmokeStatus.TIMEOUT;
                                    code = status == SmokeStatus.TIMEOUT ? IntegrationErrorCode.DxlTimeout : IntegrationErrorCode.DxlWaitCancelled;
                                    reason = "OriginalOperationMayContinue_NoRetry_NoFurtherNativeTests"; blocked = "SessionUncertainAfterWaitEnded"; uncertain = true;
                                    try { operation.StopBeforeNextDispatch(); } catch (Exception stopError) { operation.Record(new SmokeLogEntry("STOP_REQUEST", "FAIL", 0, "ExceptionType=" + stopError.GetType().FullName)); }
                                    settlements.Add(ObserveLateAsync(caseId, operation));
                                }
                            }
                            finally { timeoutStop.Cancel(); }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptionType = ex.GetType().FullName; hresult = ex.GetBaseException().HResult;
                        code = (ex as IntegrationException)?.Code ?? IntegrationErrorCode.SmokeFailed;
                        bool absent = code == IntegrationErrorCode.ActiveSessionUnavailable || code == IntegrationErrorCode.ClientUnavailable || code == IntegrationErrorCode.ComNotRegistered;
                        status = absent ? SmokeStatus.NOT_RUN : ex is OperationCanceledException ? SmokeStatus.CANCELLED : SmokeStatus.FAIL;
                        if (code == IntegrationErrorCode.DxlTimeout || code == IntegrationErrorCode.DxlWaitCancelled)
                        {
                            status = code == IntegrationErrorCode.DxlTimeout ? SmokeStatus.TIMEOUT : SmokeStatus.CANCELLED;
                            uncertain = true; blocked = "OriginalDeadlineObserved_NoFurtherNativeTests";
                            if (operation != null) settlements.Add(ObserveLateAsync(caseId, operation));
                        }
                        reason = absent ? "AccessibleRealSessionUnavailable" : "ProbeFailed_SeeErrorCodeAndMetadataLog";
                        if (ex is SmokeProbeFailure scopedFailure) { reason = scopedFailure.ReasonCode; evidence = scopedFailure.Evidence; }
                        if (code == IntegrationErrorCode.DxlTimeout || code == IntegrationErrorCode.DxlWaitCancelled) reason = "OriginalReadDeadlineObserved_NoRedispatch";
                        if (test == SmokeTest.ComSession || absent || code == IntegrationErrorCode.ComFailure || code == IntegrationErrorCode.ComServerDisconnected || code == IntegrationErrorCode.DoorsOperationBusy)
                            blocked = "NativeSessionPrerequisiteFailed";
                        if (code == IntegrationErrorCode.ComFailure || code == IntegrationErrorCode.ComServerDisconnected) uncertain = true;
                    }
                    finally { if (operation != null) log.AddRange(operation.ReadLog()); }
                }
                // Registration/process observation costs were captured independently, before any COM connection.
                double elapsed = test == SmokeTest.ComRegistrationX86 ? environment.X86InspectionMs : test == SmokeTest.ComRegistrationX64 ? environment.X64InspectionMs :
                    test == SmokeTest.ClientProcessSession ? environment.ProcessInspectionMs : timer.Elapsed.TotalMilliseconds;
                log.Add(new SmokeLogEntry("TEST_END", status.ToString(), elapsed, reason));
                var result = new SmokeTestResult(test, caseId, status, reason, caseStart, elapsed, environment, attempts, code, exceptionType, hresult, log, evidence);
                results.Add(result); Log("SmokeTestCompleted", caseId, "Test=" + test + "; Status=" + status + "; DurationMs=" + elapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "; Attempts=" + attempts);
                try { progress?.Report(result); } catch { /* Observers cannot cause redispatch or strand cleanup. */ }
            }
            return new DoorsSmokeRun(id, request, started, clock.Elapsed.TotalMilliseconds, results, uncertain, SettleAllAsync(settlements));
        }
        private static async Task<IReadOnlyList<SmokeSettlement>> SettleAllAsync(IEnumerable<Task<SmokeSettlement>> tasks) => Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
        private static async Task<SmokeSettlement> ObserveLateAsync(Guid id, SmokeProbeOperation operation)
        {
            try { await operation.Completion.ConfigureAwait(false); return new SmokeSettlement(id, "OriginalProbeCompleted_NoRedispatch", operation.ReadLog()); }
            catch (Exception ex) { return new SmokeSettlement(id, "OriginalProbeFaultObserved_NoRedispatch", operation.ReadLog(), ex.GetType().FullName); }
        }
        private void Log(string name, Guid id, string metadata) { try { logger.Write(IntegrationLogLevel.Information, name, id, metadata); } catch { } }
        private sealed class Verdict
        {
            internal SmokeStatus Status; internal string Reason; internal IntegrationErrorCode? Code; internal SmokeProbeEvidence Evidence;
            internal Verdict(SmokeStatus status, string reason, IDictionary<string, string> evidence = null, IntegrationErrorCode? code = null)
            { Status = status; Reason = reason; Evidence = new SmokeProbeEvidence(evidence); Code = code; }
        }
        private static Verdict Evaluate(SmokeTest test, DoorsSmokeRequest request, DoorsSmokeEnvironment environment, ComRegistrationSnapshot current)
        {
            if (test == SmokeTest.ComRegistrationX86 || test == SmokeTest.ComRegistrationX64)
            {
                var arch = test == SmokeTest.ComRegistrationX86 ? ComArchitecture.X86 : ComArchitecture.X64;
                var registration = environment.Compatibility.Registrations.SingleOrDefault(row => row.Architecture == arch);
                var data = new Dictionary<string, string> { ["RegistryView"] = arch.ToString(), ["ObservedState"] = registration?.State.ToString() ?? "Unavailable" };
                if (registration == null || registration.State == ComRegistrationState.NotRegistered || registration.State == ComRegistrationState.ViewUnavailable)
                    return new Verdict(SmokeStatus.NOT_RUN, "RealRegistrationUnavailable", data);
                return new Verdict(registration.State == ComRegistrationState.Registered ? SmokeStatus.PASS : SmokeStatus.FAIL,
                    "RegistrationInspection_RegistryViewIsNotServerBitness", data, registration.State == ComRegistrationState.Registered ? (IntegrationErrorCode?)null : IntegrationErrorCode.ComRegistrationReadFailed);
            }
            if (test == SmokeTest.ProgId)
            {
                if (current?.State != ComRegistrationState.Registered) return new Verdict(SmokeStatus.NOT_RUN, "CurrentViewRegistrationUnavailable");
                bool valid = Guid.TryParse(current.Clsid, out var parsed) && parsed != Guid.Empty;
                return new Verdict(valid ? SmokeStatus.PASS : SmokeStatus.FAIL, "ReadOnlyRegistryMapping_NoTypeActivation", new Dictionary<string, string>
                    { ["ProgId"] = "DOORS.Application", ["Clsid"] = current.Clsid ?? "", ["ResolutionScope"] = "RegistryMappingOnly; live ROT connection is a separate test" }, valid ? (IntegrationErrorCode?)null : IntegrationErrorCode.ComNotRegistered);
            }
            if (test == SmokeTest.ClientProcessSession)
            {
                if (!environment.ProcessEnumerationComplete) return new Verdict(SmokeStatus.NOT_RUN, "ProcessIdentityOrSessionNotFullyObservable");
                if (environment.Clients.Count == 0) return new Verdict(SmokeStatus.NOT_RUN, "NoDoorsClientInThisWindowsSession");
                if (environment.Clients.Count != 1) return new Verdict(SmokeStatus.FAIL, "MultipleClients_AmbiguousProcessContext", code: IntegrationErrorCode.CompatibilityUnknown);
                return new Verdict(SmokeStatus.PASS, "OneObservedClient_NotProofOfRotObjectOwnership", new Dictionary<string, string> { ["ProcessId"] = environment.Clients[0].ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (current?.State != ComRegistrationState.Registered || !environment.ProcessEnumerationComplete || environment.Clients.Count != 1)
                return new Verdict(SmokeStatus.NOT_RUN, "RealRegistrationAndSingleObservedClientRequired");
            var client = environment.Clients[0];
            if (client.ObservationStatus != "Observed" || client.WindowsSessionId != environment.WindowsSessionId || !client.StartedAtUtc.HasValue ||
                !client.VersionMajor.HasValue || !client.VersionMinor.HasValue || current.MatchesTarget(request.Target) == null ||
                string.IsNullOrEmpty(client.PeMachine) || client.PeMachine == "Unknown") return new Verdict(SmokeStatus.NOT_RUN, "VersionOrArchitectureEvidenceUnavailable");
            bool match = current.MatchesTarget(request.Target) == true && client.VersionMajor == 9 && client.VersionMinor == (request.Target == DoorsVersion.V95 ? 5 : 7);
            return new Verdict(match ? SmokeStatus.PASS : SmokeStatus.FAIL, match ? "MetadataEligible_LiveProbesStillRequired_CrossBitnessNotAssumed" : "RegisteredOrObservedClientVersionMismatch",
                new Dictionary<string, string> { ["RunnerArchitecture"] = environment.Compatibility.Is64BitProcess ? "x64" : "x86", ["ClientPeMachine"] = client.PeMachine,
                    ["VersionEvidence"] = "Registered binary and observed process binary; not a COM-reported client version" }, match ? (IntegrationErrorCode?)null : IntegrationErrorCode.TargetVersionMismatch);
        }
    }
}
