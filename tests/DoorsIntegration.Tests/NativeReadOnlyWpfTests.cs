using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Wpf.Controls;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("NativeReadOnly"), DoNotParallelize]
    public class NativeReadOnlyWpfTests
    {
        [TestMethod]
        public void UnseenNativeMemberIsShownAsUnknownInsteadOfMissing()
        {
            var module = new BaselineSetModuleReference(new BaselineModuleReference("db", "A", "/P/A"));
            var unknown = new DoorsIntegration.Wpf.ViewModels.NativeSetMemberRow(module, null, false);
            Assert.IsFalse(unknown.Missing); Assert.AreEqual("Unknown", unknown.Resolution);
            Assert.IsTrue(new DoorsIntegration.Wpf.ViewModels.NativeSetMemberRow(module, null).Missing);
        }
        [TestMethod]
        public Task ObserveContextPopulatesRealIdentityOffUiThreadAndPreservesReadOnlyMode() => WpfTestHost.Run(async () =>
        {
            using (var fixture = new ManagerWpfFixture())
            {
                Assert.IsFalse(fixture.Model.ChangeImpact.ObserveContextCommand.CanExecute(null));
                fixture.Model.Mode = ClientMode.RealDoors; var viewModel = fixture.Model.ChangeImpact;
                var session = (ClientSession)fixture.Model.GetType().GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Model);
                var source = new UiReadSource(); typeof(ClientSession).GetProperty(nameof(ClientSession.NativeReads)).SetValue(session, source);
                viewModel.ModulePath = "/P/A"; viewModel.LinkModules = "/P/Links";
                int dispatcherThread = Thread.CurrentThread.ManagedThreadId;
                await viewModel.ObserveContextCommand.ExecuteAsync();
                Assert.AreEqual("", fixture.Model.ErrorDetails); Assert.AreEqual("db", viewModel.EnvironmentId); Assert.AreEqual("db", viewModel.RepositoryId); Assert.AreEqual("A", viewModel.ModuleIdentity);
                Assert.AreNotEqual(dispatcherThread, source.ThreadId); Assert.IsTrue(fixture.Model.IsReadOnly); Assert.AreEqual(1, source.Calls); Assert.IsNull(viewModel.CurrentResult);
                var view = new ChangeImpactView { DataContext = viewModel }; Assert.IsNotNull(view.FindName("ObserveNativeContext"));
            }
        });
        private sealed class UiReadSource : INativeReadObservationSource
        {
            internal int ThreadId, Calls;
            public Task<BaselineSetInspection> ReadAsync(NativeReadQuery query, CancellationToken token)
            { ThreadId = Thread.CurrentThread.ManagedThreadId; Calls++; token.ThrowIfCancellationRequested(); return Task.FromResult(NativeReadOnlyTests.Parse()); }
        }
    }
}
