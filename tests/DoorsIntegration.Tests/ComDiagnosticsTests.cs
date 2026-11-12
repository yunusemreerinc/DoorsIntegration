using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ComDiagnosticsTests
    {
        [TestMethod]
        public void SuccessfulProbeUsesOneSessionAndReleasesIt()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                var logger = new RecordingLogger();
                var id = Guid.NewGuid();
                var result = new ComDoorsDiagnostics(new ReadOnlyProbeProtocol(), logger, factory).Probe(DoorsVersion.V95, id);
                Assert.AreEqual(DoorsVersion.V95, result.Target);
                Assert.AreEqual(id, result.CorrelationId);
                Assert.AreEqual(1, factory.Calls);
                Assert.IsTrue(factory.Session.Disposed);
                CollectionAssert.AreEqual(new[] { id, id }, logger.CorrelationIds);
            });
        }

        [TestMethod]
        public void ComFailureIsTranslatedAndSessionIsReleased()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                var cause = new COMException("sensitive external diagnostic", unchecked((int)0x80004005));
                factory.Session.RunFailure = cause;
                var ex = Assert.ThrowsException<IntegrationException>(() =>
                    Create(factory).Probe(DoorsVersion.V97, Guid.NewGuid()));
                Assert.AreEqual(IntegrationErrorCode.ComFailure, ex.Code);
                Assert.AreSame(cause, ex.InnerException);
                Assert.IsTrue(factory.Session.Disposed);
                Assert.IsFalse(ex.Message.Contains("sensitive"));
            });
        }

        [TestMethod]
        public void ReflectionFailurePreservesUnderlyingComCause()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                var cause = new COMException("dispatch failed");
                factory.Session.RunFailure = new TargetInvocationException(cause);
                var ex = Assert.ThrowsException<IntegrationException>(() => Create(factory).Probe(DoorsVersion.V97, Guid.NewGuid()));
                Assert.AreSame(cause, ex.InnerException);
                Assert.AreEqual(IntegrationErrorCode.ComFailure, ex.Code);
            });
        }

        [TestMethod]
        public void BadDxlResponseStillReleasesSession()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                factory.Session.ResultOverride = "stale result";
                var ex = Assert.ThrowsException<IntegrationException>(() => Create(factory).Probe(DoorsVersion.V95, Guid.NewGuid()));
                Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, ex.Code);
                Assert.IsTrue(factory.Session.Disposed);
            });
        }

        [TestMethod]
        public void CleanupFailureDoesNotReplacePrimaryFailure()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                var logger = new RecordingLogger();
                factory.Session.ResultOverride = "invalid";
                factory.Session.DisposeFailure = new COMException("cleanup failure");
                var ex = Assert.ThrowsException<IntegrationException>(() =>
                    new ComDoorsDiagnostics(new ReadOnlyProbeProtocol(), logger, factory).Probe(DoorsVersion.V97, Guid.NewGuid()));
                Assert.AreEqual(IntegrationErrorCode.DxlProtocolFailure, ex.Code);
                Assert.IsTrue(logger.Entries.Exists(entry => entry.Contains("ComCleanupFailed")));
            });
        }

        [TestMethod]
        public void CleanupFailureOnSuccessIsReported()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                factory.Session.DisposeFailure = new COMException("cleanup failure");
                var ex = Assert.ThrowsException<IntegrationException>(() => Create(factory).Probe(DoorsVersion.V97, Guid.NewGuid()));
                Assert.AreEqual(IntegrationErrorCode.ComFailure, ex.Code);
            });
        }

        [TestMethod]
        public void UnavailableClientErrorIsPreserved()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory
                {
                    Failure = new IntegrationException(IntegrationErrorCode.ClientUnavailable, "unavailable")
                };
                var ex = Assert.ThrowsException<IntegrationException>(() => Create(factory).Probe(DoorsVersion.V95, Guid.NewGuid()));
                Assert.AreSame(factory.Failure, ex);
            });
        }

        [TestMethod]
        public void MtaCallerFailsBeforeConnecting()
        {
            ApartmentTest.Run(ApartmentState.MTA, () =>
            {
                var factory = new FakeAutomationFactory();
                var ex = Assert.ThrowsException<IntegrationException>(() => Create(factory).Probe(DoorsVersion.V95, Guid.NewGuid()));
                Assert.AreEqual(IntegrationErrorCode.InvalidThread, ex.Code);
                Assert.AreEqual(0, factory.Calls);
            });
        }

        [TestMethod]
        public void UnsupportedProfileFailsBeforeConnecting()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var factory = new FakeAutomationFactory();
                Assert.ThrowsException<IntegrationException>(() => Create(factory).Probe((DoorsVersion)100, Guid.NewGuid()));
                Assert.AreEqual(0, factory.Calls);
            });
        }

        [TestMethod]
        public void SessionCannotBeUsedFromAnotherStaThread()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                using (var session = ComTestEnvironment.OpenSession())
                {
                    ApartmentTest.Run(ApartmentState.STA, () =>
                    {
                        var ex = Assert.ThrowsException<IntegrationException>(() => session.ReadResult());
                        Assert.AreEqual(IntegrationErrorCode.InvalidThread, ex.Code);
                        Assert.ThrowsException<IntegrationException>(() => session.Dispose());
                    });
                }
            });
        }

        [TestMethod]
        public void DisposedSessionRejectsCallsAndAllowsRepeatedDisposal()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var session = ComTestEnvironment.OpenSession();
                session.Dispose();
                session.Dispose();
                Assert.ThrowsException<ObjectDisposedException>(() => session.ReadResult());
            });
        }

        private static ComDoorsDiagnostics Create(FakeAutomationFactory factory) =>
            new ComDoorsDiagnostics(new ReadOnlyProbeProtocol(), new RecordingLogger(), factory);
    }
}
