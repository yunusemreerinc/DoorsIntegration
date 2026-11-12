using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, DoNotParallelize]
    public class BulkBaselinePreflightWpfTests
    {
        private static async Task Prepare(WorkspaceViewModel model)
        { var ui = model.BulkSelection; ui.FolderPath = "/Bulk/P01/F01"; ui.IncludeSubfolders = false; await ui.AddFolderCommand.ExecuteAsync(); await ui.LoadNextCommand.ExecuteAsync(); await ui.SelectAllCommand.ExecuteAsync(); }
        [TestMethod]
        public Task DryRunShowsMixedEligibilityReasonsAndNeverChangesReadOnlyMode() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await Prepare(model); var ui = model.BulkSelection; ui.BaselineType = BaselineType.Major; ui.BaselineSuffix = "SRR"; ui.BaselineAnnotation = "İnceleme 注釈";
                await ui.PlanCommand.ExecuteAsync(); Assert.AreEqual(31, ui.PreflightRows.Count); Assert.AreEqual(28, ui.DryRunResult.EligibleCount);
                Assert.AreEqual(3, ui.PreflightRows.Count(r => r.Status == BulkBaselineItemStatus.Blocked));
                Assert.IsTrue(ui.PreflightRows.Any(r => r.ReasonCode == BaselinePreflightReason.OtherSessionLock));
                Assert.IsTrue(ui.PreflightRows.Any(r => r.ReasonCode == BaselinePreflightReason.UnsavedCurrentSession));
                Assert.IsTrue(ui.PreflightRows.Any(r => r.ReasonCode == BaselinePreflightReason.CreateDenied));
                Assert.IsTrue(ui.PreflightRows.Where(r => r.Status == BulkBaselineItemStatus.Ready).All(r => r.ExpectedVersion.Suffix == "SRR"));
                Assert.IsTrue(model.IsReadOnly); Assert.IsFalse(ui.DryRunResult.WriteAttempted); Assert.AreEqual("", model.ErrorDetails);
                ui.SelectedPreflightItem = ui.PreflightRows.First(r => r.Status == BulkBaselineItemStatus.Blocked);
                StringAssert.Contains(ui.PreflightItemDetails, "Önerilen çözüm"); Assert.IsFalse(string.IsNullOrWhiteSpace(ui.SelectedPreflightItem.RecommendedResolution));
            }
        });
        [TestMethod]
        public Task StopOnFirstErrorSkipsTheRemainingModuleRows() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await Prepare(model); var ui = model.BulkSelection; ui.ExecutionPolicy = ExecutionPolicy.StopOnFirstError;
                await ui.PlanCommand.ExecuteAsync(); Assert.AreEqual(2, ui.DryRunResult.EligibleCount);
                Assert.AreEqual(1, ui.PreflightRows.Count(r => r.Status == BulkBaselineItemStatus.Blocked)); Assert.AreEqual(28, ui.PreflightRows.Count(r => r.Status == BulkBaselineItemStatus.Skipped));
                Assert.IsTrue(model.IsReadOnly);
            }
        });
        [TestMethod]
        public Task AllBlockedSelectionHasAnExplicitSummaryAndSuggestedFix() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await Prepare(model); var ui = model.BulkSelection; await ui.DeselectAllCommand.ExecuteAsync(); ui.Search = "module 003"; await ui.SelectAllCommand.ExecuteAsync();
                await ui.PlanCommand.ExecuteAsync(); Assert.AreEqual(0, ui.DryRunResult.EligibleCount); StringAssert.Contains(ui.PreflightSummary, "Tüm modüller");
                StringAssert.Contains(ui.Report, "Öneri:"); Assert.IsFalse(ui.DryRunResult.LockChanged); Assert.AreEqual("", model.ErrorDetails);
            }
        });
        [TestMethod]
        public Task ChangingInputOrSelectionInvalidatesThePreviousPlan() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await Prepare(model); var ui = model.BulkSelection; await ui.PlanCommand.ExecuteAsync(); Assert.IsTrue(ui.RevalidateCommand.CanExecute(null));
                ui.BaselineSuffix = "changed"; Assert.IsNull(ui.DryRunResult); Assert.AreEqual(0, ui.PreflightRows.Count); Assert.IsFalse(ui.RevalidateCommand.CanExecute(null));
                await ui.PlanCommand.ExecuteAsync(); await ui.DeselectAllCommand.ExecuteAsync(); Assert.IsNull(ui.DryRunResult); Assert.IsFalse(ui.PlanCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task RevalidationUsesTheSameImmutablePlanAndKeepsBlockedModulesSkipped() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await Prepare(model); var ui = model.BulkSelection; await ui.PlanCommand.ExecuteAsync(); var plan = ui.DryRunResult.Plan;
                await ui.RevalidateCommand.ExecuteAsync(); Assert.AreSame(plan, ui.DryRunResult.Plan); Assert.AreEqual(28, ui.DryRunResult.EligibleCount);
                Assert.AreEqual(3, ui.PreflightRows.Count(r => r.Status == BulkBaselineItemStatus.Blocked)); Assert.IsFalse(ui.DryRunResult.CanExecute);
            }
        });
        [TestMethod]
        public Task InvalidAnnotationCannotLeaveAnOldEligiblePlanOnScreen() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await Prepare(model); var ui = model.BulkSelection; await ui.PlanCommand.ExecuteAsync(); ui.BaselineAnnotation = "bad\0";
                await ui.PlanCommand.ExecuteAsync(); Assert.IsNull(ui.DryRunResult); Assert.AreEqual(0, ui.PreflightRows.Count); Assert.IsFalse(string.IsNullOrWhiteSpace(model.ErrorDetails)); Assert.IsTrue(model.IsReadOnly);
            }
        });
        [TestMethod]
        public Task RealCompositionReportsUnverifiedNativeEvidenceWithoutPretendingReady() => WpfTestHost.Run(async () =>
        {
            var factory = new WpfFactory { Build = (profile, logger) =>
            {
                var mock = MockDoorsAdapter.CreateSession(logger);
                return new ClientSession(mock.Projects, mock.Requirements, mock.Links, mock.Baselines, mock.Export, mock.Health, null, mock.Tools,
                    mock.Modules, mock.BulkBaselines, mock.ModuleSelection, mock.BaselineMetadata,
                    new BulkBaselinePreflightService(new UnavailableBaselinePreflightStateReader(), mock.BaselineMetadata, new BaselinePreflightClock(), logger));
            } };
            using (var model = WpfTestHost.Model(factory))
            {
                await Prepare(model); await model.BulkSelection.PlanCommand.ExecuteAsync(); Assert.AreEqual(0, model.BulkSelection.DryRunResult.EligibleCount);
                Assert.IsTrue(model.BulkSelection.PreflightRows.All(r => r.ReasonCode == BaselinePreflightReason.NativeStateUnavailable)); Assert.AreEqual("", model.ErrorDetails);
            }
        });
        [TestMethod]
        public Task DryRunTabNavigatesRendersAndPreservesAllPreviousTabs() => WpfTestHost.Run(async () =>
        {
            var model = WpfTestHost.Model(); var window = new MainWindow(model) { Width = 1480, Height = 960 };
            var previous = Application.Current.MainWindow; Application.Current.MainWindow = window;
            var log = new WpfBindingLog(); PresentationTraceSources.DataBindingSource.Listeners.Add(log); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                window.Show(); await WpfTestHost.Until(() => model.IsIdle && model.Requirements.Count > 0); await Prepare(model); await model.BulkSelection.PlanCommand.ExecuteAsync();
                var tabs = (TabControl)window.FindName("WorkspaceTabs"); Assert.AreEqual(12, tabs.Items.Count); Assert.AreEqual("Bulk baseline / Dry run", ((TabItem)tabs.SelectedItem).Header); Assert.AreEqual("Configuration Dashboard",((TabItem)tabs.Items[11]).Header);
                window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var grid = Visual<DataGrid>(window).Single(g => g.Name == "BulkBaselinePreflightGrid"); Assert.AreEqual(31, grid.Items.Count); Assert.IsTrue(grid.IsReadOnly);
                model.BulkSelection.SelectedPreflightItem = model.BulkSelection.PreflightRows.First(r => r.Status == BulkBaselineItemStatus.Blocked); window.UpdateLayout();
                var image = new RenderTargetBitmap((int)window.RenderSize.Width, (int)window.RenderSize.Height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "BulkBaselinePreflight", Environment.Is64BitProcess ? "x64" : "x86")); Directory.CreateDirectory(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(Path.Combine(root, "dry-run.png"))) encoder.Save(output);
                Assert.AreEqual(0, log.Errors.Count, string.Join("\n", log.Errors.Take(10)));
            }
            finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(log); await model.PrepareCloseAsync(); window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Application.Current.MainWindow = previous?.IsVisible == true ? previous : null; }
        });
        private static IEnumerable<T> Visual<T>(DependencyObject parent) where T : DependencyObject
        { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value; foreach (var nested in Visual<T>(child)) yield return nested; } }
    }
}
