using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    [TestClass,TestCategory("RequirementFind"),DoNotParallelize]
    public class RequirementFindWpfTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod] public Task FindsOffPageAndPublishesSelectionDetailsWithoutAnotherRead() => WpfTestHost.Run(async()=>
        {
            var reader=new RecordingWpfAdapter(); var factory=new WpfFactory { Build=(profile,log)=>{ var source=MockDoorsAdapter.CreateSession(log); return new ClientSession(new ProjectService(reader),new RequirementService(reader),new LinkService(reader),source.Baselines,source.Export,source.Health); } };
            using(var model=WpfTestHost.Model(factory))
            {
                Assert.IsFalse(model.Find.CanFind); await model.ReadCommand.ExecuteAsync(); int reads=reader.Threads.Count;
                model.Find.Text="input validation 220"; model.Find.WholeWord=true; await model.Find.NextCommand.ExecuteAsync();
                Assert.AreEqual("",model.ErrorDetails); Assert.AreEqual(220,model.SelectedRequirement.Id); StringAssert.Contains(model.PageLabel,"Sayfa 3"); Assert.AreEqual("Object Heading",model.SelectedAttribute.Name); Assert.IsTrue(model.Attributes.Count>0); StringAssert.Contains(model.Find.Status,"1 / 1");
                model.Find.MatchCase=true; await model.Find.NextCommand.ExecuteAsync(); StringAssert.Contains(model.Find.Status,"bulunamadı");
                model.Find.MatchCase=false; await model.Find.NextCommand.ExecuteAsync(); await model.Find.NextCommand.ExecuteAsync(); StringAssert.Contains(model.Find.Status,"Baştan");
                Assert.AreEqual(reads,reader.Threads.Count,"Find must not reopen a module or perform COM reads."); Assert.IsTrue(model.IsReadOnly);
                model.ModulePath="/another/module"; Assert.IsFalse(model.Find.CanFind); StringAssert.Contains(model.Find.Scope,"Önce");
            }
        });
        [TestMethod] public Task RespectsQuickAndDeletedFiltersAndNavigatesBackwards() => WpfTestHost.Run(async()=>
        {
            using(var model=WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); model.Search="Input validation 22"; model.Find.Text="validation"; await model.Find.NextCommand.ExecuteAsync(); Assert.AreEqual(22,model.SelectedRequirement.Id);
                await model.Find.NextCommand.ExecuteAsync(); Assert.AreEqual(220,model.SelectedRequirement.Id); await model.Find.PreviousCommand.ExecuteAsync(); Assert.AreEqual(22,model.SelectedRequirement.Id);
                model.Search=""; model.Filter=RequirementFilter.Deleted; model.Find.Text="FLT-8"; await model.Find.NextCommand.ExecuteAsync(); Assert.AreEqual(8,model.SelectedRequirement.Id); Assert.IsTrue(model.SelectedRequirement.IsDeleted);
                model.Find.Wrap=false; await model.Find.NextCommand.ExecuteAsync(); await model.Find.NextCommand.ExecuteAsync(); StringAssert.Contains(model.Find.Status,"başka eşleşme yok");
            }
        });
        [TestMethod] public Task ModelessDialogHasOptionsShortcutsSelectionAndReusableLifetime() => WpfTestHost.Run(async()=>
        {
            var model=WpfTestHost.Model(); var main=new MainWindow(model); var previous=Application.Current.MainWindow; Application.Current.MainWindow=main;
            var listener=new WpfBindingLog(); System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener); System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level=System.Diagnostics.SourceLevels.Error;
            try
            {
                main.Show(); await WpfTestHost.Until(()=>model.IsIdle&&model.Requirements.Count>0); Assert.IsTrue(ApplicationCommands.Find.CanExecute(null,main));
                ApplicationCommands.Find.Execute(null,main); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); var dialog=main.OwnedWindows.Cast<Window>().OfType<FindWindow>().Single();
                Assert.IsNotNull(dialog.FindName("MatchCase")); Assert.IsNotNull(dialog.FindName("WholeWord")); Assert.IsTrue(main.InputBindings.OfType<KeyBinding>().Any(k=>k.Key==Key.F&&k.Modifiers==ModifierKeys.Control));
                model.Find.Text="Input validation 220"; model.Find.WholeWord=true; await model.Find.NextCommand.ExecuteAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var grid=(DataGrid)main.FindName("RequirementsGrid"); Assert.AreEqual(220,((RequirementListRow)grid.SelectedItem).Id); Assert.IsNotNull(grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem));
                dialog.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(dialog); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                string root=Environment.GetEnvironmentVariable("DOORS_FIND_ARTIFACT_ROOT")??Path.Combine(Path.GetTempPath(),"DoorsFindProof"); Directory.CreateDirectory(root); string path=Path.Combine(root,"find-"+(Environment.Is64BitProcess?"x64":"x86")+".png"); using(var file=File.Create(path)) png.Save(file); TestContext.AddResultFile(path);
                ApplicationCommands.Find.Execute(null,main); Assert.AreEqual(1,main.OwnedWindows.Cast<Window>().OfType<FindWindow>().Count());
                ApplicationCommands.Close.Execute(null,dialog); Assert.IsFalse(dialog.IsVisible); ApplicationCommands.Find.Execute(null,main); Assert.AreEqual(model.Find.Text,((TextBox)main.OwnedWindows.Cast<Window>().OfType<FindWindow>().Single().FindName("FindText")).Text);
                Assert.AreEqual(0,listener.Errors.Count,string.Join("\n",listener.Errors));
            }
            finally { System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await model.PrepareCloseAsync(); main.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Application.Current.MainWindow=previous?.IsVisible==true?previous:null; }
        });
    }
}
