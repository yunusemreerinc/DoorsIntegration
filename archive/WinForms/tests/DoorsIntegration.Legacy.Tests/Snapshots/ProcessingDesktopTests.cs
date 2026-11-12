using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Desktop;
using DoorsIntegration.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ProcessingDesktopTests
    {
        [TestMethod]
        public void NewRequirementToolsStartOfflineAndPreserveTheExistingReadPanel()
        {
            var data = new WorkspaceTestData();
            WorkspaceUiTest.Run(data, (window, provider) =>
            {
                foreach (string panel in new[] { "RequirementReadPanel", "BatchWorkspacePanel", "QualityWorkspacePanel", "CoverageWorkspacePanel" }) WorkspaceUiTest.Select(window, panel);
                Assert.AreEqual(0, data.RequirementReads + data.LinkReads + data.WritePreviews + data.WriteCommits + data.DialogCalls);
                Assert.IsFalse(Find<Button>(window, "ApplyBatch").Enabled); Assert.IsFalse(Find<Button>(window, "RetryBatchPreview").Enabled);
                Assert.IsFalse(Find<CheckBox>(window, "CoverageRepositoryConfirmed").Checked);
                return Task.CompletedTask;
            });
        }
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void BatchIntentAndEveryNativeWriteRequireTheirOwnRealReview(bool approve)
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string input = Path.Combine(c.Root, "input.csv");
                File.WriteAllText(input, "key,action,target,attr:Object Text\na,Create,/P/M,first\nb,Create,/P/M,second\n");
                var data = new WorkspaceTestData { Approve = approve };
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "BatchWorkspacePanel");
                    Find<TextBox>(window, "BatchInputPath").Text = input; Find<TextBox>(window, "BatchRepository").Text = "test-db";
                    Find<TextBox>(window, "BatchNamespace").Text = "stable-import"; Find<ComboBox>(window, "BatchInputFormat").SelectedItem = "CSV";
                    Click(window, "PreviewBatch"); await WorkspaceUiTest.Idle(window);
                    Assert.AreEqual(2, data.WritePreviews); Assert.AreEqual(0, data.WriteCommits); Assert.AreEqual(0, data.DialogCalls);
                    Assert.IsTrue(Find<Button>(window, "ApplyBatch").Enabled); Assert.AreEqual(2, Find<DataGridView>(window, "BatchGrid").Rows.Count);
                    Render(window, "BatchWorkspacePanel");
                    Click(window, "ApplyBatch"); await WorkspaceUiTest.Idle(window);
                    Assert.AreEqual(approve ? 2 : 0, data.WriteCommits); Assert.AreEqual(approve ? 3 : 1, data.DialogCalls);
                    Assert.IsFalse(Find<Button>(window, "ApplyBatch").Enabled);
                    if (approve)
                    {
                        StringAssert.Contains(Find<RichTextBox>(window, "BatchReport").Text, "Applied/historical=2");
                        string audit = File.ReadAllText(Path.Combine(c.Root, "audit.jsonl"));
                        StringAssert.Contains(audit, "\"RepositoryId\":\"test-db\""); StringAssert.Contains(audit, "\"Action\":\"Create\"");
                    }
                    else StringAssert.Contains(Find<RichTextBox>(window, "WorkspaceErrorDetails").Text, "ApprovalRequired");
                }, services => services.AddSingleton(c.Storage));
            }
        }
        [TestMethod]
        public void ChangedBatchInputsAndProfileInvalidateThePlan()
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string input = Path.Combine(c.Root, "input.csv"); File.WriteAllText(input, "key,action,target,attr:Object Text\na,Create,/P/M,text\n");
                WorkspaceUiTest.Run(new WorkspaceTestData(), async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "BatchWorkspacePanel"); Find<TextBox>(window, "BatchInputPath").Text = input;
                    Find<TextBox>(window, "BatchRepository").Text = "test-db"; Find<TextBox>(window, "BatchNamespace").Text = "job"; Find<ComboBox>(window, "BatchInputFormat").SelectedItem = "CSV";
                    Click(window, "PreviewBatch"); await WorkspaceUiTest.Idle(window); Assert.IsTrue(Find<Button>(window, "ApplyBatch").Enabled);
                    Find<TextBox>(window, "BatchNamespace").Text = "another"; Assert.IsFalse(Find<Button>(window, "ApplyBatch").Enabled);
                    Click(window, "PreviewBatch"); await WorkspaceUiTest.Idle(window); Assert.IsTrue(Find<Button>(window, "ApplyBatch").Enabled);
                    Find<ComboBox>(window, "TargetProfileComboBox").SelectedItem = "9.5"; Assert.IsFalse(Find<Button>(window, "ApplyBatch").Enabled);
                }, services => services.AddSingleton(c.Storage));
            }
        }
        [TestMethod]
        public void QualityAndCoverageUseRetainedSnapshotsWithoutAnotherDoorsCall()
        {
            using (var c = new ProcessingTestContext())
            {
                var data = new WorkspaceTestData();
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "RequirementReadPanel"); Find<TextBox>(window, "ReadModulePath").Text = "/P/M"; Click(window, "ReadRequirements"); await WorkspaceUiTest.Idle(window);
                    WorkspaceUiTest.Select(window, "LinkWorkspacePanel"); Find<TextBox>(window, "LinkAnchorModule").Text = "/P/M"; Find<TextBox>(window, "LinkModulePath").Text = "/P/Links";
                    Click(window, "ReadLinks"); await WorkspaceUiTest.Idle(window);
                    WorkspaceUiTest.Select(window, "QualityWorkspacePanel"); Find<TextBox>(window, "QualityRepository").Text = "caller-db";
                    Find<TextBox>(window, "QualityProfilePath").Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "quality", "requirements-quality.example.xml");
                    Click(window, "RunQuality"); await WorkspaceUiTest.Idle(window);
                    StringAssert.Contains(Find<RichTextBox>(window, "QualityReport").Text, "ComplianceAssessed=False; ToolQualified=False");
                    StringAssert.Contains(Find<RichTextBox>(window, "QualityReport").Text, "Artifacts=251"); Render(window, "QualityWorkspacePanel");
                    WorkspaceUiTest.Select(window, "CoverageWorkspacePanel"); Find<TextBox>(window, "CoverageRepository").Text = "caller-db";
                    Find<ComboBox>(window, "CoverageDirection").SelectedItem = "Outgoing";
                    Click(window, "RunCoverage"); await WorkspaceUiTest.Idle(window);
                    string text = Find<RichTextBox>(window, "CoverageReport").Text;
                    StringAssert.Contains(text, "Unknown=251"); StringAssert.Contains(text, "Unavailable for incomplete scope"); Render(window, "CoverageWorkspacePanel");
                    Assert.AreEqual(1, data.RequirementReads); Assert.AreEqual(1, data.LinkReads); Assert.AreEqual(0, data.WriteCommits); Assert.AreEqual(0, data.DialogCalls);
                }, services => services.AddSingleton(c.Storage));
            }
        }
        private static T Find<T>(Control window, string name) where T : Control => WorkspaceUiTest.Find<T>(window, name);
        private static void Click(Control window, string name) => Find<Button>(window, name).PerformClick();
        private static void Render(Control window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("DOORS_UI_RENDER_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory); window.PerformLayout();
            using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, window.ClientRectangle); bitmap.Save(Path.Combine(directory, name + ".png")); }
        }
    }
}
