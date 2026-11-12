using System;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("DashboardMock")]
    public class DashboardContractTests
    {
        [TestMethod] public void CacheIdentityBindsEnvironmentPolicyProfileFiltersAndVersion()
        {
            var a=new DashboardRequest(); string key=a.Key;
            a.TraceabilityPolicyVersion="v2"; Assert.AreNotEqual(key,a.Key);
            a=new DashboardRequest(); a.Filters.RequirementStatus="Approved"; Assert.AreNotEqual(key,a.Key);
            a=new DashboardRequest(); a.IsMock=false; Assert.AreNotEqual(key,a.Key);
            a=new DashboardRequest(); a.ClientVersion=DoorsVersion.V95; Assert.AreNotEqual(key,a.Key);
        }
        [TestMethod] public void ScopeOrderAndDuplicatesDoNotCreateDifferentCaches()
        {
            var a=new DashboardRequest(); a.Scope.Projects.Add("/Other");
            var b=new DashboardRequest(); b.Scope.Projects.Insert(0,"/Other"); b.Scope.Projects.Add("/Demo");
            Assert.AreEqual(a.Key,b.Key);
        }
        [TestMethod] public void BaselineIdentityAndPersistentObjectIdentityRemainDistinct()
        {
            var a=new BaselineVersionReference("db","module","/P/M",1,4); var b=new BaselineVersionReference("db","module","/P/M",2,0);
            Assert.AreEqual(a.ObjectIdentity(42),b.ObjectIdentity(42)); Assert.AreNotEqual(a.NodeIdentity(42),b.NodeIdentity(42));
        }
        [TestMethod] public void InvalidWeightsAndAmbiguousVersionSelectionsAreRejected()
        {
            var r=new DashboardRequest(); r.Score.Weights["Baseline"]=100; Assert.ThrowsException<ArgumentException>(()=>r.Validate());
            r=new DashboardRequest(); r.Scope.Kind=DashboardScopeKind.SelectedFormalModules;
            r.Scope.Modules.Add(new BaselineVersionReference(r.DatabaseIdentity,"M","/P/M",1,0)); r.Scope.Modules.Add(new BaselineVersionReference(r.DatabaseIdentity,"M","/P/M",2,0));
            Assert.ThrowsException<ArgumentException>(()=>r.Validate());
        }
    }
}
