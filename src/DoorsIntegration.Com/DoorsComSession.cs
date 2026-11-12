using System;
using System.Reflection;
using System.IO;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public sealed class DoorsComSession : IDoorsComSession
    {
        // IBM DXL Reference Manual, "Automation interface" and "runStr".
        public const string ProgId = "DOORS.Application";
        private readonly IComRuntime runtime;
        private readonly int ownerThreadId;
        private object application;

        public DoorsComSession(DoorsConnectionMode mode = DoorsConnectionMode.Activate)
            : this(mode, new ComRegistrationInspector(), new WindowsComRuntime()) { }

        internal DoorsComSession(DoorsConnectionMode mode, IComRegistrationInspector registration, IComRuntime runtime)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            if (mode != DoorsConnectionMode.Activate && mode != DoorsConnectionMode.AttachExisting)
                throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Geçersiz COM bağlantı modu.");
            StaGuard.EnsureSta();
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            Mode = mode;

            // Read registry before ProgID resolution. A missing ProgID must not trigger OLE1 registration.
            var currentView = Environment.Is64BitProcess ? ComArchitecture.X64 : ComArchitecture.X86;
            var snapshot = registration.Inspect(currentView);
            if (snapshot.State == ComRegistrationState.Unreadable)
                throw new IntegrationException(IntegrationErrorCode.ComRegistrationReadFailed,
                    "DOORS.Application COM kaydı okunamadı. Registry erişimini kontrol edin.");
            if (snapshot.State != ComRegistrationState.Registered)
                throw new IntegrationException(IntegrationErrorCode.ComNotRegistered,
                    "DOORS.Application bu işlemin registry görünümünde kayıtlı değil veya kaydı geçersiz. --diagnose-com çalıştırın.");

            try
            {
                if (mode == DoorsConnectionMode.AttachExisting)
                {
                    // No activation fallback: a running client may not expose an accessible ROT entry.
                    application = runtime.GetActiveObject(ProgId);
                }
                else
                {
                    var type = runtime.ResolveType(ProgId);
                    if (type == null)
                        throw new IntegrationException(IntegrationErrorCode.ComNotRegistered,
                            "DOORS.Application ProgID türü çözümlenemedi.");
                    // COM decides whether to reuse a server or launch one. This does not guarantee a new process.
                    application = runtime.Activate(type);
                }
                if (application == null)
                    throw new IntegrationException(mode == DoorsConnectionMode.AttachExisting
                        ? IntegrationErrorCode.ActiveSessionUnavailable : IntegrationErrorCode.ClientUnavailable,
                        "DOORS automation nesnesi alınamadı.");
            }
            catch (Exception ex) { throw ComErrors.Translate(ex, mode == DoorsConnectionMode.Activate ? "aktivasyonu" : "ROT bağlantısı"); }
        }

        public DoorsConnectionMode Mode { get; }

        public void RunString(string script)
        {
            EnsureAccess();
            if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("DXL boş olamaz.", nameof(script));
            try { runtime.Invoke(application, "runStr", BindingFlags.InvokeMethod, new object[] { script }); }
            catch (Exception ex) { throw ComErrors.Translate(ex, "runStr çağrısı"); }
        }

        public string ReadResult()
        {
            EnsureAccess();
            try
            {
                var result = runtime.Invoke(application, "Result", BindingFlags.GetProperty, null);
                if (result != null && !(result is string))
                    throw new IntegrationException(IntegrationErrorCode.ComApiUnavailable,
                        "DOORS.Application.Result beklenen string değerini döndürmedi.");
                return result as string;
            }
            catch (Exception ex) { throw ComErrors.Translate(ex, "Result okuması"); }
        }

        // IBM documents runFile(dxlFileName), requiring a full path and UTF-8 script content.
        public void RunFile(string absolutePath)
        {
            EnsureAccess();
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath) || Path.GetPathRoot(absolutePath).Length < 3)
                throw new ArgumentException("runFile için mutlak dosya yolu gerekir.", nameof(absolutePath));
            try { runtime.Invoke(application, "runFile", BindingFlags.InvokeMethod, new object[] { absolutePath }); }
            catch (Exception ex) { throw ComErrors.Translate(ex, "runFile çağrısı"); }
        }

        public void SetResult(string value)
        {
            EnsureAccess();
            if (value == null) throw new ArgumentNullException(nameof(value));
            try { runtime.Invoke(application, "Result", BindingFlags.SetProperty, new object[] { value }); }
            catch (Exception ex) { throw ComErrors.Translate(ex, "Result yazması"); }
        }

        public void Dispose()
        {
            if (application == null) return;
            EnsureAccess();
            var ownedReference = application;
            application = null;
            try { runtime.Release(ownedReference); }
            catch (Exception ex) { throw ComErrors.Translate(ex, "referans bırakma işlemi"); }
        }

        private void EnsureAccess()
        {
            StaGuard.EnsureSta();
            if (Thread.CurrentThread.ManagedThreadId != ownerThreadId)
                throw new IntegrationException(IntegrationErrorCode.InvalidThread,
                    "COM oturumu oluşturulduğu STA iş parçacığında kullanılmalı ve kapatılmalıdır.");
            if (application == null) throw new ObjectDisposedException(nameof(DoorsComSession));
        }
    }

    public sealed class DoorsComSessionFactory : IDoorsComSessionFactory
    {
        private readonly IComRegistrationInspector registration;
        private readonly IComRuntime runtime;

        public DoorsComSessionFactory() : this(new ComRegistrationInspector()) { }
        public DoorsComSessionFactory(IComRegistrationInspector registration) : this(registration, new WindowsComRuntime()) { }
        internal DoorsComSessionFactory(IComRegistrationInspector registration, IComRuntime runtime)
        {
            this.registration = registration ?? throw new ArgumentNullException(nameof(registration));
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public IDoorsComSession Connect(DoorsConnectionMode mode) => new DoorsComSession(mode, registration, runtime);
    }

    public sealed class ComDxlSessionFactory : IDxlSessionFactory
    {
        private readonly IDoorsComSessionFactory factory;
        public ComDxlSessionFactory(IDoorsComSessionFactory factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }
        public IDxlExecutionSession Open(DoorsConnectionMode mode) => factory.Connect(mode);
    }
}
