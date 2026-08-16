using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace InvokersRu.Gui;

internal sealed class CliRunner
{
    internal const string CliFileName = "InvokersRu.Cli.exe";

    private readonly string _baseDirectory;

    public CliRunner()
    {
        _baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        CliPath = ResolveFixedCompanion(CliFileName);
    }

    public string CliPath { get; }

    public void ValidateCli()
    {
        RequireRegularCompanion(CliPath, "Исполняемый файл патчера");
    }

    public async Task<CliCommandResult> RunAsync(string command, IEnumerable<string> arguments)
    {
        ValidateCli();

        var startInfo = new ProcessStartInfo
        {
            FileName = CliPath,
            WorkingDirectory = _baseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(command);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить проверенный модуль патчера.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new CliCommandResult(process.ExitCode, stdout, stderr);
    }

    private string ResolveFixedCompanion(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(_baseDirectory, fileName));
        string expected = Path.Combine(_baseDirectory.TrimEnd(Path.DirectorySeparatorChar), fileName);
        if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Небезопасный путь к обязательному файлу {fileName}.");
        }

        return path;
    }

    private static void RequireRegularCompanion(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} не найден рядом с оболочкой: {path}", path);
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} не должен быть ссылкой или точкой повторной обработки: {path}");
        }
    }
}

internal sealed record CliCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput
    {
        get
        {
            string stdout = StandardOutput.Trim();
            string stderr = StandardError.Trim();
            if (stdout.Length == 0) return stderr;
            if (stderr.Length == 0) return stdout;
            return stdout + Environment.NewLine + stderr;
        }
    }
}
