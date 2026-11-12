using System;
using System.IO;
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
    public class RequirementWriteTests
    {
        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(" ")]
        [DataRow("Object Text\n")]
        [DataRow("Object Identifier")]
        [DataRow("Absolute Number")]
        [DataRow("Object Number")]
        public void InvalidOrManagedIdentityAttributesAreRejectedBeforeCom(string name)
        {
            Assert.ThrowsException<IntegrationException>(() => new RequirementAttributeChange(name, "value"));
        }

        [TestMethod]
        public void RequestsValidateAndFreezeChangesPositionAndMandatoryNames()
        {
            var changes = new[] { new RequirementAttributeChange("Object Text", "Text") };
            var required = new[] { "Object Text" };
            var request = new RequirementWriteRequest(new RequirementWriteTarget("/P/M"), changes, requiredAttributes: required);
            changes[0] = new RequirementAttributeChange("Priority", "High"); required[0] = "Priority";
            Assert.AreEqual("Object Text", request.Changes[0].Name);
            Assert.AreEqual("Object Text", request.RequiredAttributes[0]);
            Assert.ThrowsException<IntegrationException>(() => new RequirementWriteRequest(request.Target, request.Changes, 0));
            Assert.ThrowsException<IntegrationException>(() => new RequirementWriteRequest(request.Target, request.Changes, 3, new RequirementPosition(RequirementPositionKind.After, 3)));
            Assert.ThrowsException<IntegrationException>(() => new RequirementWriteRequest(request.Target, request.Changes, 3, new RequirementPosition(RequirementPositionKind.FirstInModule)));
            Assert.ThrowsException<IntegrationException>(() => new RequirementWriteRequest(request.Target, request.Changes.Concat(request.Changes)));
            Assert.ThrowsException<IntegrationException>(() => new RequirementWriteRequest(request.Target, request.Changes, requiredAttributes: new[] { "Priority", "Priority" }));
            Assert.ThrowsException<IntegrationException>(() => new RequirementWriteTarget("/P/M", timeout: TimeSpan.FromHours(1)));
            Assert.ThrowsException<IntegrationException>(() => new RequirementPosition(RequirementPositionKind.After));
            Assert.ThrowsException<IntegrationException>(() => new RequirementAttributeChange("Object Text", "\uD800"));
            Assert.ThrowsException<IntegrationException>(() => new RequirementAttributeChange("Object Text", new string('x', 65537)));
            Assert.ThrowsException<IntegrationException>(() => new RequirementAttributeChange("Object Text", "not RTF", RequirementWriteFormat.DoorsRichText, "text"));
        }

        [TestMethod]
        public async Task ModuleAttributeCatalogIncludesEnumTypeAndDxlMetadataWithoutClaimingRequiredRules()
        {
            using (var context = new RequirementWriteTestContext())
            {
                var schema = await context.Service.GetAttributeSchemaAsync(context.Request.Target);
                Assert.IsTrue(schema.IsAvailable);
                Assert.AreEqual("Enumeration", schema.Attributes.Single(a => a.Name == "Priority").BaseType);
                CollectionAssert.AreEqual(new[] { "Low", "High" }, schema.Attributes.Single(a => a.Name == "Priority").EnumerationValues.ToArray());
                Assert.IsTrue(schema.Attributes.Single(a => a.Name == "Calculated").IsGeneratedByDxl);
                Assert.AreEqual("NotDeclaredByStandardMetadata", schema.Attributes.First().RequiredRule);
                Assert.IsTrue(schema.Issues.Any(i => i.Code == RequirementWriteIssueCode.RequiredRulesNotDeclared));
                Assert.AreEqual("schema", context.Modes.Single());
                Assert.AreEqual(0, context.Confirmation.Calls);
                Assert.AreEqual(0, context.Audit.Entries.Count);
            }
        }

        [TestMethod]
        public async Task PreviewReturnsCatalogAndBoundFingerprintButCannotBeCommittedDirectly()
        {
            using (var context = new RequirementWriteTestContext())
            {
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Assert.IsTrue(preview.CanApply);
                Assert.AreEqual(64, preview.ApprovalFingerprint.Length);
                Assert.IsTrue(preview.ExpiresAtUtc > DateTime.UtcNow);
                Assert.IsTrue(preview.Inspection.Steps.All(s => s.Status == RequirementWriteStepStatus.Skipped));
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Gateway.CommitAsync(preview, CancellationToken.None));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteApprovalRequired, ex.Code);
                Assert.AreEqual("preview", context.Modes.Single());
            }
        }

        [TestMethod]
        public async Task DeclinedDefaultConfirmationAndForeignOrConsumedPlansNeverDispatchCommit()
        {
            using (var context = new RequirementWriteTestContext())
            {
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                var denied = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteApprovalRequired, denied.Code);
                context.Confirmation.Approved = true;
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                var fresh = await context.Service.PreviewWriteAsync(context.Request);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.NewService().ApplyWriteAsync(fresh));
                Assert.IsFalse(context.Modes.Contains("commit"));
                Assert.IsFalse(await new RejectRequirementWriteConfirmation().ConfirmAsync(fresh, CancellationToken.None));
            }
        }

        [DataTestMethod]
        [DataRow(RequirementWriteIssueCode.Locked)]
        [DataRow(RequirementWriteIssueCode.PermissionDenied)]
        [DataRow(RequirementWriteIssueCode.AttributeMissing)]
        [DataRow(RequirementWriteIssueCode.AttributeNotWritable)]
        [DataRow(RequirementWriteIssueCode.UnsupportedAttribute)]
        [DataRow(RequirementWriteIssueCode.InvalidValue)]
        [DataRow(RequirementWriteIssueCode.RequiredValueMissing)]
        [DataRow(RequirementWriteIssueCode.InvalidHierarchy)]
        [DataRow(RequirementWriteIssueCode.AlreadyOpen)]
        [DataRow(RequirementWriteIssueCode.BaselineNotWritable)]
        [DataRow(RequirementWriteIssueCode.StateTooLarge)]
        public async Task FailedPreflightNeverAsksForApprovalOrSendsAWrite(RequirementWriteIssueCode code)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Issues.Add(new RequirementWriteIssue(RequirementWriteIssueSeverity.Error, code));
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Assert.IsFalse(preview.CanApply);
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteBlocked, ex.Code);
                Assert.AreEqual(0, context.Confirmation.Calls);
                Assert.AreEqual("preview", context.Modes.Single());
            }
        }

        [DataTestMethod]
        [DataRow("Missing", "value", false)]
        [DataRow("Calculated", "value", false)]
        [DataRow("Priority", "Unknown", false)]
        [DataRow("Priority", "High\nLow", false)]
        [DataRow("Priority", "High", true)]
        [DataRow("Cost", "5", true)]
        public async Task ManagedPreflightUsesTheActualModuleAttributeCatalog(string attribute, string value, bool valid)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Request = new RequirementWriteRequest(context.Request.Target, new[] { new RequirementAttributeChange(attribute, value) });
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Assert.AreEqual(valid, preview.CanApply);
                Assert.AreEqual(0, context.Confirmation.Calls);
                Assert.IsFalse(context.Modes.Contains("commit"));
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task ConfiguredRequiredAttributesAreValidatedAgainstSuppliedValuesAndSchema(bool missing)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Request = new RequirementWriteRequest(context.Request.Target,
                    new[] { new RequirementAttributeChange("Object Text", missing ? "\r\n \t" : "valid") }, requiredAttributes: new[] { "Object Text" });
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Assert.AreEqual(!missing, preview.CanApply);
                Assert.AreEqual(missing, preview.Inspection.Schema.Issues.Any(i => i.Code == RequirementWriteIssueCode.RequiredValueMissing));
            }
        }

        [DataTestMethod]
        [DataRow(RequirementPositionKind.FirstInModule, false)]
        [DataRow(RequirementPositionKind.After, false)]
        [DataRow(RequirementPositionKind.FirstChild, false)]
        [DataRow(RequirementPositionKind.LastChild, false)]
        [DataRow(RequirementPositionKind.Keep, true)]
        [DataRow(RequirementPositionKind.After, true)]
        [DataRow(RequirementPositionKind.FirstChild, true)]
        [DataRow(RequirementPositionKind.LastChild, true)]
        public async Task CreationUpdateAndPositioningRequireOneApprovalAndReturnStableObjectIdentity(RequirementPositionKind kind, bool update)
        {
            using (var context = new RequirementWriteTestContext())
            {
                int? anchor = kind == RequirementPositionKind.Keep || kind == RequirementPositionKind.FirstInModule ? (int?)null : 7;
                context.Request = new RequirementWriteRequest(context.Request.Target, context.Request.Changes,
                    update ? (int?)42 : null, new RequirementPosition(kind, anchor));
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                var result = await context.Service.ApplyWriteAsync(preview);
                Assert.IsTrue(result.Succeeded);
                Assert.AreEqual(42, result.AbsoluteNumber);
                Assert.AreEqual("REQ-42", result.ObjectIdentifier);
                Assert.AreEqual(RequirementWritePersistence.Saved, result.Persistence);
                CollectionAssert.AreEqual(new[] { "preview", "commit" }, context.Modes);
                Assert.AreEqual(1, context.Confirmation.Calls);
                Assert.IsTrue(result.Steps.All(s => s.Status == RequirementWriteStepStatus.Applied));
                Assert.IsTrue(context.Audit.Entries.Any(e => e.Phase == "CommitAuthorized" && e.Fingerprint == preview.ApprovalFingerprint));
                Assert.IsTrue(context.Audit.Entries.Any(e => e.Phase == "CommitCompleted" && e.Result == result));
                var audit = context.Audit.Entries.Single(e => e.Phase == "CommitCompleted");
                Assert.AreEqual(update ? kind == RequirementPositionKind.Keep ? "Update" : "UpdateAndMove" : "Create", audit.Action);
                Assert.AreEqual(context.Request.AbsoluteNumber, audit.RequestedAbsoluteNumber);
                Assert.AreEqual(kind.ToString(), audit.Position);
                Assert.AreEqual(anchor, audit.AnchorAbsoluteNumber);
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(1, context.Modes.Count(m => m == "commit"));
            }
        }

        [TestMethod]
        public async Task APositionOnlyUpdateDoesNotWriteAnyAttribute()
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Request = new RequirementWriteRequest(context.Request.Target, null, 42, new RequirementPosition(RequirementPositionKind.After, 7));
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                var result = await context.Service.ApplyWriteAsync(preview);
                Assert.IsTrue(result.Succeeded);
                Assert.IsFalse(result.Steps.Any(s => s.Kind == RequirementWriteStepKind.Attribute || s.Kind == RequirementWriteStepKind.Create));
            }
        }

        [DataTestMethod]
        [DataRow("state")]
        [DataRow("database")]
        [DataRow("actor")]
        [DataRow("module")]
        public async Task RelevantChangesAfterApprovalAreReportedBeforeAnyAppliedStep(string change)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                context.Confirmation.BeforeReturn = _ =>
                {
                    if (change == "state") context.State = "new state";
                    if (change == "database") context.Database = "other database";
                    if (change == "actor") context.Actor = "other user";
                    if (change == "module") { context.ModuleId = "replacement"; context.QualifiedId = "db-1:replacement"; }
                };
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                var result = await context.Service.ApplyWriteAsync(preview);
                Assert.AreEqual(RequirementWriteOutcome.ContextChanged, result.Outcome);
                Assert.IsFalse(result.Succeeded);
                Assert.IsTrue(result.Steps.All(s => s.Status == RequirementWriteStepStatus.Skipped));
            }
        }

        [DataTestMethod]
        [DataRow("ApprovalRequested")]
        [DataRow("CommitAuthorized")]
        [DataRow("CommitDispatchRequested")]
        public async Task UnavailableAuditBlocksBeforeDispatchAndDoesNotMakeThePlanReplayable(string phase)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                context.Audit.FailPhase = phase;
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteAuditFailed, ex.Code);
                context.Audit.FailPhase = null;
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Gateway.CommitAsync(preview, CancellationToken.None));
                Assert.IsFalse(context.Modes.Contains("commit"));
            }
        }

        [TestMethod]
        public async Task PostCommitAuditFailureReportsTheActualPersistenceWithoutRetry()
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                context.Audit.FailPhase = "CommitCompleted";
                var result = await context.Service.ApplyWriteAsync(preview);
                Assert.AreEqual(RequirementWriteOutcome.Applied, result.Outcome);
                Assert.AreEqual(RequirementWritePersistence.Saved, result.Persistence);
                Assert.IsFalse(result.AuditRecorded);
                Assert.IsFalse(result.Succeeded);
                Assert.AreEqual(1, context.Modes.Count(m => m == "commit"));
            }
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public async Task PartialFailureRetainsStepResultsAndUnsavedModuleForReview(int failedStep)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                context.FailStep = failedStep;
                var result = await context.Service.ApplyWriteAsync(preview);
                Assert.AreEqual(RequirementWriteOutcome.PartialFailure, result.Outcome);
                Assert.AreEqual(RequirementWriteStepStatus.Failed, result.Steps[failedStep].Status);
                Assert.IsTrue(result.Steps.Take(failedStep).All(s => s.Status == RequirementWriteStepStatus.Applied));
                Assert.IsTrue(result.Steps.Skip(failedStep + 1).All(s => s.Status == RequirementWriteStepStatus.Skipped));
                Assert.AreEqual(RequirementWritePersistence.NotAttempted, result.Persistence);
                Assert.AreEqual(RequirementWriteCleanup.LeftOpenForReview, result.Cleanup);
                Assert.IsFalse(result.Succeeded);
            }
        }

        [TestMethod]
        public async Task CancellationDuringApprovalNeverDispatchesAndRevokesThePlan()
        {
            using (var context = new RequirementWriteTestContext())
            using (var token = new CancellationTokenSource())
            {
                context.Confirmation.Approved = true;
                context.Confirmation.BeforeReturn = _ => token.Cancel();
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await context.Service.ApplyWriteAsync(preview, token.Token));
                Assert.IsFalse(context.Modes.Contains("commit"));
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Gateway.CommitAsync(preview, CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task SoftTimeoutLeavesGateOwnedAndExposesAuditedLateCompletionWithoutResubmission()
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Request = new RequirementWriteRequest(new RequirementWriteTarget("/P/M", timeout: TimeSpan.FromMilliseconds(150)), context.Request.Changes);
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                int cleanupCalls = context.Transport.Sessions.Session.Calls.Count(c => c == "Dispose");
                context.Transport.Sessions.Session.BlockRun = true;
                var ex = await Assert.ThrowsExceptionAsync<RequirementWriteUncertainException>(async () => await context.Service.ApplyWriteAsync(preview));
                Assert.IsTrue(context.Transport.Gate.IsBusy);
                Assert.AreEqual(cleanupCalls, context.Transport.Sessions.Session.Calls.Count(c => c == "Dispose"));
                Assert.IsFalse(context.Transport.Executor.ActiveOperation.Completion.IsCompleted);
                var uncertainAudit = context.Audit.Entries.Single(e => e.Phase == "OutcomeUnknown");
                Assert.AreEqual(preview.Inspection.Schema.UserName, uncertainAudit.Actor);
                Assert.AreEqual(preview.Inspection.Schema.DatabaseId, uncertainAudit.DatabaseId);
                Assert.AreEqual(preview.Inspection.Schema.QualifiedModuleId, uncertainAudit.ModuleId);
                context.Transport.Sessions.Session.ReleaseRun.Set();
                var late = await ex.Completion;
                Assert.IsTrue(late.Succeeded);
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.AreEqual(1, context.Modes.Count(m => m == "commit"));
                Assert.IsTrue(context.Audit.Entries.Any(e => e.Phase == "CommitCompleted"));
            }
        }

        [TestMethod]
        public async Task RichTextUnicodeAndInjectionLikeValuesAreTransportedAsDataAndIncludedInTheReview()
        {
            using (var context = new RequirementWriteTestContext())
            {
                string text = "\");\r\nhalt\nTürkçe 👋\\path\tvalue";
                context.Request = new RequirementWriteRequest(context.Request.Target, new[]
                { new RequirementAttributeChange("Object Text", text), new RequirementAttributeChange("Notes", "{\\b Bold}", RequirementWriteFormat.DoorsRichText, "Bold") });
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Assert.AreEqual(context.Request.AttributePayload, DiscoveryTestContext.Parameter(context.Transport.Sessions.Session.Script, "attributes"));
                var original = preview.ApprovalFingerprint;
                context.Request = new RequirementWriteRequest(context.Request.Target, new[] { new RequirementAttributeChange("Object Text", "different") });
                var changed = await context.Service.PreviewWriteAsync(context.Request);
                Assert.AreNotEqual(original, changed.ApprovalFingerprint);
                Assert.IsFalse(context.Modes.Contains("commit"));
            }
        }

        [TestMethod]
        public void DurableAuditUsesJsonLinesAndNeverStoresRequirementPayloads()
        {
            using (var context = new ExecutorTestContext())
            {
                var options = new RequirementWriteAuditOptions(Path.Combine(context.Root, "audit", "writes.jsonl"));
                var sink = new FileRequirementWriteAuditSink(options);
                sink.Record(new RequirementWriteAuditEntry("ApprovalRequested", Guid.NewGuid(), Guid.NewGuid(), "abc123", "/P/Ünicode 👋", "user\nname", "db", "module"));
                var request = new RequirementWriteRequest(new RequirementWriteTarget("/P/M"), new[] { new RequirementAttributeChange("Object Text", "PRIVATE_CONTENT") });
                sink.Record(new RequirementWriteAuditEntry("CommitAuthorized", Guid.NewGuid(), Guid.NewGuid(), "abc123", "/P/M", request: request));
                var lines = File.ReadAllLines(options.Path);
                Assert.AreEqual(2, lines.Length);
                Assert.IsTrue(lines.All(line => line.StartsWith("{") && line.EndsWith("}")));
                Assert.IsFalse(File.ReadAllText(options.Path).Contains("PRIVATE_CONTENT"));
                StringAssert.Contains(lines[0], "timestampUtc");
                StringAssert.Contains(lines[0], "fingerprint");
                StringAssert.Contains(lines[1], "requestedSha256");
                StringAssert.Contains(lines[1], "\"action\":\"Create\"");
                StringAssert.Contains(lines[1], "\"position\":\"FirstInModule\"");
            }
        }

        [TestMethod]
        public void IncompleteAuditTailBlocksAppendWithoutOverwritingTheEvidence()
        {
            using (var context = new ExecutorTestContext())
            {
                var path = Path.Combine(context.Root, "audit.jsonl");
                File.WriteAllText(path, "{\"partial\":");
                var sink = new FileRequirementWriteAuditSink(new RequirementWriteAuditOptions(path));
                Assert.ThrowsException<InvalidDataException>(() => sink.Record(new RequirementWriteAuditEntry("CommitAuthorized", Guid.NewGuid(), Guid.Empty, "fingerprint", "/P/M")));
                Assert.AreEqual("{\"partial\":", File.ReadAllText(path));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ExpiredPlansBeforeOrDuringApprovalCannotDispatch(bool duringApproval)
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                Action expire = () => typeof(RequirementWritePreview).GetField("<ExpiresAtUtc>k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(preview, DateTime.UtcNow.AddMinutes(-1));
                if (duringApproval) context.Confirmation.BeforeReturn = _ => expire();
                else expire();
                var ex = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteApprovalRequired, ex.Code);
                Assert.IsFalse(context.Modes.Contains("commit"));
                Assert.IsFalse(preview.TryBeginCommit());
            }
        }

        [TestMethod]
        public async Task BusyGateIsADefinitePreDispatchFailureAndNativeFailureAfterDispatchIsUncertain()
        {
            using (var context = new RequirementWriteTestContext())
            {
                context.Confirmation.Approved = true;
                var preview = await context.Service.PreviewWriteAsync(context.Request);
                using (context.Transport.Gate.Enter())
                {
                    var blocked = await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await context.Service.ApplyWriteAsync(preview));
                    Assert.AreEqual(IntegrationErrorCode.DoorsOperationBusy, blocked.Code);
                    Assert.IsFalse(blocked is RequirementWriteUncertainException);
                }
                Assert.IsFalse(context.Modes.Contains("commit"));
                var next = await context.Service.PreviewWriteAsync(context.Request);
                context.Transport.Sessions.Session.RunFailure = new System.Runtime.InteropServices.COMException("PRIVATE_CONTENT");
                var uncertain = await Assert.ThrowsExceptionAsync<RequirementWriteUncertainException>(async () => await context.Service.ApplyWriteAsync(next));
                await Assert.ThrowsExceptionAsync<IntegrationException>(async () => await uncertain.Completion);
                Assert.IsFalse(context.Transport.Gate.IsBusy);
                Assert.AreEqual(1, context.Modes.Count(m => m == "commit"));
            }
        }

        [TestMethod]
        public void BothHostsComposeTheWriterAndDefaultToRejectingUnreviewedWrites()
        {
            foreach (bool desktop in new[] { false, true })
            {
                var services = new ServiceCollection();
                if (desktop) DoorsIntegration.Desktop.ServiceCollectionExtensions.AddDoorsDesktop(services);
                else DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                {
                    Assert.IsInstanceOfType(provider.GetRequiredService<IRequirementService>(), typeof(RequirementService));
                    Assert.IsInstanceOfType(provider.GetRequiredService<IRequirementWriteGateway>(), typeof(DxlRequirementWriteGateway));
                    Assert.IsInstanceOfType(provider.GetRequiredService<IRequirementWriteConfirmation>(), desktop ? typeof(DoorsIntegration.Desktop.DesktopWriteConfirmation) : typeof(RejectRequirementWriteConfirmation));
                }
            }
        }
    }
}
