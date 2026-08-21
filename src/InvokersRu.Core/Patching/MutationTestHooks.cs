using System;

namespace InvokersRu.Core.Patching
{
    internal static class MutationTestHooks
    {
        internal static Action<string>? BeforeAtomicReplace { get; set; }
        internal static Action<string>? BeforeEquivalentCatalogCommit { get; set; }
        internal static Action<string>? BeforeSupersededStateArchive { get; set; }

        internal static void InvokeBeforeAtomicReplace(string targetPath)
        {
            if (MutationCapability.IsTestWriteBuild) BeforeAtomicReplace?.Invoke(targetPath);
        }

        internal static void InvokeBeforeEquivalentCatalogCommit(string catalogPath)
        {
            if (MutationCapability.IsTestWriteBuild) BeforeEquivalentCatalogCommit?.Invoke(catalogPath);
        }

        internal static void InvokeBeforeSupersededStateArchive(string statePath)
        {
            if (MutationCapability.IsTestWriteBuild) BeforeSupersededStateArchive?.Invoke(statePath);
        }
    }
}
