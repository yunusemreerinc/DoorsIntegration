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
    [TestClass, TestCategory("ChangeImpactMock"), DoNotParallelize]
    public class ChangeImpactWpfTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod]
        public Task MockEndToEndSelectionDiffGraphLocalReviewAndFourReports() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                var vm = f.Model.ChangeImpact; Assert.IsTrue(f.Model.IsReadOnly); await vm.LoadBaselinesCommand.ExecuteAsync(); Assert.AreEqual(2, vm.BaselineChoices.Count);
                await vm.RunCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails); Assert.IsNotNull(vm.CurrentResult); Assert.AreEqual(ImpactAnalysisStatus.Completed, vm.CurrentResult.Status);
                Assert.IsTrue(vm.ChangeRows.Count > 0); Assert.AreEqual(4, vm.Scene.Nodes.Count); Assert.AreEqual(3, vm.Scene.Edges.Count);
                vm.SelectedNodeId = vm.Scene.Nodes[0].Id; StringAssert.Contains(vm.Details, "Version identity=");
                vm.SelectedFinding = vm.Findings.First(v => v.PathIdentity != null); vm.Reviewer = "test-engineer"; vm.ReviewerComments = "Potential impact confirmed for engineering review; no DOORS write.";
                await vm.ApproveImpactCommand.ExecuteAsync(); Assert.IsTrue(vm.CurrentResult.Reviews.Any(d => d.Decision == ImpactReviewStatus.ConfirmedImpacted));
                await vm.RunCommand.ExecuteAsync(); Assert.IsTrue(vm.CurrentResult.Findings.Any(d => d.ReviewStatus == ImpactReviewStatus.ConfirmedImpacted));
                vm.ExportRoot = Path.Combine(f.Root, "impact-export"); foreach (ExportFormat format in Enum.GetValues(typeof(ExportFormat))) { vm.Format = format; await vm.ExportCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails); }
                vm.ComparisonKind = ChangeImpactComparisonKind.BaselineSets; await vm.RunCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails); Assert.AreEqual(ChangeImpactComparisonKind.BaselineSets, vm.CurrentResult.Kind);
                f.Model.Version = DoorsVersion.V95; Assert.IsNull(vm.CurrentResult); Assert.IsTrue(f.Model.IsReadOnly);
            }
        });
        [TestMethod]
        public Task RenderVirtualizedReadOnlyTabAndCentralValidationErrors() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                var vm = f.Model.ChangeImpact; await vm.RunCommand.ExecuteAsync();
                var view = new ChangeImpactView { DataContext = vm }; var window = new Window { Content = view, Width = 1480, Height = 1060, ShowInTaskbar = false, Title = "Change Impact — MOCK / READ-ONLY" };
                try
                {
                    window.Show(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout();
                    Assert.IsTrue(((DataGrid)view.FindName("ImpactChanges")).EnableRowVirtualization); Assert.IsTrue(((DataGrid)view.FindName("ImpactFindings")).EnableRowVirtualization);
                    var graph = (BaselineTraceabilityGraph)view.FindName("ImpactVersionGraph"); Assert.IsNotNull(graph); Assert.IsTrue(vm.Scene.Nodes.Count <= 300); Assert.IsTrue(vm.Scene.Edges.Count <= 1000);
                    string root = Environment.GetEnvironmentVariable("DOORS_CHANGE_IMPACT_ARTIFACT_ROOT") ?? Path.Combine(Path.GetTempPath(), "DoorsChangeImpactProof"); Directory.CreateDirectory(root);
                    var tabs = (TabControl)view.FindName("ImpactTabs");
                    for (int tab = 0; tab < 3; tab++)
                    {
                        tabs.SelectedIndex = tab; view.UpdateLayout(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        if (tab == 1) { Assert.IsTrue(graph.ActualHeight > 100); graph.SetCurrentValue(BaselineTraceabilityGraph.SelectedNodeIdProperty, vm.Scene.Nodes[0].Id); StringAssert.Contains(vm.Details, "Version identity="); }
                        string image = Path.Combine(root, "change-impact-tab" + tab + "-" + Guid.NewGuid().ToString("N") + ".png");
                        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(image)) png.Save(file); TestContext.AddResultFile(image);
                    }
                }
                finally { window.Close(); }
                vm.PolicyJson = "{invalid"; await vm.RunCommand.ExecuteAsync(); Assert.IsFalse(string.IsNullOrWhiteSpace(f.Model.ErrorDetails)); Assert.IsNull(vm.CurrentResult);
            }
        });
    }
}
