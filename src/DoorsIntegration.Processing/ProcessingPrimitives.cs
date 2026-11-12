using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Processing
{
    public sealed class ProcessingException : Exception
    {
        public ProcessingException(string code, string message, Exception cause = null) : base(message, cause) { Code = code; }
        public string Code { get; }
    }
    public interface IProcessingClock
    {
        DateTime UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken token);
    }
    public sealed class ProcessingClock : IProcessingClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    }
    public sealed class ProcessingStorageOptions
    {
        public ProcessingStorageOptions() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorsIntegration", "processing")) { }
        public ProcessingStorageOptions(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("Absolute processing directory required.");
            DirectoryPath = Path.GetFullPath(directory);
        }
        public string DirectoryPath { get; }
    }
    public static class ProcessingDisclosure
    {
        public const string Statement = "Review assistance only. Findings do not establish DO-178C, DO-200B or DO-330 compliance, certification, or tool qualification. Human review and approved project evidence are required.";
    }
    internal static class ProcessingGuard
    {
        public static string Text(string value, int max, bool multiline = false, bool empty = false)
        {
            if (value == null || value.Length > max || (!empty && string.IsNullOrWhiteSpace(value)) ||
                value.Any(c => char.IsControl(c) && !(multiline && (c == '\r' || c == '\n' || c == '\t')))) throw new ArgumentException("Invalid bounded text.");
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new ArgumentException("Invalid Unicode."); }
                else if (char.IsLowSurrogate(value[i])) throw new ArgumentException("Invalid Unicode.");
            }
            return value;
        }
        public static string Hash(params string[] fields)
        {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, new UTF8Encoding(false, true), true))
            using (var sha = SHA256.Create())
            {
                foreach (var field in fields) writer.Write(field ?? "");
                writer.Flush(); return BitConverter.ToString(sha.ComputeHash(memory.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        public static void Progress<T>(IProgress<T> progress, T value) { try { progress?.Report(value); } catch { /* Presentation must not change mutation certainty. */ } }
    }
    internal static class ProcessingJson
    {
        public const int MaxLineChars = 2097152;
        public static string Encode<T>(T value)
        {
            using (var memory = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(memory, value);
                if (memory.Length > MaxLineChars) throw new ProcessingException("RecordLimit", "Serialized processing record exceeds the limit.");
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }
        public static T Decode<T>(string text)
        {
            if (text == null || text.Length > MaxLineChars) throw new SerializationException("Invalid processing record size.");
            using (var input = new MemoryStream(new UTF8Encoding(false, true).GetBytes(text)))
                return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(input);
        }
        // Never call unbounded ReadLine on imported or durable files.
        public static string ReadLine(TextReader reader, int max, out bool oversized, out bool terminated)
        {
            var b = new StringBuilder(); oversized = false; terminated = false; bool any = false;
            int next;
            while ((next = reader.Read()) >= 0)
            {
                any = true; char c = (char)next;
                if (c == '\n') { terminated = true; break; }
                if (b.Length < max) b.Append(c); else oversized = true;
            }
            if (!any) return null;
            if (b.Length > 0 && b[b.Length - 1] == '\r') b.Length--;
            return b.ToString();
        }
        public static StreamReader Reader(string path)
        {
            var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), new UTF8Encoding(false, true), false);
            try { if (reader.Peek() == '\ufeff') reader.Read(); return reader; }
            catch { reader.Dispose(); throw; }
        }
        public static void DurableAppend(string path, string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(line + "\n");
            using (var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (output.Length != 0) { output.Seek(-1, SeekOrigin.End); if (output.ReadByte() != '\n') throw new IOException("Incomplete processing journal/audit tail. Review evidence before further writes."); }
                output.Seek(0, SeekOrigin.End); output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
        }
    }
}
