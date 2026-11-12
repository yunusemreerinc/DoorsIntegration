using System;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class ReadOnlyProbeProtocol : IDxlProbeProtocol
    {
        private readonly DxlWatchdogSettings watchdogSettings;
        public ReadOnlyProbeProtocol() : this(new DxlWatchdogSettings()) { }
        public ReadOnlyProbeProtocol(DxlWatchdogSettings watchdogSettings)
        { this.watchdogSettings = watchdogSettings ?? throw new ArgumentNullException(nameof(watchdogSettings)); }

        public string CreateScript(DoorsVersion target, Guid correlationId)
        {
            DoorsVersions.Format(target);
            ValidateId(correlationId);
            // Only a GUID is interpolated: no user-provided DXL or file includes.
            // oleSetResult changes the transient automation result, not database data.
            return "// DoorsIntegration: database-read-only probe\n" +
                DxlWatchdogPragma.Line(watchdogSettings.Enabled) + "\n" +
                "oleSetResult(\"" + ExpectedResponse(correlationId) + "\")\n";
        }

        public void ValidateResponse(Guid correlationId, string response)
        {
            ValidateId(correlationId);
            if (!string.Equals(ExpectedResponse(correlationId), response, StringComparison.Ordinal))
            {
                throw new IntegrationException(IntegrationErrorCode.DxlProtocolFailure,
                    "DXL tanılama yanıtı eksik veya bu isteğe ait değil. DXL izinlerini ve istemci çıkışını kontrol edin.");
            }
        }

        private static string ExpectedResponse(Guid id) => "DOORS_INTEGRATION|1|" + id.ToString("N") + "|OK";

        private static void ValidateId(Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("İstek kimliği boş olamaz.", nameof(id));
        }
    }
}
