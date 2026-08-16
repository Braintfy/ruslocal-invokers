using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InvokersRu.Cli
{
    internal sealed class ArgumentBag
    {
        private readonly Dictionary<string, string?> _options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positionals = new List<string>();

        public ArgumentBag(string[] args, int startIndex)
        {
            for (int index = startIndex; index < args.Length; index++)
            {
                string value = args[index];
                if (!value.StartsWith("--", StringComparison.Ordinal))
                {
                    _positionals.Add(value);
                    continue;
                }

                string name = value.Substring(2);
                if (name.Length == 0 || _options.ContainsKey(name))
                {
                    throw new ArgumentException($"Invalid or duplicate option: {value}");
                }

                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    _options.Add(name, args[++index]);
                }
                else
                {
                    _options.Add(name, null);
                }
            }
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public void RequireOnlyOptions(IEnumerable<string> allowedOptions, IEnumerable<string> flagOptions)
        {
            var allowed = new HashSet<string>(allowedOptions, StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(flagOptions, StringComparer.OrdinalIgnoreCase);
            string? unknown = _options.Keys.FirstOrDefault(name => !allowed.Contains(name));
            if (unknown != null)
            {
                throw new ArgumentException($"Unknown option for this command: --{unknown}");
            }

            string? flagWithValue = _options.FirstOrDefault(pair => flags.Contains(pair.Key) && pair.Value != null).Key;
            if (flagWithValue != null)
            {
                throw new ArgumentException($"Boolean flag --{flagWithValue} does not accept a value.");
            }
        }

        public string Required(string name)
        {
            if (!_options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Required option --{name} is missing.");
            }

            return value;
        }

        public string Optional(string name, string defaultValue)
        {
            if (!_options.TryGetValue(name, out string? value))
            {
                return defaultValue;
            }

            if (value == null)
            {
                throw new ArgumentException($"Option --{name} requires a value.");
            }

            return value;
        }

        public string Positional(int index, string description)
        {
            if (index >= _positionals.Count)
            {
                throw new ArgumentException($"Missing positional argument: {description}.");
            }

            return _positionals[index];
        }

        public void RequireNoExtraPositionals(int expected)
        {
            if (_positionals.Count > expected)
            {
                throw new ArgumentException($"Unexpected positional argument: {_positionals[expected]}");
            }
        }

        public static void RequireDifferentFiles(string source, string destination)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Input and output paths must differ.");
            }
        }

        public static void RequireOutsideDirectory(string path, string directory)
        {
            string canonicalDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string canonicalPath = Path.GetFullPath(path);
            if (canonicalPath.StartsWith(canonicalDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Refusing to write a preview/output file inside the installed game directory: {canonicalPath}");
            }
        }

        public static void RequireSafeNewOutput(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new ArgumentException($"Output already exists; choose a new path: {fullPath}");
            }

            string gameRoot = InvokersRu.Core.Patching.PatchPlanner.DefaultGameRoot();
            string launcherRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Invokers Titan Legacy");
            string stateRoot = InvokersRu.Core.Patching.PatchPlanner.DefaultStateRoot();
            RequireOutsideDirectory(fullPath, gameRoot);
            RequireOutsideDirectory(fullPath, launcherRoot);
            RequireOutsideDirectory(fullPath, stateRoot);

            string? cursor = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(cursor))
            {
                if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException($"Output path traverses a reparse point: {cursor}");
                }

                string? parent = Path.GetDirectoryName(cursor);
                if (string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase)) break;
                cursor = parent;
            }
        }
    }
}
