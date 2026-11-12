using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("DashboardMock"),DoNotParallelize]
    public class DashboardWpfTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod] public Task DashboardCommandsUseBackgroundServicesAndRetainValidatedContext() => WpfTestHost.Run(async()=>
        {
            using(var fixture=new ManagerWpfFixture())
            {
                var owner=fixture.Model; var vm=owner.Dashboard;
                await vm.LoadCachedCommand.ExecuteAsync(); Assert.IsNull(vm.CurrentSnapshot);
                await vm.RefreshCommand.ExecuteAsync(); Assert.AreEqual("",owner.ErrorDetails); Assert.IsNotNull(vm.CurrentSnapshot); Assert.IsNull(DashboardTestData.Metric(vm.CurrentSnapshot,"Coverage"));
                await vm.RunAnalysisCommand.ExecuteAsync(); Assert.AreEqual("",owner.ErrorDetails); Assert.AreEqual(100m,DashboardTestData.Metric(vm.CurrentSnapshot,"Coverage")); Assert.IsTrue(vm.Kpis.Count==6);
                var before=vm.CurrentSnapshot.SnapshotId; await vm.LoadCachedCommand.ExecuteAsync(); Assert.AreEqual(before,vm.CurrentSnapshot.SnapshotId);
                vm.ProfileName="reusable"; await vm.SaveProfileCommand.ExecuteAsync(); vm.RequirementStatus="unmatched"; Assert.IsNull(vm.CurrentSnapshot);
                await vm.LoadProfileCommand.ExecuteAsync(); Assert.AreEqual("",vm.RequirementStatus); await vm.LoadCachedCommand.ExecuteAsync(); Assert.AreEqual(before,vm.CurrentSnapshot.SnapshotId);
                vm.SelectedModule=vm.Modules.First(); DashboardDestination? destination=null; owner.DashboardNavigationRequested+=d=>destination=d;
                await vm.OpenModuleCommand.ExecuteAsync(); Assert.AreEqual("",owner.ErrorDetails); Assert.AreEqual(DashboardDestination.ModuleExplorer,destination); Assert.AreEqual(vm.SelectedModule.ModulePath,owner.ModulePath); Assert.IsTrue(owner.Requirements.Count>0); StringAssert.Contains(owner.DashboardContext,vm.SelectedModule.ModuleIdentity);
                vm.SelectedFinding=vm.Findings.First(f=>f.ModuleIdentity!=null&&f.AnalysisIdentity!=null); await vm.OpenFindingCommand.ExecuteAsync(); Assert.AreEqual("",owner.ErrorDetails); StringAssert.Contains(owner.DashboardContext,vm.SelectedFinding.Id);
                Assert.IsTrue(owner.IsReadOnly); Assert.AreEqual(0,fixture.Review.Reviews.Count,"Dashboard must never request or execute writes.");
                vm.CacheTtlSeconds="bad"; await vm.RefreshCommand.ExecuteAsync(); Assert.IsFalse(string.IsNullOrEmpty(owner.ErrorDetails));
                owner.Version=DoorsVersion.V95; Assert.IsNull(vm.CurrentSnapshot);
            }
        });
        [TestMethod] public Task RenderDashboardTabsWithVirtualizedBoundedLists() => WpfTestHost.Run(async()=>
        {
            using(var fixture=new ManagerWpfFixture())
            {
                var vm=fixture.Model.Dashboard; await vm.RunAnalysisCommand.ExecuteAsync(); Assert.IsNotNull(vm.CurrentSnapshot,fixture.Model.ErrorDetails);
                var view=new ConfigurationDashboardView { DataContext=vm }; var window=new Window { Content=view,Width=1460,Height=1000,ShowInTaskbar=false,Title="Configuration Dashboard · MOCK · READ-ONLY" };
                try
                {
                    window.Show(); await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); await WpfTestHost.Until(()=>fixture.Model.IsIdle); view.UpdateLayout();
                    var tabs=Find<TabControl>(view); Assert.IsNotNull(tabs); Assert.AreEqual(10,tabs.Items.Count);
                    var environment=(ComboBox)view.FindName("DashboardEnvironment"); Assert.AreEqual(vm.EnvironmentId,environment.SelectedItem); Assert.IsNotNull(environment.SelectedItem);
                    string original=vm.EnvironmentId; vm.EnvironmentId="explicit-other-environment"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.AreEqual("explicit-other-environment",vm.EnvironmentId); Assert.AreEqual(vm.EnvironmentId,environment.SelectedItem); vm.EnvironmentId=original; await vm.LoadCachedCommand.ExecuteAsync(); Assert.AreEqual(original,environment.SelectedItem); Assert.IsNotNull(vm.CurrentSnapshot);
                    foreach(int index in new[]{0,1,2,3,6,7,9})
                    {
                        tabs.SelectedIndex=index; view.UpdateLayout(); await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
                        foreach(var grid in Descendants<DataGrid>(view)) { Assert.IsTrue(grid.EnableRowVirtualization); Assert.IsTrue(grid.EnableColumnVirtualization); }
                        if(index==0||index==2||index==9)
                        {
                            string root=Environment.GetEnvironmentVariable("DOORS_DASHBOARD_ARTIFACT_ROOT")??Path.Combine(Path.GetTempPath(),"DoorsDashboardProof"); Directory.CreateDirectory(root);
                            string path=Path.Combine(root,"dashboard-"+index+"-"+Guid.NewGuid().ToString("N")+".png");
                            var bmp=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32); bmp.Render(view); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using(var file=File.Create(path)) png.Save(file); TestContext.AddResultFile(path);
                        }
                    }
                    Assert.IsTrue(vm.Findings.Count<=100); Assert.IsTrue(vm.Modules.Count<=100);
                }
                finally { window.Close(); }
            }
        });
        private static T Find<T>(DependencyObject parent) where T:DependencyObject => Descendants<T>(parent).FirstOrDefault();
        private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        { for(int n=0;n<VisualTreeHelper.GetChildrenCount(parent);n++) { var child=VisualTreeHelper.GetChild(parent,n); if(child is T match) yield return match; foreach(var nested in Descendants<T>(child)) yield return nested; } }
    }
}
