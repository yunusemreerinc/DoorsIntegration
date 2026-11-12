using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ExportServiceTests
    {
        [DataTestMethod]
        [DataRow(ExportFormat.Json)]
        [DataRow(ExportFormat.Csv)]
        [DataRow(ExportFormat.Xlsx)]
        public async Task EmptySourceProducesCompleteReportAndEverySchema(ExportFormat format)
        {
            using (var context = new ExportTestContext())
            {
                var result = await context.Service.ExportAsync(context.Request(format), new EnumerableExportSource(Enumerable.Empty<ExportRecord>()));
                Assert.AreEqual(ExportStatus.Completed, result.Status); Assert.IsTrue(result.HasPayload);
                Assert.AreEqual(0L, result.RecordsAccepted); Assert.AreEqual(8, result.RowCounts.Count);
                var report = ExportTestContext.Json<ExportTestReport>(result.ReportPath);
                Assert.IsTrue(report.HasPayload); Assert.AreEqual("Completed", report.Status);
                StringAssert.EndsWith(report.Started, "Z"); StringAssert.EndsWith(report.Finished, "Z");
                Assert.IsFalse(Directory.Exists(Path.Combine(result.DirectoryPath, ".spool")));
                if (format == ExportFormat.Csv) Assert.AreEqual(8, Directory.GetFiles(result.DirectoryPath, "*.csv").Length);
                else Assert.IsTrue(File.Exists(Path.Combine(result.DirectoryPath, format == ExportFormat.Json ? "data.json" : "data.xlsx")));
            }
        }
        [TestMethod]
        public async Task JsonPreservesUnicodeNullsBooleansInvariantNumbersAndUtcDates()
        {
            using (var context = new ExportTestContext())
            {
                var prior = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                try
                {
                    var records = new[] {
                        ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/Türkçe/😀", "name", "İçerik\r\n\t\0\"\\\u2028", "collectedAtUtc", new DateTimeOffset(2026, 10, 16, 12, 34, 56, TimeSpan.FromHours(3))),
                        ExportTestContext.Row(ExportTableKind.Requirements, "modulePath", "/P/M", "absoluteNumber", 42, "isDeleted", true, "text", "body"),
                        ExportTestContext.Row(ExportTableKind.Attributes, "modulePath", "/P/M", "name", "Cost", "typedValue", 1234.56789m, "typedDateUtc", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                        ExportTestContext.Row(ExportTableKind.Links, "sourceAbsoluteNumber", null, "targetAbsoluteNumber", 42, "resolution", "SourceReferenceOnly") };
                    var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(records));
                    var data = ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json"));
                    Assert.AreEqual(1, data.Schema); Assert.AreEqual("İçerik\r\n\t\0\"\\\u2028", data.Tables.Projects.Single().Name);
                    Assert.AreEqual("2026-10-16T09:34:56.0000000Z", data.Tables.Projects.Single().Collected);
                    Assert.AreEqual(1234.56789m, data.Tables.Attributes.Single().Number);
                    Assert.AreEqual(true, data.Tables.Requirements.Single().Deleted); Assert.AreEqual(42L, data.Tables.Requirements.Single().Absolute);
                    Assert.IsNull(data.Tables.Links.Single().Source);
                }
                finally { CultureInfo.CurrentCulture = prior; }
            }
        }
        [DataTestMethod]
        [DataRow("missing")]
        [DataRow("wrongType")]
        [DataRow("unknownColumn")]
        [DataRow("unicode")]
        [DataRow("dateUnspecified")]
        [DataRow("dateLocal")]
        [DataRow("nan")]
        [DataRow("reservedTable")]
        [DataRow("null")]
        public async Task InvalidRowIsReportedWithoutRawValuesAndNextRowCanContinue(string kind)
        {
            using (var context = new ExportTestContext())
            {
                var valid = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P");
                ExportRecord invalid = null;
                switch (kind)
                {
                    case "missing": invalid = ExportTestContext.Row(ExportTableKind.Projects, "name", "SECRET"); break;
                    case "wrongType": invalid = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", 1); break;
                    case "unknownColumn": invalid = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P", "SECRET", "SECRET"); break;
                    case "unicode": invalid = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "SECRET\ud800"); break;
                    case "dateUnspecified": invalid = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P", "collectedAtUtc", DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified)); break;
                    case "dateLocal": invalid = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P", "collectedAtUtc", DateTime.Now); break;
                    case "nan": invalid = ExportTestContext.Row(ExportTableKind.Attributes, "modulePath", "/M", "name", "SECRET", "typedValue", double.NaN); break;
                    case "reservedTable": invalid = ExportTestContext.Row(ExportTableKind.Errors, "message", "SECRET"); break;
                }
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(new[] { invalid, valid }));
                Assert.AreEqual(ExportStatus.CompletedWithIssues, result.Status); Assert.AreEqual(1L, result.RecordsSkipped); Assert.AreEqual(1L, result.RecordsAccepted);
                string report = File.ReadAllText(result.ReportPath); Assert.IsFalse(report.Contains("SECRET"));
                Assert.IsFalse(string.Join(" ", context.Logger.Entries).Contains("SECRET"));
                var issue = ExportTestContext.Json<ExportTestReport>(result.ReportPath).Issues.Single(); Assert.AreEqual(1L, issue.Record);
                Assert.AreEqual(1, ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json")).Tables.Projects.Length);
            }
        }
        [TestMethod]
        public async Task StopPolicyPublishesReportOnlyAndRemovesStagedData()
        {
            using (var context = new ExportTestContext())
            {
                var request = new ExportRequest(context.Destination, ExportFormat.Json, ExportErrorPolicy.StopOnInvalidRow);
                var result = await context.Service.ExportAsync(request, new EnumerableExportSource(new[] { ExportTestContext.Row(ExportTableKind.Projects, "name", "bad") }));
                Assert.AreEqual(ExportStatus.Failed, result.Status); Assert.IsFalse(result.HasPayload); context.AssertOnlyReport();
            }
        }
        [TestMethod]
        public async Task FieldLimitReportsExplicitReasonWithoutTruncatingValue()
        {
            using (var context = new ExportTestContext())
            {
                var request = new ExportRequest(context.Destination, ExportFormat.Json, maxFieldCharacters: 3);
                var result = await context.Service.ExportAsync(request, new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P", "name", "large") }));
                Assert.AreEqual(ExportStatus.CompletedWithIssues, result.Status); Assert.AreEqual(0L, result.RecordsAccepted);
                var issue = ExportTestContext.Json<ExportTestReport>(result.ReportPath).Issues.Single();
                Assert.AreEqual("FieldTooLarge", issue.Code); Assert.AreEqual("name", issue.Column);
            }
        }
        [TestMethod]
        public async Task IncompleteSourceScopeSurvivesAsWarningAndTypedScopeData()
        {
            using (var context = new ExportTestContext())
            {
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Scopes, "scopeId", "scope", "isComplete", false, "readStatus", "AccessDenied", "fieldProblemCount", 4) }));
                Assert.AreEqual(ExportStatus.CompletedWithIssues, result.Status);
                var data = ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json"));
                Assert.IsFalse(data.Tables.Scopes.Single().Complete); Assert.AreEqual(4L, data.Tables.Scopes.Single().FieldProblems);
                Assert.AreEqual("IncompleteScope", ExportTestContext.Json<ExportTestReport>(result.ReportPath).Issues.Single().Code);
            }
        }
        [TestMethod]
        public async Task LazySourceIsEnumeratedOnceAndDisposedOnFailure()
        {
            using (var context = new ExportTestContext())
            {
                var source = new OnePassRows(100, fail: true);
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(source));
                Assert.AreEqual(ExportStatus.Failed, result.Status); Assert.AreEqual(1, source.Enumerations); Assert.IsTrue(source.Disposed);
                Assert.AreEqual(100L, result.RecordsAccepted); context.AssertOnlyReport();
                Assert.IsFalse(File.ReadAllText(result.ReportPath).Contains("SECRET_SOURCE"));
                Assert.AreEqual("SourceFailure", ExportTestContext.Json<ExportTestReport>(result.ReportPath).Issues.Last().Code);
            }
        }
        [TestMethod]
        public async Task PreCancellationStillPublishesCancellationReportWithoutReadingSource()
        {
            using (var context = new ExportTestContext())
            {
                var source = new OnePassRows(10);
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(source), cancellationToken: new CancellationToken(true));
                Assert.AreEqual(ExportStatus.Cancelled, result.Status); Assert.AreEqual(0, source.Enumerations); context.AssertOnlyReport();
            }
        }
        [TestMethod]
        public async Task CancellationDuringWritingRemovesPartialPayloadAndFinishesReport()
        {
            using (var context = new ExportTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var progress = new InlineProgress(value => { if (value.Phase == ExportPhase.Writing && value.RowsWritten >= 3) cancellation.Cancel(); });
                var source = new OnePassRows(10);
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(source), progress, cancellation.Token);
                Assert.AreEqual(ExportStatus.Cancelled, result.Status); Assert.IsTrue(source.Disposed); context.AssertOnlyReport();
            }
        }
        [TestMethod]
        public async Task ProgressIsMonotonicAndBrokenObserversOrLogSinksDoNotBreakExport()
        {
            using (var context = new ExportTestContext())
            {
                var values = new List<ExportProgress>();
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(new OnePassRows(50), 50), new InlineProgress(values.Add));
                Assert.AreEqual(ExportStatus.Completed, result.Status); Assert.AreEqual(ExportPhase.Completed, values.Last().Phase);
                Assert.IsTrue(values.All(item => item.CorrelationId == result.CorrelationId));
                for (int i = 1; i < values.Count; i++) { Assert.IsTrue(values[i].RecordsRead >= values[i - 1].RecordsRead); Assert.IsTrue(values[i].RowsWritten >= values[i - 1].RowsWritten); }
                Assert.AreEqual(50L, values.Last().EstimatedRecordCount);
            }
            using (var context = new ExportTestContext())
            {
                context.Logger.ThrowOnWrite = true;
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(new OnePassRows(2)), new InlineProgress(value => throw new InvalidOperationException("SECRET")));
                Assert.AreEqual(ExportStatus.Completed, result.Status);
            }
        }
        [DataTestMethod]
        [DataRow("records")]
        [DataRow("spool")]
        [DataRow("output")]
        [DataRow("issues")]
        public async Task ResourceLimitsProduceReportOnlyAndTypedLimitIssue(string limit)
        {
            using (var context = new ExportTestContext())
            {
                var request = new ExportRequest(context.Destination, ExportFormat.Json, maxRecords: limit == "records" ? 1 : 1000,
                    maxSpoolBytes: limit == "spool" ? 1024 : 1048576, maxOutputBytes: limit == "output" ? 1024 : 1048576, maxIssues: 1);
                var rows = limit == "issues" ? new[] { ExportTestContext.Row(ExportTableKind.Projects, "name", "bad"), ExportTestContext.Row(ExportTableKind.Projects, "name", "bad") } :
                    Enumerable.Range(1, 20).Select(i => ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P/" + i, "name", new string('x', 200))).ToArray();
                var result = await context.Service.ExportAsync(request, new EnumerableExportSource(rows));
                Assert.AreEqual(ExportStatus.Failed, result.Status); context.AssertOnlyReport();
                var issue = ExportTestContext.Json<ExportTestReport>(result.ReportPath).Issues.Last();
                Assert.AreEqual("LimitReached", issue.Code); Assert.AreEqual(36, issue.ErrorCode);
            }
        }
        [TestMethod]
        public async Task ConsumedRecordsAreCollectibleWhileLargeSourceIsStillBeingRead()
        {
            using (var context = new ExportTestContext())
            {
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(CollectibleRows()));
                Assert.AreEqual(ExportStatus.Completed, result.Status);
                Assert.AreEqual(5000L, result.RecordsAccepted);
            }
        }
        private static IEnumerable<ExportRecord> CollectibleRows()
        {
            var references = new List<WeakReference>();
            for (int i = 0; i < 5000; i++)
            {
                if (i > 0 && i % 500 == 0)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Assert.IsTrue(references.Count(reference => reference.IsAlive) < 16, "Consumed source records were retained during enumeration.");
                }
                var record = ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P/" + i, "name", new string('x', 1024));
                references.Add(new WeakReference(record)); yield return record;
            }
        }
        [TestMethod]
        public async Task ExistingDestinationIsUntouchedAndNeverOverwritten()
        {
            using (var context = new ExportTestContext())
            {
                Directory.CreateDirectory(context.Destination); File.WriteAllText(Path.Combine(context.Destination, "keep.txt"), "original");
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Service.ExportAsync(context.Request(), new EnumerableExportSource(new OnePassRows(1))));
                Assert.AreEqual(IntegrationErrorCode.ExportFailed, error.Code); Assert.AreEqual("original", File.ReadAllText(Path.Combine(context.Destination, "keep.txt")));
                Assert.AreEqual(1, Directory.GetFiles(context.Destination).Length);
            }
        }
        [TestMethod]
        public void HostResolvesExportServiceWithoutComAndPreservesOverride()
        {
            var services = new ServiceCollection().AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider()) Assert.IsInstanceOfType(provider.GetRequiredService<IExportService>(), typeof(ExportService));
            services.AddDoorsIntegration();
            Assert.AreEqual(Enum.GetValues(typeof(ExportFormat)).Length, services.Count(descriptor => descriptor.ServiceType == typeof(IExportFormatWriter)));
            Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(IExportFormatWriter) && descriptor.ImplementationType == typeof(HtmlExportWriter)));
        }
        private sealed class InlineProgress : IProgress<ExportProgress>
        {
            private readonly Action<ExportProgress> action;
            public InlineProgress(Action<ExportProgress> action) { this.action = action; }
            public void Report(ExportProgress value) => action(value);
        }
        internal sealed class OnePassRows : IEnumerable<ExportRecord>
        {
            private readonly int count; private readonly bool fail;
            public OnePassRows(int count, bool fail = false) { this.count = count; this.fail = fail; }
            public int Enumerations { get; private set; }
            public bool Disposed { get; private set; }
            public IEnumerator<ExportRecord> GetEnumerator()
            {
                if (++Enumerations != 1) throw new InvalidOperationException("Source enumerated twice");
                return Read().GetEnumerator();
            }
            private IEnumerable<ExportRecord> Read()
            {
                try
                {
                    for (int i = 0; i < count; i++) yield return ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P/" + i, "name", "İçerik😀");
                    if (fail) throw new IntegrationException(IntegrationErrorCode.RequirementProtocolFailure, "SECRET_SOURCE");
                }
                finally { Disposed = true; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
