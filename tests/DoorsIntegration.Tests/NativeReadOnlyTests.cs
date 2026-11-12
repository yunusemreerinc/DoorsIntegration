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
    [TestClass, TestCategory("NativeReadOnly")]
    public class NativeReadOnlyTests
    {
        private static readonly Guid Correlation = Guid.Parse("1af3fb77-655b-45fa-9cfe-43abe58cf1c0");
        internal static string Rows = "C\tdb\tuser\t9.7.2.9\t42\nF\t/P\tfolder-id\tProject\ttrue\ttrue\tfalse\nD\tReview\tİnceleme\\n注釈\nM\tReview\tA\t/P/A\nS\tReview\t2\t0\tSET\ttrue\tReview note\tuser\t1700000000\nB\tReview\t2\t0\tSET\tA\t/P/A\t1\t5\tMOD\topaque version label\tVerified\nB\tReview\t2\t0\tSET\tB\t/Other/B\t3\t2\t\tanother label\tVerified\nP\t/P/A\tA\tFormal\tPassed\tPassed\tFailed\tPassed\tReadOnly\tUnknown\tNone\tConfigured\tObserved\nL\t/P/Links\tLINK-QUID\tLink\ttrue\n";
        internal static BaselineSetInspection Parse(string rows = null, Guid? id = null, string trailer = null)
        {
            string body = rows ?? Rows; string text = "NATIVE_READ_SNAPSHOT\t1\t" + (id ?? Correlation).ToString("N") + "\n" + body + (trailer ?? "END\ttrue\t" + body.Count(c => c == '\n')) + "\n";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text))) return NativeReadObservationProtocol.Read(stream, Correlation, new NativeReadQuery(DoorsVersion.V97, "/P", new[] { "/P/A" }, new[] { "/P/Links" }));
        }
        [TestMethod]
        public void ObservedSessionUnicodeAndTypedMemberVersionsArePreserved()
        {
            var result = Parse(); Assert.IsTrue(result.SessionObserved); Assert.IsTrue(result.ReadOnlyObservation); Assert.IsFalse(result.Complete); Assert.IsTrue(result.VisibilityLimited); Assert.IsTrue(result.DefinitionCacheUnknown);
            Assert.AreEqual("db", result.DatabaseId); Assert.AreEqual("42", result.SessionId); Assert.AreEqual("İnceleme\n注釈", result.Definitions.Single().Description);
            Assert.AreEqual(2, result.Sets.Single().Version.Major); Assert.AreEqual(1, result.Sets.Single().Members[0].TypedVersion.Major); Assert.AreEqual(5, result.Sets.Single().Members[0].TypedVersion.Minor);
            Assert.AreEqual("MOD", result.Sets.Single().Members[0].TypedVersion.Suffix); Assert.AreEqual("opaque version label", result.Sets.Single().Members[0].ModuleBaselineVersion);
            Assert.AreEqual("LINK-QUID", result.ObservedLinkModuleIds["/P/Links"]); Assert.AreEqual(BaselineShareableConfiguration.Configured, result.ModuleStates.Single().Shareable);
        }
        [TestMethod]
        public void SemanticRevisionIsIndependentOfRequestCorrelation()
        {
            var a = Parse(); var id = Guid.NewGuid(); BaselineSetInspection b;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("NATIVE_READ_SNAPSHOT\t1\t" + id.ToString("N") + "\n" + Rows + "END\ttrue\t9\n")))
                b = NativeReadObservationProtocol.Read(stream, id, new NativeReadQuery(DoorsVersion.V97, "/P", new[] { "/P/A" }, new[] { "/P/Links" }));
            Assert.AreEqual(a.Revision, b.Revision);
            Assert.AreNotEqual(a.Revision, Parse(Rows.Replace("Review note", "Changed note")).Revision);
        }
        [TestMethod] public void CorrelationMismatchIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(id: Guid.NewGuid()));
        [TestMethod] public void IncompleteTransportIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(trailer: "END\tfalse\t9"));
        [TestMethod] public void CountMismatchIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(trailer: "END\ttrue\t8"));
        [TestMethod] public void TrailingRowsAreRejected() => Assert.ThrowsException<FormatException>(() => Parse(trailer: "END\ttrue\t9\nL\t/P/Links\tLINK-QUID\tLink\ttrue"));
        [TestMethod] public void WrongClientReleaseIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(Rows.Replace("9.7.2.9", "9.5.2.1")));
        [TestMethod] public void DuplicateMemberIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(Rows + "B\tReview\t2\t0\tSET\tA\t/P/A\t1\t5\tMOD\tx\tVerified\n"));
        [TestMethod] public void OutOfScopeModuleIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(Rows.Replace("P\t/P/A\tA", "P\t/Other/A\tA")));
        [TestMethod] public void InvalidEscapeIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(Rows.Replace("Review note", "bad\\q")));
        [TestMethod] public void MissingModuleProbeIsRejected() => Assert.ThrowsException<FormatException>(() => Parse(string.Join("\n", Rows.Split('\n').Where(l => !l.StartsWith("P\t")))));
        [TestMethod] public void UnsafeAndOversizedQueryScopesAreRejected()
        {
            Assert.ThrowsException<ArgumentException>(() => new NativeReadQuery(DoorsVersion.V97, modules: new[] { "/P/A\n/P/B" }));
            Assert.ThrowsException<ArgumentException>(() => new NativeReadQuery(DoorsVersion.V97, modules: Enumerable.Range(0, 129).Select(i => "/P/M" + i)));
        }
        [TestMethod]
        public async Task HistoricalSetMembersSurviveCurrentDefinitionChanges()
        {
            var read = Parse(); var request = Request(read); var context = await new NativeBaselineSetVerificationMetadataProvider().BindAsync(request, read, CancellationToken.None);
            Assert.AreEqual(2, context.Modules.Count); Assert.IsTrue(context.SelectSetMembersByDefault); Assert.IsFalse(context.MetadataComplete);
            var historical = context.Modules.Single(m => m.ModuleIdentity == "B"); Assert.IsTrue(historical.InSet); Assert.IsFalse(historical.InDefinition); Assert.AreEqual(3, historical.Version.Major); Assert.AreEqual(BaselineVerificationReadState.Verified, historical.State);
        }
        [TestMethod]
        public async Task UnseenMemberRemainsUnknownInsteadOfMissing()
        {
            var read = Parse(); read.Sets.Single().Members.RemoveAt(0);
            var context = await new NativeBaselineSetVerificationMetadataProvider().BindAsync(Request(read), read, CancellationToken.None);
            var absent = context.Modules.Single(m => m.ModuleIdentity == "A"); Assert.AreEqual(BaselineVerificationReadState.Unknown, absent.State); Assert.IsNull(absent.Version);
        }
        [TestMethod]
        public async Task KnownDeniedMemberIsReportedAsAccessDenied()
        {
            var read = Parse(); read.Sets.Single().Members.RemoveAt(0); read.ModuleStates.Single().CanRead = BaselineCheckState.Failed;
            var context = await new NativeBaselineSetVerificationMetadataProvider().BindAsync(Request(read), read, CancellationToken.None);
            Assert.AreEqual(BaselineVerificationReadState.AccessDenied, context.Modules.Single(m => m.ModuleIdentity == "A").State);
        }
        [TestMethod]
        public async Task MetadataRejectsSessionChangeDuringLinkScopeRead()
        {
            var read = Parse(); var changed = Parse(); changed.SessionId = "43";
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new NativeBaselineSetVerificationMetadataProvider(new Observation(changed)).BindAsync(Request(read), read, CancellationToken.None));
        }
        [TestMethod]
        public async Task ReadOnlyAdapterNeverDispatchesWrites()
        {
            var source = new Observation(Parse()); var adapter = new DxlReadOnlyBaselineSetAdapter(source);
            var result = await adapter.ExecuteAsync(null, CancellationToken.None); Assert.AreEqual(BaselineSetJobStatus.Blocked, result.Status); Assert.IsFalse(adapter.WritesValidated); Assert.AreEqual(0, source.Calls);
        }
        [TestMethod]
        public async Task ImpactBindsActualEnvironmentSessionModuleAndLinkIdentities()
        {
            var read = Parse(); var selection = Selection();
            var context = await new DxlImpactReadContextProvider(new Observation(read)).ObserveAsync(selection, CancellationToken.None);
            Assert.AreEqual("42", context.SessionId); Assert.AreEqual("9.7.2.9", context.ClientVersion); Assert.AreEqual("LINK-QUID", context.LinkModuleIdentities["/P/Links"]); Assert.IsTrue(context.Modules.Single().Version.IsCurrent);
            selection.EnvironmentId = "other"; await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new DxlImpactReadContextProvider(new Observation(read)).ObserveAsync(selection, CancellationToken.None));
        }
        [TestMethod]
        public async Task ImpactIdentityMismatchNeverSubstitutesCurrentModule()
        {
            var read = Parse(); read.ModuleStates.Single().ModuleIdentity = "replacement";
            var context = await new DxlImpactReadContextProvider(new Observation(read)).ObserveAsync(Selection(), CancellationToken.None); Assert.IsNull(context.Modules.Single().Version);
        }
        [TestMethod]
        public async Task PreflightReturnsObservedFactsWithoutFabricatingReliableRevisions()
        {
            var module = new BaselineModuleReference("db", "A", "/P/A"); var selected = new BaselineModuleSelection(module);
            var request = new BulkBaselinePreflightRequest(new BulkBaselineRequest(new[] { selected }));
            var result = await new DxlBaselinePreflightStateReader(new Observation(Parse())).ReadAsync(new BaselinePreflightProbe(request, selected), CancellationToken.None);
            Assert.AreEqual(BaselineCheckState.Passed, result.Facts.ReadAccess); Assert.AreEqual(BaselineCheckState.Failed, result.Facts.CreateAccess); Assert.AreEqual(BaselineEditState.ReadOnly, result.Facts.EditState);
            Assert.AreEqual(BaselineCheckState.Unknown, result.Facts.RevisionsReliable); Assert.IsNull(result.Facts.ContentRevision); Assert.AreEqual(BaselineTargetAvailability.Unknown, result.Facts.TargetAvailability);
            Assert.AreEqual(BaselineCheckState.Passed, result.Facts.SessionValid); StringAssert.Contains(result.Facts.Evidence, "Configured");
        }
        [TestMethod]
        public async Task PreflightIdentityMismatchInvalidatesPermissionsAndModuleType()
        {
            var read = Parse(); read.ModuleStates.Single().ModuleIdentity = "replacement";
            var selected = new BaselineModuleSelection(new BaselineModuleReference("db", "A", "/P/A")); var request = new BulkBaselinePreflightRequest(new BulkBaselineRequest(new[] { selected }));
            var facts = (await new DxlBaselinePreflightStateReader(new Observation(read)).ReadAsync(new BaselinePreflightProbe(request, selected), CancellationToken.None)).Facts;
            Assert.AreEqual(BaselineCheckState.Unknown, facts.ReadAccess); Assert.AreEqual(DoorsItemKind.Unknown, facts.Type); StringAssert.Contains(facts.Evidence, "identity mismatch");
        }
        [TestMethod]
        public async Task CancelledReadOnlyExecutionDoesNotContactSource()
        {
            var source = new Observation(Parse()); using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new DxlReadOnlyBaselineSetAdapter(source).ExecuteAsync(null, cancel.Token)); }
            Assert.AreEqual(0, source.Calls);
        }
        [TestMethod]
        public async Task ThousandSelectedModulesUseBoundedReadOnlyChunks()
        {
            var source = new ChunkObservation(); var adapter = new DxlReadOnlyBaselineSetAdapter(source);
            var request = new BaselineSetRequest(BaselineSetAction.AddDefinitionModules, "/P", modules: Enumerable.Range(0, 1000).Select(i => new BaselineModuleReference("db", "Q" + i, "/P/M" + i)));
            var read = await adapter.InspectAsync(request, CancellationToken.None);
            Assert.AreEqual(1000, read.ModuleStates.Count); Assert.AreEqual(1000, read.ModuleStates.Select(s => s.ModuleIdentity).Distinct().Count()); Assert.AreEqual(8, source.Calls); Assert.IsTrue(source.MaximumModules <= 128); Assert.IsFalse(adapter.WritesValidated);
        }
        [TestMethod]
        public void NewBundledScriptsContainOnlyReadAndResourceCleanupOperations()
        {
            foreach (string name in new[] { "native-read-snapshot.dxl", "readonly-link-versions.dxl" })
            {
                string code = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", name)), @"//[^\r\n]*", "");
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(code, @"\b(save|edit|lock|unlock|addBaselines|setName|setDescription|softDelete|purge)\s*\("), name);
                Assert.IsFalse(code.Contains("targetVersion(incoming)")); Assert.IsFalse(code.Contains("read(bsd)"));
            }
        }
        private sealed class ChunkObservation : INativeReadObservationSource
        {
            internal int Calls, MaximumModules;
            public Task<BaselineSetInspection> ReadAsync(NativeReadQuery query, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Calls++; MaximumModules = Math.Max(MaximumModules, query.Modules.Count); var read = Parse(); read.ModuleStates.Clear();
                foreach (string path in query.Modules) read.ModuleStates.Add(new NativeModuleReadState { FullPath = path, ModuleIdentity = "Q" + path.Substring("/P/M".Length) });
                return Task.FromResult(read);
            }
        }
        internal static BaselineSetVerificationRequest Request(BaselineSetInspection read) => new BaselineSetVerificationRequest("observed", "/P", read.Definitions.Single().Id, read.Sets.Single().Id, new TraceabilityPolicy("read"), new[] { "/P/Links" });
        internal static ImpactSnapshotSelection Selection() => new ImpactSnapshotSelection { EnvironmentId = "db", RepositoryId = "db", Modules = { new BaselineVersionReference("db", "A", "/P/A", 0, 0, isCurrent: true) }, LinkModulePaths = { "/P/Links" } };
        internal sealed class Observation : INativeReadObservationSource
        { private readonly BaselineSetInspection read; internal int Calls; internal Observation(BaselineSetInspection read) { this.read = read; } public Task<BaselineSetInspection> ReadAsync(NativeReadQuery query, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(read); } }
    }
}
