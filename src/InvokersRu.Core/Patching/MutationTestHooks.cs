using System;

namespace InvokersRu.Core.Patching
{
    internal static class MutationTestHooks
    {
        internal static Action<string>? BeforeAtomicReplace { get; set; }

        internal static void InvokeBeforeAtomicReplace(string targetPath)
        {
            if (MutationCapability.IsTestWriteBuild) BeforeAtomicReplace?.Invoke(targetPath);
        }
    }
}
