using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DoorsIntegration.Core;

namespace DoorsIntegration.Tests
{
    // Explicit test-only replacement for DPAPI in the sandbox's unloaded Windows user profile.
    // Ephemeral process key; never used by application composition or persisted user jobs.
    internal sealed class TestBaselineJournalProtector : IBaselineJournalProtector
    {
        private static readonly byte[] Key = MakeKey();
        private static byte[] MakeKey() { var key = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key); return key; }
        public byte[] Protect(byte[] plaintext)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Key; aes.GenerateIV();
                using (var encrypt = aes.CreateEncryptor()) using (var data = new MemoryStream())
                {
                    var encrypted = encrypt.TransformFinalBlock(plaintext, 0, plaintext.Length); data.Write(aes.IV, 0, aes.IV.Length); data.Write(encrypted, 0, encrypted.Length);
                    var payload = data.ToArray(); using (var hmac = new HMACSHA256(Key)) return hmac.ComputeHash(payload).Concat(payload).ToArray();
                }
            }
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (ciphertext.Length < 64) throw new CryptographicException(); var payload = ciphertext.Skip(32).ToArray();
            using (var hmac = new HMACSHA256(Key)) if (!hmac.ComputeHash(payload).SequenceEqual(ciphertext.Take(32))) throw new CryptographicException();
            using (var aes = Aes.Create())
            { aes.Key = Key; aes.IV = payload.Take(16).ToArray(); using (var decrypt = aes.CreateDecryptor()) return decrypt.TransformFinalBlock(payload, 16, payload.Length - 16); }
        }
    }
}
