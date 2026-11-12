using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class LinkWriteDesktopTests
    {
        [DataTestMethod]
        [DataRow(LinkWriteAction.Create, true, false)]
        [DataRow(LinkWriteAction.Create, false, false)]
        [DataRow(LinkWriteAction.Remove, true, true)]
        [DataRow(LinkWriteAction.Remove, true, false)]
        [DataRow(LinkWriteAction.Remove, false, false)]
        public void LinkEditorUsesDryRunAndSeparateRemovalDialog(LinkWriteAction action, bool approved, bool removalApproved)
        {
            using (var c = new LinkWriteTestContext())
            {
                c.Request = LinkWriteTests.Request(action); if (action == LinkWriteAction.Remove) c.Before.Add(c.Selected());
                var data = new WorkspaceTestData { Approve = approved };
                data.OnReview = review =>
                {
                    if (review.Title == "Explicit link removal approval")
                    {
                        Assert.IsTrue(review.Target.StartsWith("DELETE ", StringComparison.Ordinal));
                        StringAssert.Contains(review.Body, "cannot be undone"); data.Approve = removalApproved;
                    }
                    else
                    {
                        Assert.AreEqual(c.Request.DisplayTarget, review.Target);
                        StringAssert.Contains(review.Body, "db:source"); StringAssert.Contains(review.Body, "Before edges:");
                        data.Approve = approved;
                    }
                };
                WorkspaceUiTest.Run(data, async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "LinkWritePanel");
                    Assert.IsFalse(Find<Button>(window, "ApplyLinkWrite").Enabled); Assert.AreEqual(0, c.Modes.Count);
                    Fill(window, action); Click(window, "PreviewLinkWrite"); await WorkspaceUiTest.Idle(window);
                    Assert.IsTrue(Find<Button>(window, "ApplyLinkWrite").Enabled); Assert.AreEqual(0, data.DialogCalls);
                    string render = Environment.GetEnvironmentVariable("DOORS_UI_RENDER_DIR");
                    if (!string.IsNullOrWhiteSpace(render) && action == LinkWriteAction.Create && approved)
                    {
                        Directory.CreateDirectory(render); window.PerformLayout();
                        using (var bitmap = new Bitmap(window.Width, window.Height))
                        { window.DrawToBitmap(bitmap, window.ClientRectangle); bitmap.Save(Path.Combine(render, "LinkWritePanel.png")); }
                    }
                    Find<NumericUpDown>(window, "WriteLinkTargetAbsolute").Value = 3;
                    Assert.IsFalse(Find<Button>(window, "ApplyLinkWrite").Enabled);
                    Find<NumericUpDown>(window, "WriteLinkTargetAbsolute").Value = 2;
                    Click(window, "PreviewLinkWrite"); await WorkspaceUiTest.Idle(window);
                    Click(window, "ApplyLinkWrite"); await WorkspaceUiTest.Idle(window);
                    bool applied = approved && (action == LinkWriteAction.Create || removalApproved);
                    Assert.AreEqual(applied ? 1 : 0, c.Modes.Count(m => m == "commit"));
                    Assert.AreEqual(action == LinkWriteAction.Remove && approved ? 2 : 1, data.DialogCalls);
                    Assert.IsFalse(Find<Button>(window, "ApplyLinkWrite").Enabled);
                    if (applied)
                    {
                        string text = Find<RichTextBox>(window, "LinkWriteReport").Text;
                        StringAssert.Contains(text, "Persistence=Saved"); StringAssert.Contains(text, action == LinkWriteAction.Create ? "ADDED:" : "REMOVED:");
                    }
                    else StringAssert.Contains(Find<RichTextBox>(window, "WorkspaceErrorDetails").Text, "LinkWriteApprovalRequired");
                }, services => { services.AddSingleton<ILinkWriteGateway>(c.Gateway); services.AddSingleton<ILinkWriteAuditSink>(c.Audit); });
            }
        }
        [TestMethod]
        public void LinkPreviewIsInvalidatedByHostProfileChange()
        {
            using (var c = new LinkWriteTestContext())
            {
                WorkspaceUiTest.Run(new WorkspaceTestData(), async (window, provider) =>
                {
                    WorkspaceUiTest.Select(window, "LinkWritePanel"); Fill(window, LinkWriteAction.Create);
                    Click(window, "PreviewLinkWrite"); await WorkspaceUiTest.Idle(window);
                    Assert.IsTrue(Find<Button>(window, "ApplyLinkWrite").Enabled);
                    Find<ComboBox>(window, "TargetProfileComboBox").SelectedItem = "9.5";
                    Assert.IsFalse(Find<Button>(window, "ApplyLinkWrite").Enabled); Assert.IsFalse(c.Modes.Contains("commit"));
                }, services => { services.AddSingleton<ILinkWriteGateway>(c.Gateway); services.AddSingleton<ILinkWriteAuditSink>(c.Audit); });
            }
        }
        private static void Fill(Control window, LinkWriteAction action)
        {
            Find<TextBox>(window, "WriteLinkSourceModule").Text = "/P/Source";
            Find<TextBox>(window, "WriteLinkTargetModule").Text = "/P/Target";
            Find<TextBox>(window, "WriteLinkModule").Text = "/P/Links";
            Find<NumericUpDown>(window, "WriteLinkTargetAbsolute").Value = 2;
            Find<ComboBox>(window, "LinkWriteAction").SelectedItem = action.ToString();
        }
        private static T Find<T>(Control window, string name) where T : Control => WorkspaceUiTest.Find<T>(window, name);
        private static void Click(Control window, string name) => Find<Button>(window, name).PerformClick();
    }
}
