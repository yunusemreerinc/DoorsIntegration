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
    [TestClass, DoNotParallelize, TestCategory("BaselineVerificationMock")]
    public class BaselineVerificationWpfTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod]
        public Task ReadOnlyDemoShowsOverviewGraphNodeDetailsImpactAndReports() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                var vm = f.Model.BaselineTraceability; Assert.IsTrue(f.Model.IsReadOnly); Assert.IsTrue(vm.VerifyCommand.CanExecute(null));
                await vm.VerifyCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails); Assert.IsNotNull(vm.CurrentResult); Assert.AreEqual(BaselineVerificationStatus.Passed, vm.CurrentResult.Status, string.Join(";", vm.CurrentResult.Findings.Select(x => x.Code)));
                Assert.IsTrue(f.Model.IsReadOnly); Assert.AreEqual(0, f.Review.Reviews.Count, "Read-only verification must not invoke a write approval/mutation flow."); Assert.AreEqual(4, vm.Scene.Nodes.Count); Assert.AreEqual(3, vm.Scene.Edges.Count); StringAssert.Contains(vm.Overview, "CLOSED");
                vm.SelectedNodeId = vm.Scene.Nodes.Single(n => n.Node.Role == "SYS").Id; StringAssert.Contains(vm.Details, "Persistent object identity"); StringAssert.Contains(vm.Details, "Rich text");
                await vm.ImpactCommand.ExecuteAsync(); StringAssert.Contains(vm.Details, "Impact visits=4"); await vm.ClearImpactCommand.ExecuteAsync();
                vm.SelectedEdgeId = vm.Scene.Edges[0].Edge.Id; StringAssert.Contains(vm.Details, "Target baseline identity");
                vm.Search = "HLR"; Assert.AreEqual(1, vm.Scene.Nodes.Count); vm.Search = ""; Assert.AreEqual(4, vm.Scene.Nodes.Count);
                await vm.CaptureReferenceCommand.ExecuteAsync(); await vm.VerifyCommand.ExecuteAsync(); Assert.IsTrue(vm.CurrentResult.ReferenceAvailable); Assert.AreEqual(0, vm.CurrentResult.Changes.Count);
                vm.ExportRoot = Path.Combine(f.Root, "traceability-reports"); foreach (ExportFormat format in Enum.GetValues(typeof(ExportFormat))) { vm.Format = format; await vm.ExportCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails, vm.Details); }
                Assert.AreEqual(1, Directory.GetFiles(vm.ExportRoot, "data.html", SearchOption.AllDirectories).Length); Assert.AreEqual(1, Directory.GetFiles(vm.ExportRoot, "data.xlsx", SearchOption.AllDirectories).Length);
            }
        });
        [TestMethod]
        public Task InvalidPolicyIsCentrallyReportedAndProfileChangeClearsReference() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                var vm = f.Model.BaselineTraceability; await vm.VerifyCommand.ExecuteAsync(); await vm.CaptureReferenceCommand.ExecuteAsync();
                vm.PolicyJson = "{invalid"; await vm.VerifyCommand.ExecuteAsync(); Assert.IsFalse(string.IsNullOrEmpty(f.Model.ErrorDetails)); Assert.IsNull(vm.CurrentResult);
                f.Model.Version = DoorsVersion.V95; Assert.IsNull(vm.CurrentResult); StringAssert.Contains(vm.ReferenceLabel, "No reference");
            }
        });
        [TestMethod]
        public Task NewTabRendersBoundedVersionedGraphWithReadonlyControls() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                var vm = f.Model.BaselineTraceability; await vm.VerifyCommand.ExecuteAsync();
                var view = new BaselineTraceabilityView { DataContext = vm }; var window = new Window { Width = 1480, Height = 1060, Content = view, ShowInTaskbar = false, Title = "Baseline Traceability — MOCK / READ-ONLY" };
                try
                {
                    window.Show(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var graph = (BaselineTraceabilityGraph)view.FindName("BaselineVersionGraph"); Assert.IsNotNull(graph); Assert.AreEqual(4, graph.Scene.Nodes.Count); Assert.IsTrue(graph.ActualHeight > 100);
                    graph.SetCurrentValue(BaselineTraceabilityGraph.SelectedNodeIdProperty, graph.Scene.Nodes[0].Id); StringAssert.Contains(vm.Details, "Module baseline=");
                    Assert.IsTrue(((DataGrid)view.FindName("BaselineTraceabilityFindings")).EnableRowVirtualization);
                    view.UpdateLayout(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    string root = Environment.GetEnvironmentVariable("DOORS_BASELINE_VERIFICATION_ARTIFACT_ROOT") ?? Path.Combine(Path.GetTempPath(), "DoorsBaselineTraceabilityProof"); Directory.CreateDirectory(root); string path = Path.Combine(root, "baseline-traceability-" + Guid.NewGuid().ToString("N") + ".png");
                    var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(path)) encoder.Save(output); TestContext.AddResultFile(path);
                }
                finally { window.Close(); }
            }
        });
    }
}
