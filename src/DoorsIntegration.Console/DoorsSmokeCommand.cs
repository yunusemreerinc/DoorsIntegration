using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.ConsoleApp
{
    public sealed class DoorsSmokeCommand
    {
        private readonly IDoorsSmokeSuite suite;
        private readonly DoorsSmokeReportWriter reports;
        public DoorsSmokeCommand(IDoorsSmokeSuite suite, DoorsSmokeReportWriter reports) { this.suite = suite; this.reports = reports; }
        public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            var values = new Dictionary<string, string>(StringComparer.Ordinal); bool live = false, help = false;
            for (int index = 0; index < args.Length; index++)
            {
                string name = args[index];
                if (name == "--help" || name == "-h") { help = true; continue; }
                if (name == "--live") { if (live) throw Invalid(); live = true; continue; }
                if (!new[] { "--target", "--root", "--module", "--link-module", "--timeout-seconds", "--max-items", "--max-links", "--report" }.Contains(name) ||
                    values.ContainsKey(name) || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw Invalid();
                values.Add(name, args[++index]);
            }
            if (help)
            {
                output.WriteLine("doors smoke [--live] [--target 9.5|9.7] [--root /Project] [--module /Project/Module --link-module /Project/Links]");
                output.WriteLine("            [--timeout-seconds 30] [--max-items 1000] [--max-links 1000] [--report <new-directory>]");
                output.WriteLine("Default: offline, read-only, AttachExisting, no activation, no arbitrary DXL, no automatic retries. Missing real prerequisites => NOT_RUN.");
                return 0;
            }
            string Value(string name, string fallback = null) => values.TryGetValue(name, out var value) ? value : fallback;
            DoorsSmokeRequest request;
            try { request = new DoorsSmokeRequest(DoorsVersions.Parse(Value("--target", "9.7")), live, Value("--root", "/"), Value("--module"), Value("--link-module"),
                TimeSpan.FromSeconds(int.Parse(Value("--timeout-seconds", "30"), NumberStyles.None, CultureInfo.InvariantCulture)),
                int.Parse(Value("--max-items", "1000"), NumberStyles.None, CultureInfo.InvariantCulture), int.Parse(Value("--max-links", "1000"), NumberStyles.None, CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException) { throw Invalid(); }
            string destination;
            try
            {
                destination = Path.GetFullPath(Value("--report", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "SmokeReports",
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"))));
                if (Directory.Exists(destination) || File.Exists(destination) || string.IsNullOrEmpty(Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar))) ||
                    string.IsNullOrEmpty(Path.GetDirectoryName(destination))) throw Invalid();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { throw Invalid(); }
            output.WriteLine("SMOKE; ReadOnly=True; TargetProfile=" + DoorsVersions.Format(request.Target) + "; Live=" + live + "; Connection=AttachExisting; MaximumAttempts=1; AutomaticRetryAllowed=False");
            var run = await suite.RunAsync(request, new ConsoleProgress(output, error), token).ConfigureAwait(false);
            try
            {
                reports.WriteInitial(destination, run); output.WriteLine("Report: " + Path.Combine(destination, "report.json")); output.Flush();
                if (!run.Settlement.IsCompleted) { error.WriteLine("SESSION UNCERTAIN: initial report saved. Waiting for original COM completion/cleanup. No retry or further native test; Ctrl+C requests cancellation only."); error.Flush(); }
                var settled = await run.Settlement.ConfigureAwait(false); reports.WriteSettlement(destination, run, settled);
                output.WriteLine("SMOKE " + run.Status + "; ExitCode=" + run.ExitCode + "; SessionUncertain=" + run.SessionUncertain + "; Run=" + run.RunId.ToString("N"));
                return run.ExitCode;
            }
            finally { await run.Settlement.ConfigureAwait(false); }
        }
        private sealed class ConsoleProgress : IProgress<SmokeTestResult>
        {
            private readonly TextWriter output;
            private readonly TextWriter error;
            internal ConsoleProgress(TextWriter output, TextWriter error) { this.output = output; this.error = error; }
            public void Report(SmokeTestResult result)
            {
                output.WriteLine("[" + result.Status + "] " + result.Test + "; DurationMs=" + result.DurationMs.ToString("F3", CultureInfo.InvariantCulture) + "; Attempts=" + result.Attempts + "; " + result.Reason); output.Flush();
                if (result.Status == SmokeStatus.FAIL || result.Status == SmokeStatus.TIMEOUT || result.Status == SmokeStatus.CANCELLED)
                { error.WriteLine("[" + result.Status + "] " + result.Test + "; Code=" + result.ErrorCode + "; " + result.Reason + "; Case=" + result.CorrelationId.ToString("N")); error.Flush(); }
            }
        }
        private static IntegrationException Invalid() => new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Invalid smoke options. Use smoke --help. Write/script/retry/activation options are not supported.");
    }
}
