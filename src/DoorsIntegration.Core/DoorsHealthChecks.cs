using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DoorsIntegration.Core
{
    public enum DoorsHealthStep { ComRegistration, ClientVersion, ComConnection, DxlExecution, DxlResponse, ComCleanup }
    public enum DoorsHealthStatus { Passed, Failed, Skipped }

    public interface IDoorsHealthChecks
    {
        DoorsHealthReport Run(DoorsVersion target, Guid correlationId, bool live = false,
            DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting,
            Action<DoorsHealthStepResult> progress = null);
    }

    public sealed class DoorsHealthStepResult
    {
        public DoorsHealthStepResult(DoorsHealthStep step, DoorsHealthStatus status, string message,
            IntegrationErrorCode? errorCode = null)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if ((status == DoorsHealthStatus.Failed) != errorCode.HasValue)
                throw new ArgumentException("Only failed checks must have an error code.", nameof(errorCode));
            Step = step;
            Status = status;
            Message = message;
            ErrorCode = errorCode;
        }

        public DoorsHealthStep Step { get; }
        public DoorsHealthStatus Status { get; }
        public string Message { get; }
        public IntegrationErrorCode? ErrorCode { get; }
    }

    public sealed class DoorsHealthReport
    {
        public DoorsHealthReport(Guid correlationId, DoorsCompatibilityReport environment,
            IEnumerable<DoorsHealthStepResult> steps)
        {
            CorrelationId = correlationId;
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            Steps = new ReadOnlyCollection<DoorsHealthStepResult>(steps.ToArray());
        }

        public Guid CorrelationId { get; }
        public DoorsCompatibilityReport Environment { get; }
        public IReadOnlyList<DoorsHealthStepResult> Steps { get; }
        // The first failure remains primary even if cleanup also fails.
        public int ExitCode => (int?)Steps.FirstOrDefault(step => step.Status == DoorsHealthStatus.Failed)?.ErrorCode ?? 0;
    }
}
