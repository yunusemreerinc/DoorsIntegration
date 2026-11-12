using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Tests
{
    internal sealed class ExecutorTestLog : IIntegrationLogger
    {
        public ConcurrentQueue<string> Entries { get; } = new ConcurrentQueue<string>();
        public bool ThrowOnWrite { get; set; }
        public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message)
        {
            if (ThrowOnWrite) throw new IOException("broken log sink");
            Entries.Enqueue(eventName + ":" + correlationId.ToString("N") + ":" + message);
        }
    }

    internal sealed class ExecutorTestSessions : IDxlSessionFactory
    {
        public ExecutorTestSession Session { get; } = new ExecutorTestSession();
        public int OpenCalls { get; private set; }
        public int OpenThread { get; private set; }
        public ApartmentState OpenApartment { get; private set; }
        public DoorsConnectionMode Mode { get; private set; }
        public IDxlExecutionSession Open(DoorsConnectionMode mode)
        {
            OpenCalls++;
            Mode = mode;
            OpenThread = Thread.CurrentThread.ManagedThreadId;
            OpenApartment = Thread.CurrentThread.GetApartmentState();
            return Session;
        }
    }

    internal sealed class ExecutorTestSession : IDxlExecutionSession
    {
        public string Script { get; private set; }
        public string FilePath { get; private set; }
        public string Pending { get; private set; }
        public string Prefix => Pending.Substring(0, Pending.Length - "PENDING".Length);
        public string Payload { get; set; } = "sample-result";
        public string ResponseOverride { get; set; }
        public bool FileResult { get; set; }
        public bool BlockRun { get; set; }
        public bool BlockDispose { get; set; }
        public ManualResetEventSlim EnteredRun { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim ReleaseRun { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim EnteredDispose { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim ReleaseDispose { get; } = new ManualResetEventSlim();
        public Action<ExecutorTestSession> OnRun { get; set; }
        public Exception RunFailure { get; set; }
        public Exception DisposeFailure { get; set; }
        public bool Disposed { get; private set; }
        public List<int> ThreadIds { get; } = new List<int>();
        public List<string> Calls { get; } = new List<string>();

        public void SetResult(string value)
        {
            Record("SetResult");
            Pending = value;
        }
        public void RunString(string script)
        {
            Record("runStr");
            Script = script;
            Run();
        }
        public void RunFile(string path)
        {
            Record("runFile");
            FilePath = path;
            Script = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
            Run();
        }
        private void Run()
        {
            EnteredRun.Set();
            if (BlockRun && !ReleaseRun.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test worker stuck");
            OnRun?.Invoke(this);
            if (RunFailure != null) throw RunFailure;
        }
        public string ReadResult()
        {
            Record("ReadResult");
            return ResponseOverride ?? Prefix + (FileResult ? "FILE" : "OK|" + Payload);
        }
        public void Dispose()
        {
            Record("Dispose");
            EnteredDispose.Set();
            if (BlockDispose && !ReleaseDispose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test cleanup stuck");
            Disposed = true;
            if (DisposeFailure != null) throw DisposeFailure;
        }
        private void Record(string call)
        {
            ThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            Calls.Add(call);
        }
    }

    internal sealed class ExecutorTestContext : IDisposable
    {
        public ExecutorTestContext()
        {
            Root = Path.Combine(Path.GetTempPath(), "DoorsIntegrationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SourcePath = Path.Combine(Root, "source.dxl");
            File.WriteAllText(SourcePath, "oleSetResult(\"sample-result\")\n", new System.Text.UTF8Encoding(false));
            Workspaces = new DxlWorkspaceFactory(Path.Combine(Root, "operations"));
            Executor = CreateExecutor();
        }
        public string Root { get; }
        public string SourcePath { get; }
        public ExecutorTestSessions Sessions { get; } = new ExecutorTestSessions();
        public ExecutorTestLog Log { get; } = new ExecutorTestLog();
        public DoorsOperationGate Gate { get; } = new DoorsOperationGate();
        public DxlWorkspaceFactory Workspaces { get; }
        public DxlExecutor Executor { get; }
        public DxlExecutor CreateExecutor(IDxlScriptLoader loader = null) =>
            new DxlExecutor(Sessions, loader ?? new Utf8DxlScriptLoader(), Workspaces, Log, Gate);
        public string ResultPath(DxlExecutionOperation operation) => Path.Combine(operation.WorkspaceDirectory, "result.dat");
        public void Dispose()
        {
            Sessions.Session.ReleaseRun.Set();
            Sessions.Session.ReleaseDispose.Set();
            var active = Executor.ActiveOperation;
            if (active != null)
            {
                try
                {
                    if (!active.Completion.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("active test operation");
                    active.Completion.GetAwaiter().GetResult().Dispose();
                }
                catch (AggregateException) { }
            }
            // Resolve and verify the recursive cleanup target is this test's private temp directory.
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DoorsIntegrationTests")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup target");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            Sessions.Session.EnteredRun.Dispose();
            Sessions.Session.ReleaseRun.Dispose();
            Sessions.Session.EnteredDispose.Dispose();
            Sessions.Session.ReleaseDispose.Dispose();
        }
    }
}
