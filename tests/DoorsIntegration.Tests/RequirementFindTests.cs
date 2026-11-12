using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("RequirementFind")]
    public class RequirementFindTests
    {
        [DataTestMethod]
        [DataRow("Input validation","input",false,false,true)]
        [DataRow("Input validation","input",true,false,false)]
        [DataRow("Input validation","Input",true,true,true)]
        [DataRow("requirement requirements","require",false,true,false)]
        [DataRow("requirement-test","requirement",false,true,true)]
        [DataRow("requirement_test","requirement",false,true,false)]
        [DataRow("éclair","clair",false,true,false)]
        [DataRow("x\u0301","x",false,true,false)]
        [DataRow("Türkçe 注釈","注釈",false,true,true)]
        [DataRow("item [x] .+","[x]",false,false,true)]
        [DataRow("𐐀test","test",false,true,false)]
        public void LiteralCaseAndUnicodeWordBoundaries(string value,string query,bool matchCase,bool wholeWord,bool expected)
        { Assert.AreEqual(expected,RequirementFindService.Matches(value,new RequirementFindOptions(query,matchCase,wholeWord))); }
        [TestMethod] public void NextPreviousFollowSnapshotOrderAndWrapOnlyWhenEnabled()
        {
            var records=new[]{Row(20,"match"),Row(3,"match"),Row(400,"match")}; var service=new RequirementFindService();
            var next=service.Find(records,new RequirementFindOptions("match"),RequirementFindDirection.Next,3,false,r=>true,CancellationToken.None); Assert.AreEqual(400,next.Hit.Record.AbsoluteNumber); Assert.AreEqual(3,next.Matches);
            var previous=service.Find(records,new RequirementFindOptions("match"),RequirementFindDirection.Previous,3,false,r=>true,CancellationToken.None); Assert.AreEqual(20,previous.Hit.Record.AbsoluteNumber);
            var wrapped=service.Find(records,new RequirementFindOptions("match"),RequirementFindDirection.Next,400,false,r=>true,CancellationToken.None); Assert.AreEqual(20,wrapped.Hit.Record.AbsoluteNumber); Assert.IsTrue(wrapped.Wrapped);
            var end=service.Find(records,new RequirementFindOptions("match",wrap:false),RequirementFindDirection.Next,400,false,r=>true,CancellationToken.None); Assert.IsNull(end.Hit); Assert.AreEqual(3,end.Matches);
        }
        [TestMethod] public void InitialSearchIncludesAnchorAndFilteredIndicesStayAccurate()
        {
            var records=new[]{Row(1,"hit"),Row(2,"hit"),Row(3,"hit")}; var service=new RequirementFindService();
            var result=service.Find(records,new RequirementFindOptions("hit"),RequirementFindDirection.Next,3,true,r=>r.AbsoluteNumber!=2,CancellationToken.None);
            Assert.AreEqual(3,result.Hit.Record.AbsoluteNumber); Assert.AreEqual(1,result.Hit.ViewIndex); Assert.AreEqual(2,result.Hit.Ordinal); Assert.AreEqual(2,result.Matches);
            result=service.Find(records,new RequirementFindOptions("hit"),RequirementFindDirection.Previous,null,false,r=>true,CancellationToken.None); Assert.AreEqual(3,result.Hit.Record.AbsoluteNumber);
        }
        [TestMethod] public void DeniedPlainAndOpaqueRichContentAreNotSearched()
        {
            var denied=new RequirementValue("Object Text","Text",RequirementValueStatus.AccessDenied,"SECRET",RequirementValueStatus.Readable,"SECRET");
            var row=Row(1,"ordinary",denied); Assert.IsNull(RequirementFindService.MatchedField(row,new RequirementFindOptions("SECRET"),CancellationToken.None));
            Assert.AreEqual("Object Identifier",RequirementFindService.MatchedField(row,new RequirementFindOptions("REQ-1"),CancellationToken.None));
        }
        [TestMethod] public void StreamsTenThousandObjectsOnceAndHonorsCancellation()
        {
            int visits=0; var service=new RequirementFindService(); var result=service.Find(Stream(()=>visits++),new RequirementFindOptions("unique target"),RequirementFindDirection.Next,null,true,r=>true,CancellationToken.None);
            Assert.AreEqual(10000,visits); Assert.AreEqual(9999,result.Hit.Record.AbsoluteNumber); Assert.AreEqual(1,result.Matches);
            using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); Assert.ThrowsException<OperationCanceledException>(()=>service.Find(Stream(()=>{}),new RequirementFindOptions("x"),RequirementFindDirection.Next,null,false,r=>true,cancel.Token)); }
        }
        private static IEnumerable<RequirementRecord> Stream(Action visit) { for(int n=1;n<=10000;n++) { visit(); yield return Row(n,n==9999?"unique target":"ordinary"); } }
        private static RequirementRecord Row(int id,string text,RequirementValue value=null)=>new RequirementRecord(id,id,"REQ-"+id,id.ToString(),RequirementValueStatus.Readable,1,null,RequirementParentStatus.Root,false,new[]{new RequirementValue("Object Heading","Text",RequirementValueStatus.Readable,text,RequirementValueStatus.NotApplicable,null),value??new RequirementValue("Object Text","Text",RequirementValueStatus.Readable,text,RequirementValueStatus.NotApplicable,null)});
    }
}
