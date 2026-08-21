using InvokersRu.Core.Updates;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InvokersRu.UpdateReleaseTool
{
    internal static class SigningKeyService
    {
        public const string PrivateKeyFileName = "update-signing-private.pem";
        public const string PublicConfigurationFileName = "update-signing-public.json";
        public const string StateFileName = "update-signing-state.json";

        public static PublicKeyConfiguration Generate(string repositoryRoot, string outputDirectory)
        {
            string root = StrictIo.FullPath(repositoryRoot, "Repository root");
            string output = StrictIo.FullPath(outputDirectory, "Signing directory");
            StrictIo.AssertExistingPathHasNoReparsePoints(root, "Repository root");
            StrictIo.AssertOutsideRepository(output, root, "Signing directory");
            string? parent = Path.GetDirectoryName(output);
            if (parent == null || !Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException("The signing directory parent must already exist.");
            }

            StrictIo.AssertExistingPathHasNoReparsePoints(parent, "Signing directory parent");
            if (Directory.Exists(output) || File.Exists(output))
            {
                throw new IOException("The signing directory already exists. Refusing to overwrite or reuse key material.");
            }

            Directory.CreateDirectory(output);
            bool complete = false;
            try
            {
                StrictIo.ProtectSigningDirectory(output);
                using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                byte[] privateDer = key.ExportPkcs8PrivateKey();
                byte[] publicDer = key.ExportSubjectPublicKeyInfo();
                string keyId = DeriveKeyId(publicDer);
                var publicConfiguration = new PublicKeyConfiguration
                {
                    KeyId = keyId,
                    Algorithm = SignedUpdateVerifier.SignatureAlgorithm,
                    SubjectPublicKeyInfoBase64 = Convert.ToBase64String(publicDer),
                    SubjectPublicKeyInfoSha256 = StrictIo.Sha256(publicDer)
                };

                string privatePath = Path.Combine(output, PrivateKeyFileName);
                string publicPath = Path.Combine(output, PublicConfigurationFileName);
                string statePath = Path.Combine(output, StateFileName);
                byte[] privatePem = EncodePrivatePem(privateDer);
                try
                {
                    StrictIo.WriteNewFile(privatePath, privatePem, "Private signing key");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privatePem);
                    CryptographicOperations.ZeroMemory(privateDer);
                }

                StrictIo.ProtectSecretFile(privatePath);
                StrictIo.WriteNewJson(publicPath, publicConfiguration, indented: true);
                var state = new SigningSequenceState
                {
                    KeyId = keyId,
                    HighestReservedSequence = 0,
                    Records = Array.Empty<SigningSequenceRecord>()
                };
                StrictIo.WriteNewJson(statePath, state, indented: true);
                StrictIo.ProtectSecretFile(statePath);
                complete = true;
                return publicConfiguration;
            }
            finally
            {
                if (!complete) StrictIo.TryDeleteDirectory(output);
            }
        }

        public static ECDsa LoadPrivateKey(string privateKeyPath)
        {
            byte[] pemBytes = StrictIo.ReadRegularFile(privateKeyPath, "Private signing key", 16 * 1024);
            string pem = StrictIo.DecodeStrictUtf8(pemBytes, "Private signing key");
            CryptographicOperations.ZeroMemory(pemBytes);
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
                ECParameters parameters = key.ExportParameters(includePrivateParameters: true);
                if (key.KeySize != 256
                    || !string.Equals(parameters.Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal)
                    || parameters.D == null || parameters.D.Length == 0)
                {
                    throw new CryptographicException("The private key is not a NIST P-256 signing key.");
                }

                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        public static string DeriveKeyId(byte[] subjectPublicKeyInfo)
        {
            string hash = StrictIo.Sha256(subjectPublicKeyInfo).ToLowerInvariant();
            return "p256-" + hash.Substring(0, 24);
        }

        private static byte[] EncodePrivatePem(byte[] privateDer)
        {
            string base64 = Convert.ToBase64String(privateDer);
            var builder = new StringBuilder();
            builder.Append("-----BEGIN PRIVATE KEY-----\n");
            for (int offset = 0; offset < base64.Length; offset += 64)
            {
                builder.Append(base64, offset, Math.Min(64, base64.Length - offset));
                builder.Append('\n');
            }

            builder.Append("-----END PRIVATE KEY-----\n");
            return StrictIo.Utf8.GetBytes(builder.ToString());
        }
    }
}
