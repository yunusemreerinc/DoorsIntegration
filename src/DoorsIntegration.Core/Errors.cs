using System;

namespace DoorsIntegration.Core
{
    public enum IntegrationErrorCode
    {
        InvalidConfiguration = 2,
        ClientUnavailable = 10,
        ComFailure = 11,
        DxlProtocolFailure = 12,
        InvalidThread = 13,
        ComNotRegistered = 14,
        ActiveSessionUnavailable = 15,
        ComApiUnavailable = 16,
        ComAccessDenied = 17,
        ComServerBusy = 18,
        ComServerDisconnected = 19,
        ComRegistrationReadFailed = 20,
        CompatibilityUnknown = 21,
        TargetVersionMismatch = 22,
        DxlTimeout = 23,
        DxlWaitCancelled = 24,
        DoorsOperationBusy = 25,
        DxlScriptLoadFailed = 26,
        DxlResultTooLarge = 27,
        DxlResultFileFailed = 28,
        DxlScriptReportedError = 29,
        DiscoveryProtocolFailure = 30,
        JsonExportFailed = 31,
        RequirementProtocolFailure = 32,
        LinkProtocolFailure = 33,
        TraceabilityExportFailed = 34,
        ExportFailed = 35,
        ExportLimitReached = 36,
        ModuleCreationApprovalRequired = 37,
        ModuleCreationBlocked = 38,
        ModuleCreationProtocolFailure = 39,
        ModuleCreationOutcomeUnknown = 40,
        RequirementWriteApprovalRequired = 41,
        RequirementWriteBlocked = 42,
        RequirementWriteProtocolFailure = 43,
        RequirementWriteOutcomeUnknown = 44,
        RequirementWriteAuditFailed = 45,
        LinkWriteApprovalRequired = 46,
        LinkWriteBlocked = 47,
        LinkWriteProtocolFailure = 48,
        LinkWriteOutcomeUnknown = 49,
        LinkWriteAuditFailed = 50,
        ProcessingFailed = 51,
        BaselineComparisonFailed = 52,
        SmokeFailed = 53,
        TraceabilityGraphFailed = 54,
        TraceabilityGraphLimitReached = 55,
        BulkBaselineFailed = 56,
        BaselineMetadataFailed = 57,
        BulkBaselinePreflightFailed = 58,
        BaselineExecutionFailed = 59,
        BulkBaselineJobFailed = 60,
        Unexpected = 99
    }

    public class IntegrationException : Exception
    {
        public IntegrationException(IntegrationErrorCode code, string message, Exception innerException = null)
            : base(message, innerException)
        {
            Code = code;
        }

        public IntegrationErrorCode Code { get; }
    }

    public sealed class IntegrationFailure
    {
        public IntegrationFailure(IntegrationErrorCode code, string message, Guid correlationId)
        {
            Code = code;
            Message = message;
            CorrelationId = correlationId;
        }

        public IntegrationErrorCode Code { get; }
        public string Message { get; }
        public Guid CorrelationId { get; }
        public int ExitCode => (int)Code;
    }

    public sealed class IntegrationErrorHandler
    {
        private readonly IIntegrationLogger logger;

        public IntegrationErrorHandler(IIntegrationLogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IntegrationFailure Handle(Exception exception, Guid correlationId)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            var known = exception as IntegrationException;
            var code = known?.Code ?? IntegrationErrorCode.Unexpected;
            var message = known?.Message ?? "Beklenmeyen entegrasyon hatası. Tanılama kaydını kontrol edin.";
            // Do not log exception messages, DXL, credentials or requirements content.
            var cause = exception.GetBaseException();
            logger.Write(IntegrationLogLevel.Error, "OperationFailed", correlationId,
                "Code=" + code + "; ExceptionType=" + cause.GetType().FullName +
                "; HResult=0x" + cause.HResult.ToString("X8"));
            return new IntegrationFailure(code, message, correlationId);
        }
    }
}
