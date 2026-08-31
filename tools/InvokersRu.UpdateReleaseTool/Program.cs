using InvokersRu.Core.Updates;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace InvokersRu.UpdateReleaseTool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
                {
                    WriteHelp();
                    return args.Length == 0 ? 2 : 0;
                }

                Dictionary<string, string> options = ParseOptions(args, 1);
                switch (args[0])
                {
                    case "build-patcher-update":
                        RejectUnknown(options, "repository-root", "installer", "version", "release-tag", "sequence", "private-key", "output-directory", "notes-file");
                        PatcherReleaseBuilder.Build(Require(options, "repository-root"), Require(options, "installer"),
                            Require(options, "version"), Require(options, "release-tag"),
                            checked((long)ParseUlong(Require(options, "sequence"), "sequence")), Require(options, "private-key"),
                            Require(options, "output-directory"), Require(options, "notes-file"));
                        return 0;
                    case "keygen":
                        RunKeygen(options);
                        return 0;
                    case "build-release":
                        RunBuildRelease(options);
                        return 0;
                    case "build-compatibility":
                        RunBuildCompatibility(options);
                        return 0;
                    case "self-test":
                        RunSelfTest(options);
                        return 0;
                    default:
                        throw new ArgumentException($"Unknown command: {args[0]}");
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("ERROR: " + exception.Message);
                return 1;
            }
        }

        private static void RunKeygen(Dictionary<string, string> options)
        {
            RejectUnknown(options, "repository-root", "output-directory");
            PublicKeyConfiguration result = SigningKeyService.Generate(
                Require(options, "repository-root"),
                Require(options, "output-directory"));
            Console.WriteLine("Signing key generated in the protected external directory.");
            Console.WriteLine("Private key material was not displayed.");
            Console.WriteLine("key_id: " + result.KeyId);
            Console.WriteLine("SPKI SHA-256: " + result.SubjectPublicKeyInfoSha256);
        }

        private static void RunBuildRelease(Dictionary<string, string> options)
        {
            RejectUnknown(options,
                "repository-root", "catalog", "compatibility", "private-key", "signing-state",
                "output-directory", "release-id", "artifact-id", "issued-utc", "expires-utc",
                "minimum-patcher-version", "latest-patcher-version", "translation-policy",
                "notes", "notes-file", "revoked-release-ids", "sequence", "expected-previous-sequence");
            if (options.ContainsKey("notes") && options.ContainsKey("notes-file"))
            {
                throw new ArgumentException("Use either --notes or --notes-file, not both.");
            }

            string notes = options.TryGetValue("notes-file", out string? notesPath)
                ? StrictIo.DecodeStrictUtf8(StrictIo.ReadRegularFile(notesPath, "Russian release notes", 32 * 1024), "Russian release notes")
                : Optional(options, "notes", string.Empty);
            var request = new BuildReleaseRequest
            {
                RepositoryRoot = Require(options, "repository-root"),
                CatalogPath = Require(options, "catalog"),
                CompatibilityPath = Require(options, "compatibility"),
                PrivateKeyPath = Require(options, "private-key"),
                SigningStatePath = Require(options, "signing-state"),
                OutputDirectory = Require(options, "output-directory"),
                ReleaseId = Require(options, "release-id"),
                ArtifactId = Require(options, "artifact-id"),
                IssuedUtc = Require(options, "issued-utc"),
                ExpiresUtc = Require(options, "expires-utc"),
                MinimumPatcherVersion = Require(options, "minimum-patcher-version"),
                LatestPatcherVersion = Require(options, "latest-patcher-version"),
                TranslationPolicy = Require(options, "translation-policy"),
                NotesRu = notes,
                RevokedReleaseIdsPath = options.TryGetValue("revoked-release-ids", out string? revoked) ? revoked : null,
                Sequence = ParseUlong(Require(options, "sequence"), "sequence"),
                ExpectedPreviousSequence = ParseUlong(Require(options, "expected-previous-sequence"), "expected-previous-sequence")
            };
            ReleaseReceipt receipt = ReleaseBuilder.Build(request);
            Console.WriteLine("SIGNED UPDATE RELEASE READY");
            Console.WriteLine("release_id: " + receipt.ReleaseId);
            Console.WriteLine("sequence: " + receipt.Sequence.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("key_id: " + receipt.KeyId);
            Console.WriteLine("catalog records: " + receipt.CatalogRecordCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("catalog SHA-256: " + receipt.CatalogCompressedSha256);
            Console.WriteLine("payload SHA-256: " + receipt.PayloadSha256);
            Console.WriteLine("envelope SHA-256: " + receipt.EnvelopeSha256);
        }

        private static void RunBuildCompatibility(Dictionary<string, string> options)
        {
            RejectUnknown(options,
                "runtime-profile", "english-loc1", "base-loc1", "stamp", "catalog",
                "built-loc1", "build-report", "output");
            var request = new BuildCompatibilityRequest
            {
                RuntimeProfilePath = Require(options, "runtime-profile"),
                EnglishLoc1Path = Require(options, "english-loc1"),
                BaseLoc1Path = Require(options, "base-loc1"),
                StampPath = Require(options, "stamp"),
                CatalogPath = Require(options, "catalog"),
                BuiltLoc1Path = Require(options, "built-loc1"),
                BuildReportPath = Require(options, "build-report"),
                OutputPath = Require(options, "output")
            };
            SignedUpdateCompatibilityProfile profile = CompatibilityProfileBuilder.Build(request);
            Console.WriteLine("EXACT COMPATIBILITY PROFILE READY");
            Console.WriteLine("profile_id: " + profile.ProfileId);
            Console.WriteLine("game_version: " + profile.GameVersion);
            Console.WriteLine("ordered keyset SHA-256: " + profile.OrderedKeysetSha256);
            Console.WriteLine("output raw SHA-256: " + profile.Composition.OutputRawSha256);
            Console.WriteLine("applied RU: " + profile.Composition.AppliedRu.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("output: " + Path.GetFullPath(request.OutputPath));
        }

        private static void RunSelfTest(Dictionary<string, string> options)
        {
            RejectUnknown(options, "repository-root");
            string repositoryRoot = Require(options, "repository-root");
            string root = Path.Combine(Path.GetTempPath(), "InvokersRu.UpdateReleaseTool-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            try
            {
                string signing = Path.Combine(root, "signing");
                PublicKeyConfiguration publicConfiguration = SigningKeyService.Generate(repositoryRoot, signing);
                string input = Path.Combine(root, "input");
                string releases = Path.Combine(root, "releases");
                Directory.CreateDirectory(input);
                Directory.CreateDirectory(releases);
                string catalog = Path.Combine(input, "ru_RU.jsonl");
                string compatibility = Path.Combine(input, "compatibility.json");
                byte[] catalogBytes = StrictIo.Utf8.GetBytes(
                    "{\"id\":\"0000000000000001\",\"source_sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"translation\":\"Тест\",\"status\":\"approved\"}\n");
                StrictIo.WriteNewFile(catalog, catalogBytes, "Self-test catalog");
                StrictIo.WriteNewFile(compatibility, CreateSelfTestCompatibility(), "Self-test compatibility");
                DateTimeOffset issued = new DateTimeOffset(DateTime.UtcNow).ToUniversalTime();
                issued = new DateTimeOffset(issued.Year, issued.Month, issued.Day, issued.Hour, issued.Minute, issued.Second, TimeSpan.Zero);
                var request = new BuildReleaseRequest
                {
                    RepositoryRoot = repositoryRoot,
                    CatalogPath = catalog,
                    CompatibilityPath = compatibility,
                    PrivateKeyPath = Path.Combine(signing, SigningKeyService.PrivateKeyFileName),
                    SigningStatePath = Path.Combine(signing, SigningKeyService.StateFileName),
                    OutputDirectory = Path.Combine(releases, "translations-self-test-1"),
                    ReleaseId = "translations-self-test-1",
                    ArtifactId = "ru-RU-self-test-1",
                    IssuedUtc = StrictIo.CanonicalUtcSecond(issued),
                    ExpiresUtc = StrictIo.CanonicalUtcSecond(issued.AddDays(1)),
                    MinimumPatcherVersion = "3.0.0",
                    LatestPatcherVersion = "3.0.1",
                    TranslationPolicy = "release-approved-v1",
                    NotesRu = "Самопроверка.",
                    Sequence = 1,
                    ExpectedPreviousSequence = 0
                };
                ReleaseReceipt receipt = ReleaseBuilder.Build(request);
                string envelopePath = Path.Combine(request.OutputDirectory, "update-envelope.json");
                byte[] envelope = StrictIo.ReadRegularFile(envelopePath, "Self-test envelope", SignedUpdateLimits.MaxEnvelopeBytes);
                byte[] publicKey = Convert.FromBase64String(publicConfiguration.SubjectPublicKeyInfoBase64);
                VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                    envelope,
                    publicKey,
                    publicConfiguration.KeyId,
                    new SignedUpdateVerificationContext(DateTimeOffset.UtcNow, "3.0.1"));
                if (verified.Manifest.Sequence != 1 || verified.PayloadSha256 != receipt.PayloadSha256)
                {
                    throw new InvalidOperationException("Core verifier did not reproduce the signed self-test identity.");
                }

                byte[] tampered = (byte[])envelope.Clone();
                tampered[tampered.Length / 2] ^= 1;
                ExpectFailure(() => SignedUpdateVerifier.Verify(
                    tampered,
                    publicKey,
                    publicConfiguration.KeyId,
                    new SignedUpdateVerificationContext(DateTimeOffset.UtcNow, "3.0.1")));

                var reused = new BuildReleaseRequest
                {
                    RepositoryRoot = request.RepositoryRoot,
                    CatalogPath = request.CatalogPath,
                    CompatibilityPath = request.CompatibilityPath,
                    PrivateKeyPath = request.PrivateKeyPath,
                    SigningStatePath = request.SigningStatePath,
                    OutputDirectory = Path.Combine(releases, "translations-self-test-reuse"),
                    ReleaseId = "translations-self-test-reuse",
                    ArtifactId = "ru-RU-self-test-reuse",
                    IssuedUtc = request.IssuedUtc,
                    ExpiresUtc = request.ExpiresUtc,
                    MinimumPatcherVersion = request.MinimumPatcherVersion,
                    LatestPatcherVersion = request.LatestPatcherVersion,
                    TranslationPolicy = request.TranslationPolicy,
                    NotesRu = request.NotesRu,
                    Sequence = 1,
                    ExpectedPreviousSequence = 0
                };
                ExpectFailure(() => ReleaseBuilder.Build(reused));
                CompatibilityProfileBuilderSelfTest.Run();
                Console.WriteLine("SELF-TEST PASS: signing, Core verification, compatibility generation, tamper rejection, CreateNew, and sequence no-reuse.");
            }
            finally
            {
                StrictIo.TryDeleteDirectory(root);
            }
        }

        private static byte[] CreateSelfTestCompatibility()
        {
            object[] profiles =
            {
                new
                {
                    profile_id = "runtime-cache-self-test",
                    mode = "exact",
                    game_version = "0.0.1",
                    stamp_sha256 = new string('B', 64),
                    stamp_value = "0.0.1",
                    content_guid = "00000000-0000-0000-0000-000000000001",
                    loc1_schema = 4,
                    ordered_keyset_sha256 = new string('C', 64),
                    english = new
                    {
                        sha256 = new string('D', 64),
                        content_version = "SelfTest_1",
                        locale_id = 1,
                        locale_revision_hex = "00000001",
                        release_revision = 1,
                        entry_count = 1
                    },
                    @base = new
                    {
                        sha256 = new string('E', 64),
                        content_version = "SelfTest_1",
                        locale_id = 8,
                        locale_revision_hex = "00000002",
                        release_revision = 1,
                        entry_count = 1
                    },
                    composition = new
                    {
                        applied_ru = 1,
                        english_fallback = 0,
                        base_fallback = 0,
                        missing_catalog = 0,
                        stale_catalog = 0,
                        rejected_catalog = 0,
                        needs_review_fallback = 0,
                        policy_fallback = 0,
                        validation_errors = 0,
                        validation_warnings = 0,
                        output_raw_sha256 = new string('F', 64)
                    }
                }
            };
            return JsonSerializer.SerializeToUtf8Bytes(profiles, StrictIo.CompactJson);
        }

        private static Dictionary<string, string> ParseOptions(string[] args, int start)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = start; index < args.Length; index += 2)
            {
                string name = args[index];
                if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length < 3 || index + 1 >= args.Length)
                {
                    throw new ArgumentException("Options must use exact --name value pairs.");
                }

                name = name.Substring(2);
                if (!result.TryAdd(name, args[index + 1]))
                {
                    throw new ArgumentException("Duplicate option: --" + name);
                }
            }

            return result;
        }

        private static void RejectUnknown(Dictionary<string, string> options, params string[] allowed)
        {
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string name in options.Keys)
            {
                if (!set.Contains(name)) throw new ArgumentException("Unknown option: --" + name);
            }
        }

        private static string Require(Dictionary<string, string> options, string name)
        {
            if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Missing required option: --" + name);
            }

            return value;
        }

        private static string Optional(Dictionary<string, string> options, string name, string fallback)
        {
            return options.TryGetValue(name, out string? value) ? value : fallback;
        }

        private static ulong ParseUlong(string value, string name)
        {
            if (value.Length == 0 || value.Length > 20 || (value.Length > 1 && value[0] == '0')
                || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result))
            {
                throw new ArgumentException($"--{name} must be a canonical unsigned decimal integer.");
            }

            return result;
        }

        private static void ExpectFailure(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }

            throw new InvalidOperationException("Self-test expected a rejection but the operation succeeded.");
        }

        private static void WriteHelp()
        {
            Console.WriteLine("InvokersRu signed update release tool");
            Console.WriteLine("  keygen --repository-root PATH --output-directory EXTERNAL_PATH");
            Console.WriteLine("  build-release --repository-root PATH --catalog FILE --compatibility FILE");
            Console.WriteLine("    --private-key FILE --signing-state FILE --output-directory NEW_DIRECTORY");
            Console.WriteLine("    --release-id ID --artifact-id ID --sequence N --expected-previous-sequence N");
            Console.WriteLine("    --issued-utc UTC --expires-utc UTC --minimum-patcher-version X.Y.Z");
            Console.WriteLine("    --latest-patcher-version X.Y.Z --translation-policy POLICY [--notes TEXT|--notes-file FILE]");
            Console.WriteLine("    [--revoked-release-ids JSON_ARRAY_FILE]");
            Console.WriteLine("  build-compatibility --runtime-profile FILE --english-loc1 FILE --base-loc1 FILE");
            Console.WriteLine("    --stamp FILE --catalog FILE --built-loc1 FILE --build-report FILE --output NEW_FILE");
            Console.WriteLine("  self-test --repository-root PATH");
            Console.WriteLine("  build-patcher-update --repository-root PATH --installer EXE --version X.Y.Z");
            Console.WriteLine("    --release-tag TAG --sequence N --private-key EXTERNAL_FILE --output-directory NEW_DIRECTORY --notes-file FILE");
        }
    }
}
