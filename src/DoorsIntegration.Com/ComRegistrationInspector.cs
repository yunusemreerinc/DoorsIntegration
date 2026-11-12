using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    internal interface IRegistryReader
    {
        bool Is64BitOperatingSystem { get; }
        bool KeyExists(ComArchitecture architecture, string path);
        string ReadString(ComArchitecture architecture, string path, string valueName = null);
    }

    internal sealed class WindowsRegistryReader : IRegistryReader
    {
        public bool Is64BitOperatingSystem => Environment.Is64BitOperatingSystem;

        public bool KeyExists(ComArchitecture architecture, string path)
        {
            using (var root = OpenRoot(architecture))
            using (var key = root.OpenSubKey(path, false)) return key != null;
        }

        public string ReadString(ComArchitecture architecture, string path, string valueName = null)
        {
            using (var root = OpenRoot(architecture))
            using (var key = root.OpenSubKey(path, false))
                return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        private static RegistryKey OpenRoot(ComArchitecture architecture) => RegistryKey.OpenBaseKey(
            RegistryHive.ClassesRoot, architecture == ComArchitecture.X86 ? RegistryView.Registry32 : RegistryView.Registry64);
    }

    internal sealed class ServerBinaryInfo
    {
        public ServerBinaryInfo(string version, int? major, int? minor, string machine)
        {
            Version = version;
            Major = major;
            Minor = minor;
            Machine = machine;
        }
        public string Version { get; }
        public int? Major { get; }
        public int? Minor { get; }
        public string Machine { get; }
    }

    internal interface IServerBinaryInspector
    {
        ServerBinaryInfo Inspect(string path);
    }

    internal sealed class ServerBinaryInspector : IServerBinaryInspector
    {
        public ServerBinaryInfo Inspect(string path)
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new ServerBinaryInfo(version.FileVersion,
                string.IsNullOrWhiteSpace(version.FileVersion) ? (int?)null : version.FileMajorPart,
                string.IsNullOrWhiteSpace(version.FileVersion) ? (int?)null : version.FileMinorPart, ReadPeMachine(path));
        }

        private static string ReadPeMachine(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return "Unknown";
                stream.Position = 0x3C;
                var peOffset = reader.ReadInt32();
                if (peOffset < 64 || peOffset > stream.Length - 6) return "Unknown";
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) return "Unknown";
                switch (reader.ReadUInt16())
                {
                    case 0x014C: return "x86 (PE machine)";
                    case 0x8664: return "x64 (PE machine)";
                    default: return "Unknown";
                }
            }
        }
    }

    public sealed class ComRegistrationInspector : IComRegistrationInspector
    {
        private readonly IRegistryReader registry;
        private readonly IServerBinaryInspector binary;

        public ComRegistrationInspector() : this(new WindowsRegistryReader(), new ServerBinaryInspector()) { }
        internal ComRegistrationInspector(IRegistryReader registry, IServerBinaryInspector binary)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.binary = binary ?? throw new ArgumentNullException(nameof(binary));
        }

        public ComRegistrationSnapshot Inspect(ComArchitecture architecture)
        {
            if (architecture != ComArchitecture.X86 && architecture != ComArchitecture.X64)
                throw new ArgumentOutOfRangeException(nameof(architecture));
            if (architecture == ComArchitecture.X64 && !registry.Is64BitOperatingSystem)
                return new ComRegistrationSnapshot(architecture, ComRegistrationState.ViewUnavailable,
                    note: "32-bit Windows üzerinde x64 registry görünümü yok.");
            try
            {
                var rawClsid = registry.ReadString(architecture, DoorsComSession.ProgId + "\\CLSID");
                if (rawClsid == null)
                    return new ComRegistrationSnapshot(architecture, ComRegistrationState.NotRegistered);
                Guid clsid;
                if (!Guid.TryParse(rawClsid, out clsid) || clsid == Guid.Empty)
                    return new ComRegistrationSnapshot(architecture, ComRegistrationState.Invalid,
                        note: "ProgID için geçerli bir CLSID bulunamadı.");
                var classPath = "CLSID\\" + clsid.ToString("B").ToUpperInvariant();
                if (!registry.KeyExists(architecture, classPath))
                    return new ComRegistrationSnapshot(architecture, ComRegistrationState.Invalid, clsid.ToString("B"),
                        note: "CLSID sınıf kaydı eksik.");

                var serverKey = classPath + "\\LocalServer32";
                var explicitExecutable = registry.ReadString(architecture, serverKey, "ServerExecutable");
                var command = registry.ReadString(architecture, serverKey);
                // No server command line or arguments are retained or executed.
                var path = ParseExecutablePath(explicitExecutable, command);
                var inproc = registry.ReadString(architecture, classPath + "\\InprocServer32");
                if (path == null)
                    return Registered(architecture, clsid, null, null,
                        inproc == null ? "LocalServer32 yolu belirsiz veya yok; dosya sürümü okunmadı."
                            : "InprocServer32 kaydı mevcut; yerel istemci EXE sürümü bilinmiyor.");
                if (!IsLocalAbsolutePath(path))
                    return Registered(architecture, clsid, path, null,
                        "Yerel mutlak EXE yolu değil; dosya meta verisi okunmadı.");
                try { return Registered(architecture, clsid, path, binary.Inspect(path), null); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                    ex is ArgumentException || ex is System.ComponentModel.Win32Exception)
                {
                    return Registered(architecture, clsid, path, null,
                        "Kayıtlı sunucu dosyasının sürümü/mimarisi okunamadı (" + ex.GetType().Name + ").");
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return new ComRegistrationSnapshot(architecture, ComRegistrationState.Unreadable,
                    note: "Registry okunamadı (" + ex.GetType().Name + ").");
            }
        }

        private static ComRegistrationSnapshot Registered(ComArchitecture architecture, Guid clsid, string path,
            ServerBinaryInfo info, string note) => new ComRegistrationSnapshot(architecture,
                ComRegistrationState.Registered, clsid.ToString("B"), path, info?.Version, info?.Major, info?.Minor,
                info?.Machine, note);

        internal static string ParseExecutablePath(string explicitExecutable, string command)
        {
            // ServerExecutable is an executable path, not a command line (Microsoft LocalServer32 specification).
            if (!string.IsNullOrWhiteSpace(explicitExecutable))
                return explicitExecutable.Trim();
            if (string.IsNullOrWhiteSpace(command)) return null;
            // Keep environment placeholders unresolved: expansion can differ between registry views/launch contexts.
            command = command.Trim();
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : null;
            }
            var separator = command.IndexOfAny(new[] { ' ', '\t' });
            var firstToken = separator < 0 ? command : command.Substring(0, separator);
            // An unquoted path containing spaces is ambiguous. Do not guess an executable.
            return firstToken.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? firstToken : null;
        }

        private static bool IsLocalAbsolutePath(string path) => path.Length > 3 && char.IsLetter(path[0]) &&
            path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }
}
