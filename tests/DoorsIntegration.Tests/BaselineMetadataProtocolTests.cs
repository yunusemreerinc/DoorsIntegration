using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class BaselineMetadataTransport : IDisposable
    {
        internal readonly ExecutorTestContext Files = new ExecutorTestContext();
        internal readonly DxlExecutor Executor;
        internal readonly DxlBaselineHistoryAdapter Adapter;
        internal Func<Guid, string> Reply = Good;
        internal BaselineMetadataTransport(bool queued = false)
        {
            Executor = queued ? new DxlExecutor(Files.Sessions, new Utf8DxlScriptLoader(), Files.Workspaces, Files.Log, Files.Gate,
                new NoDxlRunLimitAutomation(), new DxlWatchdogSettings(), DoorsStaExecutionQueue.Shared) : Files.Executor;
            Adapter = new DxlBaselineHistoryAdapter(Executor, new BaselineMetadataScriptOptions()); Files.Sessions.Session.FileResult = true;
            Files.Sessions.Session.OnRun = session =>
            {
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                File.WriteAllText(Files.ResultPath(Executor.ActiveOperation), Reply(id), new UTF8Encoding(false, true));
            };
        }
        internal static string Good(Guid id) => "DOORS_BASELINE_METADATA\t1\t" + id.ToString("N") +
            "\nM\t/P/M\tqualified-native-1\tAvailable\tOwnedReadOnly\nB\t2\t4\told\tİnceleme\\n注釈\treviewer-1\t0\t01 Jan 1970\t1\t\n" +
            "L\tExisting\tAvailable\t2\t4\told\tExists\nL\tNumbering\tAvailable\t3\t7\tremoved\tAbsentOrUnreadable\nC\t0\t0\t\tNotRequested\nEND\t1\t0\tClosed\n";
        internal Task<BaselineMetadataSnapshot> Read(BaselineHistoryRequest request = null, CancellationToken token = default(CancellationToken)) =>
            Adapter.ReadAsync(request ?? new BaselineHistoryRequest(MetadataFixture.Module(), true), token);
        public void Dispose() { Files.Sessions.Session.ReleaseRun.Set(); Files.Sessions.Session.ReleaseDispose.Set(); Files.Dispose(); }
    }
    [TestClass]
    public class BaselineMetadataProtocolTests
    {
        [DataTestMethod, DataRow(false), DataRow(true)]
        public async Task FileProtocolReadsHistoryCreatorUtcAndSeparateNumberingWatermark(bool windows)
        {
            using (var f = new BaselineMetadataTransport())
            {
                if (windows) f.Reply = id => BaselineMetadataTransport.Good(id).Replace("\n", "\r\n");
                var result = await f.Read(); var entry = result.History.Single();
                Assert.AreEqual(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), entry.CreationDateUtc);
                Assert.AreEqual("İnceleme\n注釈", entry.Annotation); Assert.AreEqual("reviewer-1", entry.Creator);
                Assert.AreEqual(new BaselineVersion(2, 4, "old"), result.LatestExisting.Version);
                Assert.AreEqual(new BaselineVersion(3, 7, "removed"), result.LatestNumberingVersion.Version);
                Assert.IsTrue(result.LatestNumberingVersion.MayBeDeletedOrInaccessible); Assert.IsFalse(result.RuntimeCompatibilityValidated);
                Assert.IsFalse(entry.Identity.NativeIdentityAvailable); Assert.IsFalse(f.Files.Gate.IsBusy);
                Assert.AreEqual(ApartmentState.STA, f.Files.Sessions.OpenApartment); Assert.IsFalse(Directory.Exists(f.Executor.ActiveOperation?.WorkspaceDirectory ?? Path.Combine(f.Files.Root, "nonexistent")));
            }
        }
        [TestMethod]
        public async Task ObservedNativeBaselineIndexIsReturnedWithoutInventingAnId()
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Reply = id => BaselineMetadataTransport.Good(id).Replace("\t1\t\nL\tExisting", "\t1\tobserved-baseline-index\nL\tExisting");
                var row = (await f.Read()).History.Single(); Assert.IsTrue(row.Identity.NativeIdentityAvailable);
                Assert.AreEqual("observed-baseline-index", row.Identity.NativeIdentity);
            }
        }
        [TestMethod]
        public async Task NativeUnrepresentableDateKeepsRawTextWithoutFakeTimestamp()
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Reply = id => BaselineMetadataTransport.Good(id).Replace("\t0\t01 Jan 1970\t", "\t-1\tUnknown legacy date\t");
                var row = (await f.Read()).History.Single(); Assert.IsNull(row.CreationDateUtc); Assert.AreEqual("Unknown legacy date", row.CreationDateRaw);
            }
        }
        [DataTestMethod, DataRow(BaselineHistoryStatus.ClosedByPolicy), DataRow(BaselineHistoryStatus.AccessDenied), DataRow(BaselineHistoryStatus.IdentityMismatch), DataRow(BaselineHistoryStatus.AlreadyOpenForWrite)]
        public async Task NativeClosedOrRestrictedModuleReturnsAnExplicitEmptyStatus(BaselineHistoryStatus status)
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Reply = id => "DOORS_BASELINE_METADATA\t1\t" + id.ToString("N") + "\nM\t/P/M\t\t" + status +
                    "\tNotLoaded\nL\tExisting\tUnknown\t0\t0\t\tUnknown\nL\tNumbering\tUnknown\t0\t0\t\tUnknown\nC\t0\t0\t\tNotRequested\nEND\t0\t1\tNotOwned\n";
                var result = await f.Read(); Assert.AreEqual(status, result.Status); Assert.AreEqual(0, result.History.Count); Assert.IsFalse(result.HistoryComplete);
            }
        }
        [TestMethod]
        public async Task NoBaselineHistoryIsDistinctFromAnEmptyPartialRead()
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Reply = id => "DOORS_BASELINE_METADATA\t1\t" + id.ToString("N") + "\nM\t/P/M\tqualified-native-1\tAvailable\tBorrowedReadOnly\n" +
                    "L\tExisting\tNone\t0\t0\t\tUnknown\nL\tNumbering\tNone\t0\t0\t\tUnknown\nC\t0\t0\t\tNotRequested\nEND\t0\t0\tNotOwned\n";
                Assert.IsTrue((await f.Read()).HasNoBaselineHistory);
                f.Reply = id => ("DOORS_BASELINE_METADATA\t1\t" + id.ToString("N") + "\nM\t/P/M\tqualified-native-1\tAvailable\tBorrowedReadOnly\n" +
                    "L\tExisting\tUnknown\t0\t0\t\tUnknown\nL\tNumbering\tUnknown\t0\t0\t\tUnknown\nC\t0\t0\t\tNotRequested\nEND\t0\t1\tNotOwned\n");
                Assert.IsFalse((await f.Read()).HasNoBaselineHistory);
            }
        }
        [DataTestMethod, DataRow("Exists"), DataRow("AbsentOrUnreadable")]
        public async Task ExactTargetCollisionIsReadFromBaselineExistsEvidence(string status)
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Reply = id => BaselineMetadataTransport.Good(id).Replace("C\t0\t0\t\tNotRequested", "C\t2\t4\told\t" + status);
                var result = await f.Read(new BaselineHistoryRequest(MetadataFixture.Module(), true, collisionTarget: new BaselineVersion(2, 4, "old")));
                Assert.AreEqual(status, result.Collision.Status.ToString()); Assert.IsFalse(result.Collision.AuthorizesCreation);
            }
        }
        [DataTestMethod, DataRow("request"), DataRow("path"), DataRow("module"), DataRow("duplicate"), DataRow("count"), DataRow("footer"),
            DataRow("cleanup"), DataRow("epoch"), DataRow("escape"), DataRow("existing"), DataRow("trailing"), DataRow("numeric-enum")]
        public async Task MalformedOrContradictoryNativeMetadataCannotBePublished(string mutation)
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Reply = id =>
                {
                    string text = BaselineMetadataTransport.Good(id);
                    switch (mutation)
                    {
                        case "request": return BaselineMetadataTransport.Good(Guid.NewGuid());
                        case "path": return text.Replace("/P/M", "/Foreign/M");
                        case "module": return text.Replace("qualified-native-1", "different-native");
                        case "duplicate": return text.Replace("L\tExisting", "B\t2\t4\told\tduplicate\tuser\t0\traw\t1\t\nL\tExisting").Replace("END\t1\t", "END\t2\t");
                        case "count": return text.Replace("END\t1\t", "END\t2\t");
                        case "footer": return text.Substring(0, text.Length - 1);
                        case "cleanup": return text.Replace("Closed\n", "Failed\n");
                        case "epoch": return text.Replace("\t0\t01 Jan 1970\t", "\t-2\traw\t");
                        case "escape": return text.Replace("reviewer-1", "bad\\q");
                        case "existing": return text.Replace("Existing\tAvailable\t2\t4\told", "Existing\tAvailable\t9\t9\tmissing");
                        case "trailing": return text + "M\tUnexpected\n";
                        default: return text.Replace("Available\tOwnedReadOnly", "0\tOwnedReadOnly");
                    }
                };
                var error = await Assert.ThrowsExceptionAsync<IntegrationException>(() => f.Read()); Assert.AreEqual(IntegrationErrorCode.BaselineMetadataFailed, error.Code);
                Assert.IsFalse(error.Message.Contains("qualified-native-1")); Assert.IsFalse(f.Files.Gate.IsBusy);
            }
        }
        [TestMethod]
        public async Task Unverified95ApiNeverDispatchesAndKeepsExplicitCollisionTarget()
        {
            using (var f = new BaselineMetadataTransport())
            {
                var request = new BaselineHistoryRequest(MetadataFixture.Module(), target: DoorsVersion.V95);
                Assert.AreEqual(BaselineHistoryStatus.ApiNotValidated, (await f.Read(request)).Status);
                var target = new BaselineVersion(1, 0); var collision = await f.Adapter.CheckCollisionAsync(request, target, CancellationToken.None);
                Assert.AreEqual(target, collision.Target); Assert.AreEqual(BaselineExistenceStatus.Unknown, collision.Status); Assert.AreEqual(0, f.Files.Sessions.OpenCalls);
            }
        }
        [TestMethod]
        public async Task SoftTimeoutKeepsTheNativeGateUntilLateResultAndCleanupComplete()
        {
            using (var f = new BaselineMetadataTransport())
            {
                f.Files.Sessions.Session.BlockRun = true;
                await Assert.ThrowsExceptionAsync<DxlExecutionWaitException>(() => f.Read(new BaselineHistoryRequest(MetadataFixture.Module(), true, timeout: TimeSpan.FromMilliseconds(50))));
                var operation = f.Executor.ActiveOperation; Assert.IsTrue(f.Files.Gate.IsBusy); Assert.AreEqual(1, f.Files.Sessions.OpenCalls);
                f.Files.Sessions.Session.ReleaseRun.Set(); using (await operation.Completion) { }
                Assert.IsFalse(f.Files.Gate.IsBusy);
            }
        }
    }
}
