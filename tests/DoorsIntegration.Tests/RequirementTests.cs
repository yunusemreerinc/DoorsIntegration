using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class RequirementTests
    {
        [TestMethod]
        public async Task PreservesIdentityHierarchyUnicodeAndOpaqueRichTextWithOle()
        {
            var request = new RequirementReadRequest("/P/M", new[] { "Priority" });
            const string plain = "Türkçe 😀\tline\r\n\\end";
            const string rich = "{\\rtf1\\b Başlık\\b0 {\\object\\objemb{\\*\\objdata 0102AB}}\\par 😀}";
            using (var context = new RequirementTestContext(request))
            {
                context.Reply = id => RequirementTestContext.Batch(id, request,
                    RequirementTestContext.Object(1, 10, values: new[] {
                        RequirementTestContext.Value("Object Heading", "Heading"), RequirementTestContext.Value("Object Text", plain, rich),
                        RequirementTestContext.Value("Priority", "High\nMedium", richStatus: "NotApplicable", type: "PriorityType") }) +
                    RequirementTestContext.Object(2, 42, level: 2, parent: 10, values: new[] {
                        RequirementTestContext.Value("Object Heading"), RequirementTestContext.Value("Object Text"),
                        RequirementTestContext.Value("Priority", "Low", richStatus: "NotApplicable", type: "PriorityType") }), count: 2);
                using (var snapshot = await new RequirementService(context.Reader).ReadAsync(request))
                {
                    var objects = snapshot.ReadObjects().ToArray();
                    Assert.AreEqual(2, snapshot.Summary.ObjectCount);
                    Assert.IsTrue(snapshot.Summary.IsCompleteForReadableObjects);
                    Assert.AreEqual("REQ-10", objects[0].ObjectIdentifier);
                    Assert.AreEqual("1", objects[0].ObjectNumber);
                    Assert.AreEqual(plain, objects[0].Text.PlainText);
                    Assert.AreEqual(rich, objects[0].Text.RichTextWithOle);
                    Assert.AreEqual("DoorsRtfWithOle", objects[0].Text.RichTextFormat);
                    Assert.AreEqual("High\nMedium", objects[0].Values["Priority"].PlainText);
                    Assert.AreEqual(42, objects[1].AbsoluteNumber);
                    Assert.AreEqual(10, objects[1].ParentAbsoluteNumber);
                    Assert.AreEqual(2, objects[1].HierarchyLevel);
                    Assert.AreEqual(RequirementParentStatus.Resolved, objects[1].ParentStatus);
                    CollectionAssert.Contains(context.Transport.Sessions.Session.Calls, "runFile");
                    Assert.AreEqual(ApartmentState.STA, context.Transport.Sessions.OpenApartment);
                    Assert.IsTrue(context.Transport.Sessions.Session.Disposed);
                    Assert.IsFalse(string.Join(" ", context.Transport.Log.Entries).Contains("Başlık"));
                }
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }

        [DataTestMethod]
        [DataRow("DeniedOrNotFound")]
        [DataRow("AccessDenied")]
        [DataRow("NotFormalModule")]
        [DataRow("ModuleDeleted")]
        [DataRow("BaselineUnavailable")]
        [DataRow("AlreadyOpenForWrite")]
        [DataRow("NotLoadedByPolicy")]
        [DataRow("ReadFailed")]
        public async Task UnavailableModuleIsExplicitAndNeverLooksLikeSuccessfulEmptyModule(string status)
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => RequirementTestContext.Batch(id, context.Request, count: 0, status: status, ownership: "NotLoaded", cleanup: "NotOwned");
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.AreEqual(status, snapshot.Summary.ModuleStatus.ToString());
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableObjects);
                    Assert.AreEqual(0, snapshot.ReadObjects().Count());
                }
            }
        }

        [TestMethod]
        public async Task EmptyReadableModuleIsCompleteAndBorrowedModuleRemainsBorrowed()
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => RequirementTestContext.Batch(id, context.Request, count: 0, ownership: "BorrowedReadOnly", cleanup: "NotOwned");
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.IsTrue(snapshot.Summary.IsCompleteForReadableObjects);
                    Assert.AreEqual(RequirementModuleOwnership.BorrowedReadOnly, snapshot.Summary.Ownership);
                }
            }
        }

        [TestMethod]
        public async Task BaselineAndEscapedParametersAreBoundToExactRequest()
        {
            var request = new RequirementReadRequest("/P/\"M\\Türkçe", new[] { "a\"; halt; //", "Owner" },
                baseline: new RequirementBaseline(2, 3, "review\"\\"), openIfNeeded: false,
                deletedObjects: DeletedObjectMode.IncludeContent, allowAttributeDxl: true, target: DoorsVersion.V95);
            using (var context = new RequirementTestContext(request))
            {
                context.Reply = id => RequirementTestContext.Batch(id, request, count: 0);
                using (var snapshot = await context.Reader.ReadAsync(request))
                {
                    Assert.AreSame(request, snapshot.Summary.Request);
                    string script = context.Transport.Sessions.Session.Script;
                    Assert.AreEqual(request.ModulePath, DiscoveryTestContext.Parameter(script, "path"));
                    Assert.AreEqual("a\"; halt; //\nOwner\n", DiscoveryTestContext.Parameter(script, "attributes"));
                    Assert.AreEqual("2", DiscoveryTestContext.Parameter(script, "major"));
                    Assert.AreEqual("3", DiscoveryTestContext.Parameter(script, "minor"));
                    Assert.AreEqual(request.Baseline.Suffix, DiscoveryTestContext.Parameter(script, "suffix"));
                    Assert.AreEqual("Baseline", DiscoveryTestContext.Parameter(script, "version"));
                    Assert.AreEqual("0", DiscoveryTestContext.Parameter(script, "open"));
                    Assert.AreEqual("1", DiscoveryTestContext.Parameter(script, "allowDxl"));
                    Assert.AreEqual("IncludeContent", DiscoveryTestContext.Parameter(script, "deleted"));
                }
            }
        }

        [DataTestMethod]
        [DataRow("AccessDenied")]
        [DataRow("DeniedOrNotFound")]
        [DataRow("DxlEvaluationDisabled")]
        [DataRow("NotObjectAttribute")]
        [DataRow("TooLarge")]
        [DataRow("ReadFailed")]
        public async Task UnreadableFieldHasExplicitStatusAndMakesSummaryPartial(string status)
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => RequirementTestContext.Batch(id, context.Request, RequirementTestContext.Object(1, 10, values: new[] {
                    RequirementTestContext.Value("Object Heading"), RequirementTestContext.Value("Object Text", status: status, richStatus: status) }));
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    var record = snapshot.ReadObjects().Single();
                    Assert.AreEqual(status, record.Text.PlainStatus.ToString());
                    Assert.AreEqual("", record.Text.PlainText);
                    Assert.AreEqual("None", record.Text.RichTextFormat);
                    Assert.AreEqual(1L, snapshot.Summary.FieldProblemCount);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableObjects);
                }
            }
        }

        [DataTestMethod]
        [DataRow(DeletedObjectMode.MetadataOnly)]
        [DataRow(DeletedObjectMode.IncludeContent)]
        public async Task DeletedObjectsFollowExplicitPolicy(DeletedObjectMode mode)
        {
            var request = new RequirementReadRequest("/P/M", deletedObjects: mode);
            using (var context = new RequirementTestContext(request))
            {
                var status = mode == DeletedObjectMode.MetadataOnly ? "NotRequested" : "Readable";
                context.Reply = id => RequirementTestContext.Batch(id, request, RequirementTestContext.Object(1, 10, deleted: true, values: new[] {
                    RequirementTestContext.Value("Object Heading", status: status, richStatus: status),
                    RequirementTestContext.Value("Object Text", status: status, richStatus: status) }));
                using (var snapshot = await context.Reader.ReadAsync(request))
                {
                    Assert.IsTrue(snapshot.ReadObjects().Single().IsDeleted);
                    Assert.AreEqual(status, snapshot.ReadObjects().Single().Text.PlainStatus.ToString());
                }
            }
        }

        [TestMethod]
        public async Task DeniedDeletedAndFailedObjectsAreCountedWithoutInventingRecords()
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => RequirementTestContext.Batch(id, context.Request,
                    RequirementTestContext.Object(2, 42, level: 3, parentStatus: "Unavailable"), denied: 1, deletedExcluded: 1, unavailable: 1, truncated: true, failed: true);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.AreEqual(4, snapshot.Summary.VisitedObjectCount);
                    Assert.AreEqual(1, snapshot.Summary.DeniedObjectCount);
                    Assert.AreEqual(1, snapshot.Summary.DeletedExcludedCount);
                    Assert.AreEqual(1, snapshot.Summary.UnavailableObjectCount);
                    Assert.IsTrue(snapshot.Summary.IsTruncated);
                    Assert.IsTrue(snapshot.Summary.EnumerationFailed);
                    Assert.IsNull(snapshot.ReadObjects().Single().ParentAbsoluteNumber);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableObjects);
                }
            }
        }

        [TestMethod]
        public async Task CleanupFailureIsVisibleInSummary()
        {
            using (var context = new RequirementTestContext())
            {
                context.Reply = id => RequirementTestContext.Batch(id, context.Request, RequirementTestContext.Object(1, 10), cleanup: "Failed");
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    Assert.AreEqual(RequirementCleanupStatus.Failed, snapshot.Summary.CleanupStatus);
                    Assert.IsFalse(snapshot.Summary.IsCompleteForReadableObjects);
                }
            }
        }

        [TestMethod]
        public async Task SnapshotDefersDeletionUntilLastStreamingReaderCloses()
        {
            using (var context = new RequirementTestContext())
            {
                var snapshot = await context.Reader.ReadAsync(context.Request);
                var first = snapshot.OpenRawStream();
                var second = snapshot.ReadObjects().GetEnumerator();
                Assert.IsTrue(second.MoveNext());
                snapshot.Dispose();
                Assert.IsTrue(File.Exists(context.OutputPath));
                Assert.ThrowsException<ObjectDisposedException>(() => snapshot.OpenRawStream());
                first.Dispose();
                Assert.IsTrue(File.Exists(context.OutputPath));
                second.Dispose();
                Assert.IsFalse(File.Exists(context.OutputPath));
                snapshot.Dispose();
            }
        }

        [TestMethod]
        public async Task LargeTransferIsRepeatableFileBackedEnumeration()
        {
            using (var context = new RequirementTestContext())
            {
                string body = new string('x', 16384);
                context.Reply = id => RequirementTestContext.Batch(id, context.Request,
                    string.Concat(Enumerable.Range(1, 200).Select(index => RequirementTestContext.Object(index, index, body, body))), count: 200);
                using (var snapshot = await context.Reader.ReadAsync(context.Request))
                {
                    using (var stream = snapshot.OpenRawStream()) Assert.IsTrue(stream.Length > 6 * 1024 * 1024);
                    Assert.AreEqual(200, snapshot.ReadObjects().Count());
                    Assert.AreEqual(16384, snapshot.ReadObjects().First().Text.RichTextWithOle.Length);
                }
                Assert.IsFalse(File.Exists(context.OutputPath));
            }
        }

        [TestMethod]
        public async Task PreCancelledRequestNeverDispatchesCom()
        {
            using (var context = new RequirementTestContext())
            {
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => context.Reader.ReadAsync(context.Request, new CancellationToken(true)));
                Assert.AreEqual(IntegrationErrorCode.DxlWaitCancelled, error.Code);
                Assert.AreEqual(0, context.Transport.Sessions.OpenCalls);
            }
        }

        [TestMethod]
        public async Task TimeoutLeavesComRunningAndCleansLateFileResult()
        {
            var request = new RequirementReadRequest("/P/M", timeout: TimeSpan.FromMilliseconds(100));
            using (var context = new RequirementTestContext(request))
            {
                context.Transport.Sessions.Session.BlockRun = true;
                var pending = context.Reader.ReadAsync(request);
                Assert.IsTrue(context.Transport.Sessions.Session.EnteredRun.Wait(TimeSpan.FromSeconds(5)));
                var operation = context.Transport.Executor.ActiveOperation;
                var failure = await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => pending);
                Assert.AreEqual(IntegrationErrorCode.DxlTimeout, failure.Code);
                Assert.IsTrue(context.Transport.Executor.IsBusy);
                context.Transport.Sessions.Session.ReleaseRun.Set();
                await operation.Completion;
                // A file can disappear before the late owner finishes deleting its workspace.
                // Wait for the full cleanup boundary before the fixture removes the parent directory.
                Assert.IsTrue(SpinWait.SpinUntil(() => context.OutputPath != null && !Directory.Exists(operation.WorkspaceDirectory), TimeSpan.FromSeconds(5)));
                Assert.IsTrue(context.Transport.Sessions.Session.Disposed);
            }
        }

        [TestMethod]
        public void DependencyInjectionResolvesServiceAndPreservesReaderOverride()
        {
            var reader = new StubReader();
            var services = new ServiceCollection();
            services.AddSingleton<IRequirementReader>(reader);
            services.AddDoorsIntegration();
            using (var provider = services.BuildServiceProvider())
            {
                Assert.AreSame(reader, provider.GetRequiredService<IRequirementReader>());
                var request = new RequirementReadRequest("/P/M");
                var token = new CancellationToken(true);
                provider.GetRequiredService<IRequirementService>().ReadAsync(request, token).GetAwaiter().GetResult();
                Assert.AreSame(request, reader.Request);
                Assert.AreEqual(token, reader.Token);
            }
        }
        private sealed class StubReader : IRequirementReader
        {
            public RequirementReadRequest Request;
            public CancellationToken Token;
            public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
            { Request = request; Token = cancellationToken; return Task.FromResult<IRequirementSnapshot>(null); }
        }
    }
}
