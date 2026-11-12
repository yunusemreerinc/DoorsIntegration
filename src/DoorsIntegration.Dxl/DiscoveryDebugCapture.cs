using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    // Raw strings are stored as UTF-16 code units, including BOMs and unpaired surrogates.
    // No normalization, trimming or newline conversion takes place during capture.
    [DataContract]
    public sealed class DiscoveryDebugRecord
    {
        [DataMember] public Guid RequestId { get; set; }
        [DataMember] public Guid DiscoveryId { get; set; }
        [DataMember] public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
        [DataMember] public string Stage { get; set; }
        [DataMember] public string Method { get; set; }
        [DataMember] public string ScriptPath { get; set; }
        [DataMember] public string ScriptSha256 { get; set; }
        [DataMember] public string DispatchedScriptSha256 { get; set; }
        [DataMember] public string RequestedVersion { get; set; }
        [DataMember] public string ProcessArchitecture { get; set; }
        [DataMember] public string[] ObservedClients { get; set; }
        [DataMember] public string ClientEvidence { get; set; }
        [DataMember] public string RawComResponseUtf16 { get; set; }
        [DataMember] public int ComResponseChars { get; set; }
        [DataMember] public int ComResponseLfCount { get; set; }
        [DataMember] public string RawDiscoveryResponseUtf16 { get; set; }
        [DataMember] public int ResponseChars { get; set; }
        [DataMember] public int ResponseLineCount { get; set; }
        [DataMember] public int LfCount { get; set; }
        [DataMember] public int CrLfCount { get; set; }
        [DataMember] public int BareCrCount { get; set; }
        [DataMember] public bool LeadingBom { get; set; }
        [DataMember] public string ParserFailureReason { get; set; }
        [DataMember] public string ExceptionDetails { get; set; }
        public string ReadComResponse() => Decode(RawComResponseUtf16);
        public string ReadDiscoveryResponse() => Decode(RawDiscoveryResponseUtf16);
        internal static string Encode(string value)
        {
            if (value == null) return null;
            var bytes = new byte[checked(value.Length * 2)];
            Buffer.BlockCopy(value.ToCharArray(), 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }
        private static string Decode(string value)
        {
            if (value == null) return null;
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length % 2 != 0) throw new InvalidDataException("Invalid UTF-16 capture.");
            var chars = new char[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, chars, 0, bytes.Length);
            return new string(chars);
        }
    }

    public interface IDiscoveryDebugStore
    {
        void Save(DiscoveryDebugRecord record);
    }

    // Diagnostic files, not audit journals. Bound storage and fail closed: never fall back to plaintext.
    public sealed class ProtectedDiscoveryDebugStore : IDiscoveryDebugStore
    {
        public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "discovery-debug");
        private static readonly object Sync = new object();
        private readonly string directory;
        private readonly IBaselineJournalProtector protector;
        public ProtectedDiscoveryDebugStore() : this(DefaultDirectory) { }
        public ProtectedDiscoveryDebugStore(string directory) : this(directory, new WindowsBaselineJournalProtector()) { }
        public ProtectedDiscoveryDebugStore(string directory, IBaselineJournalProtector protector)
        { this.directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory))); this.protector = protector ?? throw new ArgumentNullException(nameof(protector)); }
        public void Save(DiscoveryDebugRecord record)
        {
            if (record == null || record.RequestId == Guid.Empty) throw new ArgumentException("A request identity is required.");
            lock (Sync)
            {
                EnsurePrivateDirectory();
                string path = Path.Combine(directory, record.RequestId.ToString("N") + ".dpapi");
                var files = new DirectoryInfo(directory).GetFiles("*.dpapi");
                if (!File.Exists(path) && (files.Length >= 256 || files.Sum(f => f.Length) >= 128L * 1024 * 1024))
                    throw new IOException("Diagnostic quota reached. Archive protected captures before retrying.");
                byte[] bytes;
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(DiscoveryDebugRecord)).WriteObject(stream, record);
                    if (stream.Length > 32L * 1024 * 1024) throw new IOException("Diagnostic record exceeds limit.");
                    bytes = protector.Protect(stream.ToArray());
                }
                string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
                    var fileSecurity = new FileSecurity();
                    var sid = WindowsIdentity.GetCurrent().User;
                    fileSecurity.SetOwner(sid);
                    fileSecurity.SetAccessRuleProtection(true, false);
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                    File.SetAccessControl(path, fileSecurity);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }
        public DiscoveryDebugRecord Read(Guid requestId)
        {
            lock (Sync)
            {
                var path = Path.Combine(directory, requestId.ToString("N") + ".dpapi");
                if (new FileInfo(path).Length > 34L * 1024 * 1024) throw new IOException("Diagnostic record exceeds limit.");
                using (var stream = new MemoryStream(protector.Unprotect(File.ReadAllBytes(path))))
                    return (DiscoveryDebugRecord)new DataContractJsonSerializer(typeof(DiscoveryDebugRecord)).ReadObject(stream);
            }
        }
        private void EnsurePrivateDirectory()
        {
            // Reject reparse points along the existing path before touching ACLs or writing files.
            for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Diagnostic path uses a reparse point.");
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid == null) throw new IOException("Windows identity unavailable.");
            var security = new DirectorySecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, security);
            else new DirectoryInfo(directory).SetAccessControl(security);
        }
    }

    internal sealed class DiscoveryDebugCapture
    {
        private readonly IDiscoveryDebugStore store;
        private readonly IIntegrationLogger logger;
        internal DiscoveryDebugRecord Record { get; } = new DiscoveryDebugRecord();
        internal bool Saved { get; private set; }
        internal DiscoveryDebugCapture(IDiscoveryDebugStore store, IIntegrationLogger logger, Guid discoveryId)
        { this.store = store; this.logger = logger; Record.DiscoveryId = discoveryId; }
        internal void Prepare(DxlExecutionRequest request, Guid id, string source, string dispatched)
        {
            Record.RequestId = id; Record.Method = request.Method == DxlInvocationMethod.RunString ? "runStr / Result" : "runFile / Result";
            Record.ScriptPath = request.ScriptPath; Record.RequestedVersion = DoorsVersions.Format(request.Target);
            Record.ProcessArchitecture = Environment.Is64BitProcess ? "x64" : "x86";
            Record.ScriptSha256 = Hash(source); Record.DispatchedScriptSha256 = Hash(dispatched);
            // Passive file metadata is not proof of which process owns the COM ROT entry.
            Record.ClientEvidence = "ObservedProcessFileMetadataOnly; COM server identity/version unverified";
            try
            {
                Record.ObservedClients = Process.GetProcessesByName("doors").Take(16).Select(process =>
                {
                    using (process)
                    {
                        try { string path = process.MainModule.FileName; return "PID=" + process.Id + "; EXE=" + path + "; FileVersion=" + FileVersionInfo.GetVersionInfo(path).FileVersion; }
                        catch (Exception) { return "PID=" + process.Id + "; Version/EXE unavailable"; }
                    }
                }).ToArray();
            }
            catch (Exception) { Record.ObservedClients = new[] { "Process metadata unavailable" }; }
        }
        internal void ComResponse(string response)
        {
            Record.RawComResponseUtf16 = DiscoveryDebugRecord.Encode(response);
            Record.ComResponseChars = response?.Length ?? 0;
            Record.ComResponseLfCount = response?.Count(c => c == '\n') ?? 0;
            Save("ComResultRead");
        }
        internal void BeforeParser(Guid id, string response)
        {
            Record.RequestId = id; Record.RawDiscoveryResponseUtf16 = DiscoveryDebugRecord.Encode(response);
            Record.ResponseChars = response?.Length ?? 0;
            Record.LfCount = response?.Count(c => c == '\n') ?? 0;
            Record.ResponseLineCount = string.IsNullOrEmpty(response) ? 0 : Record.LfCount + (response.EndsWith("\n", StringComparison.Ordinal) ? 0 : 1);
            Record.CrLfCount = response == null ? 0 : Enumerable.Range(1, Math.Max(0, response.Length - 1)).Count(i => response[i - 1] == '\r' && response[i] == '\n');
            Record.BareCrCount = (response?.Count(c => c == '\r') ?? 0) - Record.CrLfCount;
            Record.LeadingBom = response != null && response.StartsWith("\uFEFF", StringComparison.Ordinal);
            Save("BeforeDiscoveryParser");
        }
        internal void Accepted() => Save("DiscoveryAccepted");
        internal void Failed(Exception exception, string reason)
        { Record.ExceptionDetails = exception.ToString(); Record.ParserFailureReason = reason; Save("Rejected"); }
        private void Save(string stage)
        {
            Record.Stage = stage; Record.CapturedUtc = DateTime.UtcNow;
            try { store.Save(Record); Saved = true; }
            catch (Exception) { Saved = false; DxlLog.Write(logger, IntegrationLogLevel.Error, "DiscoveryDebugUnavailable", Record.RequestId, "Status=Unavailable; protected capture was not saved"); }
            DxlLog.Write(logger, IntegrationLogLevel.Information, "DiscoveryDebugCapture", Record.RequestId,
                "Stage=" + stage + "; Saved=" + Saved + "; ResponseChars=" + Record.ResponseChars + "; LF=" + Record.LfCount);
        }
        private static string Hash(string value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Convert.FromBase64String(DiscoveryDebugRecord.Encode(value)))).Replace("-", ""); }
    }
}
