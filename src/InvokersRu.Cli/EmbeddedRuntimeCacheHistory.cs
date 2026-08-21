using InvokersRu.Core.Patching;
using System.Collections.Generic;

namespace InvokersRu.Cli
{
    /// <summary>
    /// Immutable bootstrap authority for exact profiles shipped before the current embedded profile.
    /// These complete descriptors are intentionally compiled into the patcher so replacing the current
    /// profile cannot strand an already-installed older patch while offline. State hashes alone are never
    /// treated as authority.
    /// </summary>
    internal static class EmbeddedRuntimeCacheHistory
    {
        internal static IReadOnlyList<RuntimeCacheCompatibility> CreateProfiles()
        {
            var prod68 = new RuntimeCacheCompatibility
            {
                Schema = 1,
                Id = "runtime-cache-win64-0.60.1247-prod68",
                Mode = "exact",
                GameVersion = "0.60.1247",
                ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa",
                EnglishContentVersion = "Prod_0.60.0_68",
                BaseContentVersion = "Prod_0.60.0_68",
                EnglishSha256 = "4FF01E3D26ECF001B74F822FF11F7115834D9A3FB20FA265186A821CEFE76B21",
                BaseSha256 = "D32B038163DCA0D9830C764234AE2D5EACB5F07CD2FEBD2D2EF14B9F529E0B43",
                StampSha256 = "ABEB909701A6E1E2D597C9F24C289C43D427B3097278CC4D2AB6485BA9CDCBC2",
                StampValue = "0.60.1247",
                EnglishLocaleId = 1,
                EnglishLocaleRevision = 491775477,
                EnglishReleaseRevision = 68,
                BaseLocaleId = 8,
                BaseLocaleRevision = 3617652475,
                BaseReleaseRevision = 68,
                EntryCount = 41292,
                OrderedKeysetSha256 = "3B29E4AC11AC3EAF41E79B3C856E93B3DE2BEF4E6442AAC5D8F9EBA9EAAFDFB8",
                Readiness = "ready",
                Certified = true,
                TranslationCatalogSha256 = "C9E6281FC7918886F1A8943FD39C41B7146230F6D526AB65EA1AAFE5BF10358B",
                ExpectedOutputSha256 = "105B6A9047E6FB0E2C34B774CAE273CC024831C06896C56C4F65F92D2F920541",
                MinimumAppliedTranslations = 41037,
                ExpectedAppliedTranslations = 41037,
                ExpectedEnglishFallbacks = 1,
                ExpectedBaseFallbacks = 254,
                ExpectedNeedsReviewFallbacks = 0,
                TranslationPolicy = "community-preview-all-drafts",
                SupersededArtifacts = new[]
                {
                    new RuntimeCacheSupersededArtifact
                    {
                        OutputSha256 = "4E3AC2FBF663DA1D3EBF4B2EAEBFAADF5BC2A80AE7E424463582919F07B644CD",
                        TranslationCatalogSha256 = "CBF5211BDCB8E940829F305154CE9A92C0D202BCC6B49617A4B92BA25166600C",
                        AppliedTranslations = 41037,
                        EnglishFallbacks = 1,
                        BaseFallbacks = 254,
                        NeedsReviewFallbacks = 0
                    }
                }
            };
            prod68.Validate();
            return new[] { prod68 };
        }
    }
}
