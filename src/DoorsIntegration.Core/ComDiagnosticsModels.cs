using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DoorsIntegration.Core
{
    public enum DoorsConnectionMode { Activate, AttachExisting }
    public enum ComArchitecture { X86, X64 }
    public enum ComRegistrationState { Registered, NotRegistered, Invalid, Unreadable, ViewUnavailable }

    public interface IComRegistrationInspector
    {
        ComRegistrationSnapshot Inspect(ComArchitecture architecture);
    }

    public interface IDoorsEnvironmentDiagnostics
    {
        DoorsCompatibilityReport Inspect(DoorsVersion target);
    }

    public sealed class ComRegistrationSnapshot
    {
        public ComRegistrationSnapshot(ComArchitecture architecture, ComRegistrationState state,
            string clsid = null, string serverPath = null, string fileVersion = null,
            int? versionMajor = null, int? versionMinor = null, string peMachine = null, string note = null)
        {
            Architecture = architecture;
            State = state;
            Clsid = clsid;
            ServerPath = serverPath;
            FileVersion = fileVersion;
            VersionMajor = versionMajor;
            VersionMinor = versionMinor;
            PeMachine = peMachine;
            Note = note;
        }

        public ComArchitecture Architecture { get; }
        public ComRegistrationState State { get; }
        public string Clsid { get; }
        public string ServerPath { get; }
        public string FileVersion { get; }
        public int? VersionMajor { get; }
        public int? VersionMinor { get; }
        public string PeMachine { get; }
        public string Note { get; }

        // Registered binary metadata only: never claims the version of an active COM object.
        public bool? MatchesTarget(DoorsVersion target)
        {
            DoorsVersions.Format(target);
            if (State != ComRegistrationState.Registered || !VersionMajor.HasValue || !VersionMinor.HasValue)
                return null;
            return VersionMajor == 9 && VersionMinor == (target == DoorsVersion.V95 ? 5 : 7);
        }
    }

    public sealed class DoorsCompatibilityReport
    {
        public DoorsCompatibilityReport(DoorsVersion target, bool is64BitOperatingSystem,
            bool is64BitProcess, string apartment, IEnumerable<ComRegistrationSnapshot> registrations)
        {
            DoorsVersions.Format(target);
            if (registrations == null) throw new ArgumentNullException(nameof(registrations));
            Target = target;
            Is64BitOperatingSystem = is64BitOperatingSystem;
            Is64BitProcess = is64BitProcess;
            Apartment = apartment;
            Registrations = new ReadOnlyCollection<ComRegistrationSnapshot>(registrations.ToArray());
        }

        public DoorsVersion Target { get; }
        public bool Is64BitOperatingSystem { get; }
        public bool Is64BitProcess { get; }
        public string Apartment { get; }
        public IReadOnlyList<ComRegistrationSnapshot> Registrations { get; }
    }
}
