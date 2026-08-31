using InvokersRu.Gui;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace InvokersRu.UpdateReleaseTool;

internal static class PatcherReleaseBuilder
{
    internal static void Build(string repository, string installer, string version, string tag,
        long sequence, string privateKey, string output, string notesPath)
    {
        repository = StrictIo.FullPath(repository, "Repository");
        installer = StrictIo.FullPath(installer, "Installer");
        privateKey = StrictIo.FullPath(privateKey, "Private key");
        output = StrictIo.FullPath(output, "Output directory");
        StrictIo.AssertOutsideRepository(privateKey, repository, "Private key");
        foreach (string path in new[] { repository, installer, privateKey, output })
            StrictIo.AssertExistingPathHasNoReparsePoints(path, "Patcher release input/output");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists; releases cannot be overwritten.");
        if (!Directory.Exists(Path.GetDirectoryName(output))) throw new IOException("Create the output parent first.");
        string notes = StrictIo.DecodeStrictUtf8(StrictIo.ReadRegularFile(notesPath, "Notes", 8192), "Notes");
        using var exe = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (exe.Length is < 1 or > PatcherUpdateProtocol.MaximumInstallerBytes) throw new InvalidDataException("Installer size is invalid.");
        if (!PatcherUpdateProtocol.MatchesInstallerProduct(FileVersionInfo.GetVersionInfo(installer).ProductName, Version.Parse(version)))
            throw new InvalidDataException("Installer product version does not match the requested version.");
        string hash = Convert.ToHexString(SHA256.HashData(exe));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, kind = "invokersru-patcher-update", channel = "windows-x64", sequence, version,
            release_tag = tag,
            installer_url = $"https://github.com/Braintfy/ruslocal-invokers/releases/download/{tag}/InvokersRu-3.1-Preview-{version}-preview-win-x64.exe",
            installer_bytes = exe.Length, installer_sha256 = hash,
            issued_utc = now.ToString("yyyy-MM-ddTHH:mm:ssZ"), expires_utc = now.AddDays(120).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            notes_ru = notes
        });
        using ECDsa key = SigningKeyService.LoadPrivateKey(privateKey);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, kind = "invokersru-patcher-update-envelope", key_id = PatcherUpdateProtocol.KeyId,
            algorithm = "ECDSA-P256-SHA256", payload_b64 = Convert.ToBase64String(payload),
            signature_b64 = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
        // Use the exact production GUI verifier and its embedded public key before reserving anything.
        VerifiedPatcherUpdate verified = PatcherUpdateProtocol.Verify(envelope, now);
        string history = Path.Combine(Path.GetDirectoryName(privateKey)!, "patcher-history-v1");
        StrictIo.AssertExistingPathHasNoReparsePoints(history, "Signing history");
        Directory.CreateDirectory(history);
        StrictIo.ProtectSigningDirectory(history);
        string lockPath = Path.Combine(history, "sequence.lock");
        StrictIo.AssertExistingPathHasNoReparsePoints(lockPath, "Signing lock");
        using var signingLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        VerifiedPatcherUpdate? previous = null;
        foreach (string entry in Directory.EnumerateFiles(history, "sequence-*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var item = PatcherUpdateProtocol.Verify(StrictIo.ReadRegularFile(entry, "Signed patcher history", PatcherUpdateProtocol.MaximumEnvelopeBytes), now, allowExpiredHistory: true);
            if (item.Sequence != (previous?.Sequence ?? 0) + 1
                || Path.GetFileName(entry) != $"sequence-{item.Sequence:D8}.json")
                throw new InvalidDataException("Patcher signing history is not a complete monotonic chain.");
            PatcherUpdateProtocol.RequireNotRollback(item, previous);
            previous = item;
        }
        if (sequence != (previous?.Sequence ?? 0) + 1) throw new InvalidDataException("Sequence must follow the last reserved patcher sequence.");
        PatcherUpdateProtocol.RequireNotRollback(verified, previous);
        StrictIo.WriteNewFile(Path.Combine(history, $"sequence-{sequence:D8}.json"), envelope, "Reserved signed patcher release");
        Directory.CreateDirectory(output);
        StrictIo.WriteNewFile(Path.Combine(output, "patcher-update.v1.json"), envelope, "Signed patcher envelope");
        StrictIo.WriteNewFile(Path.Combine(output, "patcher-update.payload.json"), payload, "Readable signed payload");
        Console.WriteLine($"PATCHER UPDATE SIGNED: version={version}, sequence={sequence}, installer SHA256={hash}");
        Console.WriteLine("Envelope SHA256: " + StrictIo.Sha256(envelope));
    }
}
