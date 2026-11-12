using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DxlHierarchyReader : IDoorsHierarchyReader
    {
        private readonly IDxlExecutor executor;
        private readonly DiscoveryScriptOptions scripts;
        private readonly IIntegrationLogger logger;
        private readonly IDiscoveryDebugStore debugStore;

        public DxlHierarchyReader(IDxlExecutor executor, DiscoveryScriptOptions scripts, IIntegrationLogger logger)
            : this(executor, scripts, logger, new ProtectedDiscoveryDebugStore()) { }

        public DxlHierarchyReader(IDxlExecutor executor, DiscoveryScriptOptions scripts, IIntegrationLogger logger, IDiscoveryDebugStore debugStore)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.debugStore = debugStore ?? throw new ArgumentNullException(nameof(debugStore));
        }

        public async Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (!Enum.IsDefined(typeof(DiscoveryKind), kind)) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Geçersiz keşif türü.");
            var id = Guid.NewGuid();
            var elapsed = Stopwatch.StartNew();
            var totalTimeout = scope.Timeout;
            var observed = new Dictionary<string, DoorsDiscoveryItem>(StringComparer.Ordinal);
            var issues = new List<DiscoveryIssue>();
            var issueKeys = new HashSet<string>(StringComparer.Ordinal);
            var queued = new HashSet<string>(StringComparer.Ordinal) { scope.RootPath };
            var folders = new Queue<KeyValuePair<string, int>>();
            folders.Enqueue(new KeyValuePair<string, int>(scope.RootPath, 0));
            bool truncated = false;
            int visited = 0;
            void Issue(string path, DiscoveryIssueCode code)
            {
                if (issueKeys.Add(path + "\n" + code)) issues.Add(new DiscoveryIssue(path, code));
            }
            DxlLog.Write(logger, IntegrationLogLevel.Information, "DiscoveryStarted", id, "Kind=" + kind +
                "; TimeoutMs=" + totalTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
            try
            {
                while (folders.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (visited >= scope.MaxFolders || observed.Count >= scope.MaxItems)
                    {
                        truncated = true;
                        Issue(scope.RootPath, visited >= scope.MaxFolders ? DiscoveryIssueCode.MaxFoldersReached : DiscoveryIssueCode.MaxItemsReached);
                        break;
                    }
                    var next = folders.Dequeue();
                    var remaining = totalTimeout - elapsed.Elapsed;
                    if (remaining < TimeSpan.FromMilliseconds(1)) throw new IntegrationException(IntegrationErrorCode.DxlTimeout,
                        "Toplam keşif bekleme süresi doldu. Yeni DXL çağrısı başlatılmadı.");
                    bool lookup = kind == DiscoveryKind.ModuleByPath;
                    var batch = await ReadBatchAsync(next.Key, lookup, scope, remaining, id, cancellationToken).ConfigureAwait(false);
                    if (!lookup) visited++;
                    foreach (var issue in batch.Issues) Issue(issue.FullPath, issue.Code);
                    if (batch.IsTruncated)
                    {
                        truncated = true;
                        Issue(next.Key, DiscoveryIssueCode.MaxItemsReached);
                    }
                    if (observed.TryGetValue(batch.Scope.FullPath, out var previous) && !batch.Scope.MetadataVerified)
                        observed[batch.Scope.FullPath] = new DoorsDiscoveryItem(previous.FullPath, previous.Name, previous.Type, batch.Scope.AccessStatus, previous.MetadataVerified);
                    else observed[batch.Scope.FullPath] = batch.Scope;

                    foreach (var child in batch.Children)
                    {
                        if (observed.Count >= scope.MaxItems)
                        {
                            truncated = true;
                            Issue(next.Key, DiscoveryIssueCode.MaxItemsReached);
                            break;
                        }
                        if (observed.ContainsKey(child.FullPath)) throw new IntegrationException(IntegrationErrorCode.DiscoveryProtocolFailure,
                            "Aynı keşifte beklenmeyen yinelenen öğe alındı; kapsam doğrulanamadı.");
                        observed.Add(child.FullPath, child);
                        if (child.AccessStatus != DoorsAccessStatus.Readable)
                        {
                            Issue(child.FullPath, DiscoveryIssueCode.AccessRestricted);
                            continue;
                        }
                        if (!scope.Recursive || !DiscoveryProtocol.IsContainer(child.Type)) continue;
                        int depth = next.Value + 1;
                        if (depth >= scope.MaxDepth)
                        {
                            truncated = true;
                            Issue(child.FullPath, DiscoveryIssueCode.MaxDepthReached);
                        }
                        else if (queued.Add(child.FullPath)) folders.Enqueue(new KeyValuePair<string, int>(child.FullPath, depth));
                    }
                    if (lookup) break;
                }
                // Initial inaccessible scope is retained so an empty list cannot masquerade as successful access.
                var items = observed.Values.Where(item => Matches(kind, item.Type) ||
                    (item.FullPath == scope.RootPath && item.AccessStatus != DoorsAccessStatus.Readable));
                var snapshot = new DiscoverySnapshot(id, kind, scope, items, issues, visited, truncated, DateTime.UtcNow);
                DxlLog.Write(logger, IntegrationLogLevel.Information, "DiscoveryCompleted", id,
                    "Items=" + snapshot.Items.Count.ToString(CultureInfo.InvariantCulture) + "; Issues=" + issues.Count +
                    "; Truncated=" + truncated + "; VisitedFolders=" + visited);
                return snapshot;
            }
            catch (OperationCanceledException ex)
            {
                throw new IntegrationException(IntegrationErrorCode.DxlWaitCancelled, "Keşif beklemesi iptal edildi; yeni DXL çağrısı başlatılmadı.", ex);
            }
            catch (Exception ex)
            {
                DxlLog.Write(logger, IntegrationLogLevel.Error, "DiscoveryFailed", id,
                    "Code=" + ((ex as IntegrationException)?.Code ?? IntegrationErrorCode.Unexpected) + "; ExceptionType=" + ex.GetType().FullName);
                throw;
            }
        }

        private async Task<DiscoveryBatch> ReadBatchAsync(string path, bool lookup, DiscoveryScope scope,
            TimeSpan timeout, Guid discoveryId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new DxlExecutionRequest(scripts.ScriptPath, DxlInvocationMethod.RunString, DxlResultMode.Inline,
                new Dictionary<string, string>
                {
                    ["path"] = path, ["name"] = DoorsItemPath.LeafName(path), ["mode"] = lookup ? "lookup" : "list"
                }, timeout, scope.ConnectionMode, scope.Target, maxInlineResultChars: 2097152);
            var capture = new DiscoveryDebugCapture(debugStore, logger, discoveryId);
            request.DebugCapture = capture;
            var operation = executor.Start(request);
            DxlLog.Write(logger, IntegrationLogLevel.Information, "DiscoveryBatchStarted", discoveryId,
                "DxlRequestId=" + operation.CorrelationId.ToString("N"));
            try
            {
                using (var result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (result.IsFile) throw new IntegrationException(IntegrationErrorCode.DiscoveryProtocolFailure, "Keşif meta veri yanıtı beklenen metin biçiminde değil.");
                    capture.BeforeParser(result.CorrelationId, result.Text);
                    try
                    {
                        var batch = DiscoveryProtocol.Parse(result.Text, result.CorrelationId, path, lookup);
                        capture.Accepted();
                        return batch;
                    }
                    catch (DiscoveryProtocolException ex)
                    {
                        capture.Failed(ex, ex.DiagnosticMetadata);
                        ex.AttachDebugCapture(result.CorrelationId, capture.Saved);
                        DxlLog.Write(logger, IntegrationLogLevel.Error, "DiscoveryBatchRejected", discoveryId,
                            "DxlRequestId=" + result.CorrelationId.ToString("N") + "; " + ex.DiagnosticMetadata);
                        throw;
                    }
                }
            }
            catch (DxlExecutionWaitException)
            {
                // Completion owns the late result. Never release COM, terminate its STA or retry after a soft deadline.
                _ = operation.Completion.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        try { task.Result.Dispose(); }
                        catch (Exception) { DxlLog.Write(logger, IntegrationLogLevel.Error, "DiscoveryLateCleanupFailed", operation.CorrelationId, "Status=Failed"); }
                    }
                    else { var observedFailure = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (IntegrationException ex) when (!(ex is DiscoveryProtocolException))
            {
                throw new IntegrationException(ex.Code, ex.Message + " DebugRequestId=" + operation.CorrelationId.ToString("N") +
                    "; ProtectedCapture=" + (capture.Saved ? "Saved" : "Unavailable"), ex);
            }
        }

        private static bool Matches(DiscoveryKind kind, DoorsItemKind type)
        {
            switch (kind)
            {
                case DiscoveryKind.Projects: return type == DoorsItemKind.Project;
                case DiscoveryKind.Folders: return type == DoorsItemKind.Folder;
                case DiscoveryKind.FormalModules: return type == DoorsItemKind.FormalModule;
                case DiscoveryKind.ModuleByPath: return true;
                default: return DiscoveryProtocol.IsContainer(type) || type == DoorsItemKind.FormalModule;
            }
        }
    }
}
