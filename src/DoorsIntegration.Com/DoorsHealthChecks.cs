using System;
using System.Collections.Generic;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    // No connection is made by construction or by offline Run. Live calls belong to the caller's STA.
    public sealed class DoorsHealthChecks : IDoorsHealthChecks
    {
        private readonly IDoorsEnvironmentDiagnostics environment;
        private readonly IDoorsComSessionFactory sessions;
        private readonly IDxlProbeProtocol protocol;
        private readonly IDoorsOperationGate gate;
        private readonly IntegrationErrorHandler errors;
        private readonly IIntegrationLogger logger;
        private readonly IDxlRunLimitAutomation runLimitAutomation;

        public DoorsHealthChecks(IDoorsEnvironmentDiagnostics environment, IDoorsComSessionFactory sessions,
            IDxlProbeProtocol protocol, IDoorsOperationGate gate, IntegrationErrorHandler errors,
            IIntegrationLogger logger)
            : this(environment, sessions, protocol, gate, errors, logger, new NoDxlRunLimitAutomation()) { }

        public DoorsHealthChecks(IDoorsEnvironmentDiagnostics environment, IDoorsComSessionFactory sessions,
            IDxlProbeProtocol protocol, IDoorsOperationGate gate, IntegrationErrorHandler errors,
            IIntegrationLogger logger, IDxlRunLimitAutomation runLimitAutomation)
        {
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
            this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.runLimitAutomation = runLimitAutomation ?? throw new ArgumentNullException(nameof(runLimitAutomation));
        }

        public DoorsHealthReport Run(DoorsVersion target, Guid correlationId, bool live = false,
            DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting,
            Action<DoorsHealthStepResult> progress = null)
        {
            DoorsVersions.Format(target);
            if (correlationId == Guid.Empty) throw new ArgumentException("Request ID is required.", nameof(correlationId));
            if (mode != DoorsConnectionMode.Activate && mode != DoorsConnectionMode.AttachExisting)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (live) StaGuard.EnsureSta();
            var snapshot = environment.Inspect(target);
            var results = new List<DoorsHealthStepResult>();
            Action<DoorsHealthStep, DoorsHealthStatus, string, IntegrationErrorCode?> record = (step, status, message, code) =>
            {
                var result = new DoorsHealthStepResult(step, status, message, code);
                results.Add(result);
                Log(status == DoorsHealthStatus.Failed ? IntegrationLogLevel.Error : IntegrationLogLevel.Information,
                    "HealthStepCompleted", correlationId, "Step=" + step + "; Status=" + status + "; Code=" + code);
                progress?.Invoke(result);
            };
            Action<DoorsHealthStep, string> pass = (step, message) => record(step, DoorsHealthStatus.Passed, message, null);
            Action<DoorsHealthStep, string> skip = (step, message) => record(step, DoorsHealthStatus.Skipped, message, null);
            Action<DoorsHealthStep, Exception, string> fail = (step, exception, hint) =>
            {
                var translated = ComErrors.Translate(exception, "tanılaması");
                IntegrationFailure failure;
                try { failure = errors.Handle(translated, correlationId); }
                catch (Exception) { failure = new IntegrationFailure(translated.Code, translated.Message, correlationId); }
                record(step, DoorsHealthStatus.Failed, hint + " " + failure.Message, failure.Code);
            };
            bool Check(DoorsHealthStep step, Action action, string hint, string success)
            {
                Log(IntegrationLogLevel.Information, "HealthStepStarted", correlationId, "Step=" + step);
                try { action(); }
                catch (Exception ex)
                {
                    fail(step, ex, hint);
                    return false;
                }
                pass(step, success);
                return true;
            }

            var currentView = snapshot.Is64BitProcess ? ComArchitecture.X64 : ComArchitecture.X86;
            var registration = snapshot.Registrations.Single(item => item.Architecture == currentView);
            bool registered = registration.State == ComRegistrationState.Registered;
            if (registered)
                pass(DoorsHealthStep.ComRegistration, "DOORS.Application kaydı mevcut; işlem registry görünümü: " + currentView + ".");
            else
                fail(DoorsHealthStep.ComRegistration, new IntegrationException(
                    registration.State == ComRegistrationState.Unreadable ? IntegrationErrorCode.ComRegistrationReadFailed : IntegrationErrorCode.ComNotRegistered,
                    "DOORS.Application kaydı " + currentView + " görünümünde " + registration.State +
                    ". İstemci kurulumunu/kayıt izinlerini ve x86/x64 raporunu kontrol edin; kayıt otomatik değiştirilmedi."), "COM kayıt kontrolü başarısız.");

            if (!registered)
                skip(DoorsHealthStep.ClientVersion, "COM kaydı doğrulanamadığı için kayıtlı istemci sürümü sınanamadı.");
            else
            {
                var match = registration.MatchesTarget(target);
                if (!match.HasValue)
                    fail(DoorsHealthStep.ClientVersion, new IntegrationException(IntegrationErrorCode.CompatibilityUnknown,
                        "Kayıtlı sunucunun EXE dosya sürümü okunamadı. Raporlanan Server executable yolunu, dosya erişimini ve DOORS Help > About bilgisini kontrol edin."), "Sürüm kanıtı eksik.");
                else if (!match.Value)
                    fail(DoorsHealthStep.ClientVersion, new IntegrationException(IntegrationErrorCode.TargetVersionMismatch,
                        "Kayıtlı EXE sürümü hedef " + DoorsVersions.Format(target) + " ile eşleşmiyor. Doğru --target profilini ve kayıtlı istemciyi kontrol edin."), "Sürüm kontrolü başarısız.");
                else
                    pass(DoorsHealthStep.ClientVersion, "Kayıtlı EXE dosya sürümü hedef " + DoorsVersions.Format(target) +
                        " ile eşleşiyor. Bu bilgi çalışan/ROT istemcisinin sürümünü kanıtlamaz.");
            }

            if (!live || !registered)
            {
                var reason = !live ? "Offline tanılama; canlı test için doors ping veya doors diagnose --live kullanın." : "COM kayıt adımı başarısız; bağımlı adım çalıştırılmadı.";
                skip(DoorsHealthStep.ComConnection, reason);
                skip(DoorsHealthStep.DxlExecution, reason);
                skip(DoorsHealthStep.DxlResponse, reason);
                skip(DoorsHealthStep.ComCleanup, "COM oturumu alınmadı.");
                return new DoorsHealthReport(correlationId, snapshot, results);
            }

            IDisposable lease = null;
            IDoorsComSession session = null;
            try
            {
                var connected = Check(DoorsHealthStep.ComConnection, () =>
                {
                    lease = gate.Enter();
                    session = sessions.Connect(mode);
                    if (session == null) throw new IntegrationException(IntegrationErrorCode.ClientUnavailable, "COM oturum fabrikası oturum döndürmedi.");
                }, "Bağlantı kurulamadı. attach için erişilebilir ROT oturumu gerekir; otomatik activate geçişi yapılmaz.",
                    "STA üzerinde COM oturumu alındı; mod: " + mode + ".");
                if (!connected)
                {
                    skip(DoorsHealthStep.DxlExecution, "COM bağlantısı kurulamadı.");
                    skip(DoorsHealthStep.DxlResponse, "DXL çalıştırılmadı.");
                }
                else
                {
                    var executed = Check(DoorsHealthStep.DxlExecution,
                        () =>
                        {
                            var script = protocol.CreateScript(target, correlationId);
                            using (runLimitAutomation.Watch(correlationId, () => true)) session.RunString(script);
                        },
                        "runStr çağrısı başarısız. DOORS meşgul durumunu, DXL iznini ve istemci DXL çıkışını kontrol edin.",
                        "runStr çağrısı döndü; DXL başarısı ayrıca Result yanıtı ile sınanacak.");
                    if (!executed)
                        skip(DoorsHealthStep.DxlResponse, "runStr başarısız olduğu için eski Result değeri okunmadı.");
                    else
                        Check(DoorsHealthStep.DxlResponse, () => protocol.ValidateResponse(correlationId, session.ReadResult()),
                            "DXL cevabı alınamadı/doğrulanamadı. Boş, eski veya başka isteğe ait cevap kabul edilmez; DOORS DXL çıkışını kontrol edin.",
                            "Result içindeki oleSetResult yanıtı bu isteğin kimliğiyle doğrulandı (PONG).");
                }
            }
            finally
            {
                try
                {
                    if (session == null) skip(DoorsHealthStep.ComCleanup, "COM oturumu alınmadı.");
                    else
                    {
                        Check(DoorsHealthStep.ComCleanup, session.Dispose, "COM referansı bırakılırken hata oluştu.",
                            "Alınan COM referansı aynı STA üzerinde bırakıldı; Quit çağrılmadı.");
                    }
                }
                finally { lease?.Dispose(); }
            }
            return new DoorsHealthReport(correlationId, snapshot, results);
        }

        private void Log(IntegrationLogLevel level, string name, Guid id, string message)
        {
            // A failing log sink must not hide a result or prevent STA cleanup/gate release.
            try { logger.Write(level, name, id, message); }
            catch (Exception) { }
        }
    }
}
