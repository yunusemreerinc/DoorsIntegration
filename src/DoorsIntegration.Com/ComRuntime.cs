using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public interface IDoorsComSession : IDxlExecutionSession { }

    public interface IDoorsComSessionFactory
    {
        IDoorsComSession Connect(DoorsConnectionMode mode);
    }

    internal interface IComRuntime
    {
        Type ResolveType(string progId);
        object Activate(Type type);
        object GetActiveObject(string progId);
        object Invoke(object application, string member, BindingFlags operation, object[] arguments);
        void Release(object application);
    }

    internal sealed class WindowsComRuntime : IComRuntime
    {
        public Type ResolveType(string progId) => Type.GetTypeFromProgID(progId, false);
        public object Activate(Type type) => Activator.CreateInstance(type);
        public object GetActiveObject(string progId) => Marshal.GetActiveObject(progId);

        public object Invoke(object application, string member, BindingFlags operation, object[] arguments) =>
            application.GetType().InvokeMember(member, operation | BindingFlags.Public | BindingFlags.Instance,
                null, application, arguments, CultureInfo.InvariantCulture);

        public void Release(object application)
        {
            // Release this acquisition once. Do not invalidate other consumers' RCWs.
            if (Marshal.IsComObject(application)) Marshal.ReleaseComObject(application);
        }
    }

    internal static class StaGuard
    {
        public static void EnsureSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new IntegrationException(IntegrationErrorCode.InvalidThread,
                    "DOORS COM işlemleri STA iş parçacığında çalışmalıdır.");
        }
    }

    internal static class ComErrors
    {
        public static IntegrationException Translate(Exception exception, string operation)
        {
            while (exception is TargetInvocationException && exception.InnerException != null)
                exception = exception.InnerException;
            var known = exception as IntegrationException;
            if (known != null) return known;

            var code = IntegrationErrorCode.ComFailure;
            var hint = "İstemci oturumunu ve DXL izinlerini kontrol edin.";
            switch (unchecked((uint)exception.HResult))
            {
                case 0x80040154: // REGDB_E_CLASSNOTREG
                case 0x800401F3: // CO_E_CLASSSTRING
                    code = IntegrationErrorCode.ComNotRegistered;
                    hint = "DOORS.Application COM kaydı ve x86/x64 registry görünümünü kontrol edin.";
                    break;
                case 0x800401E3: // MK_E_UNAVAILABLE
                    code = IntegrationErrorCode.ActiveSessionUnavailable;
                    hint = "ROT içinde erişilebilir bir DOORS oturumu bulunamadı. Otomatik aktivasyon yapılmadı.";
                    break;
                case 0x80020003: // DISP_E_MEMBERNOTFOUND
                case 0x80020006: // DISP_E_UNKNOWNNAME
                    code = IntegrationErrorCode.ComApiUnavailable;
                    hint = "Kayıtlı nesne belgelenmiş runStr/runFile/Result automation üyelerini sunmuyor.";
                    break;
                case 0x80070005: // E_ACCESSDENIED
                    code = IntegrationErrorCode.ComAccessDenied;
                    hint = "DOORS oturumunun kullanıcı, yetki ve masaüstü erişimini kontrol edin.";
                    break;
                case 0x80010001: // RPC_E_CALL_REJECTED
                case 0x8001010A: // RPC_E_SERVERCALL_RETRYLATER
                    code = IntegrationErrorCode.ComServerBusy;
                    hint = "DOORS meşgul veya bir iletişim kutusu açık. Çağrı otomatik tekrarlanmadı.";
                    break;
                case 0x80010108: // RPC_E_DISCONNECTED
                case 0x800706BA: // RPC_S_SERVER_UNAVAILABLE
                case 0x800401FD: // CO_E_OBJNOTCONNECTED
                    code = IntegrationErrorCode.ComServerDisconnected;
                    hint = "DOORS COM bağlantısı kesildi; yeni bir oturum bağlantısı gerekir.";
                    break;
            }
            if (exception is MissingMemberException)
            {
                code = IntegrationErrorCode.ComApiUnavailable;
                hint = "Kayıtlı nesne belgelenmiş runStr/runFile/Result automation üyelerini sunmuyor.";
            }
            if (exception is InvalidComObjectException)
            {
                code = IntegrationErrorCode.ComServerDisconnected;
                hint = "DOORS COM referansı artık geçerli değil; yeni bir oturum bağlantısı gerekir.";
            }
            if (exception is UnauthorizedAccessException || exception is System.Security.SecurityException)
            {
                code = IntegrationErrorCode.ComAccessDenied;
                hint = "DOORS oturumunun kullanıcı, yetki ve masaüstü erişimini kontrol edin.";
            }
            return new IntegrationException(code, "DOORS COM " + operation + " başarısız. " + hint, exception);
        }
    }
}
