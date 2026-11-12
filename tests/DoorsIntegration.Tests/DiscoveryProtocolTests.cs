using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DiscoveryProtocolTests
    {
        [DataTestMethod]
        [DataRow("")]
        [DataRow("relative/path")]
        [DataRow("//P")]
        [DataRow("/P//M")]
        [DataRow("/P/../M")]
        [DataRow("/./P")]
        [DataRow("/P/\nM")]
        public void InvalidScopesAreRejectedBeforeAnyDxl(string path)
        {
            Assert.AreEqual(IntegrationErrorCode.InvalidConfiguration,
                Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope(path)).Code);
        }

        [TestMethod]
        public void ScopeNormalizesTrailingSlashButRejectsInvalidUnicodeAndLimits()
        {
            Assert.AreEqual("/P", new DiscoveryScope("/P/").RootPath);
            Assert.AreEqual("/", new DiscoveryScope().RootPath);
            Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope("/P/" + '\uD800'));
            Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope(maxItems: 0));
            Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope(maxItems: 10001));
            Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope(maxDepth: 0));
            Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope(maxFolders: 0));
            Assert.ThrowsException<IntegrationException>(() => new DiscoveryScope(timeout: TimeSpan.Zero));
        }

        [DataTestMethod]
        [DataRow("wrong-id")]
        [DataRow("wrong-scope")]
        [DataRow("outside-scope")]
        [DataRow("grandchild")]
        [DataRow("duplicate")]
        [DataRow("wrong-name")]
        [DataRow("unknown-type")]
        [DataRow("numeric-access")]
        [DataRow("numeric-warning")]
        [DataRow("unknown-warning")]
        [DataRow("wrong-count")]
        [DataRow("missing-end")]
        [DataRow("missing-newline")]
        [DataRow("invalid-escape")]
        [DataRow("hidden-without-warning")]
        public void MalformedOrUntrustedDiscoveryPayloadIsRejected(string fault)
        {
            var id = Guid.NewGuid();
            string child = DiscoveryTestContext.Row("I", "/P/M", "Formal");
            string text = DiscoveryTestContext.Batch(id, "/P", "Project", children: new[] { child });
            switch (fault)
            {
                case "wrong-id": text = text.Replace(id.ToString("N"), Guid.NewGuid().ToString("N")); break;
                case "wrong-scope": text = text.Replace("S\t/P\tP", "S\t/Q\tQ"); break;
                case "outside-scope": text = text.Replace("/P/M", "/Other/M"); break;
                case "grandchild": text = text.Replace("/P/M", "/P/F/M"); break;
                case "duplicate": text = text.Replace(child, child + child).Replace("END\t1", "END\t2"); break;
                case "wrong-name": text = text.Replace("\tM\tFormal", "\tDifferent\tFormal"); break;
                case "unknown-type": text = text.Replace("\tFormal\t", "\tArbitrary\t"); break;
                case "numeric-access": text = text.Replace("Readable", "0"); break;
                case "numeric-warning": text = text.Replace("END\t", "W\t0\nEND\t"); break;
                case "unknown-warning": text = text.Replace("END\t", "W\tPRIVATE_MESSAGE\nEND\t"); break;
                case "wrong-count": text = text.Replace("END\t1", "END\t2"); break;
                case "missing-end": text = text.Replace("END\t1\t0\n", ""); break;
                case "missing-newline": text = text.TrimEnd('\n'); break;
                case "invalid-escape": text = text.Replace("/P/M", "/P/\\x"); break;
                case "hidden-without-warning": text = DiscoveryTestContext.Batch(id, "/P", "Unknown", "DeniedOrNotFound"); break;
            }
            foreach (string lineEnding in new[] { "\n", "\r\n" })
            {
                var failure = Assert.ThrowsException<DiscoveryProtocolException>(() => DiscoveryProtocol.Parse(text.Replace("\n", lineEnding), id, "/P", false));
                Assert.AreEqual(IntegrationErrorCode.DiscoveryProtocolFailure, failure.Code);
                Assert.IsFalse(failure.Message.Contains("PRIVATE_MESSAGE"));
                StringAssert.Contains(failure.Message, "Reason=");
                StringAssert.Contains(failure.Message, "Line=");
            }
        }

        [DataTestMethod]
        [DataRow("\n")]
        [DataRow("\r\n")]
        [DataRow("mixed")]
        public void SupportedRecordLineEndingsPreserveUnicodeAndEscapedContent(string lineEnding)
        {
            var id = Guid.NewGuid();
            const string path = "/P/Özel \"A\"\\n";
            string text = DiscoveryTestContext.Batch(id, "/P", "Project",
                children: new[] { DiscoveryTestContext.Row("I", path, "Formal") });
            text = lineEnding == "mixed" ? text.Replace("\nI\t", "\r\nI\t") : text.Replace("\n", lineEnding);
            var batch = DiscoveryProtocol.Parse(text, id, "/P", false);
            Assert.AreEqual(path, batch.Children.Single().FullPath);
            Assert.AreEqual("Özel \"A\"\\n", batch.Children.Single().Name);
            Assert.AreEqual(DoorsAccessStatus.Readable, batch.Children.Single().AccessStatus);
        }

        [DataTestMethod]
        [DataRow("empty", "MissingResponse", 0)]
        [DataRow("header", "HeaderMismatch", 1)]
        [DataRow("scope", "ScopeMismatch", 2)]
        [DataRow("name", "InvalidItemMetadata", 3)]
        [DataRow("type", "UnknownItemType", 3)]
        [DataRow("count", "CountMismatch", 4)]
        [DataRow("carriage-return", "InvalidLineEnding", 3)]
        [DataRow("double-carriage-return", "InvalidLineEnding", 3)]
        public void RejectionIdentifiesTheCheckAndLineWithoutLeakingPayload(string fault, string reason, int line)
        {
            var id = Guid.NewGuid();
            string text = DiscoveryTestContext.Batch(id, "/P", "Project",
                children: new[] { DiscoveryTestContext.Row("I", "/P/PRIVATE_CONTENT", "Formal") });
            switch (fault)
            {
                case "empty": text = ""; break;
                case "header": text = text.Replace(id.ToString("N"), Guid.NewGuid().ToString("N")); break;
                case "scope": text = text.Replace("S\t/P\tP", "S\t/Q\tQ"); break;
                case "name": text = text.Replace("\tPRIVATE_CONTENT\t", "\tPRIVATE_WRONG_NAME\t"); break;
                case "type": text = text.Replace("\tFormal\t", "\tPRIVATE_TYPE\t"); break;
                case "count": text = text.Replace("END\t1", "END\t2"); break;
                case "carriage-return": text = text.Replace("\tFormal", "\r\tFormal"); break;
                case "double-carriage-return": text = text.Replace("Formal\tReadable\n", "Formal\tReadable\r\r\n"); break;
            }
            var failure = Assert.ThrowsException<DiscoveryProtocolException>(() => DiscoveryProtocol.Parse(text, id, "/P", false));
            Assert.AreEqual("Reason=" + reason + "; Line=" + line + "; ResponseChars=" + text.Length, failure.DiagnosticMetadata);
            StringAssert.Contains(failure.Message, failure.DiagnosticMetadata);
            Assert.IsFalse(failure.ToString().Contains("PRIVATE_"));
        }

        [TestMethod]
        public void LookupMustNotReturnChildrenAndNonFormalTypeNeedsWarning()
        {
            var id = Guid.NewGuid();
            var withChild = DiscoveryTestContext.Batch(id, "/P", "Project", children: new[] { DiscoveryTestContext.Row("I", "/P/M", "Formal") });
            Assert.ThrowsException<DiscoveryProtocolException>(() => DiscoveryProtocol.Parse(withChild, id, "/P", true));
            Assert.ThrowsException<DiscoveryProtocolException>(() => DiscoveryProtocol.Parse(DiscoveryTestContext.Batch(id, "/P", "Project"), id, "/P", true));
            Assert.ThrowsException<DiscoveryProtocolException>(() => DiscoveryProtocol.Parse(DiscoveryTestContext.Batch(id, "/P/M", "Formal"), id, "/P/M", false));
        }

        [TestMethod]
        public void ExplicitPermissionDenialKeepsKnownMetadataAndDeniedStatus()
        {
            var id = Guid.NewGuid();
            var batch = DiscoveryProtocol.Parse(DiscoveryTestContext.Batch(id, "/P/M", "Formal", "AccessDenied", warning: "AccessRestricted"), id, "/P/M", true);
            Assert.AreEqual(DoorsAccessStatus.AccessDenied, batch.Scope.AccessStatus);
            Assert.AreEqual(DoorsItemKind.FormalModule, batch.Scope.Type);
            Assert.IsTrue(batch.Scope.MetadataVerified);
        }

        [TestMethod]
        public void DistributedScriptIsExternalAndDoesNotOpenModulesOrChangeCurrentFolder()
        {
            string script = File.ReadAllText(new DiscoveryScriptOptions().ScriptPath);
            StringAssert.Contains(script, "for discoveryChild in discoveryFolder do");
            StringAssert.Contains(script, "canRead(discoveryChild)");
            StringAssert.Contains(script, "lastError()");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(script, @"\b(read|edit|share|save|createFolder|createModule)\s*\("));
            Assert.IsFalse(script.Contains("current ="));
            Assert.IsFalse(script.Contains("eval_("));
        }

        [DataTestMethod]
        [DataRow(DiscoveryKind.Hierarchy)]
        [DataRow(DiscoveryKind.Projects)]
        [DataRow(DiscoveryKind.Folders)]
        [DataRow(DiscoveryKind.FormalModules)]
        [DataRow(DiscoveryKind.ModuleByPath)]
        public async Task ServicesForwardScopeAndCancellationToTheReader(DiscoveryKind kind)
        {
            var reader = new RecordingHierarchyReader();
            var scope = new DiscoveryScope("/P", target: DoorsVersion.V95, connectionMode: DoorsConnectionMode.Activate);
            using (var cancellation = new CancellationTokenSource())
            {
                var projects = new ProjectService(reader);
                var modules = new ModuleService(reader);
                switch (kind)
                {
                    case DiscoveryKind.Projects: await projects.DiscoverProjectsAsync(scope, cancellation.Token); break;
                    case DiscoveryKind.Folders: await projects.DiscoverFoldersAsync(scope, cancellation.Token); break;
                    case DiscoveryKind.FormalModules: await modules.DiscoverFormalModulesAsync(scope, cancellation.Token); break;
                    case DiscoveryKind.ModuleByPath: await modules.GetFormalModuleAsync(scope, cancellation.Token); break;
                    default: await projects.DiscoverHierarchyAsync(scope, cancellation.Token); break;
                }
                Assert.AreSame(scope, reader.Scope);
                Assert.AreEqual(kind, reader.Kind);
                Assert.AreEqual(cancellation.Token, reader.Token);
            }
        }
    }
}
