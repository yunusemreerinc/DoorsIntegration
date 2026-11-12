using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, DoNotParallelize, TestCategory("DoorsBaselineIntegration")]
    public class BaselineExecutionIntegrationTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod, DataRow("9.5", "Major"), DataRow("9.5", "Minor"), DataRow("9.7", "Major"), DataRow("9.7", "Minor")]
        public async Task ReadOnlyNativeCompilationAndNextResolutionProbe(string version, string type)
        {
            RequireMode("baseline-probe", version);
            var values = Values(type); values["clientVersion"] = Required("DOORS_BASELINE_CLIENT_VERSION");
            values["mode"] = "validate"; values["approval"] = "";
            values["session"] = "0"; values["expires"] = "0";
            using (var provider = new ServiceCollection().AddDoorsIntegration().BuildServiceProvider())
            {
                var executor = provider.GetRequiredService<IDxlExecutor>(); var scripts = provider.GetRequiredService<BaselineExecutionScriptOptions>();
                // Compile all write-script syntax but the fixed invalid mode/empty approval prevents edit/create.
                var operation = executor.Start(new DxlExecutionRequest(scripts.ExecutePath, parameters: values, target: Version(version), connectionMode: DoorsConnectionMode.AttachExisting));
                using (var result = await operation.WaitAsync(CancellationToken.None))
                {
                    StringAssert.Contains(result.Text, "R\tBlocked\t0\tApprovalRequired\tNotOpened\n");
                    TestContext.WriteLine("Compile-only: no edit, create, save, discard or unlock. Script SHA256=" + Hash(scripts.ExecutePath));
                }
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "baseline-next-probe.dxl");
                var probe = executor.Start(new DxlExecutionRequest(path, parameters: values, target: Version(version), connectionMode: DoorsConnectionMode.AttachExisting));
                using (var result = await probe.WaitAsync(CancellationToken.None))
                    Assert.AreEqual("DOORS_BASELINE_PROBE\t1\t" + probe.CorrelationId.ToString("N") + "\tRESOLUTION_VERIFIED\n", result.Text,
                        "Symbolic next-handle resolution was not verified; native baseline writes must stay disabled.");
                TestContext.WriteLine("READ_ONLY_PROBE_PASSED; family=" + version + "; type=" + type + "; client=" + values["clientVersion"] + "; baseline creation NOT_RUN.");
            }
        }
        [DataTestMethod, DataRow("9.5"), DataRow("9.7")]
        public async Task NativeExecutionPreflightPreviewDoesNotWrite(string version)
        {
            RequireMode("baseline-preview", version);
            using (var provider = Services(version).BuildServiceProvider())
            {
                var plan = await provider.GetRequiredService<IBulkBaselinePreflightService>().CreatePlanAsync(Request(version));
                var item = plan.Items.Single(); Assert.AreEqual(BulkBaselineItemStatus.Ready, item.Status, item.Reason);
                Assert.AreEqual(BaselineMetadataSource.NativeDxl, item.Precondition.Facts.Source);
                TestContext.WriteLine("Module=" + item.Module.ModuleId + "; Path=" + item.Module.CanonicalFullPath + "; Current=" + item.CurrentVersion + "; Expected=" + item.ExpectedVersion);
                TestContext.WriteLine("Suffix=" + item.Probe.Suffix + "; Annotation=" + item.Probe.Annotation);
                TestContext.WriteLine("APPROVED_PRECONDITION=" + item.Precondition.Fingerprint + "; Plan=" + plan.Fingerprint + "; NO_WRITE");
            }
        }
        [DataTestMethod, DataRow("9.5"), DataRow("9.7")]
        public async Task NativeCreateRequiresExplicitExactTargetAndReviewedPrecondition(string version)
        {
            RequireMode("baseline-create", version);
            Assert.AreEqual("I_CONFIRM_IRREVERSIBLE_BASELINE_CREATE", Required("DOORS_BASELINE_WRITE_CONSENT"));
            var services = Services(version); var scripts = new BaselineExecutionScriptOptions();
            Assert.AreEqual(Hash(scripts.ExecutePath), Required("DOORS_BASELINE_EXECUTE_SHA256"));
            Assert.AreEqual(Hash(scripts.ReconcilePath), Required("DOORS_BASELINE_RECONCILE_SHA256"));
            services.AddSingleton<IBaselineExecutionCompatibility>(new ReviewedBaselineExecutionCompatibility(Version(version),
                Required("DOORS_BASELINE_DATABASE"), Required("DOORS_BASELINE_USER"), Required("DOORS_BASELINE_EXECUTE_SHA256"),
                Required("DOORS_BASELINE_RECONCILE_SHA256"), Required("DOORS_BASELINE_MANUAL_REVIEW"), Required("DOORS_BASELINE_RUNTIME_EVIDENCE"), Required("DOORS_BASELINE_CLIENT_VERSION")));
            services.AddSingleton<IBaselineExecutionConfirmation>(new EnvironmentConfirmation());
            using (var provider = services.BuildServiceProvider())
            {
                var plan = await provider.GetRequiredService<IBulkBaselinePreflightService>().CreatePlanAsync(Request(version));
                var item = plan.Items.Single(); Assert.AreEqual(BulkBaselineItemStatus.Ready, item.Status, item.Reason);
                Assert.AreEqual(Required("DOORS_BASELINE_APPROVED_PRECONDITION"), item.Precondition.Fingerprint);
                var report = await provider.GetRequiredService<IBaselineExecutionService>().ExecuteAsync(plan);
                var result = report.Items.Single(); TestContext.WriteLine("Outcome=" + result.Outcome + "; Operation=" + result.OperationId + "; Reason=" + result.Reason);
                Assert.AreEqual(BaselineWriteOutcome.Created, result.Outcome, "Inspect unknown/blocked outcomes manually; do not retry. No rollback is implemented.");
                Assert.IsFalse(result.AutomaticRetryAllowed); // Created baseline intentionally retained.
            }
        }
        private static ServiceCollection Services(string version)
        {
            // The existing default native preflight reader/proposal are deliberately unavailable.
            // Only actual validated host adapters may supply native evidence; never relabel Mock as Real.
            var services = new ServiceCollection();
            services.AddSingleton(Adapter<IBaselinePreflightStateReader>("DOORS_BASELINE_NATIVE_STATE_READER_TYPE"));
            services.AddSingleton(Adapter<IBaselineMetadataService>("DOORS_BASELINE_NATIVE_METADATA_SERVICE_TYPE"));
            services.AddDoorsIntegration(); return services;
        }
        private static T Adapter<T>(string variable) where T : class
        {
            string name = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(name)) Assert.Inconclusive("NOT_RUN: validated native adapter not supplied: " + variable + ". Default native preflight remains closed; no write.");
            var type = Type.GetType(name, true); var value = Activator.CreateInstance(type) as T;
            Assert.IsNotNull(value, variable + " must implement " + typeof(T).FullName + " and have a parameterless constructor."); return value;
        }
        private static BulkBaselinePreflightRequest Request(string version) => new BulkBaselinePreflightRequest(new BulkBaselineRequest(new[]
        {
            new BaselineModuleSelection(new BaselineModuleReference(Required("DOORS_BASELINE_REPOSITORY"), Required("DOORS_BASELINE_MODULE_ID"), Required("DOORS_BASELINE_PATH")))
        }, BaselineTypeFromEnvironment(),
            suffix: Text("DOORS_BASELINE_SUFFIX"), annotation: Text("DOORS_BASELINE_ANNOTATION")), target: Version(version), connectionMode: DoorsConnectionMode.AttachExisting);
        private static Dictionary<string, string> Values(string type) => new Dictionary<string, string>
        {
            ["path"] = Required("DOORS_BASELINE_PATH"), ["moduleId"] = Required("DOORS_BASELINE_MODULE_ID"), ["database"] = Required("DOORS_BASELINE_DATABASE"),
            ["user"] = Required("DOORS_BASELINE_USER"), ["type"] = type, ["suffix"] = Text("DOORS_BASELINE_SUFFIX"), ["annotation"] = Text("DOORS_BASELINE_ANNOTATION"),
            ["currentVersion"] = Environment.GetEnvironmentVariable("DOORS_BASELINE_CURRENT_VERSION") ?? "", ["expectedVersion"] = Required("DOORS_BASELINE_EXPECTED_VERSION"),
            ["major"] = Required("DOORS_BASELINE_EXPECTED_MAJOR"), ["minor"] = Required("DOORS_BASELINE_EXPECTED_MINOR")
        };
        private static DoorsVersion Version(string value) => value == "9.5" ? DoorsVersion.V95 : DoorsVersion.V97;
        private static BaselineType BaselineTypeFromEnvironment()
        {
            string type = Required("DOORS_BASELINE_TYPE"); Assert.IsTrue(type == "Major" || type == "Minor", "Baseline type must be Major or Minor.");
            return type == "Major" ? BaselineType.Major : BaselineType.Minor;
        }
        private static string Text(string key) { string value = Environment.GetEnvironmentVariable(key); Assert.IsNotNull(value, "Missing explicit test configuration: " + key); return value; }
        private static string Hash(string path) { using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
        private static void RequireMode(string mode, string version)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != mode || Environment.GetEnvironmentVariable("DOORS_TEST_VERSION") != version)
                Assert.Inconclusive("NOT_RUN: explicit real DOORS " + version + " environment and DOORS_TEST_MODE=" + mode + " not enabled. No native baseline call.");
            Assert.AreEqual(version, Required("DOORS_BASELINE_MANUAL_REVIEW_VERSION"), "Review the deployed release manual before even compilation/probe tests.");
        }
        private static string Required(string key) { string value = Environment.GetEnvironmentVariable(key); Assert.IsFalse(string.IsNullOrWhiteSpace(value), "Missing explicit test configuration: " + key); return value; }
        private sealed class EnvironmentConfirmation : IBaselineExecutionConfirmation
        {
            public Task<bool> ConfirmAsync(BaselineExecutionReview review, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var item = review.Plan.Items.Single();
                return Task.FromResult(item.Module.CanonicalFullPath == Required("DOORS_BASELINE_CONFIRM_TARGET") && item.Module.ModuleId == Required("DOORS_BASELINE_MODULE_ID") &&
                    item.Precondition.Fingerprint == Required("DOORS_BASELINE_APPROVED_PRECONDITION") && item.Precondition.Facts.Source == BaselineMetadataSource.NativeDxl &&
                    item.Precondition.Facts.DatabaseIdentity == Required("DOORS_BASELINE_DATABASE") && item.Precondition.Facts.CurrentUserId == Required("DOORS_BASELINE_USER"));
            }
        }
    }
}
