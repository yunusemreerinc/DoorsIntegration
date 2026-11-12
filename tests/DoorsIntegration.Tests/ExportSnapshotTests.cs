using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ExportSnapshotTests
    {
        [TestMethod]
        public async Task ExistingDiscoveryRequirementsAttributesAndLinksMapToNormalizedTables()
        {
            using (var context = new ExportTestContext())
            using (var requirements = new RequirementTestContext())
            using (var links = new LinkTestContext())
            using (var requirementSnapshot = await requirements.Reader.ReadAsync(requirements.Request))
            using (var linkSnapshot = await links.Reader.ReadAsync(links.Request))
            {
                var discovery = new DiscoverySnapshot(Guid.NewGuid(), DiscoveryKind.Hierarchy, new DiscoveryScope("/P"), new[] {
                    new DoorsDiscoveryItem("/P/F", "F", DoorsItemKind.Folder, DoorsAccessStatus.Readable),
                    new DoorsDiscoveryItem("/P/M", "M", DoorsItemKind.FormalModule, DoorsAccessStatus.Readable) }, Enumerable.Empty<DiscoveryIssue>(), 1, false, DateTime.UtcNow);
                var source = new SnapshotExportSource(new[] { discovery }, new[] { requirementSnapshot }, new[] { linkSnapshot });
                var result = await context.Service.ExportAsync(context.Request(), source);
                Assert.AreEqual(ExportStatus.Completed, result.Status);
                var data = ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json"));
                Assert.AreEqual("/P/F", data.Tables.Projects.Single().Path); Assert.AreEqual("/P/M", data.Tables.Modules.Single().Path);
                Assert.AreEqual(10L, data.Tables.Requirements.Single().Absolute);
                Assert.AreEqual("body", data.Tables.Attributes.Single(value => value.Name == "Object Text").Plain);
                Assert.AreEqual("{\\rtf1 body}", data.Tables.Attributes.Single(value => value.Name == "Object Text").Rich);
                Assert.AreEqual(10, data.Tables.Links.Single().Source); Assert.AreEqual(20, data.Tables.Links.Single().Target);
                Assert.AreEqual(3, data.Tables.Scopes.Length);
                Assert.AreEqual(1, requirementSnapshot.ReadObjects().Count()); Assert.AreEqual(1, linkSnapshot.ReadLinks().Count());
            }
        }
        [TestMethod]
        public async Task PartialSnapshotExportsExplicitScopeAndUnavailableAttributeState()
        {
            using (var context = new ExportTestContext())
            using (var requirements = new RequirementTestContext())
            {
                requirements.Reply = id => RequirementTestContext.Batch(id, requirements.Request, RequirementTestContext.Object(1, 10, values: new[] {
                    RequirementTestContext.Value("Object Heading"), RequirementTestContext.Value("Object Text", status: "AccessDenied", richStatus: "AccessDenied") }));
                using (var snapshot = await requirements.Reader.ReadAsync(requirements.Request))
                {
                    var result = await context.Service.ExportAsync(context.Request(), new SnapshotExportSource(requirements: new[] { snapshot }));
                    Assert.AreEqual(ExportStatus.CompletedWithIssues, result.Status);
                    var data = ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json"));
                    Assert.IsFalse(data.Tables.Scopes.Single().Complete);
                    Assert.AreEqual("AccessDenied", data.Tables.Attributes.Single(value => value.Name == "Object Text").Status);
                    Assert.AreEqual("", data.Tables.Attributes.Single(value => value.Name == "Object Text").Plain);
                }
            }
        }
    }
}
