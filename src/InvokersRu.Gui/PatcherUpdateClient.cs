using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.Gui;

internal sealed class VerifiedPatcherInstaller : IDisposable
{
    internal string Path { get; }
    internal VerifiedPatcherUpdate Update { get; }
    private readonly FileStream _guard;
    internal VerifiedPatcherInstaller(string path, VerifiedPatcherUpdate update, FileStream guard)
        => (Path, Update, _guard) = (path, update, guard);
    public void Dispose() => _guard.Dispose();
}

internal sealed class PatcherUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly byte[]? _testPublicKey;
    private readonly Func<DateTimeOffset> _clock;

    internal PatcherUpdateClient(string? root = null, HttpMessageHandler? handler = null,
        byte[]? testPublicKey = null, Func<DateTimeOffset>? clock = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InvokersRu", "self-update"));
        _testPublicKey = testPublicKey;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InvokersRu-SelfUpdate/1");
    }

    internal async Task<VerifiedPatcherUpdate> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await GetAsync(new Uri(PatcherUpdateProtocol.ChannelUrl), deadline.Token);
        using var memory = new MemoryStream();
        await CopyBoundedAsync(response, memory, PatcherUpdateProtocol.MaximumEnvelopeBytes, null, deadline.Token);
        var update = PatcherUpdateProtocol.Verify(memory.ToArray(), _clock(), testPublicKey: _testPublicKey);
        EnsureDirectory(_root);
        using FileStream stateLock = OpenStateLock();
        PatcherUpdateProtocol.RequireNotRollback(update, ReadAccepted());
        string temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(update.Envelope);
                file.Flush(flushToDisk: true);
            }
            string destination = Path.Combine(_root, "accepted-envelope.v1.json");
            AssertNoReparsePath(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return update;
    }

    internal async Task<VerifiedPatcherInstaller> DownloadAsync(VerifiedPatcherUpdate offered, Version installedVersion,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        PatcherUpdateProtocol.Require(offered.Version > installedVersion, "Новая версия патчера не требуется.");
        // Recheck authority after the user confirms; do not install a withdrawn or superseded offer.
        VerifiedPatcherUpdate current = await CheckAsync(cancellationToken);
        PatcherUpdateProtocol.Require(current.PayloadSha256 == offered.PayloadSha256, "Обновление изменилось. Повторите проверку.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        string directory = Path.Combine(_root, "downloads", Guid.NewGuid().ToString("N"));
        EnsureDirectory(directory);
        string path = Path.Combine(directory, "InvokersRu-" + current.Version + "-Setup.exe");
        FileStream? guard = null;
        try
        {
            using (HttpResponseMessage response = await GetAsync(current.InstallerUrl, deadline.Token))
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                string digest = await CopyBoundedAsync(response, output, PatcherUpdateProtocol.MaximumInstallerBytes,
                    current.InstallerBytes, deadline.Token, progress);
                PatcherUpdateProtocol.Require(digest == current.InstallerSha256, "SHA-256 скачанного EXE не совпал с подписью. Установка запрещена.");
                await output.FlushAsync(deadline.Token);
                output.Flush(flushToDisk: true);
            }
            AssertNoReparsePath(path);
            guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            PatcherUpdateProtocol.Require(guard.Length == current.InstallerBytes
                && Convert.ToHexString(await SHA256.HashDataAsync(guard, deadline.Token)) == current.InstallerSha256,
                "Установщик изменился после загрузки.");
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            PatcherUpdateProtocol.Require(PatcherUpdateProtocol.MatchesInstallerProduct(info.ProductName, current.Version),
                "Версия внутри установщика не совпадает с подписанным обновлением.");
            using (FileStream stateLock = OpenStateLock())
                PatcherUpdateProtocol.Require(ReadAccepted()?.PayloadSha256 == current.PayloadSha256
                    && current.ExpiresUtc > _clock(), "Разрешение на обновление изменилось или истекло.");
            var result = new VerifiedPatcherInstaller(path, current, guard);
            guard = null; // Keep a deny-write/deny-delete handle until Setup has started and the GUI exits.
            return result;
        }
        catch
        {
            guard?.Dispose();
            AssertNoReparsePath(path);
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(VerifiedPatcherInstaller installer)
    {
        PatcherUpdateProtocol.Require(installer.Update.ExpiresUtc > DateTimeOffset.UtcNow, "Срок обновления истёк.");
        AssertNoReparsePath(installer.Path);
        var start = new ProcessStartInfo(installer.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer.Path)! };
        // Fixed arguments only. No manifest-supplied command line, shell, /DIR, reboot or process killing.
        foreach (string argument in new[] { "/SILENT", "/SP-", "/NORESTART", "/NOCLOSEAPPLICATIONS",
            "/NORESTARTAPPLICATIONS", "/INVOKERSRU_AUTOUPDATE=1" })
            start.ArgumentList.Add(argument);
        return start;
    }

    private VerifiedPatcherUpdate? ReadAccepted()
    {
        string path = Path.Combine(_root, "accepted-envelope.v1.json");
        AssertNoReparsePath(path);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PatcherUpdateProtocol.Require(file.Length is > 0 and <= PatcherUpdateProtocol.MaximumEnvelopeBytes, "Повреждена история автообновления.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return PatcherUpdateProtocol.Verify(bytes, _clock(), allowExpiredHistory: true, testPublicKey: _testPublicKey);
    }

    private FileStream OpenStateLock()
    {
        string path = Path.Combine(_root, "channel.lock");
        AssertNoReparsePath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            PatcherUpdateProtocol.ValidateResponseUrl(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location == null || redirects == 5) throw new InvalidDataException("Некорректная цепочка перенаправлений GitHub.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                PatcherUpdateProtocol.Require(response.StatusCode == HttpStatusCode.OK
                    && response.Content.Headers.ContentEncoding.Count == 0, "Неподдерживаемый HTTP-ответ обновления.");
                PatcherUpdateProtocol.ValidateResponseUrl(response.RequestMessage?.RequestUri ?? uri);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Слишком много перенаправлений.");
    }

    internal static async Task<string> CopyBoundedAsync(HttpResponseMessage response, Stream destination,
        long maximum, long? expected, CancellationToken token, IProgress<int>? progress = null)
    {
        if (response.Content.Headers.ContentLength is long declared)
            PatcherUpdateProtocol.Require(declared <= maximum && (expected == null || declared == expected), "Размер загрузки не совпадает с подписанным.");
        using Stream source = await response.Content.ReadAsStreamAsync(token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0;
        int previousPercent = -1;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            PatcherUpdateProtocol.Require(total <= maximum && (expected == null || total <= expected), "Превышен допустимый размер загрузки.");
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            if (expected is > 0)
            {
                int percent = (int)(total * 100 / expected.Value);
                if (percent != previousPercent) { progress?.Report(percent); previousPercent = percent; }
            }
        }
        PatcherUpdateProtocol.Require(total > 0 && (expected == null || total == expected), "Загрузка установщика не завершена.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void EnsureDirectory(string path)
    {
        AssertNoReparsePath(path);
        Directory.CreateDirectory(path);
        AssertNoReparsePath(path);
    }

    internal static void AssertNoReparsePath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                PatcherUpdateProtocol.Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                    "Автообновление через символическую ссылку или junction запрещено.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public void Dispose() => _http.Dispose();
}
