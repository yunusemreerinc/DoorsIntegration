using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class ModuleSelectionScriptOptions
    {
        public string ScriptPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "module-selection.dxl");
    }
    public sealed class DxlModuleSelectionMetadataProvider : IModuleSelectionMetadataProvider
    {
        private readonly IDxlExecutor executor;
        private readonly ModuleSelectionScriptOptions options;
        public DxlModuleSelectionMetadataProvider(IDxlExecutor executor, ModuleSelectionScriptOptions options)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.options = options ?? throw new ArgumentNullException(nameof(options)); }
        public async Task<ModuleSelectionMetadata> ReadAsync(string repositoryId, string canonicalModulePath, DoorsVersion target,
            DoorsConnectionMode connectionMode, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Validate binding and path before any native work; temporary ID is never returned as identity.
            new BaselineModuleReference(repositoryId, "validation-only", canonicalModulePath);
            cancellationToken.ThrowIfCancellationRequested();
            var request = new DxlExecutionRequest(options.ScriptPath, DxlInvocationMethod.RunString, DxlResultMode.Inline,
                new Dictionary<string, string> { ["path"] = canonicalModulePath }, timeout, connectionMode, target, maxInlineResultChars: 131072);
            var operation = executor.Start(request);
            try
            {
                using (var result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (result.IsFile) throw Failure();
                    return Parse(result.Text, result.CorrelationId, repositoryId, canonicalModulePath);
                }
            }
            catch (DxlExecutionWaitException)
            {
                // Never cancel/abandon the original COM call, retry it or release its late result early.
                _ = operation.Completion.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) { try { task.Result.Dispose(); } catch { } }
                    else { var observed = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        private static ModuleSelectionMetadata Parse(string text, Guid id, string repository, string path)
        {
            try
            {
                if (text == null || text.Length > 131072 || text.IndexOf('\0') >= 0) throw Failure();
                text = text.Replace("\r\n", "\n");
                if (text.IndexOf('\r') >= 0) throw Failure();
                var lines = text.Split('\n');
                if (lines.Length != 4 || lines[0] != "MODULE_SELECTION|1|" + id.ToString("N") || lines[2] != "END" || lines[3] != "") throw Failure();
                var fields = lines[1].Split('\t'); if (fields.Length != 10 || fields[0] != "M" || Decode(fields[1]) != path) throw Failure();
                if (!Enum.TryParse(fields[3], false, out DoorsAccessStatus access) || !Enum.IsDefined(typeof(DoorsAccessStatus), access) || fields[3] != access.ToString() ||
                    !Enum.TryParse(fields[4], false, out SelectionBaselineStatus status) || !Enum.IsDefined(typeof(SelectionBaselineStatus), status) || fields[4] != status.ToString() ||
                    (fields[9] != "Closed" && fields[9] != "NotOwned" && fields[9] != "Failed")) throw Failure();
                string moduleId = Decode(fields[2]), suffix = Decode(fields[7]), annotation = Decode(fields[8]);
                if (!int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
                    !int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)) throw Failure();
                if (access != DoorsAccessStatus.Readable && status != SelectionBaselineStatus.Unavailable) throw Failure();
                if (fields[9] == "Failed" && access == DoorsAccessStatus.Readable) throw Failure();
                if (status != SelectionBaselineStatus.Available && (major != 0 || minor != 0 || suffix != "" || annotation != "")) throw Failure();
                var module = moduleId.Length == 0 ? null : new BaselineModuleReference(repository, moduleId, path);
                if (access == DoorsAccessStatus.Readable && module == null) throw Failure();
                return new ModuleSelectionMetadata(path, module, access, status,
                    status == SelectionBaselineStatus.Available ? new BaselineVersion(major, minor, suffix) : null, annotation,
                    access != DoorsAccessStatus.Readable ? "Native module metadata is inaccessible or read-only cleanup could not be verified." :
                    status == SelectionBaselineStatus.Unavailable ? "Latest existing baseline metadata could not be confirmed." : null);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException) { throw Failure(); }
        }
        private static string Decode(string field)
        {
            var value = new StringBuilder();
            for (int i = 0; i < field.Length; i++)
            {
                char c = field[i];
                if (c != '\\') { value.Append(c); continue; }
                if (++i == field.Length) throw Failure();
                switch (field[i])
                { case '\\': value.Append('\\'); break; case 'n': value.Append('\n'); break; case 'r': value.Append('\r'); break; case 't': value.Append('\t'); break; default: throw Failure(); }
            }
            return value.ToString();
        }
        private static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.DiscoveryProtocolFailure, "Module selection metadata protocol validation failed; no unverified identity was accepted.");
    }
}
