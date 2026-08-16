using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Patching
{
    internal static class PatchJournalStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };

        public static string JournalDirectory(string statePath)
        {
            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? PatchPlanner.DefaultStateRoot();
            return Path.Combine(stateRoot, "journals");
        }

        public static string PathFor(string statePath, string transactionId)
        {
            ValidateTransactionId(transactionId);
            return Path.Combine(JournalDirectory(statePath), transactionId + ".json");
        }

        public static PatchJournal? FindActive(string statePath)
        {
            string directory = JournalDirectory(statePath);
            PatchService.RejectExistingReparseComponents(directory, "transaction journal directory");
            if (!Directory.Exists(directory)) return null;
            var active = new List<PatchJournal>();
            foreach (string path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                PatchService.RejectExistingReparseComponents(path, "transaction journal path");
                try
                {
                    PatchJournal? journal = JsonSerializer.Deserialize<PatchJournal>(File.ReadAllText(path), Options);
                    if (journal == null)
                    {
                        throw new InvalidDataException($"Empty patch journal requires manual inspection: {path}");
                    }
                    if (journal.Schema != 1)
                    {
                        throw new InvalidDataException($"Unknown patch journal schema {journal.Schema} requires manual inspection: {path}");
                    }
                    ValidateTransactionId(journal.TransactionId);
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(PathFor(statePath, journal.TransactionId)), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Journal filename does not match its transaction id: {path}");
                    }

                    // Terminal records remain recovery-visible until their transaction file is
                    // removed. This closes the crash window between durable Completed/Aborted
                    // and quarantine/journal cleanup.
                    active.Add(journal);
                }
                catch (JsonException)
                {
                    throw new InvalidDataException($"Unreadable patch journal requires manual inspection: {path}");
                }
            }

            if (active.Count > 1)
            {
                throw new InvalidDataException("Multiple active patch journals exist; refusing automatic recovery.");
            }

            return active.SingleOrDefault();
        }

        public static void Save(string statePath, PatchJournal journal)
        {
            journal.UpdatedAt = DateTimeOffset.UtcNow;
            string path = PathFor(statePath, journal.TransactionId);
            string directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Journal path has no parent directory.");
            PatchService.RejectExistingReparseComponents(path, "transaction journal path");
            Directory.CreateDirectory(directory);
            PatchService.RejectExistingReparseComponents(path, "transaction journal path");
            string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonSerializer.Serialize(journal, Options));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                PatchService.RejectExistingReparseComponents(path, "transaction journal path");
                if (File.Exists(path))
                {
                    File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public static void Delete(string statePath, string transactionId)
        {
            string path = PathFor(statePath, transactionId);
            PatchService.RejectExistingReparseComponents(path, "transaction journal path");
            if (File.Exists(path)) File.Delete(path);
        }

        private static void ValidateTransactionId(string? value)
        {
            if (value == null || value.Length != 32 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("Patch transaction id must be exactly 32 hexadecimal characters.");
            }
        }
    }
}
