using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("ChangeImpactMock")]
    public class ChangeImpactDetectionTests
    {
        private static ImpactRequirementVersion Sys(ImpactAnalysisSnapshot s) => s.Requirements.Single(v => v.Version.ModuleIdentity == "SYS");
        [TestMethod]
        public async Task TextChangeRetainsPersistentAndSeparateVersionIdentity()
        {
            var provider = new MockChangeImpactSnapshotProvider(); var request = MockChangeImpactSnapshotProvider.DemoRequest();
            var a = await provider.CaptureAsync(request.Source, request.Policy, null, CancellationToken.None); var b = await provider.CaptureAsync(request.Target, request.Policy, null, CancellationToken.None);
            var change = new RequirementChangeDetector().Detect(a, b, request.Policy).Single();
            Assert.AreEqual(change.Before.RequirementIdentity, change.After.RequirementIdentity); Assert.AreNotEqual(change.Before.RequirementVersionIdentity, change.After.RequirementVersionIdentity);
            Assert.AreEqual(RequirementChangeType.Modified, change.PrimaryType); Assert.AreEqual(ImpactSeverity.High, change.Severity); Assert.AreEqual(1, change.Attributes.Count(v => v.SemanticChanged));
        }
        [DataTestMethod, DataRow("Add", RequirementChangeType.Added), DataRow("Delete", RequirementChangeType.Deleted), DataRow("Move", RequirementChangeType.Moved), DataRow("Attribute", RequirementChangeType.AttributeChanged), DataRow("Unknown", RequirementChangeType.Unknown), DataRow("Unchanged", RequirementChangeType.Unchanged)]
        public void LifecycleHierarchyAndUnavailableAttributesAreSeparate(string scenario, RequirementChangeType expected)
        {
            var mock = new MockChangeImpactSnapshotProvider(); var a = ImpactSerialization.Clone(mock.Before); var b = ImpactSerialization.Clone(a); var target = Sys(b);
            switch (scenario)
            {
                case "Add": a.Requirements.Remove(Sys(a)); break;
                case "Delete": b.Requirements.Remove(target); break;
                case "Move": target.ParentAbsoluteNumber = 7; break;
                case "Attribute": MockChangeImpactSnapshotProvider.SetAttribute(target, "Priority", "3"); break;
                case "Unknown": target.Attributes["Priority"].PlainStatus = RequirementValueStatus.AccessDenied; target.Attributes["Priority"].PlainText = null; break;
            }
            var change = new RequirementChangeDetector().Detect(a, b, new ImpactAnalysisPolicy()).Single(v => (v.After ?? v.Before).Version.ModuleIdentity == "SYS");
            Assert.AreEqual(expected, change.PrimaryType); if (scenario == "Delete") Assert.IsNotNull(change.Before.Value("Object Text"));
        }
        [TestMethod]
        public void DeniedInventoryCannotProveDeletedObjects()
        {
            var mock = new MockChangeImpactSnapshotProvider(); var b = ImpactSerialization.Clone(mock.Before); b.Requirements.Remove(Sys(b)); b.Links.Context.Modules.Single(m => m.ModuleIdentity == "SYS").State = BaselineVerificationReadState.AccessDenied;
            var c = new RequirementChangeDetector().Detect(mock.Before, b, new ImpactAnalysisPolicy()).Single(v => v.Before.Version.ModuleIdentity == "SYS");
            Assert.AreEqual(RequirementChangeType.Unknown, c.PrimaryType); Assert.IsFalse(c.Flags.HasFlag(RequirementChangeFlags.Deleted));
        }
        [TestMethod]
        public void TypedNumericNormalizationAndOpaqueFormattingAreDistinguished()
        {
            var m = new MockChangeImpactSnapshotProvider(); var a = ImpactSerialization.Clone(m.Before); var b = ImpactSerialization.Clone(a);
            MockChangeImpactSnapshotProvider.SetAttribute(Sys(a), "Priority", "02", "Integer"); MockChangeImpactSnapshotProvider.SetAttribute(Sys(b), "Priority", "2", "Integer");
            Sys(b).Attributes["Object Text"].RichText += " ";
            var c = new RequirementChangeDetector().Detect(a, b, new ImpactAnalysisPolicy()).Single(v => v.Before.Version.ModuleIdentity == "SYS");
            Assert.IsTrue(c.Flags.HasFlag(RequirementChangeFlags.FormattingOnly)); Assert.IsTrue(c.Flags.HasFlag(RequirementChangeFlags.NormalizationOnly)); Assert.IsFalse(c.Attributes.Single(v => v.Name == "Priority").SemanticChanged); Assert.AreEqual(ImpactSeverity.Low, c.Severity);
        }
        [TestMethod]
        public void JsonPolicyDefaultsAndInvalidLimitsAreValidated()
        {
            var p = new JsonImpactPolicyProvider().ReadJson("{\"Id\":\"project-test\",\"MaximumDepth\":4}"); Assert.AreEqual(4, p.MaximumDepth); Assert.AreEqual(20000, p.MaximumPaths);
            Assert.ThrowsException<ArgumentException>(() => new JsonImpactPolicyProvider().ReadJson("{\"MaximumDepth\":101}"));
        }
    }
}
