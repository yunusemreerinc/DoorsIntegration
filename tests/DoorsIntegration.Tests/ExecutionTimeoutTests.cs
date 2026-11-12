using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ExecutionTimeoutTests
    {
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(30)]
        [DataRow(120)]
        [DataRow(86400)]
        public async Task ExecutorUsesTheConfiguredTimeoutWithoutMutatingTheRequest(int seconds)
        {
            using (var context = new ExecutorTestContext())
            {
                var request = new DxlExecutionRequest(context.SourcePath, timeout: TimeSpan.FromSeconds(seconds));
                var operation = context.Executor.Start(request);
                Assert.AreEqual(request.Timeout, operation.EffectiveTimeout);
                using (await operation.WaitAsync()) { }
                Assert.AreEqual(TimeSpan.FromSeconds(seconds), request.Timeout);
                Assert.AreEqual(1, context.Sessions.Session.Calls.Count(call => call == "runStr"));
            }
        }

        [TestMethod]
        public async Task ExplicitWaitTimesOutWithoutRetryOrEarlyComCleanup()
        {
            using (var context = new ExecutorTestContext())
            {
                context.Sessions.Session.BlockRun = true;
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                try
                {
                    Assert.IsTrue(context.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                    var failure = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(async () =>
                        await operation.WaitAsync(TimeSpan.FromMilliseconds(30)));
                    Assert.AreEqual(IntegrationErrorCode.DxlTimeout, failure.Code);
                    Assert.IsTrue(context.Gate.IsBusy);
                    Assert.IsFalse(context.Sessions.Session.Disposed);
                    Assert.IsFalse(operation.Completion.IsCompleted);
                }
                finally { context.Sessions.Session.ReleaseRun.Set(); using (await operation.Completion) { } }
                Assert.IsFalse(context.Gate.IsBusy);
                Assert.AreEqual(1, context.Sessions.Session.Calls.Count(call => call == "runStr"));
            }
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(172800)]
        public async Task ExplicitWaitRejectsInvalidDurations(int seconds)
        {
            using (var context = new ExecutorTestContext())
            {
                var operation = context.Executor.Start(new DxlExecutionRequest(context.SourcePath));
                await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(async () =>
                    await operation.WaitAsync(TimeSpan.FromSeconds(seconds)));
                using (await operation.Completion) { }
            }
        }

        [TestMethod]
        public async Task DiscoveryUsesOneConfiguredTotalBudgetAcrossBatches()
        {
            using (var context = new DiscoveryTestContext())
            {
                var budgets = new List<TimeSpan>();
                context.Replies["/"] = id =>
                {
                    budgets.Add(context.Transport.Executor.ActiveOperation.EffectiveTimeout);
                    Thread.Sleep(20);
                    return DiscoveryTestContext.Batch(id, "/", "DatabaseRoot", children: new[] { DiscoveryTestContext.Row("I", "/P", "Project") });
                };
                context.Replies["/P"] = id =>
                {
                    budgets.Add(context.Transport.Executor.ActiveOperation.EffectiveTimeout);
                    return DiscoveryTestContext.Batch(id, "/P", "Project");
                };
                var scope = new DiscoveryScope();
                var result = await context.Reader.DiscoverAsync(DiscoveryKind.Hierarchy, scope);
                Assert.AreEqual(2, result.VisitedFolders);
                Assert.IsTrue(budgets.All(budget => budget > TimeSpan.Zero && budget <= scope.Timeout));
                Assert.IsTrue(budgets[1] < budgets[0]);
                Assert.AreEqual(TimeSpan.FromSeconds(30), scope.Timeout);
            }
        }

        [TestMethod]
        public async Task FileReadersRetainTheirConfiguredTwoMinuteTimeouts()
        {
            using (var context = new RequirementTestContext())
            {
                var original = context.Transport.Sessions.Session.OnRun;
                context.Transport.Sessions.Session.OnRun = session =>
                {
                    Assert.AreEqual(context.Request.Timeout, context.Transport.Executor.ActiveOperation.EffectiveTimeout);
                    original(session);
                };
                using (await context.Reader.ReadAsync(context.Request)) { }
                Assert.AreEqual(TimeSpan.FromMinutes(2), context.Request.Timeout);
            }
            using (var context = new LinkTestContext())
            {
                var original = context.Transport.Sessions.Session.OnRun;
                context.Transport.Sessions.Session.OnRun = session =>
                {
                    Assert.AreEqual(context.Request.Timeout, context.Transport.Executor.ActiveOperation.EffectiveTimeout);
                    original(session);
                };
                using (await context.Reader.ReadAsync(context.Request)) { }
                Assert.AreEqual(TimeSpan.FromMinutes(2), context.Request.Timeout);
            }
        }

        [TestMethod]
        public async Task CreationRetainsItsConfiguredBudgetAndRequiresOneApprovedCommit()
        {
            using (var context = new ModuleCreationTestContext())
            {
                context.Confirmation.Approved = true;
                var original = context.Transport.Sessions.Session.OnRun;
                var budgets = new List<TimeSpan>();
                context.Transport.Sessions.Session.OnRun = session =>
                {
                    budgets.Add(context.Transport.Executor.ActiveOperation.EffectiveTimeout);
                    original(session);
                };
                var preview = await context.Service.PreviewFormalModuleCreationAsync(context.Request);
                Assert.IsTrue(preview.ExpiresAtUtc < DateTime.UtcNow.AddMinutes(6));
                var result = await context.Service.CreateFormalModuleAsync(preview);
                Assert.AreEqual(ModuleCreationOutcome.Created, result.Outcome);
                CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) }, budgets);
                Assert.AreEqual(1, context.Modes.Count(mode => mode == "commit"));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        
        public async Task ObsoleteStartupEnvironmentCannotChangeConsoleTimeouts(bool desktop)
        {
            var previous = Environment.GetEnvironmentVariable("DOORS_DOUBLE_EXECUTION_TIME");
            try
            {
                Environment.SetEnvironmentVariable("DOORS_DOUBLE_EXECUTION_TIME", "1");
                using (var context = new ExecutorTestContext())
                {
                    var services = new ServiceCollection();
                    services.AddSingleton<IDxlSessionFactory>(context.Sessions);
                    services.AddSingleton<IDoorsOperationGate>(context.Gate);
                    DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
                    using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                    {
                        var executor = provider.GetRequiredService<IDxlExecutor>();
                        var operation = executor.Start(new DxlExecutionRequest(context.SourcePath));
                        Assert.AreEqual(TimeSpan.FromSeconds(30), operation.EffectiveTimeout);
                        using (await operation.Completion) { }
                    }
                }
            }
            finally { Environment.SetEnvironmentVariable("DOORS_DOUBLE_EXECUTION_TIME", previous); }
        }
    }
}
