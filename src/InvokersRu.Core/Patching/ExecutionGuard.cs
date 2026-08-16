using System;
using System.IO;
using System.Threading;

namespace InvokersRu.Core.Patching
{
    internal sealed class ExecutionGuard : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly FileStream _lockStream;
        private bool _ownsMutex;

        private ExecutionGuard(Mutex mutex, FileStream lockStream, bool ownsMutex)
        {
            _mutex = mutex;
            _lockStream = lockStream;
            _ownsMutex = ownsMutex;
        }

        public static ExecutionGuard Acquire(string gameRoot, string statePath)
        {
            string name = "Local\\InvokersRuPatcher-" + Hashing.Sha256Text(Path.GetFullPath(gameRoot).ToUpperInvariant()).Substring(0, 24);
            var mutex = new Mutex(initiallyOwned: false, name);
            bool owns = false;
            try
            {
                try
                {
                    owns = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    owns = true;
                }

                if (!owns)
                {
                    throw new InvalidOperationException("Another patcher process is already working with this installation.");
                }

                string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? PatchPlanner.DefaultStateRoot();
                string lockPath = Path.Combine(stateRoot, "patcher.lock");
                PatchService.RejectExistingReparseComponents(statePath, "patch state path");
                PatchService.RejectExistingReparseComponents(lockPath, "execution lock path");
                Directory.CreateDirectory(stateRoot);
                PatchService.RejectExistingReparseComponents(statePath, "patch state path");
                PatchService.RejectExistingReparseComponents(lockPath, "execution lock path");
                FileStream stream;
                try
                {
                    stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException exception)
                {
                    throw new InvalidOperationException("Patcher lock file is held by another process.", exception);
                }

                return new ExecutionGuard(mutex, stream, owns);
            }
            catch
            {
                if (owns) mutex.ReleaseMutex();
                mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _lockStream.Dispose();
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }

            _mutex.Dispose();
        }
    }
}
