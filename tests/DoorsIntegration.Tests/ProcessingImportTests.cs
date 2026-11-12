using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Processing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class ProcessingImportTests
    {
        [TestMethod]
        public async Task JsonLinesCapturesUnicodeRichTextAndDoesNotReopenChangedInputAtApply()
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string path = Path.Combine(c.Root, "input.jsonl");
                string json = "{\"key\":\"a\",\"action\":\"Create\",\"target\":\"/P/M\",\"attributes\":[{\"name\":\"Object Text\",\"value\":\"Türkçe 👋\"}]}\n";
                File.WriteAllText(path, json, new UTF8Encoding(true));
                var plan = c.Hold(await c.Service.PreviewAsync(new JsonLinesBatchSource(path), c.Options()));
                Assert.IsTrue(plan.CanApply); Assert.AreEqual("Türkçe 👋", plan.ReadRows().Single().Change.Attributes.Single().Value);
                File.WriteAllText(path, "different input"); await c.Service.ApplyAsync(plan);
                Assert.AreEqual("Türkçe 👋", c.Adapter.Applied.Single().Attributes.Single().Value);
                string rich = "{\"key\":\"rich\",\"action\":\"Update\",\"target\":\"/P/M\",\"objectId\":\"3\",\"attributes\":[{\"name\":\"Object Text\",\"format\":\"OpaqueRichText\",\"value\":\"{\\\\b gövde}\",\"expectedPlainText\":\"gövde\"}]}\n";
                File.WriteAllText(path, rich); var record = new JsonLinesBatchSource(path).Read(CancellationToken.None).Single();
                Assert.AreEqual(BatchValueFormat.OpaqueRichText, record.Change.Attributes.Single().Format);
                Assert.AreEqual("{\\b gövde}", record.Change.Attributes.Single().Value);
            }
        }
        [DataTestMethod]
        [DataRow("{\"key\":\"a\",\"key\":\"b\",\"action\":\"Create\",\"target\":\"/P/M\",\"attributes\":[{\"name\":\"X\",\"value\":\"v\"}]}")]
        [DataRow("{\"key\":\"a\",\"action\":\"Delete\",\"target\":\"/P/M\",\"attributes\":[]}")]
        [DataRow("{\"key\":\"a\",\"action\":\"Create\",\"target\":\"/P/M\",\"attributes\":[{\"name\":\"X\",\"value\":\"v\"}],\"typo\":\"bad\"}")]
        [DataRow("{\"key\":\"a\",\"action\":\"Create\",\"target\":\"/P/M\",\"attributes\":[{\"name\":\"X\",\"value\":42}]}")]
        [DataRow("{\"key\":\"a\",\"action\":\"Update\",\"target\":\"/P/M\",\"attributes\":[{\"name\":\"X\",\"value\":\"v\"}]}")]
        [DataRow("")]
        public void InvalidJsonRecordsAreReportedIndividuallyWithoutGuessing(string json)
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string path = Path.Combine(c.Root, "invalid.jsonl"); File.WriteAllText(path, json + "\n");
                var row = new JsonLinesBatchSource(path).Read(CancellationToken.None).Single(); Assert.IsNull(row.Change); Assert.AreEqual("InvalidJsonRecord", row.ErrorCode);
            }
        }
        [TestMethod]
        public void CsvQuotedMultilineAndEmptyAssignmentsArePreserved()
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string path = Path.Combine(c.Root, "input.csv");
                File.WriteAllText(path, "key,action,target,objectId,attr:Object Text,attr:Priority\r\nrow-1,Update,/P/M,5,\"Line 1, \"\"quoted\"\"\r\nŞ line 2\",\r\n", new UTF8Encoding(true));
                var row = new CsvBatchSource(path).Read(CancellationToken.None).Single(); Assert.IsNotNull(row.Change);
                Assert.AreEqual("Line 1, \"quoted\"\r\nŞ line 2", row.Change.Attributes.First().Value);
                Assert.AreEqual("", row.Change.Attributes.Last().Value); Assert.AreEqual("5", row.Change.ObjectId);
            }
        }
        [TestMethod]
        public void BadCsvHeaderQuoteAndOversizedRowsFailClosed()
        {
            using (var c = new ProcessingTestContext())
            {
                Directory.CreateDirectory(c.Root); string path = Path.Combine(c.Root, "input.csv");
                File.WriteAllText(path, "key,action,target,misspelledColumn\na,Create,/P/M,v\n");
                Assert.ThrowsException<ProcessingException>(() => new CsvBatchSource(path).Read(CancellationToken.None).ToArray());
                File.WriteAllText(path, "key,action,target,attr:Object Text\na,Create,/P/M,\"unterminated");
                Assert.AreEqual("InvalidCsvRecord", new CsvBatchSource(path).Read(CancellationToken.None).Single().ErrorCode);
                File.WriteAllText(path, "key,action,target,attr:Object Text\na,Create,/P/M," + new string('x', 65537) + "\n");
                Assert.AreEqual("InvalidCsvRecord", new CsvBatchSource(path).Read(CancellationToken.None).Single().ErrorCode);
                File.WriteAllText(path, new string('x', 262145) + "\n");
                Assert.AreEqual("RecordTooLarge", new JsonLinesBatchSource(path).Read(CancellationToken.None).Single().ErrorCode);
            }
        }
        [TestMethod]
        public async Task InputLimitStopsBeforeAdapterInspectionAndEmptyInputIsNotExecutable()
        {
            using (var c = new ProcessingTestContext())
            {
                var many = Enumerable.Range(1, 11).Select(i => ProcessingTestContext.Change(i.ToString())).ToArray();
                await Assert.ThrowsExceptionAsync<ProcessingException>(async () => await c.Service.PreviewAsync(new ProcessingArraySource(many), c.Options(maxRecords: 10)));
                Assert.AreEqual(0, c.Adapter.Inspections); Assert.AreEqual(0, c.Adapter.Calls);
                var empty = await c.Preview(); Assert.IsFalse(empty.CanApply);
            }
        }
    }
}
