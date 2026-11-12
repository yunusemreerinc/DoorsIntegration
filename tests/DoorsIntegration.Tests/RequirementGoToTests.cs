using System;
using System.Collections.Generic;
using System.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("RequirementGoTo")]
    public class RequirementGoToTests
    {
        [DataTestMethod]
        [DataRow("0"),DataRow("-1"),DataRow("1.2"),DataRow("+2"),DataRow("2147483648"),DataRow(""),DataRow("not-a-number")]
        public void InvalidAbsoluteNumbersAreRejected(string value)
        { Assert.IsFalse(RequirementGoToRequest.Validate(RequirementGoToKind.AbsoluteNumber,value,out _)); Assert.ThrowsException<ArgumentException>(()=>new RequirementGoToRequest(RequirementGoToKind.AbsoluteNumber,value)); }
        [TestMethod] public void EachKindIsExplicitAndUsesExactMatching()
        {
            var records=new[]{Row(19,"FLT-220","1.2.3"),Row(220,"OTHER-220","9.4")}; var service=new RequirementGoToService();
            Assert.AreEqual(220,Locate(service,records,RequirementGoToKind.AbsoluteNumber," 220 ").Record.AbsoluteNumber);
            Assert.AreEqual(19,Locate(service,records,RequirementGoToKind.ObjectIdentifier,"flt-220").Record.AbsoluteNumber);
            Assert.AreEqual(19,Locate(service,records,RequirementGoToKind.ObjectNumber,"1.2.3").Record.AbsoluteNumber);
            Assert.AreEqual(RequirementGoToStatus.NotFound,Locate(service,records,RequirementGoToKind.ObjectIdentifier,"220").Status);
            Assert.AreEqual(RequirementGoToStatus.NotFound,Locate(service,records,RequirementGoToKind.ObjectNumber,"1.2").Status);
        }
        [TestMethod] public void FilteredTargetIsDistinguishedFromMissingAndPageIndexUsesVisibleOrder()
        {
            var rows=new[]{Row(30,"A","1"),Row(4,"B","2"),Row(99,"C","3")}; var service=new RequirementGoToService();
            var hidden=service.Locate(rows,new RequirementGoToRequest(RequirementGoToKind.AbsoluteNumber,"4"),r=>r.AbsoluteNumber!=4,CancellationToken.None); Assert.AreEqual(RequirementGoToStatus.HiddenByFilter,hidden.Status); Assert.AreEqual(-1,hidden.ViewIndex);
            var visible=service.Locate(rows,new RequirementGoToRequest(RequirementGoToKind.AbsoluteNumber,"99"),r=>r.AbsoluteNumber!=4,CancellationToken.None); Assert.AreEqual(1,visible.ViewIndex);
        }
        [TestMethod] public void AmbiguousDisplayIdentifiersNeverSelectAnArbitraryObject()
        {
            var rows=new[]{Row(1,"same","1"),Row(2,"SAME","2")}; var result=Locate(new RequirementGoToService(),rows,RequirementGoToKind.ObjectIdentifier,"Same");
            Assert.AreEqual(RequirementGoToStatus.Ambiguous,result.Status); Assert.AreEqual(2,result.MatchingObjects); Assert.IsNull(result.Record);
        }
        [TestMethod] public void UnreadableNumberIsNotTrustedOrTreatedAsConfirmedAbsence()
        {
            var rows=new[]{Row(1,"A","1.2",RequirementValueStatus.AccessDenied)};
            var result=Locate(new RequirementGoToService(),rows,RequirementGoToKind.ObjectNumber,"1.2"); Assert.AreEqual(RequirementGoToStatus.NotFound,result.Status); Assert.IsTrue(result.NumberingUnavailable);
        }
        [TestMethod] public void StreamsTenThousandRecordsOnceAndHonorsCancellation()
        {
            int count=0; var service=new RequirementGoToService(); var result=Locate(service,Stream(()=>count++),RequirementGoToKind.AbsoluteNumber,"9999"); Assert.AreEqual(10000,count); Assert.AreEqual(9998,result.ViewIndex);
            using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); Assert.ThrowsException<OperationCanceledException>(()=>service.Locate(Stream(()=>{}),new RequirementGoToRequest(RequirementGoToKind.AbsoluteNumber,"1"),r=>true,cancel.Token)); }
        }
        [TestMethod] public void InvalidKindAndControlCharactersAreRejected()
        { Assert.IsFalse(RequirementGoToRequest.Validate((RequirementGoToKind)999,"1",out _)); Assert.IsFalse(RequirementGoToRequest.Validate(RequirementGoToKind.ObjectIdentifier,"A\nB",out _)); Assert.IsFalse(RequirementGoToRequest.Validate(RequirementGoToKind.ObjectIdentifier,new string('x',257),out _)); }
        private static RequirementGoToResult Locate(RequirementGoToService service,IEnumerable<RequirementRecord> rows,RequirementGoToKind kind,string value)=>service.Locate(rows,new RequirementGoToRequest(kind,value),r=>true,CancellationToken.None);
        private static IEnumerable<RequirementRecord> Stream(Action visit) { for(int n=1;n<=10000;n++) { visit(); yield return Row(n,"R-"+n,n.ToString()); } }
        private static RequirementRecord Row(int id,string identifier,string number,RequirementValueStatus status=RequirementValueStatus.Readable)=>new RequirementRecord(id,id,identifier,number,status,1,null,RequirementParentStatus.Root,false,Array.Empty<RequirementValue>());
    }
}
