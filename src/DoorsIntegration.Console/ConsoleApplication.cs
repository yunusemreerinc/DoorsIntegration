using System;
using System.IO;
using System.Linq;
using System.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.ConsoleApp
{
    public sealed class ConsoleApplication
    {
        private readonly IDoorsDiagnostics diagnostics;
        private readonly IDxlProbeProtocol protocol;
        private readonly IntegrationErrorHandler errors;
        private readonly IDoorsEnvironmentDiagnostics environment;
        private readonly IDoorsHealthChecks healthChecks;
        private readonly DoorsSmokeCommand smoke;
        private readonly DxlPingService isolatedPing;

        public ConsoleApplication(IDoorsDiagnostics diagnostics, IDxlProbeProtocol protocol,
            IntegrationErrorHandler errors, IDoorsEnvironmentDiagnostics environment)
            : this(diagnostics, protocol, errors, environment, null) { }

        public ConsoleApplication(IDoorsDiagnostics diagnostics, IDxlProbeProtocol protocol,
            IntegrationErrorHandler errors, IDoorsEnvironmentDiagnostics environment, IDoorsHealthChecks healthChecks)
            : this(diagnostics, protocol, errors, environment, healthChecks, null) { }

        public ConsoleApplication(IDoorsDiagnostics diagnostics, IDxlProbeProtocol protocol,
            IntegrationErrorHandler errors, IDoorsEnvironmentDiagnostics environment, IDoorsHealthChecks healthChecks, DoorsSmokeCommand smoke)
            : this(diagnostics, protocol, errors, environment, healthChecks, smoke, null) { }

        public ConsoleApplication(IDoorsDiagnostics diagnostics, IDxlProbeProtocol protocol,
            IntegrationErrorHandler errors, IDoorsEnvironmentDiagnostics environment, IDoorsHealthChecks healthChecks, DoorsSmokeCommand smoke, DxlPingService isolatedPing)
        {
            this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            this.healthChecks = healthChecks;
            this.smoke = smoke;
            this.isolatedPing = isolatedPing;
        }

        public int Run(string[] args, TextWriter output, TextWriter error)
            => Run(args, output, error, CancellationToken.None);

        public int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        {
            var operationId = Guid.NewGuid();
            try
            {
                if (args != null && args.Length > 0 && args[0] == "smoke")
                {
                    if (smoke == null) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "IDoorsSmokeSuite/DoorsSmokeCommand registration is missing.");
                    return smoke.RunAsync(args.Skip(1).ToArray(), output, error, cancellationToken).GetAwaiter().GetResult();
                }
                var options = CommandLineOptions.Parse(args);
                if (options.Help)
                {
                    output.WriteLine("doors diagnose [--live] [--target 9.5|9.7] [--connection attach|activate]");
                    output.WriteLine("doors ping [--target 9.5|9.7] [--connection attach|activate]");
                    output.WriteLine("doors ping --dxl-only [--target 9.5|9.7]: independent runStr/Result DXL_PING_OK with fresh request token; protected capture enabled.");
                    output.WriteLine("doors smoke [--live] [--target 9.5|9.7] [--report <new-directory>] (full options: smoke --help)");
                    output.WriteLine("diagnose: offline kayıt/dosya sürümü; ping veya diagnose --live: sabit salt okunur DXL gidiş dönüşü.");
                    output.WriteLine("Yeni komutlarda canlı bağlantı varsayılanı attach; --connection yalnızca canlı modda kullanılabilir.");
                    output.WriteLine("DoorsIntegration.Console [--dry-run | --probe | --diagnose-com | --diagnose-compatibility] [--target 9.5|9.7]");
                    output.WriteLine("--probe [--connection activate|attach]: activate COM aktivasyonu yapar; attach yalnızca mevcut ROT oturumunu kullanır.");
                    output.WriteLine("--diagnose-com ve --diagnose-compatibility: salt okunur registry/dosya tanılaması; istemci başlatılmaz.");
                    output.WriteLine("Varsayılan: --dry-run --target 9.7. --probe yalnızca sabit tanılama DXL'i çalıştırır.");
                    return 0;
                }

                if (options.IsolatedPing)
                {
                    if (isolatedPing == null) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "DxlPingService registration is missing.");
                    var ping = isolatedPing.PingAsync(options.Target, options.ConnectionMode, cancellationToken: cancellationToken).GetAwaiter().GetResult();
                    output.WriteLine("PASS DXL_PING_OK; RequestId=" + ping.RequestId.ToString("N") + "; DurationMs=" + ping.Duration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                    output.WriteLine("Requested target is not proof of the COM client's version. Run diagnose separately. No discovery parser or database writes.");
                    return 0;
                }

                if (options.Diagnose || options.Ping)
                    return RunHealthChecks(options, operationId, output, error);

                if (options.DiagnoseCom || options.DiagnoseCompatibility)
                    return DiagnoseEnvironment(options, output);

                if (!options.Probe)
                {
                    output.WriteLine("DRY RUN; DOORS bağlantısı kurulmadı. Hedef profil: " + DoorsVersions.Format(options.Target));
                    output.WriteLine(protocol.CreateScript(options.Target, operationId));
                    return 0;
                }

                var result = diagnostics.Probe(options.Target, operationId, options.ConnectionMode);
                output.WriteLine("Tanılama başarılı. Hedef profil: " + DoorsVersions.Format(result.Target) +
                    "; İstek: " + result.CorrelationId.ToString("N"));
                output.WriteLine("Hedef profil kurulu istemcinin otomatik sürüm tespiti değildir.");
                return 0;
            }
            catch (Exception ex)
            {
                var failure = errors.Handle(ex, operationId);
                error.WriteLine("Hata " + failure.Code + ": " + failure.Message +
                    " İstek: " + failure.CorrelationId.ToString("N"));
                return failure.ExitCode;
            }
        }

        private int RunHealthChecks(CommandLineOptions options, Guid id, TextWriter output, TextWriter error)
        {
            if (healthChecks == null) throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
                "IDoorsHealthChecks kaydı eksik. AddDoorsIntegration() ile servisi kaydedin.");
            output.WriteLine((options.Ping ? "PING" : "DIAGNOSE") + "; Target profile: " + DoorsVersions.Format(options.Target) +
                "; Mode: " + (options.Live ? options.ConnectionMode.ToString() : "Offline") + "; Request: " + id.ToString("N"));
            if (options.Live) output.WriteLine("Canlı COM çağrısı istemci etkileşimini bekleyebilir; zorla iptal uygulanmaz. Veritabanına yazan DXL yoktur.");
            var report = healthChecks.Run(options.Target, id, options.Live, options.ConnectionMode, step =>
            {
                var line = "[" + step.Status + "] " + step.Step +
                    (step.ErrorCode.HasValue ? " (" + (int)step.ErrorCode.Value + "/" + step.ErrorCode.Value + ")" : "") + ": " + step.Message;
                output.WriteLine(line);
                output.Flush();
                if (step.Status == DoorsHealthStatus.Failed) error.WriteLine(line + " Request: " + id.ToString("N"));
            });
            WriteEnvironment(report.Environment, output);
            output.WriteLine("Exit code: " + report.ExitCode + "; Request: " + id.ToString("N"));
            return report.ExitCode;
        }

        private int DiagnoseEnvironment(CommandLineOptions options, TextWriter output)
        {
            var report = environment.Inspect(options.Target);
            WriteEnvironment(report, output);
            output.WriteLine("Dosya meta verisi çalışan istemci sürümünü veya canlı COM/DXL uyumluluğunu kanıtlamaz. DOORS'a bağlanılmadı.");
            var currentView = report.Is64BitProcess ? ComArchitecture.X64 : ComArchitecture.X86;
            var current = report.Registrations.Single(item => item.Architecture == currentView);
            if (current.State == ComRegistrationState.Unreadable) return (int)IntegrationErrorCode.ComRegistrationReadFailed;
            if (current.State != ComRegistrationState.Registered) return (int)IntegrationErrorCode.ComNotRegistered;
            if (!options.DiagnoseCompatibility) return 0;
            var currentMatch = current.MatchesTarget(options.Target);
            return !currentMatch.HasValue ? (int)IntegrationErrorCode.CompatibilityUnknown :
                currentMatch.Value ? 0 : (int)IntegrationErrorCode.TargetVersionMismatch;
        }

        private static void WriteEnvironment(DoorsCompatibilityReport report, TextWriter output)
        {
            output.WriteLine("ProgID: DOORS.Application; OS: " + (report.Is64BitOperatingSystem ? "x64" : "x86") +
                "; Process: " + (report.Is64BitProcess ? "x64" : "x86") + "; Apartment: " + report.Apartment);
            output.WriteLine("Target profile: " + DoorsVersions.Format(report.Target));
            foreach (var item in report.Registrations)
            {
                output.WriteLine("Registry view " + item.Architecture + ": " + item.State + "; CLSID=" + (item.Clsid ?? "unknown"));
                output.WriteLine("  Server executable: " + (item.ServerPath ?? "unknown"));
                output.WriteLine("  File version: " + (item.FileVersion ?? "unknown") + "; PE machine: " + (item.PeMachine ?? "unknown"));
                var match = item.MatchesTarget(report.Target);
                output.WriteLine("  Target file metadata: " + (!match.HasValue ? "UNKNOWN" : match.Value ? "MATCH" : "MISMATCH"));
                if (item.Note != null) output.WriteLine("  " + item.Note);
            }
            output.WriteLine("Registry görünümü sunucu mimarisi değildir; yerel COM sunucusunda çapraz mimari davranışı ortamda doğrulanmalıdır.");
        }
    }
}
