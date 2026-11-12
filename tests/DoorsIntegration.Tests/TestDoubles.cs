using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using DoorsIntegration.Com;
using DoorsIntegration.Core;

namespace DoorsIntegration.Tests
{
    internal sealed class RecordingLogger : IIntegrationLogger
    {
        public List<string> Entries { get; } = new List<string>();
        public List<Guid> CorrelationIds { get; } = new List<Guid>();

        public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message)
        {
            Entries.Add(level + ":" + eventName + ":" + message);
            CorrelationIds.Add(correlationId);
        }
    }

    internal sealed class FakeDiagnostics : IDoorsDiagnostics
    {
        public int Calls { get; private set; }
        public DoorsVersion LastTarget { get; private set; }
        public Guid LastId { get; private set; }
        public DoorsConnectionMode LastMode { get; private set; }
        public Exception Failure { get; set; }

        public ProbeResult Probe(DoorsVersion target, Guid correlationId,
            DoorsConnectionMode mode = DoorsConnectionMode.Activate)
        {
            Calls++;
            LastTarget = target;
            LastId = correlationId;
            LastMode = mode;
            if (Failure != null) throw Failure;
            return new ProbeResult(target, correlationId);
        }
    }

    internal sealed class FakeAutomationFactory : IDoorsComSessionFactory
    {
        public FakeAutomationSession Session { get; } = new FakeAutomationSession();
        public int Calls { get; private set; }
        public Exception Failure { get; set; }

        public int ConnectThreadId { get; private set; }
        public ApartmentState ConnectApartment { get; private set; }
        public DoorsConnectionMode LastMode { get; private set; }

        public IDoorsComSession Connect(DoorsConnectionMode mode)
        {
            Calls++;
            ConnectThreadId = Thread.CurrentThread.ManagedThreadId;
            ConnectApartment = Thread.CurrentThread.GetApartmentState();
            LastMode = mode;
            if (Failure != null) throw Failure;
            return Session;
        }
    }

    internal sealed class FakeAutomationSession : IDoorsComSession
    {
        public string Script { get; private set; }
        public string ResultOverride { get; set; }
        public Exception RunFailure { get; set; }
        public Exception ReadFailure { get; set; }
        public int ReadCalls { get; private set; }
        public Exception DisposeFailure { get; set; }
        public bool Disposed { get; private set; }
        public int RunThreadId { get; private set; }
        public int DisposeThreadId { get; private set; }

        public void RunString(string script)
        {
            RunThreadId = Thread.CurrentThread.ManagedThreadId;
            Script = script;
            if (RunFailure != null) throw RunFailure;
        }

        public string ReadResult()
        {
            ReadCalls++;
            if (ReadFailure != null) throw ReadFailure;
            return ResultOverride ?? Regex.Match(Script, @"DOORS_INTEGRATION\|1\|[0-9a-f]{32}\|OK").Value;
        }

        public void SetResult(string value) { ResultOverride = value; }
        public void RunFile(string absolutePath) { RunString(System.IO.File.ReadAllText(absolutePath)); }

        public void Dispose()
        {
            DisposeThreadId = Thread.CurrentThread.ManagedThreadId;
            Disposed = true;
            if (DisposeFailure != null) throw DisposeFailure;
        }
    }

    internal static class ApartmentTest
    {
        public static void Run(ApartmentState apartment, Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(apartment);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test iş parçacığı tamamlanmadı.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
