using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvokersRu.Gui;

internal sealed record VerifiedPatcherUpdate(
    long Sequence, Version Version, Uri InstallerUrl, long InstallerBytes,
    string InstallerSha256, DateTimeOffset ExpiresUtc, string Notes,
    string PayloadSha256, byte[] Envelope);

// A separate signed domain: a translation signature can never authorize an EXE.
internal static class PatcherUpdateProtocol
{
    internal const string ChannelUrl = "https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/patcher-update.v1.json";
    internal const string KeyId = "p256-528d957fd2a6839913367750";
    internal const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESqdGBmrjJ6LAvNVleBO3NwXIDLTu1faOO4bQb11KPE29QMZHZZwwrC/ucVj5zovp1Rs9UDF3Ky2J6bq79FkckQ==";
    internal const int MaximumEnvelopeBytes = 32 * 1024;
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    internal const string RunningMutex = @"Local\InvokersRu.Gui.Running";
    private const string DownloadPrefix = "/Braintfy/ruslocal-invokers/releases/download/";

    internal static VerifiedPatcherUpdate Verify(byte[] envelope, DateTimeOffset now,
        bool allowExpiredHistory = false, byte[]? testPublicKey = null)
    {
        try
        {
            Require(envelope.Length is > 0 and <= MaximumEnvelopeBytes, "Размер манифеста обновления вне допустимого диапазона.");
            using JsonDocument outer = JsonDocument.Parse(envelope, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = outer.RootElement;
            ExactProperties(root, "schema", "kind", "key_id", "algorithm", "payload_b64", "signature_b64");
            Require(root.GetProperty("schema").GetInt32() == 1
                && Text(root, "kind") == "invokersru-patcher-update-envelope"
                && Text(root, "key_id") == KeyId && Text(root, "algorithm") == "ECDSA-P256-SHA256",
                "Неизвестный формат или ключ обновления патчера.");
            byte[] payload = Convert.FromBase64String(Text(root, "payload_b64"));
            byte[] signature = Convert.FromBase64String(Text(root, "signature_b64"));
            Require(signature.Length == 64 && payload.Length is > 0 and <= 16384, "Некорректный размер подписи.");
            using ECDsa key = ECDsa.Create();
            byte[] spki = testPublicKey ?? Convert.FromBase64String(PublicKey);
            key.ImportSubjectPublicKeyInfo(spki, out int read);
            Require(read == spki.Length && key.KeySize == 256
                && key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                "Подпись обновления патчера недействительна. Установка запрещена.");
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement data = document.RootElement;
            ExactProperties(data, "schema", "kind", "channel", "sequence", "version", "release_tag",
                "installer_url", "installer_bytes", "installer_sha256", "issued_utc", "expires_utc", "notes_ru");
            Require(data.GetProperty("schema").GetInt32() == 1
                && Text(data, "kind") == "invokersru-patcher-update"
                && Text(data, "channel") == "windows-x64", "Манифест предназначен не для Windows-патчера.");
            long sequence = data.GetProperty("sequence").GetInt64();
            long size = data.GetProperty("installer_bytes").GetInt64();
            Require(sequence > 0 && size is > 0 and <= MaximumInstallerBytes, "Некорректный номер или размер обновления.");
            string versionText = Text(data, "version");
            Require(Regex.IsMatch(versionText, @"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z"), "Некорректная версия патчера.");
            Version version = Version.Parse(versionText);
            Require(version.Major <= 65535 && version.Minor <= 65535 && version.Build <= 65535, "Версия вне диапазона Windows.");
            string tag = Text(data, "release_tag");
            Require(Regex.IsMatch(tag, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z"), "Некорректный тег релиза.");
            string expectedUrl = "https://github.com" + DownloadPrefix + tag
                + "/InvokersRu-3.1-Preview-" + versionText + "-preview-win-x64.exe";
            Require(Text(data, "installer_url") == expectedUrl, "Адрес установщика не соответствует подписанному релизу.");
            Uri url = ValidateResponseUrl(new Uri(expectedUrl));
            string hash = Text(data, "installer_sha256");
            Require(Regex.IsMatch(hash, @"\A[A-F0-9]{64}\z"), "Некорректный SHA-256 установщика.");
            DateTimeOffset issued = ParseUtc(Text(data, "issued_utc"));
            DateTimeOffset expires = ParseUtc(Text(data, "expires_utc"));
            Require(expires > issued && expires - issued <= TimeSpan.FromDays(180)
                && issued <= now.AddMinutes(15) && (allowExpiredHistory || expires > now),
                "Манифест обновления просрочен или часы компьютера неверны.");
            string notes = Text(data, "notes_ru");
            Require(notes.Length <= 2048 && !notes.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')),
                "Некорректное описание обновления.");
            return new(sequence, version, url, size, hash, expires, notes,
                Convert.ToHexString(SHA256.HashData(payload)), envelope.ToArray());
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException
            or InvalidOperationException or ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("Некорректный подписанный манифест обновления патчера.", exception);
        }
    }

    internal static void RequireNotRollback(VerifiedPatcherUpdate current, VerifiedPatcherUpdate? previous)
    {
        if (previous == null) return;
        Require(current.Sequence >= previous.Sequence && current.Version >= previous.Version,
            "GitHub вернул более старое обновление патчера. Установка запрещена.");
        Require(current.Sequence != previous.Sequence || current.PayloadSha256 == previous.PayloadSha256,
            "Содержимое уже принятого номера обновления изменилось.");
    }

    internal static bool MatchesInstallerProduct(string? productName, Version version)
        // Inno Setup pads its fixed-width VERSIONINFO strings with trailing spaces.
        => productName?.TrimEnd(' ', '\0') == "InvokersRu " + version + "-preview";

    internal static Uri ValidateResponseUrl(Uri uri)
    {
        string text = uri.OriginalString;
        Require(uri.IsAbsoluteUri && text.Length <= 4096 && uri.Scheme == "https" && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && !text.Contains('\\')
            && !text.Any(char.IsControl), "Небезопасный адрес загрузки патчера.");
        bool trusted = uri.IdnHost.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith(DownloadPrefix, StringComparison.Ordinal);
        string path = uri.GetLeftPart(UriPartial.Path);
        Require(trusted && !path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("%5c", StringComparison.OrdinalIgnoreCase), "Недоверенный адрес или путь загрузки патчера.");
        return uri;
    }

    private static void ExactProperties(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Ожидался объект JSON.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            Require(names.Contains(property.Name, StringComparer.Ordinal) && found.Add(property.Name), "Неизвестное или повторное поле JSON.");
        Require(found.Count == names.Length, "В манифесте отсутствуют обязательные поля.");
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new InvalidDataException("Отсутствует строка " + name);

    private static DateTimeOffset ParseUtc(string value)
    {
        Require(DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result), "Некорректная дата обновления.");
        return result;
    }

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }
}
