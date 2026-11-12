using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Desktop;
using DoorsIntegration.Dxl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DesktopWorkspaceTests
    {
        [TestMethod]
        public void AllWorkspaceTabsStartOfflineAndWriteButtonsStartDisabled()
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, (window, provider) =>
            {
                foreach (string name in new[] { "RequirementReadPanel", "RequirementWritePanel", "ModuleCreationPanel", "LinkWorkspacePanel", "ExportWorkspacePanel", "DxlWorkspacePanel", "DiagnosticsWorkspacePanel" })
                    WorkspaceUiTest.Select(window, name);
                Assert.AreEqual(0, data.RequirementReads + data.LinkReads + data.SchemaReads + data.WritePreviews + data.ModulePreviews + data.DiagnosticCalls + data.DialogCalls);
                Assert.IsFalse(Find<Button>(window, "ApplyRequirementWrite").Enabled);
                Assert.IsFalse(Find<Button>(window, "ApplyModuleCreation").Enabled);
                Assert.IsTrue(Find<RichTextBox>(window, "RequirementDetails").ReadOnly);
                Assert.IsTrue(Find<DataGridView>(window, "RequirementsGrid").ReadOnly);
                string render = Environment.GetEnvironmentVariable("DOORS_UI_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(render))
                {
                    Directory.CreateDirectory(render);
                    foreach (string name in new[] { "RequirementReadPanel", "RequirementWritePanel", "ModuleCreationPanel", "LinkWorkspacePanel", "ExportWorkspacePanel", "DxlWorkspacePanel", "DiagnosticsWorkspacePanel" })
                    {
                        WorkspaceUiTest.Select(window, name); window.PerformLayout();
                        using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, window.ClientRectangle); bitmap.Save(Path.Combine(render, name + ".png")); }
                    }
                }
                return Task.CompletedTask;
            });
        }

        [DataTestMethod]
        [DataRow("unbound")]
        [DataRow("hidden")]
        [DataRow("expired")]
        [DataRow("cancelled")]
        [DataRow("blocked")]
        public void ConfirmationCannotApproveWithoutALiveVisibleOwnerAndAnEligibleReview(string fault)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var data = new WorkspaceTestData { Approve = true }; var confirmation = new DesktopWriteConfirmation(data);
                using (var window = new Form())
                using (var cancellation = new CancellationTokenSource())
                {
                    IDisposable binding = fault == "unbound" ? null : confirmation.Attach(window);
                    try
                    {
                        if (fault == "cancelled") cancellation.Cancel();
                        var review = new DesktopWriteReview("Review", "/P/M", "Body", fault == "expired" ? DateTime.UtcNow.AddSeconds(-1) : DateTime.UtcNow.AddMinutes(1), fault != "blocked");
                        Assert.IsFalse(confirmation.ReviewAsync(review, cancellation.Token).GetAwaiter().GetResult());
                        Assert.AreEqual(0, data.DialogCalls);
                    }
                    finally { binding?.Dispose(); }
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RequirementEditorUsesCatalogInvalidatesEditedPlansAndRequiresExplicitApproval(bool approved)
        {
            var data = new WorkspaceTestData { Approve = approved };
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                await LoadSchema(window, data);
                var grid = Find<DataGridView>(window, "RequirementAttributeCatalog");
                Assert.AreEqual(4, grid.Rows.Count); Assert.IsTrue(grid.Rows[3].Cells[0].ReadOnly);
                StringAssert.Contains(grid.Rows[2].Cells[1].ToolTipText, "Low | High");
                grid.Rows[0].Cells[0].Value = true; grid.Rows[0].Cells[3].Value = "Initial heading";
                Click(window, "PreviewRequirementWrite"); await WorkspaceUiTest.Idle(window);
                Assert.IsTrue(Find<Button>(window, "ApplyRequirementWrite").Enabled);
                Assert.AreEqual(0, data.WriteCommits); Assert.AreEqual(0, data.DialogCalls);
                grid.Rows[0].Cells[3].Value = "Reviewed Türkçe 👋";
                Assert.IsFalse(Find<Button>(window, "ApplyRequirementWrite").Enabled);
                Click(window, "PreviewRequirementWrite"); await WorkspaceUiTest.Idle(window);
                Click(window, "ApplyRequirementWrite"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(1, data.DialogCalls); Assert.AreEqual(approved ? 1 : 0, data.WriteCommits);
                StringAssert.Contains(data.LastReview.Body, "Reviewed Türkçe 👋");
                Assert.IsFalse(Find<Button>(window, "ApplyRequirementWrite").Enabled);
                Click(window, "ApplyRequirementWrite"); Assert.AreEqual(approved ? 1 : 0, data.WriteCommits);
                if (approved) StringAssert.Contains(Find<RichTextBox>(window, "RequirementWriteReport").Text, "Persistence=Saved");
                else StringAssert.Contains(Find<RichTextBox>(window, "WorkspaceErrorDetails").Text, "RequirementWriteApprovalRequired");
            });
        }

        [TestMethod]
        public void UpdateAndMoveUseObjectIdentityAndShowOldRichTextInTheApproval()
        {
            var data = new WorkspaceTestData { Approve = true };
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                await LoadSchema(window, data);
                Find<ComboBox>(window, "RequirementWriteAction").SelectedItem = "Update";
                Find<NumericUpDown>(window, "RequirementWriteAbsolute").Value = 42;
                var grid = Find<DataGridView>(window, "RequirementAttributeCatalog");
                grid.Rows[1].Cells[0].Value = true; grid.Rows[1].Cells[3].Value = "{\\b NEW}";
                grid.Rows[1].Cells[4].Value = "DoorsRichText"; grid.Rows[1].Cells[5].Value = "NEW";
                Click(window, "PreviewRequirementWrite"); await WorkspaceUiTest.Idle(window); Click(window, "ApplyRequirementWrite"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(42, data.WriteRequest.AbsoluteNumber); Assert.AreEqual(RequirementWriteFormat.DoorsRichText, data.WriteRequest.Changes.Single().Format);
                StringAssert.Contains(data.LastReview.Body, "OLD RICH"); StringAssert.Contains(data.LastReview.Body, "EXPECTED PLAIN:");
                Find<ComboBox>(window, "RequirementWriteAction").SelectedItem = "Move";
                Find<ComboBox>(window, "RequirementWritePosition").SelectedItem = "LastChild";
                Find<NumericUpDown>(window, "RequirementWriteAnchor").Value = 7;
                Click(window, "PreviewRequirementWrite"); await WorkspaceUiTest.Idle(window); Click(window, "ApplyRequirementWrite"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(2, data.WriteCommits); Assert.AreEqual(0, data.WriteRequest.Changes.Count);
                Assert.AreEqual(RequirementPositionKind.LastChild, data.WriteRequest.Position.Kind); Assert.AreEqual(7, data.WriteRequest.Position.AnchorAbsoluteNumber);
            });
        }

        [TestMethod]
        public void PartialWriteShowsAppliedFailedSkippedAndLeavesNoReusablePlan()
        {
            var data = new WorkspaceTestData { Approve = true, PartialWrite = true };
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                await LoadSchema(window, data); var grid = Find<DataGridView>(window, "RequirementAttributeCatalog");
                grid.Rows[0].Cells[0].Value = true; grid.Rows[0].Cells[3].Value = "Heading";
                Click(window, "PreviewRequirementWrite"); await WorkspaceUiTest.Idle(window); Click(window, "ApplyRequirementWrite"); await WorkspaceUiTest.Idle(window);
                string report = Find<RichTextBox>(window, "RequirementWriteReport").Text;
                foreach (string text in new[] { "PartialFailure", "LeftOpenForReview", "Applied", "Failed", "Skipped" }) StringAssert.Contains(report, text);
                Assert.IsFalse(Find<Button>(window, "ApplyRequirementWrite").Enabled); Assert.AreEqual(1, data.WriteCommits);
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FormalModuleCreationRequiresDryRunAndSeparateHumanApproval(bool approved)
        {
            var data = new WorkspaceTestData { Approve = approved };
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "ModuleCreationPanel"); Find<TextBox>(window, "CreateModuleFolder").Text = "/P";
                Find<TextBox>(window, "CreateModuleName").Text = "New Module"; Find<TextBox>(window, "CreateModuleDescription").Text = "Description 👋";
                Click(window, "PreviewModuleCreation"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(0, data.ModuleCommits); Assert.IsTrue(Find<Button>(window, "ApplyModuleCreation").Enabled);
                Find<TextBox>(window, "CreateModuleDescription").Text = "Reviewed description";
                Assert.IsFalse(Find<Button>(window, "ApplyModuleCreation").Enabled);
                Click(window, "PreviewModuleCreation"); await WorkspaceUiTest.Idle(window); Click(window, "ApplyModuleCreation"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(1, data.DialogCalls); Assert.AreEqual(approved ? 1 : 0, data.ModuleCommits);
                Assert.AreEqual("/P/New Module", data.LastReview.Target); StringAssert.Contains(data.LastReview.Body, "Reviewed description");
                if (approved) StringAssert.Contains(Find<RichTextBox>(window, "CreateModuleReport").Text, "test-db:new-module");
            });
        }

        [TestMethod]
        public void RequirementReadPassesBaselinePoliciesAndPagesTheRetainedSnapshot()
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "RequirementReadPanel"); Find<TextBox>(window, "ReadModulePath").Text = "/P/M";
                Find<TextBox>(window, "ReadCustomAttributes").Text = "Priority\r\nOwner"; Find<CheckBox>(window, "RequirementsBaselineEnabled").Checked = true;
                Find<NumericUpDown>(window, "RequirementsBaselineMajor").Value = 2; Find<TextBox>(window, "RequirementsBaselineSuffix").Text = "review";
                Find<ComboBox>(window, "ReadDeletedMode").SelectedItem = "MetadataOnly"; Find<NumericUpDown>(window, "ReadTimeoutSeconds").Value = 240;
                Click(window, "ReadRequirements"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(2, data.ReadRequest.Baseline.Major); Assert.AreEqual("review", data.ReadRequest.Baseline.Suffix);
                Assert.AreEqual(DeletedObjectMode.MetadataOnly, data.ReadRequest.DeletedObjects); Assert.AreEqual(TimeSpan.FromMinutes(4), data.ReadRequest.Timeout);
                CollectionAssert.AreEqual(new[] { "Priority", "Owner" }, data.ReadRequest.CustomAttributes.ToArray());
                var grid = Find<DataGridView>(window, "RequirementsGrid"); Assert.AreEqual(100, grid.Rows.Count);
                Assert.IsTrue(((string)grid.Rows[0].Cells[7].Value).Length < 400);
                Find<NumericUpDown>(window, "RequirementPage").Value = 3; Click(window, "RequirementsShowPage"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(51, grid.Rows.Count); Assert.AreEqual("201", grid.Rows[0].Cells[0].Value);
                Click(window, "RequirementShowDetails"); await WorkspaceUiTest.Idle(window);
                StringAssert.Contains(Find<RichTextBox>(window, "RequirementDetails").Text, "RAW OLE");
            });
        }

        [TestMethod]
        public void LinkScreenPreservesUnresolvedEndpointsAndExplicitSurrogateMapping()
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "LinkWorkspacePanel"); Find<TextBox>(window, "LinkAnchorModule").Text = "/P/M"; Find<TextBox>(window, "LinkModulePath").Text = "/P/Links";
                Find<ComboBox>(window, "LinkDirection").SelectedItem = "Incoming"; Find<CheckBox>(window, "LinksUseSurrogate").Checked = true;
                Find<TextBox>(window, "SurrogateMarkerAttribute").Text = "Kind"; Find<TextBox>(window, "SurrogateMarkerValue").Text = "Surrogate";
                Find<TextBox>(window, "SurrogateReferenceAttribute").Text = "External ID"; Click(window, "ReadLinks"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(LinkDirection.Incoming, data.LinkRequest.Direction); Assert.AreEqual("External ID", data.LinkRequest.SurrogateMapping.ReferenceAttribute);
                var grid = Find<DataGridView>(window, "LinksGrid"); Assert.AreEqual(1, grid.Rows.Count); StringAssert.Contains((string)grid.Rows[0].Cells[6].Value, "EndpointUnresolved");
                Click(window, "LinkShowDetails"); await WorkspaceUiTest.Idle(window);
                StringAssert.Contains(Find<RichTextBox>(window, "LinkDetails").Text, "AccessDenied");
            });
        }

        [DataTestMethod]
        [DataRow(ExportFormat.Json, "*.json")]
        [DataRow(ExportFormat.Csv, "*.csv")]
        [DataRow(ExportFormat.Xlsx, "*.xlsx")]
        public void ExportScreenUsesFullSnapshotInAllFormatsWithoutNewDoorCalls(ExportFormat format, string pattern)
        {
            using (var files = new ExecutorTestContext())
            {
                var data = new WorkspaceTestData();
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "RequirementReadPanel"); Find<TextBox>(window, "ReadModulePath").Text = "/P/M";
                    Click(window, "ReadRequirements"); await WorkspaceUiTest.Idle(window);
                    WorkspaceUiTest.Select(window, "ExportWorkspacePanel"); Find<CheckBox>(window, "ExportIncludeDiscovery").Checked = false; Find<CheckBox>(window, "ExportIncludeLinks").Checked = false;
                    string destination = Path.Combine(files.Root, "export-" + format); Find<TextBox>(window, "ExportDestination").Text = destination;
                    Find<ComboBox>(window, "ExportFormat").SelectedItem = format.ToString(); Click(window, "RunExport"); await WorkspaceUiTest.Idle(window);
                    StringAssert.Contains(Find<RichTextBox>(window, "ExportReport").Text, "Status=Completed");
                    Assert.IsTrue(File.Exists(Path.Combine(destination, "report.json"))); Assert.IsTrue(Directory.GetFiles(destination, pattern).Length > 0);
                    Assert.AreEqual(1, data.RequirementReads); Assert.AreEqual(0, data.WriteCommits + data.LinkReads);
                });
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DiagnosticsRunsTheSelectedModeOnStaAndRendersIndividualSteps(bool live)
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "DiagnosticsWorkspacePanel"); Click(window, live ? "DiagnoseLive" : "DiagnoseOffline"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(1, data.DiagnosticCalls); Assert.AreEqual(live, data.DiagnosticLive); Assert.AreEqual(ApartmentState.STA, data.DiagnosticApartment);
                Assert.AreEqual(1, Find<DataGridView>(window, "HealthCheckSteps").Rows.Count); StringAssert.Contains(Find<RichTextBox>(window, "HealthCheckReport").Text, "X86");
            });
        }

        [TestMethod]
        public void BusyWorkspaceDisablesOtherActionsAndCancellationDoesNotStartAnotherRead()
        {
            var data = new WorkspaceTestData { ReadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "RequirementReadPanel"); Find<TextBox>(window, "ReadModulePath").Text = "/P/M"; Click(window, "ReadRequirements");
                Assert.IsFalse(Find<Button>(window, "ProbeButton").Enabled); Assert.IsFalse(Find<Button>(window, "DiscoverButton").Enabled);
                Assert.IsFalse(Find<Button>(window, "LoadRequirementSchema").Enabled); Assert.IsFalse(Find<Button>(window, "SaveSettingsButton").Enabled);
                Click(window, "WorkspaceCancel"); Assert.IsFalse(Find<ComboBox>(window, "TargetProfileComboBox").Enabled);
                data.ReadRelease.SetResult(true); await WorkspaceUiTest.Idle(window); Assert.AreEqual(1, data.RequirementReads);
                Assert.AreEqual(0, Find<DataGridView>(window, "RequirementsGrid").Rows.Count);
            });
        }

        [DataTestMethod]
        [DataRow(DxlInvocationMethod.RunString)]
        [DataRow(DxlInvocationMethod.RunFile)]
        public void CustomDxlDispatchesTheReviewedCaptureAndParametersOnlyOnce(DxlInvocationMethod method)
        {
            using (var files = new ExecutorTestContext())
            {
                var data = new WorkspaceTestData { Approve = true };
                data.OnReview = _ => File.WriteAllText(files.SourcePath, "UNREVIEWED_REPLACEMENT");
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "DxlWorkspacePanel"); Find<TextBox>(window, "DxlScriptPath").Text = files.SourcePath;
                    Find<ComboBox>(window, "DxlInvocationMethod").SelectedItem = method.ToString();
                    var grid = Find<DataGridView>(window, "DxlParameters"); grid.Rows.Add("message", "Unicode 👋\t\";halt");
                    Click(window, "DxlRunReviewed"); await WorkspaceUiTest.Idle(window);
                    Assert.AreEqual(1, data.DialogCalls); Assert.AreEqual(1, files.Sessions.OpenCalls);
                    StringAssert.Contains(files.Sessions.Session.Script, "sample-result"); Assert.IsFalse(files.Sessions.Session.Script.Contains("UNREVIEWED_REPLACEMENT"));
                    Assert.AreEqual("Unicode 👋\t\";halt", DiscoveryTestContext.Parameter(files.Sessions.Session.Script, "message"));
                    StringAssert.Contains(Find<RichTextBox>(window, "DxlResultPreview").Text, "sample-result");
                }, services => { services.AddSingleton<IDxlExecutor>(files.Executor); services.AddSingleton<IDoorsOperationGate>(files.Gate); });
            }
        }

        [TestMethod]
        public void SnapshotStoreReleasesReplacedAndFinalFilesAndPagingStopsAtOnePage()
        {
            var first = new WorkspaceRequirementSnapshot(new RequirementReadRequest("/P/M"), 1000000);
            var second = new WorkspaceRequirementSnapshot(new RequirementReadRequest("/P/N"), 1);
            using (var store = new DesktopSnapshotStore())
            {
                store.SetRequirements(first); var rows = DesktopSnapshotPages.Requirements(first, 0);
                Assert.AreEqual(100, rows.Count); Assert.AreEqual(100, first.Enumerated);
                store.SetRequirements(second); Assert.IsTrue(first.Disposed); Assert.IsFalse(second.Disposed);
            }
            Assert.IsTrue(second.Disposed);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RealReviewDialogRequiresBothExactTargetAndAcknowledgementAndDefaultsToCancel(bool approve)
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                using (var owner = new Form { Location = new Point(-15000, -15000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
                using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
                {
                    owner.Show(); Exception failure = null;
                    timer.Tick += (s, e) =>
                    {
                        var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "CONTROLLED REVIEW");
                        if (dialog == null) return;
                        timer.Stop();
                        try
                        {
                            var apply = Find<Button>(dialog, "WriteReviewApply"); Assert.IsFalse(apply.Enabled);
                            var target = Find<TextBox>(dialog, "WriteReviewTarget"); var ack = Find<CheckBox>(dialog, "WriteReviewAcknowledgement");
                            target.Text = "/P/M"; Assert.IsFalse(apply.Enabled); ack.Checked = true; Assert.IsTrue(apply.Enabled);
                            target.Text = "/P/WRONG"; Assert.IsFalse(apply.Enabled); target.Text = "/P/M";
                            if (approve) apply.PerformClick(); else Find<Button>(dialog, "WriteReviewCancel").PerformClick();
                        }
                        catch (Exception ex) { failure = ex; dialog.DialogResult = DialogResult.Cancel; }
                    };
                    timer.Start();
                    bool result = new DesktopReviewDialog().Show(owner, new DesktopWriteReview("CONTROLLED REVIEW", "/P/M", "Complete review", DateTime.UtcNow.AddMinutes(1), true), CancellationToken.None);
                    if (failure != null) throw failure;
                    Assert.AreEqual(approve, result);
                }
            });
        }

        [TestMethod]
        public void QueuedReviewIsDeniedWhenItsOwnerBindingIsDisposed()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var data = new WorkspaceTestData { Approve = true }; var confirmation = new DesktopWriteConfirmation(data);
                using (var owner = new Form())
                {
                    var handle = owner.Handle; var binding = confirmation.Attach(owner);
                    var pending = confirmation.ReviewAsync(new DesktopWriteReview("Review", "/P/M", "Details", DateTime.UtcNow.AddMinutes(1), true), CancellationToken.None);
                    binding.Dispose(); Assert.IsFalse(pending.GetAwaiter().GetResult()); Assert.AreEqual(0, data.DialogCalls);
                }
            });
        }

        [TestMethod]
        public void ChangedProfileInvalidatesTheLoadedWriteCatalogAndPreview()
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                await LoadSchema(window, data); var grid = Find<DataGridView>(window, "RequirementAttributeCatalog");
                grid.Rows[0].Cells[0].Value = true; grid.Rows[0].Cells[3].Value = "Heading";
                Click(window, "PreviewRequirementWrite"); await WorkspaceUiTest.Idle(window); Assert.IsTrue(Find<Button>(window, "ApplyRequirementWrite").Enabled);
                Find<ComboBox>(window, "TargetProfileComboBox").SelectedItem = "9.5";
                Assert.AreEqual(0, grid.Rows.Count); Assert.IsFalse(Find<Button>(window, "ApplyRequirementWrite").Enabled); Assert.AreEqual(0, data.WriteCommits);
            });
        }

        [TestMethod]
        public void MissingExportSourceIsReportedInsteadOfSilentlyOmittingIt()
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "ExportWorkspacePanel"); Click(window, "RunExport"); await WorkspaceUiTest.Idle(window);
                StringAssert.Contains(Find<RichTextBox>(window, "WorkspaceErrorDetails").Text, "selected source is unavailable");
                Assert.AreEqual(0, data.RequirementReads + data.LinkReads + data.SchemaReads);
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CalculatedAttributeEvaluationIsSeparatelyReviewedBeforeRead(bool approved)
        {
            var data = new WorkspaceTestData { Approve = approved };
            WorkspaceUiTest.Run(data, async (window, provider) =>
            {
                WorkspaceUiTest.Select(window, "RequirementReadPanel"); Find<TextBox>(window, "ReadModulePath").Text = "/P/M";
                Find<CheckBox>(window, "ReadAllowAttributeDxl").Checked = true; Click(window, "ReadRequirements"); await WorkspaceUiTest.Idle(window);
                Assert.AreEqual(1, data.DialogCalls); Assert.AreEqual(approved ? 1 : 0, data.RequirementReads);
            });
        }

        [TestMethod]
        public void SoftTimeoutAndCancelKeepTheOriginalComCallOwnedAndDoNotResubmit()
        {
            using (var files = new ExecutorTestContext())
            {
                files.Sessions.Session.BlockRun = true;
                var data = new WorkspaceTestData { Approve = true };
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "DxlWorkspacePanel"); Find<TextBox>(window, "DxlScriptPath").Text = files.SourcePath;
                    Find<NumericUpDown>(window, "DxlTimeoutSeconds").Value = 1; Click(window, "DxlRunReviewed");
                    await WorkspaceUiTest.Wait(() => files.Sessions.Session.EnteredRun.IsSet);
                    await WorkspaceUiTest.Wait(() => Find<Label>(window, "WorkspaceStatus").Text.Contains("wait ended"));
                    Assert.IsTrue(files.Gate.IsBusy); Assert.IsFalse(Find<ComboBox>(window, "TargetProfileComboBox").Enabled);
                    Click(window, "WorkspaceCancel"); Assert.IsTrue(files.Gate.IsBusy); Assert.AreEqual(1, files.Sessions.OpenCalls);
                    files.Sessions.Session.ReleaseRun.Set(); await WorkspaceUiTest.Idle(window);
                    Assert.IsFalse(files.Gate.IsBusy); Assert.AreEqual(1, files.Sessions.OpenCalls);
                    StringAssert.Contains(Find<RichTextBox>(window, "DxlResultPreview").Text, "sample-result");
                }, services => { services.AddSingleton<IDxlExecutor>(files.Executor); services.AddSingleton<IDoorsOperationGate>(files.Gate); });
            }
        }

        [TestMethod]
        public void FileDxlResultsArePreviewedWithABoundAndReleasedOnWindowClose()
        {
            using (var files = new ExecutorTestContext())
            {
                string resultPath = null;
                files.Sessions.Session.FileResult = true;
                files.Sessions.Session.OnRun = _ => { resultPath = files.ResultPath(files.Executor.ActiveOperation); File.WriteAllText(resultPath, new string('x', 100000) + "TAIL"); };
                var data = new WorkspaceTestData { Approve = true };
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "DxlWorkspacePanel"); Find<TextBox>(window, "DxlScriptPath").Text = files.SourcePath;
                    Find<ComboBox>(window, "DxlResultMode").SelectedItem = "File"; Click(window, "DxlRunReviewed"); await WorkspaceUiTest.Idle(window);
                    string preview = Find<RichTextBox>(window, "DxlResultPreview").Text;
                    Assert.IsTrue(preview.Length < 65700); Assert.IsFalse(preview.Contains("TAIL")); Assert.IsTrue(File.Exists(resultPath));
                }, services => { services.AddSingleton<IDxlExecutor>(files.Executor); services.AddSingleton<IDoorsOperationGate>(files.Gate); });
                Assert.IsFalse(File.Exists(resultPath));
            }
        }

        private static async Task LoadSchema(MainForm window, WorkspaceTestData data)
        {
            WorkspaceUiTest.Select(window, "RequirementWritePanel"); Find<TextBox>(window, "WriteRequirementModule").Text = "/P/M";
            Click(window, "LoadRequirementSchema"); await WorkspaceUiTest.Idle(window); Assert.AreEqual(1, data.SchemaReads);
        }
        private static T Find<T>(Control window, string name) where T : Control => WorkspaceUiTest.Find<T>(window, name);
        private static void Click(Control window, string name) => Find<Button>(window, name).PerformClick();
    }
}
