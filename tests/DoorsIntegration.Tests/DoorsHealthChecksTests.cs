using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DoorsHealthChecksTests
    {
        [TestMethod]
        public void OfflineChecksRegistrationAndVersionAndSkipsLiveStepsEvenOnMta()
        {
            ApartmentTest.Run(ApartmentState.MTA, () =>
            {
                var fixture = new HealthFixture();
                var report = fixture.Service.Run(DoorsVersion.V97, Guid.NewGuid());
                Assert.AreEqual(0, report.ExitCode);
                AssertStep(report, DoorsHealthStep.ComRegistration, DoorsHealthStatus.Passed);
                AssertStep(report, DoorsHealthStep.ClientVersion, DoorsHealthStatus.Passed);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Skipped);
                AssertStep(report, DoorsHealthStep.DxlResponse, DoorsHealthStatus.Skipped);
                Assert.AreEqual(0, fixture.Factory.Calls);
                Assert.IsFalse(fixture.Gate.IsBusy);
                Assert.AreEqual(6, report.Steps.Count);
            });
        }

        [DataTestMethod]
        [DataRow(ComRegistrationState.NotRegistered, 14)]
        [DataRow(ComRegistrationState.Invalid, 14)]
        [DataRow(ComRegistrationState.Unreadable, 20)]
        public void FailedRegistrationNeverConnectsAndNamesFailedStep(ComRegistrationState state, int code)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Environment.State = state;
                var report = fixture.Live();
                Assert.AreEqual(code, report.ExitCode);
                AssertStep(report, DoorsHealthStep.ComRegistration, DoorsHealthStatus.Failed);
                AssertStep(report, DoorsHealthStep.ClientVersion, DoorsHealthStatus.Skipped);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Skipped);
                Assert.AreEqual(0, fixture.Factory.Calls);
                StringAssert.Contains(report.Steps[0].Message, "DOORS.Application");
            });
        }

        [DataTestMethod]
        [DataRow(DoorsVersion.V95, 5)]
        [DataRow(DoorsVersion.V97, 7)]
        public void EachSupportedProfileMatchesRegisteredFileMetadata(DoorsVersion target, int minor)
        {
            var fixture = new HealthFixture();
            fixture.Environment.Minor = minor;
            var report = fixture.Service.Run(target, Guid.NewGuid());
            Assert.AreEqual(0, report.ExitCode);
            StringAssert.Contains(report.Steps[1].Message, "çalışan/ROT istemcisinin sürümünü kanıtlamaz");
        }

        [DataTestMethod]
        [DataRow(true, 21)]
        [DataRow(false, 22)]
        public void UnknownOrMismatchedVersionFailsSeparatelyAndLiveRoundTripStillRuns(bool unknown, int code)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Environment.Minor = unknown ? (int?)null : 5;
                var report = fixture.Live();
                Assert.AreEqual(code, report.ExitCode);
                AssertStep(report, DoorsHealthStep.ComRegistration, DoorsHealthStatus.Passed);
                AssertStep(report, DoorsHealthStep.ClientVersion, DoorsHealthStatus.Failed);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Passed);
                AssertStep(report, DoorsHealthStep.DxlResponse, DoorsHealthStatus.Passed);
            });
        }

        [TestMethod]
        public void ProcessViewIsRequiredEvenIfOtherViewIsRegistered()
        {
            var fixture = new HealthFixture();
            fixture.Environment.Is64BitProcess = true;
            var report = fixture.Service.Run(DoorsVersion.V97, Guid.NewGuid());
            Assert.AreEqual(14, report.ExitCode);
            StringAssert.Contains(report.Steps[0].Message, "X64");
            fixture.Environment.X64State = ComRegistrationState.Registered;
            Assert.AreEqual(0, fixture.Service.Run(DoorsVersion.V97, Guid.NewGuid()).ExitCode);
        }

        [DataTestMethod]
        [DataRow(DoorsConnectionMode.AttachExisting)]
        [DataRow(DoorsConnectionMode.Activate)]
        public void LiveRoundTripUsesSelectedModeFixedScriptAndOneStaOwner(DoorsConnectionMode mode)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                var id = Guid.NewGuid();
                var progress = new List<DoorsHealthStepResult>();
                var report = fixture.Service.Run(DoorsVersion.V97, id, true, mode, progress.Add);
                Assert.AreEqual(0, report.ExitCode);
                Assert.IsTrue(report.Steps.All(step => step.Status == DoorsHealthStatus.Passed));
                CollectionAssert.AreEqual(report.Steps.ToArray(), progress.ToArray());
                Assert.AreEqual(mode, fixture.Factory.LastMode);
                Assert.AreEqual(ApartmentState.STA, fixture.Factory.ConnectApartment);
                Assert.AreEqual(fixture.Factory.ConnectThreadId, fixture.Factory.Session.RunThreadId);
                Assert.AreEqual(fixture.Factory.ConnectThreadId, fixture.Factory.Session.DisposeThreadId);
                Assert.AreEqual(new ReadOnlyProbeProtocol().CreateScript(DoorsVersion.V97, id), fixture.Factory.Session.Script);
                Assert.AreEqual(1, fixture.Factory.Session.ReadCalls);
                Assert.IsTrue(fixture.Factory.Session.Disposed);
                Assert.IsFalse(fixture.Gate.IsBusy);
                Assert.IsTrue(fixture.Logger.CorrelationIds.All(value => value == id));
                Assert.IsFalse(string.Join(" ", fixture.Logger.Entries).Contains("oleSetResult"));
            });
        }

        [TestMethod]
        public void MissingActiveSessionFailsConnectionAndSkipsRunAndRead()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Factory.Failure = new COMException("PRIVATE_CONTENT", unchecked((int)0x800401E3));
                var report = fixture.Live();
                Assert.AreEqual(15, report.ExitCode);
                AssertStep(report, DoorsHealthStep.ComConnection, DoorsHealthStatus.Failed);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Skipped);
                AssertStep(report, DoorsHealthStep.DxlResponse, DoorsHealthStatus.Skipped);
                Assert.AreEqual(DoorsConnectionMode.AttachExisting, fixture.Factory.LastMode);
                Assert.AreEqual(1, fixture.Factory.Calls);
                Assert.AreEqual(0, fixture.Factory.Session.ReadCalls);
                Assert.IsFalse(fixture.Gate.IsBusy);
                AssertNoPrivateContent(fixture, report);
            });
        }

        [DataTestMethod]
        [DataRow(unchecked((int)0x80010001), 18)]
        [DataRow(unchecked((int)0x80070005), 17)]
        [DataRow(unchecked((int)0x80020003), 16)]
        [DataRow(unchecked((int)0x80010108), 19)]
        public void RunFailureIsSeparateFromResponseAndCleanupStillRuns(int hresult, int code)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Factory.Session.RunFailure = new COMException("PRIVATE_CONTENT", hresult);
                var report = fixture.Live();
                Assert.AreEqual(code, report.ExitCode);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Failed);
                AssertStep(report, DoorsHealthStep.DxlResponse, DoorsHealthStatus.Skipped);
                AssertStep(report, DoorsHealthStep.ComCleanup, DoorsHealthStatus.Passed);
                Assert.AreEqual(0, fixture.Factory.Session.ReadCalls);
                Assert.IsFalse(fixture.Gate.IsBusy);
                AssertNoPrivateContent(fixture, report);
            });
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("PRIVATE_CONTENT")]
        [DataRow("DOORS_INTEGRATION|1|00000000000000000000000000000001|OK")]
        public void EmptyMalformedAndStaleResultFailResponseAfterSuccessfulRun(string response)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Factory.Session.ResultOverride = response;
                var report = fixture.Live();
                Assert.AreEqual(12, report.ExitCode);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Passed);
                AssertStep(report, DoorsHealthStep.DxlResponse, DoorsHealthStatus.Failed);
                AssertStep(report, DoorsHealthStep.ComCleanup, DoorsHealthStatus.Passed);
                AssertNoPrivateContent(fixture, report);
            });
        }

        [TestMethod]
        public void ResultGetterFailureIsAttributedToResponse()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Factory.Session.ReadFailure = new COMException("PRIVATE_CONTENT", unchecked((int)0x80020003));
                var report = fixture.Live();
                Assert.AreEqual(16, report.ExitCode);
                AssertStep(report, DoorsHealthStep.DxlExecution, DoorsHealthStatus.Passed);
                AssertStep(report, DoorsHealthStep.DxlResponse, DoorsHealthStatus.Failed);
                Assert.IsTrue(fixture.Factory.Session.Disposed);
                AssertNoPrivateContent(fixture, report);
            });
        }

        [DataTestMethod]
        [DataRow(false, 19)]
        [DataRow(true, 12)]
        public void CleanupFailureIsReportedWithoutReplacingPrimaryFailure(bool failResponse, int code)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Factory.Session.DisposeFailure = new COMException("PRIVATE_CONTENT", unchecked((int)0x80010108));
                if (failResponse) fixture.Factory.Session.ResultOverride = "";
                var report = fixture.Live();
                Assert.AreEqual(code, report.ExitCode);
                Assert.AreEqual(6, report.Steps.Count);
                AssertStep(report, DoorsHealthStep.ComCleanup, DoorsHealthStatus.Failed);
                Assert.IsFalse(fixture.Gate.IsBusy);
                AssertNoPrivateContent(fixture, report);
            });
        }

        [TestMethod]
        public void BusyGatePreventsConnectionAndRemainsOwnedByExistingOperation()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                using (fixture.Gate.Enter())
                {
                    Assert.AreEqual(25, fixture.Live().ExitCode);
                    Assert.AreEqual(0, fixture.Factory.Calls);
                    Assert.IsTrue(fixture.Gate.IsBusy);
                }
                Assert.AreEqual(0, fixture.Live().ExitCode);
            });
        }

        [TestMethod]
        public void LiveOnMtaFailsBeforeInspectingOrConnecting()
        {
            ApartmentTest.Run(ApartmentState.MTA, () =>
            {
                var fixture = new HealthFixture();
                Assert.AreEqual(IntegrationErrorCode.InvalidThread,
                    Assert.ThrowsException<IntegrationException>(() => fixture.Live()).Code);
                Assert.AreEqual(0, fixture.Environment.Calls);
                Assert.AreEqual(0, fixture.Factory.Calls);
            });
        }

        [TestMethod]
        public void BrokenLoggerDoesNotPreventCleanupOrHideResponseError()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var fixture = new HealthFixture();
                fixture.Factory.Session.ResultOverride = "";
                var logger = new BrokenHealthLogger();
                var service = new DoorsHealthChecks(fixture.Environment, fixture.Factory, new ReadOnlyProbeProtocol(),
                    fixture.Gate, new IntegrationErrorHandler(logger), logger);
                Assert.AreEqual(12, service.Run(DoorsVersion.V97, Guid.NewGuid(), true).ExitCode);
                Assert.IsTrue(fixture.Factory.Session.Disposed);
                Assert.IsFalse(fixture.Gate.IsBusy);
            });
        }

        private static void AssertStep(DoorsHealthReport report, DoorsHealthStep step, DoorsHealthStatus status) =>
            Assert.AreEqual(status, report.Steps.Single(item => item.Step == step).Status, step.ToString());

        private static void AssertNoPrivateContent(HealthFixture fixture, DoorsHealthReport report)
        {
            Assert.IsFalse(string.Join(" ", report.Steps.Select(step => step.Message)).Contains("PRIVATE_CONTENT"));
            Assert.IsFalse(string.Join(" ", fixture.Logger.Entries).Contains("PRIVATE_CONTENT"));
        }

        private sealed class BrokenHealthLogger : IIntegrationLogger
        {
            public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message) =>
                throw new InvalidOperationException("PRIVATE_CONTENT");
        }
    }

    internal sealed class HealthFixture
    {
        public FakeEnvironmentDiagnostics Environment { get; } = new FakeEnvironmentDiagnostics();
        public FakeAutomationFactory Factory { get; } = new FakeAutomationFactory();
        public DoorsOperationGate Gate { get; } = new DoorsOperationGate();
        public RecordingLogger Logger { get; } = new RecordingLogger();
        public IDoorsHealthChecks Service => new DoorsHealthChecks(Environment, Factory, new ReadOnlyProbeProtocol(),
            Gate, new IntegrationErrorHandler(Logger), Logger);
        public DoorsHealthReport Live() => Service.Run(DoorsVersion.V97, Guid.NewGuid(), true);
    }
}
