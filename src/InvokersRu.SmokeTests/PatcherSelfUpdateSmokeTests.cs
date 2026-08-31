using InvokersRu.Gui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.SmokeTests;

internal static class PatcherSelfUpdateSmokeTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        JsonObject payload = Payload(now);
        byte[] signed = Sign(key, payload);
        VerifiedPatcherUpdate verified = PatcherUpdateProtocol.Verify(signed, now, testPublicKey: publicKey);
        Require(verified.Version == new Version(3, 1, 5) && verified.Sequence == 1, "Signed patcher identity lost.");
        Require(PatcherUpdateProtocol.MatchesInstallerProduct("InvokersRu 3.1.5-preview    ", verified.Version), "Inno VERSIONINFO padding rejected.");
        Require(!PatcherUpdateProtocol.MatchesInstallerProduct("InvokersRu 3.1.4-preview    ", verified.Version)
            && !PatcherUpdateProtocol.MatchesInstallerProduct(" InvokersRu 3.1.5-preview", verified.Version)
            && !PatcherUpdateProtocol.MatchesInstallerProduct("InvokersRu 3.1.5-preview\t", verified.Version), "Wrong product accepted.");
        ExpectInvalid(() => PatcherUpdateProtocol.Verify(signed, now)); // Wrong key.
        var outer = JsonNode.Parse(signed)!.AsObject();
        byte[] signature = Convert.FromBase64String(outer["signature_b64"]!.GetValue<string>());
        signature[0] ^= 1;
        outer["signature_b64"] = Convert.ToBase64String(signature);
        ExpectInvalid(() => PatcherUpdateProtocol.Verify(JsonSerializer.SerializeToUtf8Bytes(outer), now, testPublicKey: publicKey));
        foreach (var change in new (string Name, JsonNode? Value)[]
        {
            ("kind", JsonValue.Create("invokers-ru-update-manifest")),
            ("channel", JsonValue.Create("macos")),
            ("version", JsonValue.Create("3.1.5;calc")),
            ("sequence", JsonValue.Create(0)),
            ("installer_bytes", JsonValue.Create(PatcherUpdateProtocol.MaximumInstallerBytes + 1)),
            ("installer_url", JsonValue.Create("https://attacker.example/setup.exe")),
            ("installer_sha256", JsonValue.Create("abc")),
            ("unexpected_command", JsonValue.Create("calc.exe")),
            ("expires_utc", JsonValue.Create(now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"))),
            ("issued_utc", JsonValue.Create(now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ")))
        })
        {
            JsonObject altered = payload.DeepClone().AsObject();
            altered[change.Name] = change.Value;
            ExpectInvalid(() => PatcherUpdateProtocol.Verify(Sign(key, altered), now, testPublicKey: publicKey));
        }
        byte[] duplicate = Encoding.UTF8.GetBytes(payload.ToJsonString().Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1", StringComparison.Ordinal));
        ExpectInvalid(() => PatcherUpdateProtocol.Verify(SignBytes(key, duplicate), now, testPublicKey: publicKey));
        JsonObject later = payload.DeepClone().AsObject();
        later["sequence"] = 2;
        var laterVerified = PatcherUpdateProtocol.Verify(Sign(key, later), now, testPublicKey: publicKey);
        ExpectInvalid(() => PatcherUpdateProtocol.RequireNotRollback(verified, laterVerified));
        JsonObject equivocation = payload.DeepClone().AsObject();
        equivocation["notes_ru"] = "Changed same sequence";
        var equivocated = PatcherUpdateProtocol.Verify(Sign(key, equivocation), now, testPublicKey: publicKey);
        ExpectInvalid(() => PatcherUpdateProtocol.RequireNotRollback(equivocated, verified));
        PatcherUpdateProtocol.RequireNotRollback(laterVerified, verified);
        PatcherUpdateProtocol.ValidateResponseUrl(new Uri("https://release-assets.githubusercontent.com/a?sig=a%2Fb&response-content-type=application%2Foctet-stream"));
        foreach (string url in new[] { "http://github.com/Braintfy/ruslocal-invokers/releases/download/x/a.exe",
            "https://evil.example/a.exe", "https://github.com/other/repo/releases/download/x/a.exe",
            "https://release-assets.githubusercontent.com/a%2Fb", "https://user@objects.githubusercontent.com/a" })
            ExpectInvalid(() => PatcherUpdateProtocol.ValidateResponseUrl(new Uri(url)));

        string root = Path.Combine(Path.GetTempPath(), "InvokersRu-self-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/a?type=application%2Foctet-stream");
            using var client = new PatcherUpdateClient(root, new QueueHandler(redirect, Ok(signed), Ok(signed), Ok(new byte[] { 9, 9, 9 })), publicKey, () => now);
            var offer = await client.CheckAsync();
            Require(File.Exists(Path.Combine(root, "accepted-envelope.v1.json")), "Accepted signed head was not persisted.");
            await ExpectInvalidAsync(async () => { using var unexpected = await client.DownloadAsync(offer, new Version(3, 1, 4)); });
            Require(Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Length == 0, "Tampered installer survived failed verification.");
            using var noDowngrade = new PatcherUpdateClient(root, new QueueHandler(), publicKey, () => now);
            await ExpectInvalidAsync(async () => { using var unexpected = await noDowngrade.DownloadAsync(offer, new Version(3, 1, 5)); });
            using var advance = new PatcherUpdateClient(root, new QueueHandler(Ok(Sign(key, later))), publicKey, () => now);
            await advance.CheckAsync();
            using var replay = new PatcherUpdateClient(root, new QueueHandler(Ok(signed)), publicKey, () => now);
            await ExpectInvalidAsync(async () => { await replay.CheckAsync(); });
            Require(PatcherUpdateProtocol.Verify(File.ReadAllBytes(Path.Combine(root, "accepted-envelope.v1.json")), now,
                testPublicKey: publicKey).Sequence == 2, "Rejected replay changed accepted history.");
            var evilRedirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            evilRedirect.Headers.Location = new Uri("https://evil.example/installer");
            using var redirectClient = new PatcherUpdateClient(root, new QueueHandler(evilRedirect), publicKey, () => now);
            await ExpectInvalidAsync(async () => { await redirectClient.CheckAsync(); });

            using var truncated = Ok(new byte[] { 1 });
            truncated.Content.Headers.ContentLength = null;
            using var output = new MemoryStream();
            await ExpectInvalidAsync(async () => { await PatcherUpdateClient.CopyBoundedAsync(truncated, output, 3, 3, default); });
            using var oversized = Ok(new byte[] { 1, 2, 3, 4 });
            oversized.Content.Headers.ContentLength = null;
            await ExpectInvalidAsync(async () => { await PatcherUpdateClient.CopyBoundedAsync(oversized, output, 3, 3, default); });
            using var valid = Ok(new byte[] { 1, 2, 3 });
            using var validOutput = new MemoryStream();
            string digest = await PatcherUpdateClient.CopyBoundedAsync(valid, validOutput, 3, 3, default);
            Require(digest == verified.InstallerSha256 && validOutput.Length == 3, "Bounded download hash was incorrect.");
            string inert = Path.Combine(root, "inert-test-file.exe");
            File.WriteAllBytes(inert, new byte[] { 1, 2, 3 });
            using var guarded = new VerifiedPatcherInstaller(inert, verified, new FileStream(inert, FileMode.Open, FileAccess.Read, FileShare.Read));
            var start = PatcherUpdateClient.CreateInstallerStartInfo(guarded);
            Require(start.FileName == inert && start.UseShellExecute && start.ArgumentList.Count == 6
                && start.ArgumentList.Contains("/NORESTART") && start.ArgumentList.Contains("/NOCLOSEAPPLICATIONS")
                && start.ArgumentList.Contains("/INVOKERSRU_AUTOUPDATE=1"), "Installer handoff lost its fixed safe arguments.");
            // Never Process.Start here: this test only validates the prepared handoff.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static JsonObject Payload(DateTimeOffset now) => new()
    {
        ["schema"] = 1, ["kind"] = "invokersru-patcher-update", ["channel"] = "windows-x64",
        ["sequence"] = 1, ["version"] = "3.1.5", ["release_tag"] = "v3.1.4-preview",
        ["installer_url"] = "https://github.com/Braintfy/ruslocal-invokers/releases/download/v3.1.4-preview/InvokersRu-3.1-Preview-3.1.5-preview-win-x64.exe",
        ["installer_bytes"] = 3, ["installer_sha256"] = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })),
        ["issued_utc"] = now.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["expires_utc"] = now.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ"), ["notes_ru"] = "Test"
    };
    private static byte[] Sign(ECDsa key, JsonObject payload) => SignBytes(key, JsonSerializer.SerializeToUtf8Bytes(payload));
    private static byte[] SignBytes(ECDsa key, byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1, kind = "invokersru-patcher-update-envelope", key_id = PatcherUpdateProtocol.KeyId,
        algorithm = "ECDSA-P256-SHA256", payload_b64 = Convert.ToBase64String(payload),
        signature_b64 = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    });
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void ExpectInvalid(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe self-update input was accepted.");
    }
    private static async Task ExpectInvalidAsync(Func<Task> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe self-update operation was accepted.");
    }
}
