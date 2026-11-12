using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.ConsoleApp;
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, DoNotParallelize]
    public class ModuleSelectionWpfTests
    {
        private static async Task Prepare(WorkspaceViewModel model, string path = "/Bulk/P01/F01")
        { model.BulkSelection.FolderPath = path; model.BulkSelection.IncludeSubfolders = false; await model.BulkSelection.AddFolderCommand.ExecuteAsync(); await model.BulkSelection.LoadNextCommand.ExecuteAsync(); }
        [TestMethod]
        public Task WpfSingleFolderFilteredAndAllSelectionRemainReadOnly() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                var ui = model.BulkSelection; await Prepare(model); Assert.AreEqual(32, ui.Rows.Count);
                ui.Rows[0].IsSelected = true; Assert.AreEqual(1, ui.Rows.Count(r => r.IsSelected));
                await ui.SelectAllCommand.ExecuteAsync(); Assert.AreEqual(31, ui.Rows.Count(r => r.IsSelected));
                ui.Search = "module 007"; Assert.AreEqual(1, ui.Rows.Count); Assert.AreEqual("None", ui.Rows[0].Baseline);
                await ui.DeselectVisibleCommand.ExecuteAsync(); ui.SelectedOnly = true; Assert.AreEqual(0, ui.Rows.Count);
                ui.Search = ""; Assert.AreEqual(30, ui.Rows.Count); ui.SelectedOnly = false;
                ui.SelectedFolder = ui.Folders[0]; await ui.DeselectFolderCommand.ExecuteAsync(); Assert.AreEqual(0, ui.Rows.Count(r => r.IsSelected));
                await ui.SelectFolderCommand.ExecuteAsync(); Assert.AreEqual(31, ui.Rows.Count(r => r.IsSelected));
                await ui.PlanCommand.ExecuteAsync(); StringAssert.Contains(ui.Report, "NotRun"); StringAssert.Contains(ui.Report, "WriteAttempted=False");
                Assert.IsTrue(model.IsReadOnly); Assert.AreEqual("", model.ErrorDetails);
            }
        });
        [TestMethod]
        public Task WpfProfilesRestoreOnlyAfterMetadataRevalidation() => WpfTestHost.Run(async () =>
        {
            using (var files = new ExecutorTestContext()) using (var model = WpfTestHost.Model())
            {
                var ui = model.BulkSelection; await Prepare(model); await ui.SelectAllCommand.ExecuteAsync();
                ui.ProfileName = "Uçuş profili"; ui.ProfilePath = Path.Combine(files.Root, "modules.json"); await ui.SaveProfileCommand.ExecuteAsync(); Assert.IsTrue(File.Exists(ui.ProfilePath));
                await ui.DeselectAllCommand.ExecuteAsync(); await ui.LoadProfileCommand.ExecuteAsync(); Assert.AreEqual(0, ui.Rows.Count);
                StringAssert.Contains(ui.Summary, "profilden bekleyen=31"); await ui.LoadNextCommand.ExecuteAsync(); Assert.AreEqual(31, ui.Rows.Count(r => r.IsSelected));
                Assert.AreEqual("Uçuş profili", ui.ProfileName); Assert.IsTrue(model.IsReadOnly); Assert.AreEqual("", model.ErrorDetails);
            }
        });
        [TestMethod]
        public Task ChangingScopeOrConnectionInvalidatesOldSelections() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                var ui = model.BulkSelection; await Prepare(model); await ui.SelectAllCommand.ExecuteAsync();
                ui.Folders[0].IncludeSubfolders = true; Assert.AreEqual(0, ui.Rows.Count); Assert.IsFalse(ui.PlanCommand.CanExecute(null));
                await ui.LoadNextCommand.ExecuteAsync(); await ui.SelectAllCommand.ExecuteAsync(); model.Mode = ClientMode.RealDoors;
                Assert.AreEqual(0, ui.Rows.Count); Assert.AreEqual(0, ui.Folders.Count); Assert.AreEqual("", ui.RepositoryId); Assert.IsTrue(model.IsReadOnly);
            }
        });
        [TestMethod]
        public Task InvalidSaveNameAndForeignProfileAreHandledWithoutOverwriting() => WpfTestHost.Run(async () =>
        {
            using (var files = new ExecutorTestContext()) using (var model = WpfTestHost.Model())
            {
                var ui = model.BulkSelection; await Prepare(model); ui.ProfilePath = Path.Combine(files.Root, "selection.json"); ui.ProfileName = "";
                await ui.SaveProfileCommand.ExecuteAsync(); Assert.IsFalse(File.Exists(ui.ProfilePath)); Assert.IsFalse(string.IsNullOrEmpty(model.ErrorDetails));
                ui.ProfileName = "valid"; await ui.SaveProfileCommand.ExecuteAsync(); var before = File.ReadAllBytes(ui.ProfilePath);
                await ui.SaveProfileCommand.ExecuteAsync(); CollectionAssert.AreEqual(before, File.ReadAllBytes(ui.ProfilePath));
                ui.RepositoryId = "other-repository"; await ui.LoadProfileCommand.ExecuteAsync(); Assert.AreEqual(0, ui.Rows.Count); Assert.IsFalse(string.IsNullOrEmpty(model.ErrorDetails));
            }
        });
        [TestMethod]
        public Task SelectionServiceRunsOffDispatcherAndGlobalCancellationReleasesTheUi() => WpfTestHost.Run(async () =>
        {
            int uiThread = Thread.CurrentThread.ManagedThreadId; var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var adapter = new SelectionTestAdapter(); adapter.Folder = async (scope, token) =>
            { Assert.AreNotEqual(uiThread, Thread.CurrentThread.ManagedThreadId); entered.SetResult(true); await Task.Delay(Timeout.Infinite, token); return null; };
            var factory = new WpfFactory { Build = (profile, logger) =>
            {
                var source = MockDoorsAdapter.CreateSession(logger);
                return new ClientSession(source.Projects, source.Requirements, source.Links, source.Baselines, source.Export, source.Health,
                    null, source.Tools, source.Modules, source.BulkBaselines, new FolderModuleSelectionService(adapter, adapter, logger));
            } };
            using (var model = WpfTestHost.Model(factory))
            {
                var ui = model.BulkSelection; ui.FolderPath = "/Bulk"; await ui.AddFolderCommand.ExecuteAsync(); var load = ui.LoadNextCommand.ExecuteAsync();
                await entered.Task; Assert.IsTrue(model.IsBusy); Assert.IsFalse(ui.LoadNextCommand.CanExecute(null));
                await model.CancelCommand.ExecuteAsync(); await load; Assert.IsTrue(model.IsIdle); Assert.IsTrue(model.IsReadOnly); Assert.AreEqual(1, adapter.FolderReads.Count);
            }
        });
        [TestMethod]
        public Task BulkSelectionTabRendersAndDeniesCheckboxesWithoutBindingErrors() => WpfTestHost.Run(async () =>
        {
            var model = WpfTestHost.Model(); await Prepare(model); var window = new MainWindow(model) { Width = 1480, Height = 960 };
            var previous = Application.Current.MainWindow; Application.Current.MainWindow = window;
            var log = new WpfBindingLog(); PresentationTraceSources.DataBindingSource.Listeners.Add(log); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                window.Show(); await WpfTestHost.Until(() => model.IsIdle && model.Requirements.Count > 0); var tabs = (TabControl)window.FindName("WorkspaceTabs");
                tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Bulk baseline / Modül seçimi");
                window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var grid = Visual<DataGrid>(window).Single(g => g.Name == "SelectionModules"); Assert.AreEqual(32, grid.Items.Count); Assert.IsTrue(grid.EnableRowVirtualization);
                var image = new RenderTargetBitmap((int)window.RenderSize.Width, (int)window.RenderSize.Height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "ModuleSelection", Environment.Is64BitProcess ? "x64" : "x86")); Directory.CreateDirectory(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(Path.Combine(root, "selection.png"))) encoder.Save(output);
                model.BulkSelection.Access = ModuleSelectionAccessFilter.Restricted; window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(1, grid.Items.Count); Assert.IsTrue(Visual<CheckBox>(grid).Any(c => !c.IsEnabled));
                Assert.AreEqual(0, log.Errors.Count, string.Join("\n", log.Errors.Take(10)));
            }
            finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(log); await model.PrepareCloseAsync(); window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Application.Current.MainWindow = previous?.IsVisible == true ? previous : null; }
        });
        [TestMethod]
        public void SelectionDiSupportsHostOverridesAndRepeatedRegistration()
        {
            var metadata = new UnavailableModuleSelectionMetadataProvider(); var store = new JsonModuleSelectionProfileStore(); var services = new ServiceCollection();
            services.AddSingleton<IModuleSelectionMetadataProvider>(metadata); services.AddSingleton<IModuleSelectionProfileStore>(store);
            services.AddDoorsIntegration(); services.AddDoorsIntegration();
            using (var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            {
                Assert.AreSame(metadata, host.GetRequiredService<IModuleSelectionMetadataProvider>()); Assert.AreSame(store, host.GetRequiredService<IModuleSelectionProfileStore>());
                var cursor = host.GetRequiredService<IFolderModuleSelectionService>().Create(SelectionTestAdapter.Request()); Assert.AreEqual(0, cursor.Snapshot().Candidates.Count);
                Assert.AreEqual(1, services.Count(d => d.ServiceType == typeof(IFolderModuleSelectionService)));
            }
        }
        private static IEnumerable<T> Visual<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value; foreach (var nested in Visual<T>(child)) yield return nested; }
        }
    }
}
