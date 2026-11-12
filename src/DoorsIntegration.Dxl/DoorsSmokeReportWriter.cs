using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DoorsSmokeReportWriter
    {
        public void WriteInitial(string directory, DoorsSmokeRun run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            string destination = Validate(directory), parent = Path.GetDirectoryName(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IntegrationException(IntegrationErrorCode.SmokeFailed, "Smoke report destination must be a new directory.");
            Directory.CreateDirectory(parent); string stage = Path.Combine(parent, ".doors-smoke-" + run.RunId.ToString("N"));
            if (Directory.Exists(stage)) throw new IntegrationException(IntegrationErrorCode.SmokeFailed, "Smoke report staging directory already exists.");
            Directory.CreateDirectory(stage); var ownedFiles = new List<string>();
            try
            {
                Directory.CreateDirectory(Path.Combine(stage, "logs"));
                string report = Path.Combine(stage, "report.json"); ownedFiles.Add(report); WriteJson(report, ToReport(run));
                foreach (var test in run.Results)
                {
                    string logPath = Path.Combine(stage, "logs", test.Test + ".jsonl"); ownedFiles.Add(logPath);
                    using (var file = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var text = new StreamWriter(file, new UTF8Encoding(false, true)))
                        foreach (var entry in test.Log) text.WriteLine(Json(new LogDto { AtUtc = Utc(entry.AtUtc), Phase = entry.Phase, Status = entry.Status,
                            ElapsedMs = entry.ElapsedMs, Message = entry.Message, CorrelationId = test.CorrelationId.ToString("N"), Test = test.Test.ToString(), Target = DoorsVersions.Format(run.Request.Target) }));
                }
                Directory.Move(stage, destination);
            }
            catch (Exception ex)
            {
                // Only exact files created by this job and its empty directories; never recursive deletion.
                foreach (string file in ownedFiles) try { File.Delete(file); } catch { }
                try { Directory.Delete(Path.Combine(stage, "logs"), false); Directory.Delete(stage, false); } catch { }
                throw new IntegrationException(IntegrationErrorCode.SmokeFailed, "Smoke report could not be published; check the destination and local permissions.", ex);
            }
        }
        public void WriteSettlement(string directory, DoorsSmokeRun run, IReadOnlyList<SmokeSettlement> settled)
        {
            string destination = Validate(directory);
            var existing = ReadJson<ReportDto>(Path.Combine(destination, "report.json"));
            if (existing.RunId != run.RunId.ToString("N")) throw new IntegrationException(IntegrationErrorCode.SmokeFailed, "Settlement report belongs to another run.");
            WriteJson(Path.Combine(destination, "settlement.json"), new SettlementReportDto { SchemaVersion = 1, RunId = run.RunId.ToString("N"),
                OriginalStatus = run.Status.ToString(), AutomaticRetryAllowed = false, Settlements = settled.Select(row => new SettlementDto {
                    CorrelationId = row.CorrelationId.ToString("N"), ObservedAtUtc = Utc(row.ObservedAtUtc), Outcome = row.Outcome, ExceptionType = row.ExceptionType,
                    Log = row.Log.Select(entry => new LogDto { AtUtc = Utc(entry.AtUtc), Phase = entry.Phase, Status = entry.Status, ElapsedMs = entry.ElapsedMs, Message = entry.Message }).ToArray() }).ToArray() });
        }
        private static string Validate(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("An absolute report directory is required.");
            string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(Path.GetFileName(path)) || string.IsNullOrEmpty(Path.GetDirectoryName(path))) throw new ArgumentException("Report directory cannot be a drive root.");
            return path;
        }
        private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        private static DataContractJsonSerializer Serializer<T>() => new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        private static string Json<T>(T value) { using (var stream = new MemoryStream()) { Serializer<T>().WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
        private static void WriteJson<T>(string path, T value)
        { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { Serializer<T>().WriteObject(file, value); file.Flush(true); } }
        private static T ReadJson<T>(string path) { using (var file = File.OpenRead(path)) return (T)Serializer<T>().ReadObject(file); }
        private static ReportDto ToReport(DoorsSmokeRun run) => new ReportDto { SchemaVersion = 1, RunId = run.RunId.ToString("N"), TargetProfile = DoorsVersions.Format(run.Request.Target),
            LiveRequested = run.Request.Live, ReadOnly = true, AutomaticRetryAllowed = false, NonIdempotentOperationsAllowed = false, ConnectionMode = "AttachExisting",
            StartedAtUtc = Utc(run.StartedAtUtc), ReportedAtUtc = Utc(DateTime.UtcNow), DurationMs = run.DurationMs, Status = run.Status.ToString(), ExitCode = run.ExitCode,
            SessionUncertain = run.SessionUncertain, TimeoutMs = run.Request.Timeout.TotalMilliseconds, RootPath = run.Request.RootPath, ModulePath = run.Request.ModulePath,
            LinkModulePath = run.Request.LinkModulePath, MaximumAttempts = 1,
            Tests = run.Results.Select(row => new TestDto { Test = row.Test.ToString(), CorrelationId = row.CorrelationId.ToString("N"), Status = row.Status.ToString(),
                Required = row.Required, Reason = row.Reason, StartedAtUtc = Utc(row.StartedAtUtc), DurationMs = row.DurationMs, Attempts = row.Attempts, Retries = 0,
                ErrorCode = row.ErrorCode?.ToString(), ExceptionType = row.ExceptionType, HResult = row.HResult.HasValue ? "0x" + row.HResult.Value.ToString("X8") : null,
                Evidence = row.Evidence.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value), Environment = ToEnvironment(row.Environment), LogFile = "logs/" + row.Test + ".jsonl" }).ToArray() };
        private static EnvironmentDto ToEnvironment(DoorsSmokeEnvironment environment) => new EnvironmentDto { TargetProfile = DoorsVersions.Format(environment.Compatibility.Target),
            ObservedAtUtc = Utc(environment.ObservedAtUtc), OperatingSystem = environment.OperatingSystem, ClrVersion = environment.ClrVersion, TargetFramework = environment.TargetFramework,
            IntegrationVersion = environment.IntegrationVersion, OsArchitecture = environment.Compatibility.Is64BitOperatingSystem ? "x64" : "x86",
            RunnerArchitecture = environment.Compatibility.Is64BitProcess ? "x64" : "x86", InspectionApartment = environment.Compatibility.Apartment,
            WindowsSessionId = environment.WindowsSessionId, ProcessEnumerationComplete = environment.ProcessEnumerationComplete,
            VersionEvidence = "Registered/observed executable metadata. No undocumented COM version member is assumed. Process identity is not a ROT ownership proof.",
            Registrations = environment.Compatibility.Registrations.Select(row => new RegistrationDto { Architecture = row.Architecture.ToString(), State = row.State.ToString(), Clsid = row.Clsid,
                Executable = row.ServerPath, FileVersion = row.FileVersion, PeMachine = row.PeMachine, Note = row.Note }).ToArray(),
            Clients = environment.Clients.Select(row => new ClientDto { ProcessId = row.ProcessId, WindowsSessionId = row.WindowsSessionId, StartedAtUtc = row.StartedAtUtc.HasValue ? Utc(row.StartedAtUtc.Value) : null,
                Executable = row.ExecutablePath, FileVersion = row.FileVersion, PeMachine = row.PeMachine, ObservationStatus = row.ObservationStatus }).ToArray() };

        [DataContract] private sealed class ReportDto
        {
            [DataMember(Name="schemaVersion")] public int SchemaVersion;
            [DataMember(Name="runId")] public string RunId;
            [DataMember(Name="targetProfile")] public string TargetProfile;
            [DataMember(Name="liveRequested")] public bool LiveRequested;
            [DataMember(Name="readOnly")] public bool ReadOnly;
            [DataMember(Name="automaticRetryAllowed")] public bool AutomaticRetryAllowed;
            [DataMember(Name="nonIdempotentOperationsAllowed")] public bool NonIdempotentOperationsAllowed;
            [DataMember(Name="connectionMode")] public string ConnectionMode;
            [DataMember(Name="startedAtUtc")] public string StartedAtUtc;
            [DataMember(Name="reportedAtUtc")] public string ReportedAtUtc;
            [DataMember(Name="durationMs")] public double DurationMs;
            [DataMember(Name="timeoutMs")] public double TimeoutMs;
            [DataMember(Name="maximumAttempts")] public int MaximumAttempts;
            [DataMember(Name="status")] public string Status;
            [DataMember(Name="exitCode")] public int ExitCode;
            [DataMember(Name="sessionUncertain")] public bool SessionUncertain;
            [DataMember(Name="rootPath")] public string RootPath;
            [DataMember(Name="modulePath")] public string ModulePath;
            [DataMember(Name="linkModulePath")] public string LinkModulePath;
            [DataMember(Name="tests")] public TestDto[] Tests;
        }
        [DataContract] private sealed class TestDto
        {
            [DataMember(Name="test")] public string Test;
            [DataMember(Name="correlationId")] public string CorrelationId;
            [DataMember(Name="status")] public string Status;
            [DataMember(Name="required")] public bool Required;
            [DataMember(Name="reason")] public string Reason;
            [DataMember(Name="startedAtUtc")] public string StartedAtUtc;
            [DataMember(Name="durationMs")] public double DurationMs;
            [DataMember(Name="attempts")] public int Attempts;
            [DataMember(Name="retries")] public int Retries;
            [DataMember(Name="errorCode")] public string ErrorCode;
            [DataMember(Name="exceptionType")] public string ExceptionType;
            [DataMember(Name="hresult")] public string HResult;
            [DataMember(Name="evidence")] public Dictionary<string,string> Evidence;
            [DataMember(Name="environment")] public EnvironmentDto Environment;
            [DataMember(Name="logFile")] public string LogFile;
        }
        [DataContract] private sealed class EnvironmentDto
        {
            [DataMember(Name="targetProfile")] public string TargetProfile;
            [DataMember(Name="observedAtUtc")] public string ObservedAtUtc;
            [DataMember(Name="operatingSystem")] public string OperatingSystem;
            [DataMember(Name="clrVersion")] public string ClrVersion;
            [DataMember(Name="targetFramework")] public string TargetFramework;
            [DataMember(Name="integrationVersion")] public string IntegrationVersion;
            [DataMember(Name="osArchitecture")] public string OsArchitecture;
            [DataMember(Name="runnerArchitecture")] public string RunnerArchitecture;
            [DataMember(Name="inspectionApartment")] public string InspectionApartment;
            [DataMember(Name="windowsSessionId")] public int WindowsSessionId;
            [DataMember(Name="processEnumerationComplete")] public bool ProcessEnumerationComplete;
            [DataMember(Name="versionEvidence")] public string VersionEvidence;
            [DataMember(Name="registrations")] public RegistrationDto[] Registrations;
            [DataMember(Name="clients")] public ClientDto[] Clients;
        }
        [DataContract] private sealed class RegistrationDto
        {
            [DataMember(Name="architecture")] public string Architecture;
            [DataMember(Name="state")] public string State;
            [DataMember(Name="clsid")] public string Clsid;
            [DataMember(Name="executable")] public string Executable;
            [DataMember(Name="fileVersion")] public string FileVersion;
            [DataMember(Name="peMachine")] public string PeMachine;
            [DataMember(Name="note")] public string Note;
        }
        [DataContract] private sealed class ClientDto
        {
            [DataMember(Name="processId")] public int ProcessId;
            [DataMember(Name="windowsSessionId")] public int WindowsSessionId;
            [DataMember(Name="startedAtUtc")] public string StartedAtUtc;
            [DataMember(Name="executable")] public string Executable;
            [DataMember(Name="fileVersion")] public string FileVersion;
            [DataMember(Name="peMachine")] public string PeMachine;
            [DataMember(Name="observationStatus")] public string ObservationStatus;
        }
        [DataContract] private sealed class LogDto
        {
            [DataMember(Name="atUtc")] public string AtUtc;
            [DataMember(Name="phase")] public string Phase;
            [DataMember(Name="status")] public string Status;
            [DataMember(Name="elapsedMs")] public double ElapsedMs;
            [DataMember(Name="message")] public string Message;
            [DataMember(Name="correlationId")] public string CorrelationId;
            [DataMember(Name="test")] public string Test;
            [DataMember(Name="target")] public string Target;
        }
        [DataContract] private sealed class SettlementReportDto
        {
            [DataMember(Name="schemaVersion")] public int SchemaVersion;
            [DataMember(Name="runId")] public string RunId;
            [DataMember(Name="originalStatus")] public string OriginalStatus;
            [DataMember(Name="automaticRetryAllowed")] public bool AutomaticRetryAllowed;
            [DataMember(Name="settlements")] public SettlementDto[] Settlements;
        }
        [DataContract] private sealed class SettlementDto
        {
            [DataMember(Name="correlationId")] public string CorrelationId;
            [DataMember(Name="observedAtUtc")] public string ObservedAtUtc;
            [DataMember(Name="outcome")] public string Outcome;
            [DataMember(Name="exceptionType")] public string ExceptionType;
            [DataMember(Name="log")] public LogDto[] Log;
        }
    }
}
