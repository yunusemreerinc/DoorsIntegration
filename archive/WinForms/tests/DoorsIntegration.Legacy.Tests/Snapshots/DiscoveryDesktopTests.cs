using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DiscoveryDesktopTests
    {
        [TestMethod]
        public void DiscoveryIsExplicitReadOnlyAndUsesHostReaderOverride()
        {
            ApartmentTest.Run(ApartmentState.STA, () =>
            {
                var reader = new RecordingHierarchyReader();
                var services = new ServiceCollection();
                services.AddSingleton<IDoorsHierarchyReader>(reader);
                services.AddSingleton<IDoorsOperationGate>(new DoorsOperationGate());
                services.AddDoorsDesktop();
                using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
                {
                    var window = provider.GetRequiredService<MainForm>();
                    Assert.AreEqual(0, reader.Calls);
                    var grid = window.Controls.Find("DiscoveryGrid", true).OfType<DataGridView>().Single();
                    var export = window.Controls.Find("ExportDiscoveryButton", true).OfType<Button>().Single();
                    Assert.IsTrue(grid.ReadOnly);
                    Assert.IsFalse(export.Enabled);
                    var path = window.Controls.Find("DiscoveryPathTextBox", true).OfType<TextBox>().Single();
                    path.Text = "/P";
                    // PerformClick requires a visible parent tab; selecting it is sufficient before handle creation.
                    var tabs = FindTabs(window);
                    tabs.SelectedIndex = 2;
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new System.Drawing.Point(-15000, -15000);
                    window.ShowInTaskbar = false;
                    window.Show();
                    var button = window.Controls.Find("DiscoverButton", true).OfType<Button>().Single();
                    button.PerformClick();
                    Assert.AreEqual(1, reader.Calls);
                    Assert.AreEqual("/P", reader.Scope.RootPath);
                    Assert.AreEqual(DiscoveryKind.Hierarchy, reader.Kind);
                    Assert.AreEqual(1, grid.Rows.Count);
                    Assert.AreEqual("/P/M", grid.Rows[0].Cells[0].Value);
                    Assert.IsTrue(export.Enabled);
                    reader.Failure = new IntegrationException(IntegrationErrorCode.ComAccessDenied, "Controlled access failure.");
                    button.PerformClick();
                    Assert.IsFalse(export.Enabled);
                    Assert.AreEqual(0, grid.Rows.Count);
                    StringAssert.Contains(window.Controls.Find("DiscoverySummaryLabel", true).OfType<Label>().Single().Text, "ComAccessDenied");
                    var issues = window.Controls.Find("DiscoveryIssuesTextBox", true).OfType<RichTextBox>().Single();
                    StringAssert.Contains(issues.Text, "Controlled access failure.");
                    reader.Failure = new DoorsIntegration.Dxl.DiscoveryProtocolException(DoorsIntegration.Dxl.DiscoveryRejectionReason.HeaderMismatch, 1, 100);
                    button.PerformClick();
                    StringAssert.Contains(issues.Text, "Reason=HeaderMismatch; Line=1; ResponseChars=100");
                    StringAssert.Contains(issues.Text, "Request:");
                    Assert.IsFalse(export.Enabled);
                    Assert.AreEqual(0, grid.Rows.Count);
                }
            });
        }

        private static TabControl FindTabs(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is TabControl tabs) return tabs;
                var nested = FindTabs(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
