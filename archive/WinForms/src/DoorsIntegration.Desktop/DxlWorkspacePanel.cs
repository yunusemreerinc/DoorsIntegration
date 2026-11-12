using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Desktop
{
    // The reviewed bytes are frozen before approval. A later edit of the original file cannot change the dispatched script.
    public sealed class DesktopCapturedScript : IDisposable
    {
        private readonly string directory;
        public DesktopCapturedScript(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var encoding = new UTF8Encoding(false, true); byte[] bytes = encoding.GetBytes(source);
            if (bytes.Length > 1048576) throw new InvalidDataException("Script exceeds 1 MiB.");
            directory = Path.Combine(Path.GetTempPath(), "DoorsIntegration-desktop", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); PathName = Path.Combine(directory, "reviewed.dxl");
            try { File.WriteAllBytes(PathName, bytes); }
            catch { Directory.Delete(directory, false); throw; }
            using (var sha = SHA256.Create()) Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            Source = source;
        }
        public string Source { get; }
        public string PathName { get; }
        public string Sha256 { get; }
        public void Dispose()
        {
            if (File.Exists(PathName)) File.Delete(PathName);
            if (Directory.Exists(directory)) Directory.Delete(directory, false);
        }
    }

    internal sealed class DxlWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox file = WorkspaceUi.Input("DxlScriptPath");
        private readonly ComboBox method = WorkspaceUi.Choice<DxlInvocationMethod>("DxlInvocationMethod"), mode = WorkspaceUi.Choice<DxlResultMode>("DxlResultMode");
        private readonly NumericUpDown timeout = WorkspaceUi.Number("DxlTimeoutSeconds", 30, 1, 86400),
            inlineLimit = WorkspaceUi.Number("DxlInlineCharacters", 65536, 1, 16777216), fileLimit = WorkspaceUi.Number("DxlResultMiB", 256, 1, 1024);
        private readonly DataGridView parameters = WorkspaceUi.Grid("DxlParameters", "Parameter name", "Value");
        private readonly RichTextBox source = WorkspaceUi.Output("DxlSourcePreview", 220), output = WorkspaceUi.Output("DxlResultPreview", 180);
        private DxlExecutionResult result;
        public DxlWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "DxlWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("External UTF-8 DXL file", file, WorkspaceUi.Button("DxlBrowse", "Browse...", (s, e) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "DXL scripts|*.dxl|All files|*.*", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) file.Text = dialog.FileName;
            }), WorkspaceUi.Button("DxlLoadPreview", "Load offline preview", async (s, e) => await workspace.RunAsync("Loading external script offline...", async token =>
            {
                string path = file.Text; source.Text = await Task.Run(() => workspace.Services.Scripts.Load(Path.GetFullPath(path), 1048576), token);
                workspace.SetStatus("Offline source preview loaded. Nothing was executed.");
            }))));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Method", method, "Result transport", mode, "Timeout seconds", timeout, "Max inline chars", inlineLimit, "Max result MiB", fileLimit));
            parameters.ReadOnly = false; parameters.AllowUserToAddRows = true; parameters.AllowUserToDeleteRows = true; parameters.Height = 150;
            WorkspaceUi.Add(form, parameters); WorkspaceUi.Add(form, source);
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("DxlRunReviewed", "Review script and run...", async (s, e) => await RunScriptAsync()),
                WorkspaceUi.Button("DxlSaveResult", "Save complete result...", async (s, e) => await SaveResultAsync())));
            WorkspaceUi.Add(form, output);
            output.Text = "Custom DXL can modify the database. Only run trusted, reviewed scripts. File mode scripts must write di_resultPath and call di_completeFile(); inline scripts use oleSetResult().\nTimeout/cancellation does not force-stop COM. Raw result preview is limited to 65,536 characters; Save keeps the complete file.";
        }
        private Task RunScriptAsync() => workspace.RunAsync("Preparing explicit custom DXL review...", async token =>
        {
            parameters.EndEdit(); string originalPath = Path.GetFullPath(file.Text);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DataGridViewRow row in parameters.Rows)
            {
                if (row.IsNewRow) continue;
                string name = Convert.ToString(row.Cells[0].Value), value = Convert.ToString(row.Cells[1].Value);
                if (name.Length == 0 && value.Length == 0) continue;
                if (values.ContainsKey(name)) throw WorkspaceUi.Invalid("Duplicate DXL parameter name: " + name);
                values.Add(name, value);
            }
            string text = await Task.Run(() => workspace.Services.Scripts.Load(originalPath, 1048576), token);
            using (var captured = new DesktopCapturedScript(text))
            {
                var request = new DxlExecutionRequest(captured.PathName, WorkspaceUi.Selected<DxlInvocationMethod>(method), WorkspaceUi.Selected<DxlResultMode>(mode),
                    values, TimeSpan.FromSeconds((double)timeout.Value), workspace.Connection(), workspace.Target(), maxInlineResultChars: (int)inlineLimit.Value,
                    maxResultFileBytes: (long)fileLimit.Value * 1048576);
                var review = new DesktopWriteReview("Review custom DXL execution", originalPath,
                    "CUSTOM DXL MAY CHANGE OR DELETE DATABASE DATA. This application cannot infer its effects or roll them back.\nFile: " + originalPath +
                    "\nSHA-256: " + captured.Sha256 + "\nProfile: " + request.Target + "; Connection: " + request.ConnectionMode + "; Method: " + request.Method +
                    "; Result: " + request.ResultMode + "; Timeout: " + request.Timeout + "\nPARAMETERS (data):\n" + string.Join("\n", values.Select(p => p.Key + " = " + p.Value)) +
                    "\nEXACT CAPTURED SOURCE:\n" + captured.Source, DateTime.UtcNow.AddMinutes(5), true);
                source.Text = captured.Source;
                if (!await workspace.Services.Confirmation.ReviewAsync(review, token)) { workspace.SetStatus("Custom script execution declined; no dispatch."); return; }
                token.ThrowIfCancellationRequested(); DisposeResult(); workspace.InvalidateCaches();
                var operation = workspace.Services.Dxl.Start(request);
                try { result = await operation.WaitAsync(token); }
                catch (DxlExecutionWaitException)
                {
                    workspace.SetStatus("DXL wait ended; original COM call still owns the operation. Waiting for late result; no retry.");
                    result = await operation.Completion;
                }
                output.Text = await Task.Run(() => PreviewResult(result));
                workspace.SetStatus("DXL completed. Request=" + result.CorrelationId.ToString("N") + "; File bytes=" + result.ResultFileBytes);
            }
        });
        private static string PreviewResult(DxlExecutionResult response)
        {
            if (!response.IsFile) return WorkspaceUi.Clip(response.Text, 65536);
            using (var stream = response.OpenResultStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                var chars = new char[65537]; int length = 0, count;
                while (length < chars.Length && (count = reader.Read(chars, length, chars.Length - length)) != 0) length += count;
                return WorkspaceUi.Clip(new string(chars, 0, length), 65536);
            }
        }
        private async Task SaveResultAsync()
        {
            string path = WorkspaceUi.SavePath(this, "Save full custom DXL result", "txt"); if (path == null) return;
            await workspace.RunAsync("Saving complete DXL result...", async token =>
            {
                var retained = result ?? throw WorkspaceUi.Invalid("Run a script first.");
                await Task.Run(() =>
                {
                    if (!retained.IsFile) File.WriteAllText(path, retained.Text, new UTF8Encoding(false, true));
                    else using (var input = retained.OpenResultStream()) using (var destination = File.Create(path)) input.CopyTo(destination);
                }, token);
                workspace.SetStatus("Complete DXL result saved.");
            });
        }
        public void DisposeResult() { var old = result; result = null; old?.Dispose(); }
    }
}
