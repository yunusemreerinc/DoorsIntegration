using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConsoleApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class BaselineMockAdapter : IRequirementReader
    {
        internal readonly List<RequirementReadRequest> Calls = new List<RequirementReadRequest>();
        internal readonly List<BaselineMockSnapshot> Snapshots = new List<BaselineMockSnapshot>();
        internal Func<RequirementReadRequest, int, BaselineMockSnapshot> OnRead;
        public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(request);
            var snapshot = OnRead(request, Calls.Count); Snapshots.Add(snapshot); return Task.FromResult<IRequirementSnapshot>(snapshot);
        }
    }
    internal sealed class BaselineMockSnapshot : IRequirementSnapshot
    {
        internal bool Disposed;
        internal Func<IEnumerable<RequirementRecord>> Rows;
        public BaselineMockSnapshot(RequirementReadRequest request, RequirementRecord[] rows, string moduleId = "qualified-module-1",
            bool truncated = false, bool enumerationFailed = false, int denied = 0, int unavailable = 0,
            RequirementModuleStatus status = RequirementModuleStatus.Available, RequirementCleanupStatus cleanup = RequirementCleanupStatus.Closed, int? expectedCount = null)
        {
            Rows = () => rows;
            Summary = new RequirementReadSummary(Guid.NewGuid(), request, status, RequirementModuleOwnership.OwnedReadOnly, cleanup,
                expectedCount ?? rows.Length, (expectedCount ?? rows.Length) + denied + unavailable, denied, 0, unavailable, 0, truncated, enumerationFailed, moduleId);
        }
        public RequirementReadSummary Summary { get; }
        public IEnumerable<RequirementRecord> ReadObjects() => Rows();
        public Stream OpenRawStream() => throw new NotSupportedException();
        public void Dispose() { Disposed = true; }
    }
    internal sealed class BaselineTestLog : IIntegrationLogger
    {
        internal readonly List<string> Messages = new List<string>();
        public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message) { Messages.Add(eventName + ":" + message); }
    }
    internal static class BaselineFixture
    {
        internal static BaselineComparisonRequest Request(IEnumerable<string> attributes = null, BaselineTextNormalization normalization = BaselineTextNormalization.UnicodeNfcAndLineEndings,
            int maxObjects = 100000, long maxSpoolBytes = 1073741824) => new BaselineComparisonRequest("/P/M", new RequirementBaseline(1, 0, "a"),
                new RequirementBaseline(2, 0, "b"), attributes, normalization, maxObjects, maxSpoolBytes);
        internal static RequirementValue Value(string name = "Object Text", string plain = "text", string rich = "{\\rtf1 opaque}",
            RequirementValueStatus status = RequirementValueStatus.Readable, RequirementValueStatus richStatus = RequirementValueStatus.Readable, string type = "Text") =>
            new RequirementValue(name, type, status, plain, richStatus, rich);
        internal static RequirementRecord Object(int id = 1, string plain = "text", string rich = "{\\rtf1 opaque}", string number = "1", int order = 1,
            int? parent = null, bool deleted = false, RequirementValue value = null, IEnumerable<RequirementValue> custom = null,
            string identifier = null, RequirementParentStatus? parentStatus = null) => new RequirementRecord(order, id, identifier ?? "REQ-" + id, number,
                RequirementValueStatus.Readable, parent.HasValue ? 2 : 1, parent, parentStatus ?? (parent.HasValue ? RequirementParentStatus.Resolved : RequirementParentStatus.Root), deleted,
                new[] { deleted ? Value("Object Heading", "", "", RequirementValueStatus.NotRequested, RequirementValueStatus.NotRequested) : Value("Object Heading", "heading"),
                    value ?? (deleted ? Value("Object Text", "", "", RequirementValueStatus.NotRequested, RequirementValueStatus.NotRequested) : Value(plain: plain, rich: rich)) }
                    .Concat(custom ?? Array.Empty<RequirementValue>()));
        internal static BaselineMockAdapter Adapter(RequirementRecord[] before, RequirementRecord[] after) => new BaselineMockAdapter
            { OnRead = (request, call) => new BaselineMockSnapshot(request, call == 1 ? before : after) };
        internal static BaselineComparisonService Service(IRequirementReader adapter) => new BaselineComparisonService(adapter, new BaselineTestLog());
    }

    [TestClass]
    public class BaselineComparisonTests
    {
        [TestMethod]
        public async Task ClassifiesAllFourStatesAndAttributeDeltasUsingPersistentIdentities()
        {
            var adapter = BaselineFixture.Adapter(new[] { BaselineFixture.Object(1), BaselineFixture.Object(2), BaselineFixture.Object(3) },
                new[] { BaselineFixture.Object(1), BaselineFixture.Object(2, "updated"), BaselineFixture.Object(4) });
            using (var result = await BaselineFixture.Service(adapter).CompareAsync(BaselineFixture.Request()))
            {
                var rows = result.ReadChanges().ToDictionary(value => value.Identity.AbsoluteNumber);
                Assert.AreEqual(BaselineChangeStatus.Unchanged, rows[1].Status);
                Assert.AreEqual(BaselineChangeStatus.Modified, rows[2].Status);
                Assert.AreEqual(BaselineChangeStatus.Deleted, rows[3].Status);
                Assert.AreEqual(BaselineChangeStatus.Added, rows[4].Status);
                Assert.IsTrue(rows[2].Attributes.Single(value => value.Name == "Object Text").PlainTextChanged);
                Assert.AreEqual("text", rows[2].Attributes.Single(value => value.Name == "Object Text").Before.PlainText);
                Assert.IsTrue(result.Summary.IsConclusive);
                foreach (var status in new[] { BaselineChangeStatus.Added, BaselineChangeStatus.Deleted, BaselineChangeStatus.Modified, BaselineChangeStatus.Unchanged })
                    Assert.AreEqual(1, result.Summary.Counts[status]);
            }
            Assert.IsTrue(adapter.Snapshots.All(value => value.Disposed));
        }
        [TestMethod]
        public async Task RenumberingReorderingAndIdentifierPrefixChangesNeverCreateFalseAddedDeleted()
        {
            var adapter = BaselineFixture.Adapter(new[] { BaselineFixture.Object(17, number: "1.2", order: 2) },
                new[] { BaselineFixture.Object(17, number: "9.8", order: 100, identifier: "NEWPREFIX-17") });
            using (var result = await BaselineFixture.Service(adapter).CompareAsync(BaselineFixture.Request()))
            {
                var row = result.ReadChanges().Single();
                Assert.AreEqual(BaselineChangeStatus.Unchanged, row.Status);
                Assert.AreEqual(17, row.Identity.AbsoluteNumber); Assert.AreEqual("qualified-module-1", row.Identity.QualifiedModuleId);
                Assert.AreEqual("1.2", row.Before.ObjectNumber); Assert.AreEqual("9.8", row.After.ObjectNumber);
                Assert.AreEqual(0, result.ReadEvents().Count());
            }
        }
        [TestMethod]
        public async Task SameVisibleNumberWithDifferentAbsoluteIdsIsDeletePlusAdd()
        {
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(17) }, new[] { BaselineFixture.Object(18) }))
                .CompareAsync(BaselineFixture.Request()))
            {
                CollectionAssert.AreEquivalent(new[] { BaselineChangeStatus.Added, BaselineChangeStatus.Deleted }, result.ReadChanges().Select(value => value.Status).ToArray());
            }
        }
        [DataTestMethod]
        [DataRow(true)] [DataRow(false)]
        public async Task NativeReaderReceivesOnlyTwoSelectedBaselinesWithAttributeDxlDisabled(bool target95)
        {
            var adapter = BaselineFixture.Adapter(Array.Empty<RequirementRecord>(), Array.Empty<RequirementRecord>());
            var request = new BaselineComparisonRequest("/P/M", new RequirementBaseline(1, 2, "A"), new RequirementBaseline(3, 4, "B"),
                new[] { "Status" }, target: target95 ? DoorsVersion.V95 : DoorsVersion.V97);
            using (var result = await BaselineFixture.Service(adapter).CompareAsync(request)) Assert.IsTrue(result.Summary.IsConclusive);
            Assert.AreEqual(2, adapter.Calls.Count);
            foreach (var call in adapter.Calls)
            {
                Assert.IsNotNull(call.Baseline); Assert.IsFalse(call.AllowAttributeDxl); Assert.AreEqual(DeletedObjectMode.MetadataOnly, call.DeletedObjects);
                Assert.AreEqual(target95 ? DoorsVersion.V95 : DoorsVersion.V97, call.Target);
                CollectionAssert.AreEqual(new[] { "Status" }, call.CustomAttributes.ToArray());
            }
            Assert.AreSame(request.Before, adapter.Calls[0].Baseline); Assert.AreSame(request.After, adapter.Calls[1].Baseline);
        }
        [DataTestMethod]
        [DataRow("differentModule")] [DataRow("legacyIdentity")] [DataRow("truncated")] [DataRow("enumeration")]
        [DataRow("denied")] [DataRow("unavailable")] [DataRow("missingBaseline")] [DataRow("cleanup")]
        [DataRow("wrongVersion")] [DataRow("duplicate")]
        public async Task RejectsUnverifiedIdentityOrIncompletePopulationBeforePublishingChanges(string problem)
        {
            var adapter = new BaselineMockAdapter { OnRead = (request, call) =>
                new BaselineMockSnapshot(problem == "wrongVersion" ? new RequirementReadRequest("/P/M", baseline: new RequirementBaseline(999, 0), deletedObjects: DeletedObjectMode.MetadataOnly) : request,
                    problem == "duplicate" ? new[] { BaselineFixture.Object(), BaselineFixture.Object() } : new[] { BaselineFixture.Object() },
                    moduleId: problem == "legacyIdentity" ? null : problem == "differentModule" && call == 2 ? "another-native-module" : "qualified-module-1",
                    truncated: problem == "truncated", enumerationFailed: problem == "enumeration", denied: problem == "denied" ? 1 : 0,
                    unavailable: problem == "unavailable" ? 1 : 0, status: problem == "missingBaseline" ? RequirementModuleStatus.BaselineUnavailable : RequirementModuleStatus.Available,
                    cleanup: problem == "cleanup" ? RequirementCleanupStatus.Failed : RequirementCleanupStatus.Closed) };
            var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => BaselineFixture.Service(adapter).CompareAsync(BaselineFixture.Request()));
            Assert.AreEqual(IntegrationErrorCode.BaselineComparisonFailed, error.Code);
            Assert.IsTrue(adapter.Snapshots.All(value => value.Disposed));
            if (problem != "differentModule") Assert.AreEqual(1, adapter.Calls.Count);
        }
        [DataTestMethod]
        [DataRow(false, true, "Deleted")] [DataRow(true, false, "Added")] [DataRow(true, true, "Unchanged")]
        public async Task TombstonesSupportDeletionAndRestorationWithoutComparingUnreadContent(bool oldDeleted, bool newDeleted, string status)
        {
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(deleted: oldDeleted) }, new[] { BaselineFixture.Object(deleted: newDeleted) }))
                .CompareAsync(BaselineFixture.Request()))
            { Assert.AreEqual(status, result.ReadChanges().Single().Status.ToString()); Assert.IsTrue(result.Summary.IsConclusive); }
        }
        [TestMethod]
        public async Task OnlyTombstonesOnEitherSideDoNotBecomeNewOrDeletedLiveRequirements()
        {
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(1, deleted: true) }, new[] { BaselineFixture.Object(2, deleted: true) }))
                .CompareAsync(BaselineFixture.Request()))
            { Assert.AreEqual(2, result.Summary.Counts[BaselineChangeStatus.Unchanged]); Assert.AreEqual(0, result.ReadEvents().Count()); }
        }
        [DataTestMethod]
        [DataRow(BaselineTextNormalization.UnicodeNfcAndLineEndings, "Unchanged")]
        [DataRow(BaselineTextNormalization.Exact, "Modified")]
        public async Task NormalizationIsExplicitAndOriginalValuesAreRetained(BaselineTextNormalization policy, string status)
        {
            const string left = "caf\u0065\u0301\r\nTürkçe\rnext"; const string right = "caf\u00e9\nTürkçe\nnext";
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(plain: left) }, new[] { BaselineFixture.Object(plain: right) }))
                .CompareAsync(BaselineFixture.Request(normalization: policy)))
            {
                var change = result.ReadChanges().Single(); var field = change.Attributes.Single(value => value.Name == "Object Text");
                Assert.AreEqual(status, change.Status.ToString()); Assert.AreEqual(left, field.Before.PlainText); Assert.AreEqual(right, field.After.PlainText);
                Assert.AreEqual(policy != BaselineTextNormalization.Exact, field.HasNormalizationDifference);
                Assert.AreEqual(policy != BaselineTextNormalization.Exact ? 1 : 0, result.Summary.NormalizationDifferenceCount);
            }
        }
        [DataTestMethod]
        [DataRow("text ", "text")] [DataRow("A", "a")] [DataRow("a  b", "a b")] [DataRow("a\u00a0b", "a b")]
        public async Task NormalizationNeverDropsCaseSpacingOrNonBreakingSpaces(string left, string right)
        {
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(plain: left) }, new[] { BaselineFixture.Object(plain: right) }))
                .CompareAsync(BaselineFixture.Request())) Assert.AreEqual(BaselineChangeStatus.Modified, result.ReadChanges().Single().Status);
        }
        [TestMethod]
        public async Task RichFormattingAndEmbeddedOleChangesAreConservativeModificationsEvenWithSamePlainText()
        {
            const string oldRich = "{\\rtf1 body {\\object\\objdata 00FF}}"; const string newRich = "{\\rtf1\\b body {\\object\\objdata 01FF}}";
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(rich: oldRich) }, new[] { BaselineFixture.Object(rich: newRich) }))
                .CompareAsync(BaselineFixture.Request()))
            {
                var row = result.ReadChanges().Single(); var field = row.Attributes.Single(value => value.Name == "Object Text");
                Assert.AreEqual(BaselineChangeStatus.Modified, row.Status); Assert.IsFalse(field.PlainTextChanged); Assert.IsTrue(field.RichTextChanged);
                Assert.AreEqual(oldRich, field.Before.RichTextWithOle); Assert.AreEqual(newRich, field.After.RichTextWithOle);
            }
        }
        [DataTestMethod]
        [DataRow(RequirementValueStatus.AccessDenied)] [DataRow(RequirementValueStatus.DeniedOrNotFound)]
        [DataRow(RequirementValueStatus.TooLarge)] [DataRow(RequirementValueStatus.DxlEvaluationDisabled)]
        public async Task UnavailableAttributeCannotBeTreatedAsEmptyOrUnchanged(RequirementValueStatus status)
        {
            var value = BaselineFixture.Value("Status", "", "", status, status);
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(
                new[] { BaselineFixture.Object(custom: new[] { value }) }, new[] { BaselineFixture.Object(custom: new[] { value }) }))
                .CompareAsync(BaselineFixture.Request(new[] { "Status" })))
            {
                var row = result.ReadChanges().Single(); Assert.AreEqual(BaselineChangeStatus.Indeterminate, row.Status);
                Assert.IsFalse(row.IsConclusive); Assert.IsFalse(result.Summary.IsConclusive);
                Assert.AreEqual(BaselineAttributeStatus.Unavailable, row.Attributes.Single(item => item.Name == "Status").Status);
                Assert.IsTrue(result.ReadEvents().Single().RequiresReview);
            }
        }
        [TestMethod]
        public async Task ProvenTextChangeWithAnotherUnknownAttributeRemainsModifiedButNeedsReview()
        {
            var unknown = BaselineFixture.Value("Status", "", "", RequirementValueStatus.AccessDenied, RequirementValueStatus.AccessDenied);
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(
                new[] { BaselineFixture.Object(custom: new[] { unknown }) }, new[] { BaselineFixture.Object(plain: "changed", custom: new[] { unknown }) }))
                .CompareAsync(BaselineFixture.Request(new[] { "Status" })))
            { Assert.AreEqual(BaselineChangeStatus.Modified, result.ReadChanges().Single().Status); Assert.IsTrue(result.ReadEvents().Single().RequiresReview); }
        }
        [TestMethod]
        public async Task AttributeTypeChangesAndParentIdentityChangesAreReported()
        {
            var adapter = BaselineFixture.Adapter(new[] { BaselineFixture.Object(parent: 10, custom: new[] { BaselineFixture.Value("Status", "1", "", richStatus: RequirementValueStatus.NotApplicable, type: "Integer") }) },
                new[] { BaselineFixture.Object(parent: 11, custom: new[] { BaselineFixture.Value("Status", "1", "", richStatus: RequirementValueStatus.NotApplicable, type: "String") }) });
            using (var result = await BaselineFixture.Service(adapter).CompareAsync(BaselineFixture.Request(new[] { "Status" })))
            { var row = result.ReadChanges().Single(); Assert.IsTrue(row.HierarchyChanged); Assert.IsTrue(row.Attributes.Single(value => value.Name == "Status").TypeChanged); }
        }
        [TestMethod]
        public async Task UnknownParentIsIndeterminateWithoutFalseHierarchyChange()
        {
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(parentStatus: RequirementParentStatus.Unavailable) },
                new[] { BaselineFixture.Object(parentStatus: RequirementParentStatus.Unavailable) })).CompareAsync(BaselineFixture.Request()))
            { Assert.AreEqual(BaselineChangeStatus.Indeterminate, result.ReadChanges().Single().Status); Assert.IsFalse(result.ReadChanges().Single().HierarchyChanged); }
        }
        [TestMethod]
        public async Task EventsHaveStableEvidenceIdsAndOnlyChangedObjectsBecomeImpactCandidates()
        {
            var adapter = BaselineFixture.Adapter(new[] { BaselineFixture.Object(1), BaselineFixture.Object(2) }, new[] { BaselineFixture.Object(1), BaselineFixture.Object(2, "SECRET-BODY") });
            var log = new BaselineTestLog();
            using (var result = await new BaselineComparisonService(adapter, log).CompareAsync(BaselineFixture.Request()))
            {
                var first = result.ReadEvents().Single(); var second = result.ReadEvents().Single();
                Assert.AreEqual(first.EventId, second.EventId); Assert.AreEqual(64, first.EventId.Length);
                Assert.AreEqual(result.ComparisonId, first.ComparisonId); Assert.IsTrue(first.IsImpactCandidate); Assert.IsFalse(first.RequiresReview);
                Assert.AreEqual(2, first.Change.Identity.AbsoluteNumber); Assert.AreEqual(1, first.SchemaVersion);
                Assert.IsTrue(log.Messages.All(value => !value.Contains("SECRET-BODY")));
            }
        }
        [TestMethod]
        public async Task MissingTypeEvidenceDoesNotBecomeConclusiveUnchanged()
        {
            var value = BaselineFixture.Value(type: "");
            using (var result = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(value: value) }, new[] { BaselineFixture.Object(value: value) }))
                .CompareAsync(BaselineFixture.Request()))
            { Assert.AreEqual(BaselineChangeStatus.Indeterminate, result.ReadChanges().Single().Status); Assert.IsFalse(result.Summary.IsConclusive); }
        }
        [TestMethod]
        public async Task EquivalentEvidenceAcrossJobsDeduplicatesWhileDifferentPolicyChangesEventIdentity()
        {
            Func<BaselineMockAdapter> adapter = () => BaselineFixture.Adapter(new[] { BaselineFixture.Object() }, new[] { BaselineFixture.Object(plain: "changed") });
            using (var first = await BaselineFixture.Service(adapter()).CompareAsync(BaselineFixture.Request()))
            using (var second = await BaselineFixture.Service(adapter()).CompareAsync(BaselineFixture.Request()))
            using (var exact = await BaselineFixture.Service(adapter()).CompareAsync(BaselineFixture.Request(normalization: BaselineTextNormalization.Exact)))
            {
                Assert.AreNotEqual(first.ComparisonId, second.ComparisonId);
                Assert.AreEqual(first.ReadEvents().Single().EventId, second.ReadEvents().Single().EventId);
                Assert.AreNotEqual(first.ReadEvents().Single().EventId, exact.ReadEvents().Single().EventId);
            }
        }
        [TestMethod]
        public async Task HostCanInjectReadOnlyAdapterAndCaptureLargeLazyPopulationsOnlyOnce()
        {
            const int count = 2500; int enumerations = 0;
            var adapter = new BaselineMockAdapter { OnRead = (request, call) => new BaselineMockSnapshot(request, Array.Empty<RequirementRecord>(), expectedCount: count)
                { Rows = () => { enumerations++; return Enumerable.Range(1, count).Select(id => BaselineFixture.Object(id, new string('x', 4096), order: id)); } } };
            var services = new ServiceCollection(); services.AddSingleton<IRequirementReader>(adapter); services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                var result = await provider.GetRequiredService<IBaselineComparisonService>().CompareAsync(BaselineFixture.Request());
                string scratch = Path.Combine(Path.GetTempPath(), "doors-baseline-" + result.ComparisonId.ToString("N"));
                Assert.IsTrue(Directory.Exists(scratch)); Assert.AreEqual(2, enumerations); Assert.IsTrue(adapter.Snapshots.All(value => value.Disposed));
                Assert.AreEqual(count, result.Summary.Counts[BaselineChangeStatus.Unchanged]);
                Assert.AreEqual(count, result.ReadChanges().Count()); Assert.AreEqual(0, result.ReadEvents().Count());
                Assert.AreEqual(2, enumerations); result.Dispose(); Assert.IsFalse(Directory.Exists(scratch));
            }
        }
        [TestMethod]
        public async Task CancellationAndDisposedLeasesPreventFurtherEnumeration()
        {
            var adapter = BaselineFixture.Adapter(new[] { BaselineFixture.Object() }, new[] { BaselineFixture.Object() });
            var token = new CancellationToken(true);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => BaselineFixture.Service(adapter).CompareAsync(BaselineFixture.Request(), token));
            Assert.AreEqual(0, adapter.Calls.Count);
            var result = await BaselineFixture.Service(adapter).CompareAsync(BaselineFixture.Request());
            Assert.ThrowsException<OperationCanceledException>(() => result.ReadChanges(token).ToArray());
            result.Dispose(); result.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => result.ReadChanges().ToArray());
        }
        [DataTestMethod]
        [DataRow(true)] [DataRow(false)]
        public async Task BoundedObjectAndSpoolLimitsFailClosed(bool objectLimit)
        {
            var rows = objectLimit ? new[] { BaselineFixture.Object(1), BaselineFixture.Object(2) } : new[] { BaselineFixture.Object(plain: new string('x', 70000)) };
            var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => BaselineFixture.Service(BaselineFixture.Adapter(rows, rows))
                .CompareAsync(BaselineFixture.Request(maxObjects: objectLimit ? 1 : 10, maxSpoolBytes: 65536)));
            Assert.AreEqual(IntegrationErrorCode.BaselineComparisonFailed, error.Code);
        }
        [TestMethod]
        public void RejectsCurrentNullOrIdenticalBaselineSelection()
        {
            Assert.ThrowsException<ArgumentNullException>(() => new BaselineComparisonRequest("/P/M", null, new RequirementBaseline(1, 0)));
            Assert.ThrowsException<ArgumentException>(() => new BaselineComparisonRequest("/P/M", new RequirementBaseline(1, 0, "A"), new RequirementBaseline(1, 0, "A")));
            // Different suffix is a distinct selected version; never compare just the formatted numeric part.
            var request = new BaselineComparisonRequest("/P/M", new RequirementBaseline(1, 0, "A"), new RequirementBaseline(1, 0, "B"));
            Assert.AreNotEqual(BaselineComparisonRequest.Version(request.Before), BaselineComparisonRequest.Version(request.After));
        }
    }
}
