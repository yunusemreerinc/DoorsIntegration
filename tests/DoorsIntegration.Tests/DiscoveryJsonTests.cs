using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class DiscoveryJsonTests
    {
        [TestMethod]
        public void ExportPreservesUnicodeTypesAccessAndPartialResultMetadata()
        {
            var snapshot = Snapshot();
            using (var destination = new MemoryStream())
            {
                new DiscoveryJsonExporter().Export(destination, snapshot);
                Assert.IsTrue(destination.CanWrite);
                var document = Parse(destination.ToArray());
                Assert.AreEqual("1", document.SelectSingleNode("/root/schemaVersion").InnerText);
                Assert.AreEqual("ReadableItemsOnly", document.SelectSingleNode("/root/visibility").InnerText);
                Assert.AreEqual("false", document.SelectSingleNode("/root/isCompleteForVisibleScope").InnerText);
                Assert.AreEqual("true", document.SelectSingleNode("/root/isTruncated").InnerText);
                Assert.AreEqual(snapshot.Items[0].FullPath, document.SelectSingleNode("/root/items/item/fullPath").InnerText);
                Assert.AreEqual("FormalModule", document.SelectSingleNode("/root/items/item/type").InnerText);
                Assert.AreEqual("DeniedOrNotFound", document.SelectSingleNode("/root/items/item/accessStatus").InnerText);
                Assert.AreEqual("false", document.SelectSingleNode("/root/items/item/metadataVerified").InnerText);
                Assert.AreEqual("MaxItemsReached", document.SelectSingleNode("/root/issues/item/code").InnerText);
                Assert.IsFalse(Encoding.UTF8.GetString(destination.ToArray()).Contains("\uFFFD"));
            }
        }

        [TestMethod]
        public void FileExportDoesNotOverwriteWithoutExplicitOptInAndLeavesNoTemporaryFile()
        {
            using (var context = new ExecutorTestContext())
            {
                var exporter = new DiscoveryJsonExporter();
                var destination = Path.Combine(context.Root, "discovery.json");
                File.WriteAllText(destination, "existing contents");
                var failure = Assert.ThrowsException<IntegrationException>(() => exporter.ExportFile(destination, Snapshot()));
                Assert.AreEqual(IntegrationErrorCode.JsonExportFailed, failure.Code);
                Assert.AreEqual("existing contents", File.ReadAllText(destination));
                Assert.AreEqual(0, Directory.GetFiles(context.Root, ".doors-export-*.tmp").Length);
                exporter.ExportFile(destination, Snapshot(), overwrite: true);
                Assert.AreEqual("1", Parse(File.ReadAllBytes(destination)).SelectSingleNode("/root/schemaVersion").InnerText);
                Assert.AreEqual(0, Directory.GetFiles(context.Root, ".doors-export-*.tmp").Length);
            }
        }

        [TestMethod]
        public void MissingDestinationDirectoryHasControlledErrorAndDoesNotCreateFolders()
        {
            using (var context = new ExecutorTestContext())
            {
                var missingDirectory = Path.Combine(context.Root, "private-missing-directory");
                var failure = Assert.ThrowsException<IntegrationException>(() =>
                    new DiscoveryJsonExporter().ExportFile(Path.Combine(missingDirectory, "result.json"), Snapshot()));
                Assert.AreEqual(IntegrationErrorCode.JsonExportFailed, failure.Code);
                Assert.IsFalse(failure.Message.Contains("private-missing-directory"));
                Assert.IsFalse(Directory.Exists(missingDirectory));
            }
        }

        [TestMethod]
        public void ExportingToClosedStreamIsTypedAndDoesNotExposeItsMessage()
        {
            var destination = new MemoryStream();
            destination.Dispose();
            var failure = Assert.ThrowsException<IntegrationException>(() => new DiscoveryJsonExporter().Export(destination, Snapshot()));
            Assert.AreEqual(IntegrationErrorCode.JsonExportFailed, failure.Code);
        }

        private static DiscoverySnapshot Snapshot() => new DiscoverySnapshot(Guid.NewGuid(), DiscoveryKind.ModuleByPath,
            new DiscoveryScope("/Özel/Modül \"A\"\\v"),
            new[] { new DoorsDiscoveryItem("/Özel/Modül \"A\"\\v", "Modül \"A\"\\v", DoorsItemKind.FormalModule, DoorsAccessStatus.DeniedOrNotFound, false) },
            new[] { new DiscoveryIssue("/Özel/Modül \"A\"\\v", DiscoveryIssueCode.MaxItemsReached) }, 0, true, DateTime.UtcNow);

        private static XmlDocument Parse(byte[] json)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(json, XmlDictionaryReaderQuotas.Max))
            {
                var document = new XmlDocument();
                document.Load(reader);
                return document;
            }
        }
    }
}
