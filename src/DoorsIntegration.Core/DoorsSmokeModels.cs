using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum SmokeStatus { PASS, FAIL, NOT_RUN, TIMEOUT, CANCELLED }
    public enum SmokeTest { ComRegistrationX86, ComRegistrationX64, ProgId, ClientProcessSession, ArchitectureCompatibility, ComSession, RunStr, RunFile, Result, ModuleListing, LinkRead }
    public enum SmokeOperationSemantics { Unknown, IdempotentReadOnly, NonIdempotent }

    public sealed class DoorsSmokeRequest
    {
        public DoorsSmokeRequest(DoorsVersion target = DoorsVersion.V97, bool live = false, string rootPath = "/", string modulePath = null,
            string linkModulePath = null, TimeSpan? timeout = null, int maxItems = 1000, int maxLinks = 1000)
        {
            DoorsVersions.Format(target); Target = target; Live = live; RootPath = DoorsItemPath.Normalize(rootPath);
            ModulePath = modulePath == null ? null : DoorsItemPath.Normalize(modulePath);
            LinkModulePath = linkModulePath == null ? null : DoorsItemPath.Normalize(linkModulePath);
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(30) || maxItems < 1 || maxItems > 10000 || maxLinks < 1 || maxLinks > 100000 ||
                (ModulePath == null) != (LinkModulePath == null) || ModulePath == "/" || LinkModulePath == "/" || (ModulePath != null && ModulePath == LinkModulePath))
                throw new ArgumentException("Smoke scope, timeout or bounds are invalid. Module and link-module paths must be supplied together.");
            MaxItems = maxItems; MaxLinks = maxLinks;
        }
        public DoorsVersion Target { get; }
        public bool Live { get; }
        public string RootPath { get; }
        public string ModulePath { get; }
        public string LinkModulePath { get; }
        public TimeSpan Timeout { get; }
        public int MaxItems { get; }
        public int MaxLinks { get; }
        public bool ReadOnly => true;
        public DoorsConnectionMode ConnectionMode => DoorsConnectionMode.AttachExisting;
        public int MaximumAttempts => 1;
        public bool AutomaticRetryAllowed => false;
    }
    public sealed class SmokeClientProcess
    {
        public SmokeClientProcess(int processId, int windowsSessionId, DateTime? startedAtUtc, string executablePath,
            string fileVersion, int? major, int? minor, string peMachine, string observationStatus)
        { ProcessId = processId; WindowsSessionId = windowsSessionId; StartedAtUtc = startedAtUtc; ExecutablePath = executablePath;
            FileVersion = fileVersion; VersionMajor = major; VersionMinor = minor; PeMachine = peMachine; ObservationStatus = observationStatus; }
        public int ProcessId { get; }
        public int WindowsSessionId { get; }
        public DateTime? StartedAtUtc { get; }
        public string ExecutablePath { get; }
        public string FileVersion { get; }
        public int? VersionMajor { get; }
        public int? VersionMinor { get; }
        public string PeMachine { get; }
        public string ObservationStatus { get; }
    }
    public sealed class DoorsSmokeEnvironment
    {
        public DoorsSmokeEnvironment(DoorsCompatibilityReport compatibility, IEnumerable<SmokeClientProcess> clients, bool processEnumerationComplete,
            int windowsSessionId, double x86InspectionMs = 0, double x64InspectionMs = 0, double processInspectionMs = 0,
            DateTime? x86StartedAtUtc = null, DateTime? x64StartedAtUtc = null, DateTime? processStartedAtUtc = null)
        {
            Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
            Clients = new ReadOnlyCollection<SmokeClientProcess>((clients ?? throw new ArgumentNullException(nameof(clients))).ToArray());
            ProcessEnumerationComplete = processEnumerationComplete; WindowsSessionId = windowsSessionId;
            X86InspectionMs = x86InspectionMs; X64InspectionMs = x64InspectionMs; ProcessInspectionMs = processInspectionMs; ObservedAtUtc = DateTime.UtcNow;
            X86StartedAtUtc = x86StartedAtUtc ?? ObservedAtUtc; X64StartedAtUtc = x64StartedAtUtc ?? ObservedAtUtc; ProcessStartedAtUtc = processStartedAtUtc ?? ObservedAtUtc;
        }
        public DoorsCompatibilityReport Compatibility { get; }
        public IReadOnlyList<SmokeClientProcess> Clients { get; }
        public bool ProcessEnumerationComplete { get; }
        public int WindowsSessionId { get; }
        public double X86InspectionMs { get; }
        public double X64InspectionMs { get; }
        public double ProcessInspectionMs { get; }
        public DateTime ObservedAtUtc { get; }
        public DateTime X86StartedAtUtc { get; }
        public DateTime X64StartedAtUtc { get; }
        public DateTime ProcessStartedAtUtc { get; }
        public string OperatingSystem => Environment.OSVersion.VersionString;
        public string ClrVersion => Environment.Version.ToString();
        public string TargetFramework => AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName;
        public string IntegrationVersion => typeof(DoorsSmokeRequest).Assembly.GetName().Version.ToString();
    }
    public interface IDoorsSmokeEnvironmentInspector
    { Task<DoorsSmokeEnvironment> InspectAsync(DoorsVersion target, CancellationToken cancellationToken); }
    public sealed class SmokeLogEntry
    {
        public SmokeLogEntry(string phase, string status, double elapsedMs, string message, DateTime? atUtc = null)
        { AtUtc = atUtc ?? DateTime.UtcNow; Phase = phase; Status = status; ElapsedMs = elapsedMs; Message = message; }
        public DateTime AtUtc { get; }
        public string Phase { get; }
        public string Status { get; }
        public double ElapsedMs { get; }
        public string Message { get; }
    }
    public sealed class SmokeProbeEvidence
    {
        public SmokeProbeEvidence(IDictionary<string, string> metadata = null)
        { Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal)); }
        public IReadOnlyDictionary<string, string> Metadata { get; }
    }
    public sealed class SmokeProbeFailure : IntegrationException
    {
        public SmokeProbeFailure(string reasonCode, SmokeProbeEvidence evidence)
            : base(IntegrationErrorCode.SmokeFailed, reasonCode)
        { ReasonCode = reasonCode; Evidence = evidence; }
        public string ReasonCode { get; }
        public SmokeProbeEvidence Evidence { get; }
    }
    public sealed class SmokeProbeOperation
    {
        private readonly Action stopBeforeDispatch;
        private readonly ConcurrentQueue<SmokeLogEntry> log = new ConcurrentQueue<SmokeLogEntry>();
        public SmokeProbeOperation(Task<SmokeProbeEvidence> completion, Action stopBeforeDispatch)
        { Completion = completion ?? throw new ArgumentNullException(nameof(completion)); this.stopBeforeDispatch = stopBeforeDispatch ?? throw new ArgumentNullException(nameof(stopBeforeDispatch)); }
        // Completion is settled only after original COM, owned STA and local cleanup finish.
        public Task<SmokeProbeEvidence> Completion { get; }
        public void StopBeforeNextDispatch() => stopBeforeDispatch();
        public void Record(SmokeLogEntry entry) => log.Enqueue(entry);
        public IReadOnlyList<SmokeLogEntry> ReadLog() => Array.AsReadOnly(log.ToArray());
    }
    public interface IDoorsSmokeProbe
    {
        SmokeOperationSemantics Semantics { get; }
        SmokeProbeOperation Start(SmokeTest test, DoorsSmokeRequest request, Guid correlationId);
    }
    public sealed class SmokeTestResult
    {
        internal SmokeTestResult(SmokeTest test, Guid id, SmokeStatus status, string reason, DateTime started, double elapsed,
            DoorsSmokeEnvironment environment, int attempts, IntegrationErrorCode? code, string exceptionType, int? hresult,
            IEnumerable<SmokeLogEntry> log, SmokeProbeEvidence evidence = null)
        { Test = test; CorrelationId = id; Status = status; Reason = reason; StartedAtUtc = started; DurationMs = elapsed;
            Environment = environment; Attempts = attempts; ErrorCode = code; ExceptionType = exceptionType; HResult = hresult;
            Log = new ReadOnlyCollection<SmokeLogEntry>(log.ToArray()); Evidence = evidence ?? new SmokeProbeEvidence(); }
        public SmokeTest Test { get; }
        public Guid CorrelationId { get; }
        public SmokeStatus Status { get; }
        public string Reason { get; }
        public DateTime StartedAtUtc { get; }
        public double DurationMs { get; }
        public DoorsSmokeEnvironment Environment { get; }
        public int Attempts { get; }
        public int Retries => 0;
        public bool Required => Environment.Compatibility.Registrations.Count == 0 || (Test == SmokeTest.ComRegistrationX86 ? !Environment.Compatibility.Is64BitProcess :
            Test != SmokeTest.ComRegistrationX64 || Environment.Compatibility.Is64BitProcess);
        public IntegrationErrorCode? ErrorCode { get; }
        public string ExceptionType { get; }
        public int? HResult { get; }
        public IReadOnlyList<SmokeLogEntry> Log { get; }
        public SmokeProbeEvidence Evidence { get; }
    }
    public sealed class SmokeSettlement
    {
        public SmokeSettlement(Guid correlationId, string outcome, IEnumerable<SmokeLogEntry> log, string exceptionType = null)
        { CorrelationId = correlationId; ObservedAtUtc = DateTime.UtcNow; Outcome = outcome; ExceptionType = exceptionType; Log = Array.AsReadOnly(log.ToArray()); }
        public Guid CorrelationId { get; }
        public DateTime ObservedAtUtc { get; }
        public string Outcome { get; }
        public string ExceptionType { get; }
        public IReadOnlyList<SmokeLogEntry> Log { get; }
    }
    public sealed class DoorsSmokeRun
    {
        internal DoorsSmokeRun(Guid id, DoorsSmokeRequest request, DateTime started, double elapsed, IEnumerable<SmokeTestResult> results,
            bool uncertain, Task<IReadOnlyList<SmokeSettlement>> settlement)
        { RunId = id; Request = request; StartedAtUtc = started; DurationMs = elapsed; Results = Array.AsReadOnly(results.ToArray()); SessionUncertain = uncertain; Settlement = settlement; }
        public Guid RunId { get; }
        public DoorsSmokeRequest Request { get; }
        public DateTime StartedAtUtc { get; }
        public double DurationMs { get; }
        public IReadOnlyList<SmokeTestResult> Results { get; }
        public bool SessionUncertain { get; }
        public Task<IReadOnlyList<SmokeSettlement>> Settlement { get; }
        public SmokeStatus Status => Results.Any(row => row.Required && row.Status == SmokeStatus.TIMEOUT) ? SmokeStatus.TIMEOUT :
            Results.Any(row => row.Required && row.Status == SmokeStatus.CANCELLED) ? SmokeStatus.CANCELLED : Results.Any(row => row.Required && row.Status == SmokeStatus.FAIL) ? SmokeStatus.FAIL :
            Results.Any(row => row.Required && row.Status == SmokeStatus.NOT_RUN) ? SmokeStatus.NOT_RUN : SmokeStatus.PASS;
        public int ExitCode => Status == SmokeStatus.PASS ? 0 : Status == SmokeStatus.FAIL ? 1 : Status == SmokeStatus.NOT_RUN ? 3 : Status == SmokeStatus.TIMEOUT ? 4 : 130;
    }
    public interface IDoorsSmokeSuite
    { Task<DoorsSmokeRun> RunAsync(DoorsSmokeRequest request, IProgress<SmokeTestResult> progress = null, CancellationToken cancellationToken = default(CancellationToken)); }
}
