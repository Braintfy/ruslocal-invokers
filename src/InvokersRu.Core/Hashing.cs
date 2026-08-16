using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InvokersRu.Core
{
    public static class Hashing
    {
        public static string Sha256File(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            return ToHex(sha.ComputeHash(stream));
        }

        public static string Sha256Bytes(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return ToHex(sha.ComputeHash(bytes));
        }

        public static string Sha256Text(string value)
        {
            return Sha256Bytes(Encoding.UTF8.GetBytes(value));
        }

        public static bool FixedEqualsHex(string? left, string? right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= char.ToUpperInvariant(left[index]) ^ char.ToUpperInvariant(right[index]);
            }

            return difference == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty, StringComparison.Ordinal);
        }
    }
}
