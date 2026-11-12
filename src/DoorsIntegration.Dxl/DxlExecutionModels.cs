using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public enum DxlInvocationMethod { RunString, RunFile }
    public enum DxlResultMode { Inline, File }

    public interface IDxlExecutor
    {
        DxlExecutionOperation ActiveOperation { get; }
        bool IsBusy { get; }
        DxlExecutionOperation Start(DxlExecutionRequest request);
        Task<DxlExecutionResult> ExecuteAsync(DxlExecutionRequest request, CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class DxlExecutionRequest
    {
        public DxlExecutionRequest(string scriptPath, DxlInvocationMethod method = DxlInvocationMethod.RunString,
            DxlResultMode resultMode = DxlResultMode.Inline, IDictionary<string, string> parameters = null,
            TimeSpan? timeout = null, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting,
            DoorsVersion target = DoorsVersion.V97, int maxScriptBytes = 1024 * 1024,
            int maxInlineResultChars = 65536, long maxResultFileBytes = 256L * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) throw new ArgumentException("Script yolu boş olamaz.", nameof(scriptPath));
            if (method != DxlInvocationMethod.RunString && method != DxlInvocationMethod.RunFile) throw new ArgumentOutOfRangeException(nameof(method));
            if (resultMode != DxlResultMode.Inline && resultMode != DxlResultMode.File) throw new ArgumentOutOfRangeException(nameof(resultMode));
            if (connectionMode != DoorsConnectionMode.Activate && connectionMode != DoorsConnectionMode.AttachExisting)
                throw new ArgumentOutOfRangeException(nameof(connectionMode));
            DoorsVersions.Format(target);
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            ValidateTimeout(Timeout);
            if (maxScriptBytes < 1 || maxScriptBytes > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxScriptBytes));
            if (maxInlineResultChars < 1 || maxInlineResultChars > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxInlineResultChars));
            if (maxResultFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxResultFileBytes));
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            var totalChars = 0;
            if (parameters != null)
            {
                if (parameters.Count > 128) throw new ArgumentException("En fazla 128 parametre desteklenir.", nameof(parameters));
                foreach (var item in parameters)
                {
                    DxlStringLiteral.ValidateParameterName(item.Key);
                    if (item.Value == null || item.Value.Length > 65536) throw new ArgumentException("Parametre değeri geçersiz veya çok büyük.", nameof(parameters));
                    totalChars += item.Value.Length;
                    if (totalChars > 262144) throw new ArgumentException("Toplam parametre boyutu sınırı aşıldı.", nameof(parameters));
                    DxlStringLiteral.Encode(item.Value);
                    copy.Add(item.Key, item.Value);
                }
            }
            ScriptPath = Path.GetFullPath(scriptPath);
            Method = method;
            ResultMode = resultMode;
            ConnectionMode = connectionMode;
            Target = target;
            MaxScriptBytes = maxScriptBytes;
            MaxInlineResultChars = maxInlineResultChars;
            MaxResultFileBytes = maxResultFileBytes;
            Parameters = new ReadOnlyDictionary<string, string>(copy);
        }

        public string ScriptPath { get; }
        public DxlInvocationMethod Method { get; }
        public DxlResultMode ResultMode { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }
        public TimeSpan Timeout { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public DoorsVersion Target { get; }
        public int MaxScriptBytes { get; }
        public int MaxInlineResultChars { get; }
        public long MaxResultFileBytes { get; }
        // Infrastructure-only final guard, checked on the STA worker after a queue wait.
        internal Func<bool> DispatchPrecondition { get; set; }
        internal DiscoveryDebugCapture DebugCapture { get; set; }

        internal static void ValidateTimeout(TimeSpan timeout)
        {
            if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromHours(24))
                throw new ArgumentOutOfRangeException(nameof(timeout), "Bekleme süresi 1 ms ile 24 saat arasında olmalıdır.");
        }
    }

    public sealed class DxlExecutionWaitException : IntegrationException
    {
        internal DxlExecutionWaitException(IntegrationErrorCode code, DxlExecutionOperation operation)
            : base(code, "DXL beklemesi sona erdi. COM çağrısı devam ediyor olabilir; Completion ve COM temizliği bitmeden yeni işlem başlatmayın.")
        {
            Operation = operation;
        }
        public DxlExecutionOperation Operation { get; }
    }

    public sealed class DxlScriptException : IntegrationException
    {
        internal DxlScriptException(string scriptError)
            : base(IntegrationErrorCode.DxlScriptReportedError, "DXL betiği bir uygulama hatası bildirdi.")
        {
            ScriptError = scriptError;
        }
        // Available explicitly to the caller; excluded from exception Message and automatic logs.
        public string ScriptError { get; }
    }

    public sealed class DxlExecutionResult : IDisposable
    {
        private readonly object gate = new object();
        private readonly IDxlWorkspace workspace;
        private readonly Stream fileLease;
        private readonly long maxFileBytes;
        private bool disposed;

        internal DxlExecutionResult(Guid id, string text)
        {
            CorrelationId = id;
            Text = text;
        }
        internal DxlExecutionResult(Guid id, IDxlWorkspace workspace, Stream fileLease, long maxFileBytes)
        {
            CorrelationId = id;
            this.workspace = workspace;
            this.fileLease = fileLease;
            this.maxFileBytes = maxFileBytes;
            ResultFilePath = workspace.ResultPath;
            ResultFileBytes = fileLease.Length;
        }
        public Guid CorrelationId { get; }
        public string Text { get; }
        public string ResultFilePath { get; }
        public long? ResultFileBytes { get; }
        public bool IsFile => workspace != null;

        public Stream OpenResultStream()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(DxlExecutionResult));
                if (workspace == null) throw new InvalidOperationException("Bu sonuç inline metindir.");
                return workspace.OpenResult(maxFileBytes);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                fileLease?.Dispose();
                workspace?.Dispose();
            }
        }
    }
}
