using System;
using System.Collections.Generic;
using System.Reflection;
using DoorsIntegration.Com;
using DoorsIntegration.Core;

namespace DoorsIntegration.Tests
{
    internal sealed class FakeComRuntime : IComRuntime
    {
        public Type ResolvedType { get; set; } = typeof(object);
        public object AutomationObject { get; set; } = new object();
        public object Result { get; set; } = "OK";
        public Exception ActivateFailure { get; set; }
        public Exception AttachFailure { get; set; }
        public Exception InvokeFailure { get; set; }
        public Exception ReleaseFailure { get; set; }
        public int ResolveCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int AttachCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public string LastProgId { get; private set; }
        public string LastMember { get; private set; }
        public BindingFlags LastOperation { get; private set; }
        public object[] LastArguments { get; private set; }
        public object ReleasedObject { get; private set; }

        public Type ResolveType(string progId) { ResolveCalls++; LastProgId = progId; return ResolvedType; }
        public object Activate(Type type)
        {
            ActivateCalls++;
            if (ActivateFailure != null) throw ActivateFailure;
            return AutomationObject;
        }
        public object GetActiveObject(string progId)
        {
            AttachCalls++;
            LastProgId = progId;
            if (AttachFailure != null) throw AttachFailure;
            return AutomationObject;
        }
        public object Invoke(object application, string member, BindingFlags operation, object[] arguments)
        {
            LastMember = member;
            LastOperation = operation;
            LastArguments = arguments;
            if (InvokeFailure != null) throw InvokeFailure;
            return Result;
        }
        public void Release(object application)
        {
            ReleaseCalls++;
            ReleasedObject = application;
            if (ReleaseFailure != null) throw ReleaseFailure;
        }
    }

    internal sealed class FakeRegistrationInspector : IComRegistrationInspector
    {
        public ComRegistrationState State { get; set; } = ComRegistrationState.Registered;
        public List<ComArchitecture> Reads { get; } = new List<ComArchitecture>();
        public ComRegistrationSnapshot Inspect(ComArchitecture architecture)
        {
            Reads.Add(architecture);
            return new ComRegistrationSnapshot(architecture, State);
        }
    }

    internal static class ComTestEnvironment
    {
        public static DoorsComSession OpenSession(FakeComRuntime runtime = null,
            ComRegistrationState state = ComRegistrationState.Registered,
            DoorsConnectionMode mode = DoorsConnectionMode.Activate) =>
            new DoorsComSession(mode, new FakeRegistrationInspector { State = state }, runtime ?? new FakeComRuntime());
    }

    internal sealed class FakeRegistryReader : IRegistryReader
    {
        public bool Is64BitOperatingSystem { get; set; } = true;
        public Exception Failure { get; set; }
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Keys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool KeyExists(ComArchitecture architecture, string path)
        {
            if (Failure != null) throw Failure;
            return Keys.Contains(architecture + ":" + path);
        }
        public string ReadString(ComArchitecture architecture, string path, string valueName = null)
        {
            if (Failure != null) throw Failure;
            string value;
            return Values.TryGetValue(architecture + ":" + path + ":" + valueName, out value) ? value : null;
        }
        public void Set(ComArchitecture architecture, string path, string value, string name = null) =>
            Values[architecture + ":" + path + ":" + name] = value;
    }

    internal sealed class FakeBinaryInspector : IServerBinaryInspector
    {
        public Exception Failure { get; set; }
        public string LastPath { get; private set; }
        public ServerBinaryInfo Info { get; set; } = new ServerBinaryInfo("9.7.2.10", 9, 7, "x64 (PE machine)");
        public ServerBinaryInfo Inspect(string path)
        {
            LastPath = path;
            if (Failure != null) throw Failure;
            return Info;
        }
    }

    internal sealed class FakeEnvironmentDiagnostics : IDoorsEnvironmentDiagnostics
    {
        public int Calls { get; private set; }
        public ComRegistrationState State { get; set; } = ComRegistrationState.Registered;
        public int? Major { get; set; } = 9;
        public int? Minor { get; set; } = 7;
        public bool Is64BitProcess { get; set; }
        public ComRegistrationState X64State { get; set; } = ComRegistrationState.NotRegistered;
        public DoorsCompatibilityReport Inspect(DoorsVersion target)
        {
            Calls++;
            return new DoorsCompatibilityReport(target, true, Is64BitProcess, "STA", new[]
            {
                new ComRegistrationSnapshot(ComArchitecture.X86, State, fileVersion: "test-file-version", versionMajor: Major, versionMinor: Minor),
                new ComRegistrationSnapshot(ComArchitecture.X64, X64State, fileVersion: "test-file-version", versionMajor: Major, versionMinor: Minor)
            });
        }
    }
}
