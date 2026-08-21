using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Updates
{
    /// <summary>
    /// Immutable trust anchor embedded into the patcher. The remote manifest may select data and
    /// compatibility profiles, but it may never replace this endpoint or public key.
    /// </summary>
    public sealed class SignedUpdateChannelConfig
    {
        public const int CurrentSchema = 1;
        public const string ExpectedKind = "invokers-ru-update-channel";

        private static readonly JsonSerializerOptions StrictJson = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            MaxDepth = 8,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        [JsonRequired]
        [JsonPropertyName("schema")]
        public int Schema { get; init; }

        [JsonRequired]
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("envelope_url")]
        public string EnvelopeUrl { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("key_id")]
        public string KeyId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("public_key_spki_base64")]
        public string PublicKeySpkiBase64 { get; init; } = string.Empty;

        [JsonIgnore]
        public byte[] PublicKeySubjectPublicKeyInfo { get; private set; } = Array.Empty<byte>();

        public static SignedUpdateChannelConfig Parse(byte[] utf8)
        {
            ArgumentNullException.ThrowIfNull(utf8);
            if (utf8.Length is < 2 or > 16 * 1024)
            {
                throw new InvalidDataException("Signed-update channel config is empty or exceeds its fixed size cap.");
            }

            if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
            {
                throw new InvalidDataException("Signed-update channel config must be UTF-8 without a BOM.");
            }

            SignedUpdateChannelConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<SignedUpdateChannelConfig>(utf8, StrictJson);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Signed-update channel config is not strict schema-valid JSON.", exception);
            }

            if (config == null) throw new InvalidDataException("Signed-update channel config is JSON null.");
            config.Validate();
            return config;
        }

        private void Validate()
        {
            if (Schema != CurrentSchema || !string.Equals(Kind, ExpectedKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed-update channel config has an unsupported identity.");
            }

            SignedUpdateUrlPolicy.ValidateEnvelopeUrl(EnvelopeUrl);
            if (!IsSafeToken(KeyId))
            {
                throw new InvalidDataException("Signed-update key_id is empty or unsafe.");
            }

            byte[] publicKey;
            try
            {
                publicKey = Convert.FromBase64String(PublicKeySpkiBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Signed-update public key is not canonical Base64.", exception);
            }

            if (publicKey.Length is < 1 or > SignedUpdateLimits.MaxPublicKeyBytes
                || !string.Equals(Convert.ToBase64String(publicKey), PublicKeySpkiBase64, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed-update public key is empty, oversized, or non-canonical.");
            }

            try
            {
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
                ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: false);
                if (consumed != publicKey.Length
                    || parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
                    || parameters.Q.X?.Length != 32
                    || parameters.Q.Y?.Length != 32)
                {
                    throw new InvalidDataException("Signed-update public key must be one exact NIST P-256 SPKI value.");
                }
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("Signed-update public key is not a valid NIST P-256 SPKI value.", exception);
            }

            PublicKeySubjectPublicKeyInfo = publicKey;
        }

        private static bool IsSafeToken(string value)
        {
            return value.Length is > 0 and <= 128
                && char.IsAsciiLetterOrDigit(value[0])
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character == '-' || character == '_' || character == '.');
        }
    }
}
