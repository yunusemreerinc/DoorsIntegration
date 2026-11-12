using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DoorsComSessionTests
    {
        [TestMethod]
        public void ActivationResolvesDocumentedProgIdAndDoesNotAttach()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime();
                var registry = new FakeRegistrationInspector();
                using (var session = new DoorsComSessionFactory(registry, runtime).Connect(DoorsConnectionMode.Activate))
                {
                    Assert.AreEqual("DOORS.Application", runtime.LastProgId);
                    Assert.AreEqual(1, runtime.ResolveCalls);
                    Assert.AreEqual(1, runtime.ActivateCalls);
                    Assert.AreEqual(0, runtime.AttachCalls);
                    Assert.AreEqual(Environment.Is64BitProcess ? ComArchitecture.X64 : ComArchitecture.X86, registry.Reads[0]);
                }
                Assert.AreEqual(1, runtime.ReleaseCalls);
                Assert.AreSame(runtime.AutomationObject, runtime.ReleasedObject);
            });
        }

        [TestMethod]
        public void AttachOnlyUsesRotWithoutResolutionOrActivation()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime();
                using (var session = ComTestEnvironment.OpenSession(runtime, mode: DoorsConnectionMode.AttachExisting))
                {
                    Assert.AreEqual(DoorsConnectionMode.AttachExisting, session.Mode);
                    Assert.AreEqual("DOORS.Application", runtime.LastProgId);
                    Assert.AreEqual(1, runtime.AttachCalls);
                    Assert.AreEqual(0, runtime.ResolveCalls);
                    Assert.AreEqual(0, runtime.ActivateCalls);
                }
            });
        }

        [TestMethod]
        public void MissingRotObjectDoesNotFallBackToActivation()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var cause = new COMException("private", unchecked((int)0x800401E3));
                var runtime = new FakeComRuntime { AttachFailure = cause };
                var failure = Assert.ThrowsException<IntegrationException>(() =>
                    ComTestEnvironment.OpenSession(runtime, mode: DoorsConnectionMode.AttachExisting));
                Assert.AreEqual(IntegrationErrorCode.ActiveSessionUnavailable, failure.Code);
                Assert.AreSame(cause, failure.InnerException);
                Assert.AreEqual(0, runtime.ActivateCalls);
                Assert.AreEqual(0, runtime.ReleaseCalls);
                Assert.IsFalse(failure.Message.Contains("private"));
            });
        }

        [DataTestMethod]
        [DataRow(ComRegistrationState.NotRegistered, IntegrationErrorCode.ComNotRegistered)]
        [DataRow(ComRegistrationState.Invalid, IntegrationErrorCode.ComNotRegistered)]
        [DataRow(ComRegistrationState.Unreadable, IntegrationErrorCode.ComRegistrationReadFailed)]
        public void RegistryFailureStopsAllComCalls(ComRegistrationState state, IntegrationErrorCode expected)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime();
                var failure = Assert.ThrowsException<IntegrationException>(() => ComTestEnvironment.OpenSession(runtime, state));
                Assert.AreEqual(expected, failure.Code);
                Assert.AreEqual(0, runtime.ResolveCalls + runtime.ActivateCalls + runtime.AttachCalls);
            });
        }

        [TestMethod]
        public void MtaCallerCannotReachRegistryOrCom()
        {
            ApartmentTest.Run(ApartmentState.MTA, () =>
            {
                var registry = new FakeRegistrationInspector();
                var runtime = new FakeComRuntime();
                var failure = Assert.ThrowsException<IntegrationException>(() =>
                    new DoorsComSession(DoorsConnectionMode.Activate, registry, runtime));
                Assert.AreEqual(IntegrationErrorCode.InvalidThread, failure.Code);
                Assert.AreEqual(0, registry.Reads.Count);
                Assert.AreEqual(0, runtime.ActivateCalls);
            });
        }

        [TestMethod]
        public void DispatchUsesVerifiedRunStrAndResultNamesOnly()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime();
                using (var session = ComTestEnvironment.OpenSession(runtime))
                {
                    session.RunString("oleSetResult(\"OK\")");
                    Assert.AreEqual("runStr", runtime.LastMember);
                    Assert.AreEqual(BindingFlags.InvokeMethod, runtime.LastOperation);
                    Assert.AreEqual("oleSetResult(\"OK\")", runtime.LastArguments[0]);
                    Assert.AreEqual("OK", session.ReadResult());
                    Assert.AreEqual("Result", runtime.LastMember);
                    Assert.AreEqual(BindingFlags.GetProperty, runtime.LastOperation);
                }
            });
        }

        [DataTestMethod]
        [DataRow(unchecked((int)0x80040154), IntegrationErrorCode.ComNotRegistered)]
        [DataRow(unchecked((int)0x80070005), IntegrationErrorCode.ComAccessDenied)]
        [DataRow(unchecked((int)0x80010001), IntegrationErrorCode.ComServerBusy)]
        [DataRow(unchecked((int)0x8001010A), IntegrationErrorCode.ComServerBusy)]
        [DataRow(unchecked((int)0x80010108), IntegrationErrorCode.ComServerDisconnected)]
        [DataRow(unchecked((int)0x800706BA), IntegrationErrorCode.ComServerDisconnected)]
        [DataRow(unchecked((int)0x80020003), IntegrationErrorCode.ComApiUnavailable)]
        [DataRow(unchecked((int)0x80020006), IntegrationErrorCode.ComApiUnavailable)]
        public void ComHResultsAreTranslatedAndOriginalCausePreserved(int hresult, IntegrationErrorCode code)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var cause = new COMException("PRIVATE COM DETAIL", hresult);
                var runtime = new FakeComRuntime { InvokeFailure = new TargetInvocationException(cause) };
                using (var session = ComTestEnvironment.OpenSession(runtime))
                {
                    var failure = Assert.ThrowsException<IntegrationException>(() => session.ReadResult());
                    Assert.AreEqual(code, failure.Code);
                    Assert.AreSame(cause, failure.InnerException);
                    Assert.IsFalse(failure.Message.Contains("PRIVATE COM DETAIL"));
                }
            });
        }

        [TestMethod]
        public void ActivationFailureDoesNotAttachOrLeakReference()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime { ActivateFailure = new COMException("failed", unchecked((int)0x80080005)) };
                Assert.ThrowsException<IntegrationException>(() => ComTestEnvironment.OpenSession(runtime));
                Assert.AreEqual(0, runtime.AttachCalls + runtime.ReleaseCalls);
            });
        }

        [TestMethod]
        public void NullResolvedTypeIsNotActivated()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime { ResolvedType = null };
                var failure = Assert.ThrowsException<IntegrationException>(() => ComTestEnvironment.OpenSession(runtime));
                Assert.AreEqual(IntegrationErrorCode.ComNotRegistered, failure.Code);
                Assert.AreEqual(0, runtime.ActivateCalls);
            });
        }

        [TestMethod]
        public void WrongStaCannotUseOrReleaseOwnedReference()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime();
                using (var session = ComTestEnvironment.OpenSession(runtime))
                {
                    ApartmentTest.Run(ApartmentState.STA, () =>
                    {
                        Assert.ThrowsException<IntegrationException>(() => session.Dispose());
                        Assert.ThrowsException<IntegrationException>(() => session.ReadResult());
                        Assert.AreEqual(0, runtime.ReleaseCalls);
                    });
                }
                Assert.AreEqual(1, runtime.ReleaseCalls);
            });
        }

        [TestMethod]
        public void DisposeReleasesExactlyOnceEvenAfterReleaseFailure()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime { ReleaseFailure = new COMException("release failed") };
                var session = ComTestEnvironment.OpenSession(runtime);
                Assert.ThrowsException<IntegrationException>(() => session.Dispose());
                session.Dispose();
                Assert.AreEqual(1, runtime.ReleaseCalls);
                Assert.ThrowsException<ObjectDisposedException>(() => session.ReadResult());
            });
        }

        [TestMethod]
        public void InvalidReturnTypeAndEmptyScriptAreRejected()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                using (var session = ComTestEnvironment.OpenSession(new FakeComRuntime { Result = 42 }))
                {
                    Assert.ThrowsException<ArgumentException>(() => session.RunString(" "));
                    var failure = Assert.ThrowsException<IntegrationException>(() => session.ReadResult());
                    Assert.AreEqual(IntegrationErrorCode.ComApiUnavailable, failure.Code);
                }
            });
        }

        [TestMethod]
        public void InvalidConnectionModeDoesNotTouchRegistryOrCom()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var registry = new FakeRegistrationInspector();
                var runtime = new FakeComRuntime();
                var failure = Assert.ThrowsException<IntegrationException>(() =>
                    new DoorsComSession((DoorsConnectionMode)99, registry, runtime));
                Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration, failure.Code);
                Assert.AreEqual(0, registry.Reads.Count + runtime.ActivateCalls + runtime.AttachCalls);
            });
        }

        [TestMethod]
        public void MissingDispatchMemberIsReportedWithoutInventingAlternativeApiNames()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime { InvokeFailure = new MissingMethodException("PRIVATE") };
                using (var session = ComTestEnvironment.OpenSession(runtime))
                {
                    var failure = Assert.ThrowsException<IntegrationException>(() => session.ReadResult());
                    Assert.AreEqual(IntegrationErrorCode.ComApiUnavailable, failure.Code);
                    Assert.AreEqual("Result", runtime.LastMember);
                    Assert.IsFalse(failure.Message.Contains("PRIVATE"));
                }
            });
        }

        [TestMethod]
        public void ProductionLateBinderInvokesDocumentedMemberShapes()
        {
            var runtime = new WindowsComRuntime();
            var standIn = new AutomationStandIn();
            runtime.Invoke(standIn, "runStr", BindingFlags.InvokeMethod, new object[] { "diagnostic-placeholder" });
            Assert.AreEqual("diagnostic-placeholder", standIn.LastScript);
            Assert.AreEqual("stand-in-result", runtime.Invoke(standIn, "Result", BindingFlags.GetProperty, null));
        }

        public sealed class AutomationStandIn
        {
            public string LastScript { get; private set; }
            public string Result => "stand-in-result";
            public void runStr(string script) { LastScript = script; }
        }

        [TestMethod]
        public void RunFileAndResultSetterUseVerifiedLateBoundMembers()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var runtime = new FakeComRuntime();
                using (var session = ComTestEnvironment.OpenSession(runtime))
                {
                    session.RunFile(@"C:\scripts\read-only.dxl");
                    Assert.AreEqual("runFile", runtime.LastMember);
                    Assert.AreEqual(BindingFlags.InvokeMethod, runtime.LastOperation);
                    Assert.AreEqual(@"C:\scripts\read-only.dxl", runtime.LastArguments[0]);
                    session.SetResult("pending");
                    Assert.AreEqual("Result", runtime.LastMember);
                    Assert.AreEqual(BindingFlags.SetProperty, runtime.LastOperation);
                    Assert.ThrowsException<ArgumentException>(() => session.RunFile("relative.dxl"));
                    Assert.ThrowsException<ArgumentException>(() => session.RunFile(@"C:relative.dxl"));
                    Assert.ThrowsException<ArgumentException>(() => session.RunFile(@"\root-relative.dxl"));
                }
            });
        }
    }
}
