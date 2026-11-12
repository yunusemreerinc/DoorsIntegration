using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DoorsIntegration.Processing;
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, DoNotParallelize]
    public class RuleEngineWpfTests
    {
        private static Task Click(ToolPage page, string label) => page.Actions.Single(a => a.Label.StartsWith(label, StringComparison.Ordinal)).Command.ExecuteAsync();
        [TestMethod]
        public Task JsonQualityCanSelectAStandardAndRunDraftWithoutNativeCalls() => WpfTestHost.Run(async () =>
        {
            using (var files = new ExecutorTestContext()) using (var model = WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); var page = model.Tools.Quality; page.Field("standard").Value = "DO200B"; page.Field("save").Value = Path.Combine(files.Root, "draft.json");
                await Click(page, "Örnek"); Assert.AreEqual(RuleStandard.DO200B, RuleProfile.LoadJson(page.Text("profile")).Standard);
                page.Field("repository").Value = "mock-repo"; page.Field("project").Value = "project-A";
                await Click(page, "Kalite"); StringAssert.Contains(page.Report, "ApprovalValid=False");
                page.Field("mode").Value = "DraftPreview"; await Click(page, "Kalite"); StringAssert.Contains(page.Report, "Mode=DraftPreview"); StringAssert.Contains(page.Report, "FullyEvaluated=False");
                Assert.IsTrue(model.IsReadOnly); Assert.AreEqual("", model.ErrorDetails);
            }
        });
        [DataTestMethod, DataRow(false), DataRow(true)]
        public Task ProjectProfileApprovalRequiresFingerprintAndAcknowledgement(bool approve) => WpfTestHost.Run(async () =>
        {
            using (var files = new ExecutorTestContext())
            {
                var model = WpfTestHost.Model(); var window = new MainWindow(model); var previous = Application.Current.MainWindow; Application.Current.MainWindow = window;
                try
                {
                    window.Show(); await WpfTestHost.Until(() => model.IsIdle && model.Requirements.Count > 0);
                    string input = Path.Combine(files.Root, "input.json"), output = Path.Combine(files.Root, "approved.json");
                    var draft = new RuleProfile("project", "1", RuleStandard.DO330, new Rule[] { new ConfiguredRule("empty", "1", RuleKind.EmptyRequirement, RuleSeverity.Error, new[] { "Object Text" }) }, "project-A", "PLAN", false);
                    draft.SaveJson(input); var page = model.Tools.Quality; page.Field("profile").Value = input; page.Field("project").Value = "project-A"; page.Field("reference").Value = "PLAN";
                    page.Field("reviewer").Value = "reviewer-A"; page.Field("save").Value = output;
                    var work = Click(page, "Proje profilini"); await WpfTestHost.Until(() => Application.Current.Windows.Cast<Window>().Any(w => w.Title == "Proje kalite profili onayı"));
                    var review = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "Proje kalite profili onayı");
                    var apply = Children<Button>(review).Single(b => Equals(b.Content, "Onayla ve kaydet")); Assert.IsFalse(apply.IsEnabled);
                    if (approve) { Children<TextBox>(review).Single(t => !t.IsReadOnly).Text = draft.Fingerprint; Children<CheckBox>(review).Single().IsChecked = true; Assert.IsTrue(apply.IsEnabled); apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    else review.Close();
                    await work; Assert.AreEqual(approve, File.Exists(output)); Assert.IsTrue(model.IsReadOnly);
                    if (approve) Assert.IsTrue(RuleProfile.LoadJson(output).IsApprovedFor("project-A", DateTime.UtcNow)); Assert.IsNull(RuleProfile.LoadJson(input).Approval);
                }
                finally { await model.PrepareCloseAsync(); window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Application.Current.MainWindow = previous?.IsVisible == true ? previous : null; }
            }
        });
        private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
        { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item; foreach (var nested in Children<T>(child)) yield return nested; } }
    }
}
