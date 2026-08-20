using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.Gui;

internal sealed class CliRunner
{
    internal const string CliFileName = "InvokersRu.Cli.exe";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(10);

    private readonly string _baseDirectory;
    private readonly TimeSpan _timeout;

    public CliRunner()
        : this(AppContext.BaseDirectory, DefaultTimeout)
    {
    }

    internal CliRunner(string baseDirectory, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Рабочий каталог проверяющего модуля не задан.", nameof(baseDirectory));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Тайм-аут проверяющего модуля должен быть больше нуля и не превышать 30 минут.");

        _baseDirectory = Path.GetFullPath(baseDirectory);
        _timeout = timeout;
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
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateOwnChildAsync(process).ConfigureAwait(false);
            await DrainTerminatedOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Проверяющий модуль не завершил операцию за {_timeout.TotalSeconds:N0} с. Его собственный процесс был остановлен; повторите проверку.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new CliCommandResult(process.ExitCode, stdout, stderr);
    }

    private static async Task TerminateOwnChildAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // This Process instance is the exact companion started above. Never search by name and never
                // terminate game, launcher, updater, or a second unrelated patcher process.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "Проверяющий модуль превысил время ожидания, но его собственный процесс не удалось остановить.", exception);
        }

        using var termination = new CancellationTokenSource(TerminationTimeout);
        try
        {
            await process.WaitForExitAsync(termination.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                "Проверяющий модуль превысил время ожидания и не завершился после команды остановки.", exception);
        }
    }

    private static async Task DrainTerminatedOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
        {
            // The timed-out child is already terminated. Broken redirected pipes contain no trustworthy result,
            // so the caller receives the timeout message instead of a secondary stream exception.
        }
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
