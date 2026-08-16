using System;
using System.IO;
using System.Collections.Generic;

namespace InvokersRu.Core.Patching
{
    internal static class MutationPolicy
    {
        private static readonly Dictionary<string, string> TestRuntimeBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal static bool IsEnabled => MutationCapability.IsEnabled;
        internal static bool IsTestWriteBuild => MutationCapability.IsTestWriteBuild;

        internal static void RequireEnabled()
        {
            if (!IsEnabled)
            {
                throw new InvalidOperationException("Core mutation is compile-time disabled in this ordinary build.");
            }
        }

        internal static void RequireRuntimeStatePath(string statePath)
        {
            if (!IsTestWriteBuild && !PathEquals(statePath, RuntimeCacheService.DefaultStatePath()))
            {
                throw new InvalidDataException("Runtime-cache mutation state path is not the fixed LocalAppData path.");
            }
        }

        internal static void RequireRuntimeRoot(string cacheRoot)
        {
            if (!IsTestWriteBuild && !PathEquals(cacheRoot, RuntimeCacheService.DefaultCacheRoot()))
            {
                throw new InvalidDataException("Runtime-cache mutation root is not the fixed LocalLow cache root.");
            }
        }

        internal static void BindTestRuntimePaths(string cacheRoot, string statePath)
        {
            if (!IsTestWriteBuild) return;
            lock (TestRuntimeBindings)
            {
                TestRuntimeBindings[Path.GetFullPath(statePath)] = Path.GetFullPath(cacheRoot);
            }
        }

        internal static void RequireRuntimeBinding(string cacheRoot, string statePath)
        {
            RequireRuntimeStatePath(statePath);
            RequireRuntimeRoot(cacheRoot);
            if (!IsTestWriteBuild) return;
            lock (TestRuntimeBindings)
            {
                if (!TestRuntimeBindings.TryGetValue(Path.GetFullPath(statePath), out string? boundRoot)
                    || !PathEquals(boundRoot, cacheRoot))
                    throw new InvalidDataException("Test-write runtime root is not bound to this isolated state path.");
            }
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
