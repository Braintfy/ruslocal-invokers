using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.UpdateReleaseTool
{
    internal static class StrictIo
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static readonly JsonSerializerOptions StrictJson = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            MaxDepth = 32,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        internal static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        internal static readonly JsonSerializerOptions DisplayJson = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string FullPath(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} is required.");
            return Path.GetFullPath(value);
        }

        public static byte[] ReadRegularFile(string path, string label, int maximumBytes = int.MaxValue)
        {
            string fullPath = FullPath(path, label);
            AssertExistingPathHasNoReparsePoints(fullPath, label);
            var info = new FileInfo(fullPath);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            {
                throw new FileNotFoundException($"{label} is not an existing regular file.", fullPath);
            }

            if (info.Length < 1 || info.Length > maximumBytes)
            {
                throw new InvalidDataException($"{label} is empty or exceeds its fixed byte limit.");
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != info.Length || stream.Length > maximumBytes)
            {
                throw new IOException($"{label} changed while it was being opened.");
            }

            var result = new byte[checked((int)stream.Length)];
            int offset = 0;
            while (offset < result.Length)
            {
                int read = stream.Read(result, offset, result.Length - offset);
                if (read == 0) throw new EndOfStreamException($"{label} ended unexpectedly.");
                offset += read;
            }

            if (stream.ReadByte() != -1) throw new IOException($"{label} changed while it was being read.");
            return result;
        }

        public static string DecodeStrictUtf8(byte[] bytes, string label)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                throw new InvalidDataException($"{label} must not start with a UTF-8 BOM.");
            }

            try
            {
                return Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"{label} is not strict UTF-8.", exception);
            }
        }

        public static T DeserializeStrict<T>(byte[] bytes, string label) where T : class
        {
            DecodeStrictUtf8(bytes, label);
            try
            {
                return JsonSerializer.Deserialize<T>(bytes, StrictJson)
                    ?? throw new InvalidDataException($"{label} is JSON null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{label} is not strict schema-valid JSON: {exception.Message}", exception);
            }
        }

        public static void WriteNewFile(string path, byte[] bytes, string label)
        {
            string fullPath = FullPath(path, label);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException($"{label} has no parent directory.");
            AssertExistingPathHasNoReparsePoints(parent, $"{label} parent");
            using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        public static void WriteNewFileAtomically(string path, byte[] bytes, string label)
        {
            string fullPath = FullPath(path, label);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException($"{label} has no parent directory.");
            AssertExistingPathHasNoReparsePoints(parent, $"{label} parent");
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new IOException($"{label} already exists; refusing to overwrite it.");
            }

            string temporary = Path.Combine(
                parent,
                "." + Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            try
            {
                WriteNewFile(temporary, bytes, $"Temporary {label}");
                AssertExistingPathHasNoReparsePoints(parent, $"{label} parent");
                File.Move(temporary, fullPath, overwrite: false);
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }

        public static void WriteNewJson<T>(string path, T value, bool indented)
        {
            WriteNewFile(path, JsonSerializer.SerializeToUtf8Bytes(value, indented ? DisplayJson : CompactJson), "JSON output");
        }

        public static void WriteJsonAtomically<T>(string path, T value)
        {
            string fullPath = FullPath(path, "Signing state");
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("Signing state has no parent directory.");
            AssertExistingPathHasNoReparsePoints(parent, "Signing state parent");
            string temporary = Path.Combine(parent, "." + Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            try
            {
                WriteNewFile(temporary, JsonSerializer.SerializeToUtf8Bytes(value, DisplayJson), "Temporary signing state");
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }

        public static string Sha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        public static bool IsUpperSha256(string? value)
        {
            return value != null && value.Length == 64
                && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
        }

        public static string CanonicalUtcSecond(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public static void AssertOutsideRepository(string path, string repositoryRoot, string label)
        {
            string fullPath = FullPath(path, label);
            string root = FullPath(repositoryRoot, "Repository root");
            if (IsSameOrChild(fullPath, root))
            {
                throw new InvalidOperationException($"{label} must be outside the repository. Refusing to place signing material in Git scope.");
            }
        }

        public static void AssertExistingPathHasNoReparsePoints(string path, string label)
        {
            string fullPath = FullPath(path, label);
            var pending = new Stack<string>();
            string? current = fullPath;
            while (!string.IsNullOrEmpty(current))
            {
                pending.Push(current);
                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, PathComparison)) break;
                current = parent;
            }

            while (pending.Count > 0)
            {
                string candidate = pending.Pop();
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) continue;
                FileAttributes attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"{label} traverses a reparse point: {candidate}");
                }
            }
        }

        public static void ProtectSigningDirectory(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                ProtectWindowsPath(directory, directoryAcl: true);
            }
            else
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public static void ProtectSecretFile(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                ProtectWindowsPath(path, directoryAcl: false);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        [SupportedOSPlatform("windows")]
        private static void ProtectWindowsPath(string path, bool directoryAcl)
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException("Could not determine the current Windows user SID for signing-key ACLs.");
            }

            string grant = directoryAcl ? $"*{sid}:(OI)(CI)(F)" : $"*{sid}:(F)";
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("/inheritance:r");
            start.ArgumentList.Add("/grant:r");
            start.ArgumentList.Add(grant);
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start icacls.exe.");
            string standardError = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Could not restrict signing material ACLs (icacls exit {process.ExitCode}): {standardError.Trim()}");
            }
        }

        private static bool IsSameOrChild(string candidate, string root)
        {
            string normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedCandidate, normalizedRoot, PathComparison)) return true;
            return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        public static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Preserve the original exception. Temporary files contain no private key material.
            }
        }

        public static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Preserve the original exception. The caller reports the failed operation.
            }
        }
    }
}
