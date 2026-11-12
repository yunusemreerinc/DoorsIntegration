using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("RequirementGoTo"),DoNotParallelize]
    public class RequirementGoToWpfTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod] public Task OffPageGoToLoadsSelectionDetailsWithoutAnotherAdapterRead() => WpfTestHost.Run(async()=>
        {
            var reader=new RecordingWpfAdapter(); var factory=new WpfFactory { Build=(p,l)=>{ var source=MockDoorsAdapter.CreateSession(l); return new ClientSession(new ProjectService(reader),new RequirementService(reader),new LinkService(reader),source.Baselines,source.Export,source.Health); } };
            using(var model=WpfTestHost.Model(factory))
            {
                Assert.IsFalse(model.GoTo.CanGo); await model.ReadCommand.ExecuteAsync(); int reads=reader.Threads.Count;
                model.GoTo.Text="220"; await model.GoTo.GoCommand.ExecuteAsync(); Assert.AreEqual("",model.ErrorDetails); Assert.AreEqual(220,model.SelectedRequirement.Id); StringAssert.Contains(model.PageLabel,"Sayfa 3"); Assert.IsTrue(model.Attributes.Count>0);
                model.GoTo.Kind=RequirementGoToKind.ObjectIdentifier; model.GoTo.Text="flt-150"; await model.GoTo.GoCommand.ExecuteAsync(); Assert.AreEqual(150,model.SelectedRequirement.Id);
                model.GoTo.Kind=RequirementGoToKind.ObjectNumber; model.GoTo.Text="1"; await model.GoTo.GoCommand.ExecuteAsync(); Assert.AreEqual(1,model.SelectedRequirement.Id);
                Assert.AreEqual(reads,reader.Threads.Count); Assert.IsTrue(model.IsReadOnly);
                model.ModulePath="/changed"; Assert.IsFalse(model.GoTo.CanGo); StringAssert.Contains(model.GoTo.Scope,"Önce");
            }
        });
        [TestMethod] public Task HiddenTargetNeedsExplicitFilterClearingAndMissLeavesSelectionUntouched() => WpfTestHost.Run(async()=>
        {
            using(var model=WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); model.GoTo.Text="1"; await model.GoTo.GoCommand.ExecuteAsync();
                model.Search="Input validation 22"; await model.SearchCommand.ExecuteAsync(); model.SelectedRequirement=model.Requirements.First(); int previous=model.SelectedRequirement.Id;
                model.GoTo.Text="8"; await model.GoTo.GoCommand.ExecuteAsync(); StringAssert.Contains(model.GoTo.Status,"filtrelerde gizli"); Assert.AreEqual(previous,model.SelectedRequirement.Id); Assert.AreEqual("Input validation 22",model.Search);
                model.GoTo.ClearFilters=true; await model.GoTo.GoCommand.ExecuteAsync(); Assert.AreEqual(8,model.SelectedRequirement.Id); Assert.IsTrue(model.SelectedRequirement.IsDeleted); Assert.AreEqual("",model.Search); Assert.AreEqual(RequirementFilter.All,model.Filter);
                model.GoTo.Text="99999"; await model.GoTo.GoCommand.ExecuteAsync(); Assert.AreEqual(8,model.SelectedRequirement.Id); StringAssert.Contains(model.GoTo.Status,"snapshot içinde bulunamadı");
                model.GoTo.Text="-1"; Assert.IsFalse(model.GoTo.CanGo); StringAssert.Contains(model.GoTo.Status,"pozitif");
            }
        });
        [TestMethod] public Task SelectedBaselineRemainsInScope() => WpfTestHost.Run(async()=>
        {
            using(var model=WpfTestHost.Model())
            {
                model.Tools.ReadOptions.Field("baseline").Flag=true; model.Tools.ReadOptions.Field("major").Value="1"; model.Tools.ReadOptions.Field("minor").Value="2"; model.Tools.ReadOptions.Field("suffix").Value="TEST";
                await model.ReadCommand.ExecuteAsync(); model.GoTo.Text="220"; await model.GoTo.GoCommand.ExecuteAsync(); Assert.AreEqual(220,model.SelectedRequirement.Id); StringAssert.Contains(model.GoTo.Scope,"Baseline 1.2 [TEST]");
                model.GoTo.Text="9"; await model.GoTo.GoCommand.ExecuteAsync(); StringAssert.Contains(model.GoTo.Status,"filtrelerde gizli"); Assert.AreEqual(220,model.SelectedRequirement.Id);
            }
        });
        [TestMethod] public Task DialogCtrlGSingleInstanceSelectionAndFindStillWork() => WpfTestHost.Run(async()=>
        {
            var model=WpfTestHost.Model(); var main=new MainWindow(model); var previous=Application.Current.MainWindow; Application.Current.MainWindow=main;
            var log=new WpfBindingLog(); System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(log); System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level=System.Diagnostics.SourceLevels.Error;
            try
            {
                Assert.IsFalse(WorkspaceNavigationCommands.GoTo.CanExecute(null,main)); main.Show(); await WpfTestHost.Until(()=>model.IsIdle&&model.Requirements.Count>0); Assert.IsTrue(WorkspaceNavigationCommands.GoTo.CanExecute(null,main));
                Assert.IsTrue(main.InputBindings.OfType<KeyBinding>().Any(k=>k.Key==Key.G&&k.Modifiers==ModifierKeys.Control)); WorkspaceNavigationCommands.GoTo.Execute(null,main); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var dialog=main.OwnedWindows.Cast<Window>().OfType<GoToWindow>().Single(); Assert.IsNotNull(dialog.FindName("GoToKind")); Assert.IsNotNull(dialog.FindName("ClearFilters"));
                model.GoTo.Text="220"; await model.GoTo.GoCommand.ExecuteAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); var grid=(DataGrid)main.FindName("RequirementsGrid"); Assert.AreEqual(220,((RequirementListRow)grid.SelectedItem).Id); Assert.IsNotNull(grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem));
                dialog.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(dialog); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                string root=Environment.GetEnvironmentVariable("DOORS_GOTO_ARTIFACT_ROOT")??Path.Combine(Path.GetTempPath(),"DoorsGoToProof"); Directory.CreateDirectory(root); string path=Path.Combine(root,"goto-"+(Environment.Is64BitProcess?"x64":"x86")+".png"); using(var file=File.Create(path)) png.Save(file); TestContext.AddResultFile(path);
                WorkspaceNavigationCommands.GoTo.Execute(null,main); Assert.AreEqual(1,main.OwnedWindows.Cast<Window>().OfType<GoToWindow>().Count()); ApplicationCommands.Close.Execute(null,dialog);
                WorkspaceNavigationCommands.GoTo.Execute(null,main); Assert.AreEqual("220",((TextBox)main.OwnedWindows.Cast<Window>().OfType<GoToWindow>().Single().FindName("GoToText")).Text);
                model.Find.Text="Input validation 221"; model.Find.WholeWord=true; await model.Find.NextCommand.ExecuteAsync(); Assert.AreEqual(221,model.SelectedRequirement.Id);
                Assert.AreEqual(0,log.Errors.Count,string.Join("\n",log.Errors));
            }
            finally { System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(log); await model.PrepareCloseAsync(); main.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Application.Current.MainWindow=previous?.IsVisible==true?previous:null; }
        });
    }
}
