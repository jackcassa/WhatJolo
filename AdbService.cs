using System.Diagnostics;
using System.IO;

namespace WhatJolo;

internal sealed class AdbService
{
    public string AdbExecutablePath { get; }

    public AdbService()
    {
        AdbExecutablePath = Path.GetFullPath(Path.Combine(
            SharedDatabase.GetHidRootPath(),
            "..",
            "tools",
            "scrcpy-win64-v3.3.4",
            "scrcpy-win64-v3.3.4",
            "adb.exe"));
    }

    public bool Exists()
    {
        return File.Exists(AdbExecutablePath);
    }

    public async Task StartServerAsync()
    {
        var result = await RunAdbAsync("start-server");
        EnsureSuccess(result, "adb start-server");
    }

    public async Task<IReadOnlyList<string>> GetConnectedDevicesAsync()
    {
        var result = await RunAdbAsync("devices");
        EnsureSuccess(result, "adb devices");

        return result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && string.Equals(parts[1], "device", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0].Trim())
            .Where(serial => !string.IsNullOrWhiteSpace(serial))
            .ToList();
    }

    public async Task<byte[]> CapturePngAsync(string deviceSerial)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AdbExecutablePath,
            Arguments = $"-s {deviceSerial} exec-out screencap -p",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        using var output = new MemoryStream();

        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();

        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"adb ha restituito exit code {process.ExitCode}."
                : error.Trim());
        }

        var bytes = output.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("adb non ha restituito dati immagine.");
        }

        return bytes;
    }

    private async Task<AdbCommandResult> RunAdbAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AdbExecutablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new AdbCommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static void EnsureSuccess(AdbCommandResult result, string commandName)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var errorText = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
            ? $"{commandName} ha restituito exit code {result.ExitCode}."
            : errorText.Trim());
    }

    private sealed record AdbCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
