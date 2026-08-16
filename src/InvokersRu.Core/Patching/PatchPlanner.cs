using InvokersRu.Core.Loc1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Patching
{
    public static class PatchPlanner
    {
        public static string DefaultGameRoot()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming, "zone.hitzone.invokers.launcher", "game");
        }

        public static string DefaultStateRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "InvokersRussian");
        }

        public static InstallationInspection Inspect(string gameRoot, CompatibilityManifest compatibility, string statePath)
        {
            string resolvedRoot = Path.GetFullPath(gameRoot);
            PatchState? state = TryLoadState(statePath);
            PatchJournal? journal = PatchJournalStore.FindActive(statePath);
            if (journal != null)
            {
                return new InstallationInspection
                {
                    Status = InstallationStatus.RecoveryRequired,
                    Message = $"An interrupted patch transaction is at phase {journal.Phase}. Run recovery before any other write.",
                    GameRoot = resolvedRoot,
                    State = state,
                    Journal = journal
                };
            }
            var candidates = new List<(CompatibleBuild Build, string English, string Base, string Manifest)>();
            foreach (CompatibleBuild build in compatibility.Builds)
            {
                string english = ResolveInside(resolvedRoot, build.EnglishRelativePath);
                string baseLocale = ResolveInside(resolvedRoot, build.BaseRelativePath);
                string manifest = ResolveInside(resolvedRoot, build.ManifestRelativePath);
                if (File.Exists(english) && File.Exists(baseLocale) && File.Exists(manifest))
                {
                    candidates.Add((build, english, baseLocale, manifest));
                }
            }

            if (candidates.Count == 0)
            {
                return new InstallationInspection
                {
                    Status = InstallationStatus.MissingFiles,
                    Message = "Required i18n files were not found under the selected game directory.",
                    GameRoot = resolvedRoot,
                    State = state
                };
            }

            var inspected = new List<InstallationInspection>();
            foreach ((CompatibleBuild build, string english, string baseLocale, string manifest) in candidates)
            {
                string englishHash = Hashing.Sha256File(english);
                string baseHash = Hashing.Sha256File(baseLocale);
                string manifestHash = Hashing.Sha256File(manifest);
                var result = new InstallationInspection
                {
                    GameRoot = resolvedRoot,
                    Build = build,
                    EnglishSha256 = englishHash,
                    BaseSha256 = baseHash,
                    ManifestSha256 = manifestHash,
                    State = state
                };

                TryReadContentIdentity(english, result);
                (result.GameFilesMatch, result.GameFileProblem) = ValidateGameFiles(resolvedRoot, build);
                bool englishMatches = Hashing.FixedEqualsHex(englishHash, build.EnglishSha256);
                bool manifestMatches = Hashing.FixedEqualsHex(manifestHash, build.ManifestSha256);
                bool baseMatches = Hashing.FixedEqualsHex(baseHash, build.BaseSha256);
                if (englishMatches && manifestMatches && baseMatches && result.GameFilesMatch)
                {
                    result.Status = InstallationStatus.CompatibleOriginal;
                    result.Message = $"Compatible original build {build.Id}.";
                    return result;
                }

                if (englishMatches && manifestMatches && result.GameFilesMatch && state != null
                    && string.Equals(state.BuildId, build.Id, StringComparison.Ordinal)
                    && Hashing.FixedEqualsHex(baseHash, state.PatchedSha256)
                    && PathEquals(state.GameRoot, resolvedRoot))
                {
                    result.Status = InstallationStatus.PatchedByThisTool;
                    result.Message = $"Build {build.Id} is already patched by this tool.";
                    return result;
                }

                result.Status = state != null && PathEquals(state.GameRoot, resolvedRoot)
                    ? InstallationStatus.InconsistentState
                    : InstallationStatus.UnknownBuild;
                result.Message = result.Status == InstallationStatus.InconsistentState
                    ? "Patch state exists, but installed hashes do not match either the known original or recorded patched file. Refusing to overwrite it."
                    : "Installed hashes are not in the compatibility manifest. A new game release may be installed; refusing to patch it.";
                inspected.Add(result);
            }

            return inspected.OrderByDescending(item => IdentityMatches(item)).First();
        }

        public static PatchState? TryLoadState(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    AllowDuplicateProperties = false
                };
                PatchState? state = JsonSerializer.Deserialize<PatchState>(File.ReadAllText(path), options);
                return state?.Schema == 1 ? state : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string ResolveInside(string root, string relativePath)
        {
            string resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(Path.Combine(resolvedRoot, relativePath));
            if (!resolved.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Relative path escapes the game root: {relativePath}");
            }

            return resolved;
        }

        private static int IdentityMatches(InstallationInspection inspection)
        {
            if (inspection.Build == null)
            {
                return 0;
            }

            int score = 0;
            if (string.Equals(inspection.Build.ContentGuid, inspection.DetectedContentGuid, StringComparison.Ordinal)) score++;
            if (string.Equals(inspection.Build.ContentVersion, inspection.DetectedContentVersion, StringComparison.Ordinal)) score++;
            return score;
        }

        private static (bool Matches, string? Problem) ValidateGameFiles(string root, CompatibleBuild build)
        {
            foreach (KnownFile expected in build.GameFiles)
            {
                string path = ResolveInside(root, expected.RelativePath);
                if (!File.Exists(path))
                {
                    return (false, $"Missing pinned game file: {expected.RelativePath}");
                }

                long actualSize = new FileInfo(path).Length;
                if (actualSize != expected.Size)
                {
                    return (false, $"Size mismatch for pinned game file: {expected.RelativePath}");
                }

                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(path), expected.Sha256))
                {
                    return (false, $"SHA-256 mismatch for pinned game file: {expected.RelativePath}");
                }
            }

            return (true, null);
        }

        private static void TryReadContentIdentity(string englishPath, InstallationInspection inspection)
        {
            try
            {
                Loc1Document english = Loc1Codec.ReadFile(englishPath);
                inspection.DetectedContentGuid = english.ContentGuid;
                inspection.DetectedContentVersion = english.ContentVersion;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is Loc1FormatException)
            {
                inspection.DetectedContentGuid = null;
                inspection.DetectedContentVersion = null;
            }
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
    }
}
