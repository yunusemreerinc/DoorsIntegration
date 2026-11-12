using System;
using System.IO;
using System.Runtime.InteropServices;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ErrorAndCompositionTests
    {
        [TestMethod]
        public void UnexpectedErrorDoesNotExposeExternalMessage()
        {
            var logger = new RecordingLogger();
            var failure = new IntegrationErrorHandler(logger).Handle(new InvalidOperationException("SECRET_PASSWORD"), Guid.NewGuid());
            Assert.AreEqual(99, failure.ExitCode);
            Assert.IsFalse(failure.Message.Contains("SECRET_PASSWORD"));
            Assert.IsFalse(string.Join(" ", logger.Entries).Contains("SECRET_PASSWORD"));
        }

        [TestMethod]
        public void WrappedComErrorLogsUnderlyingHResultWithoutItsMessage()
        {
            var logger = new RecordingLogger();
            var cause = new COMException("PRIVATE_CONTENT", unchecked((int)0x80040154));
            var failure = new IntegrationErrorHandler(logger).Handle(
                new IntegrationException(IntegrationErrorCode.ClientUnavailable, "Unavailable", cause), Guid.NewGuid());
            Assert.AreEqual(10, failure.ExitCode);
            StringAssert.Contains(logger.Entries[0], "80040154");
            Assert.IsFalse(logger.Entries[0].Contains("PRIVATE_CONTENT"));
        }

        [TestMethod]
        public void DefaultCompositionResolvesAndDryRunsWithoutComActivation()
        {
            var services = new ServiceCollection().AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true, ValidateScopes = true
            }))
            {
                Assert.AreEqual(0, provider.GetRequiredService<ConsoleApplication>()
                    .Run(new string[0], new StringWriter(), new StringWriter()));
            }
        }

        [TestMethod]
        public void HostRegistrationsBeforeDefaultsArePreserved()
        {
            var services = new ServiceCollection();
            var fake = new FakeDiagnostics();
            services.AddSingleton<IDoorsDiagnostics>(fake);
            services.AddDoorsIntegration();
            services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                Assert.AreSame(fake, provider.GetRequiredService<IDoorsDiagnostics>());
            }
        }

        [TestMethod]
        public void HostCanReplaceDefaultRegistration()
        {
            var services = new ServiceCollection().AddDoorsIntegration();
            var fake = new FakeDiagnostics();
            services.Replace(ServiceDescriptor.Singleton<IDoorsDiagnostics>(fake));
            using (var provider = services.BuildServiceProvider())
            {
                Assert.AreSame(fake, provider.GetRequiredService<IDoorsDiagnostics>());
            }
        }
    }
}
