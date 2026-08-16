using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InvokersRu.Gui;

internal sealed class CliPlanResult
{
    private static readonly Regex BuildPattern = new(
        "^Build: (?<id>.+?) / game (?<game>.+?) / (?<content>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int ExitCode { get; private init; }

    public string Status { get; private init; } = string.Empty;

    public string PlanAction { get; private init; } = string.Empty;

    public string GameRoot { get; private init; } = string.Empty;

    public string BuildId { get; private init; } = string.Empty;

    public string GameVersion { get; private init; } = string.Empty;

    public string ContentVersion { get; private init; } = string.Empty;

    public int ProcessConflicts { get; private init; }

    public string RawOutput { get; private init; } = string.Empty;

    public bool CanApply => ExitCode == 0
        && string.Equals(Status, "CompatibleOriginal", StringComparison.Ordinal)
        && string.Equals(PlanAction, "READY_TO_APPLY", StringComparison.Ordinal)
        && ProcessConflicts == 0;

    public bool CanRestore => ExitCode == 0
        && string.Equals(Status, "PatchedByThisTool", StringComparison.Ordinal)
        && string.Equals(PlanAction, "NOOP_OR_RESTORE", StringComparison.Ordinal)
        && ProcessConflicts == 0;

    public bool IsPatched => string.Equals(Status, "PatchedByThisTool", StringComparison.Ordinal);

    public bool IsVersionRisk => string.Equals(Status, "UnknownBuild", StringComparison.Ordinal)
        || string.Equals(Status, "InconsistentState", StringComparison.Ordinal)
        || string.Equals(Status, "MissingFiles", StringComparison.Ordinal)
        || string.Equals(Status, "RecoveryRequired", StringComparison.Ordinal)
        || string.Equals(PlanAction, "REFUSE_UNKNOWN_OR_INCONSISTENT", StringComparison.Ordinal)
        || string.Equals(PlanAction, "REFUSE_UNTIL_CERTIFIED", StringComparison.Ordinal)
        || string.Equals(PlanAction, "REFUSE_DEV_WRITES_DISABLED", StringComparison.Ordinal);

    public static CliPlanResult Parse(CliCommandResult command)
    {
        string status = string.Empty;
        string action = string.Empty;
        string gameRoot = string.Empty;
        string buildId = string.Empty;
        string gameVersion = string.Empty;
        string contentVersion = string.Empty;
        int conflicts = 0;

        string output = command.CombinedOutput;
        foreach (string rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Status: ", StringComparison.Ordinal))
            {
                status = line[8..].Trim();
            }
            else if (line.StartsWith("Plan: ", StringComparison.Ordinal))
            {
                action = line[6..].Trim();
            }
            else if (line.StartsWith("Game root: ", StringComparison.Ordinal))
            {
                gameRoot = line[11..].Trim();
            }
            else if (line.StartsWith("Process conflicts: ", StringComparison.Ordinal))
            {
                string value = line[19..].Trim();
                if (!string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out conflicts);
                }
            }
            else
            {
                Match match = BuildPattern.Match(line);
                if (match.Success)
                {
                    buildId = match.Groups["id"].Value;
                    gameVersion = match.Groups["game"].Value;
                    contentVersion = match.Groups["content"].Value;
                }
            }
        }

        return new CliPlanResult
        {
            ExitCode = command.ExitCode,
            Status = status,
            PlanAction = action,
            GameRoot = gameRoot,
            BuildId = buildId,
            GameVersion = gameVersion,
            ContentVersion = contentVersion,
            ProcessConflicts = conflicts,
            RawOutput = output
        };
    }
}
