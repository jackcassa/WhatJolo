using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WhatJolo;

public sealed class AdbService
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

    public async Task TapAsync(string deviceSerial, int x, int y)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new InvalidOperationException("Nessun device ADB selezionato.");
        }

        var clampedX = Math.Max(0, x);
        var clampedY = Math.Max(0, y);
        var result = await RunAdbAsync($"-s {deviceSerial} shell input tap {clampedX} {clampedY}");
        EnsureSuccess(result, "adb shell input tap");
    }

    public async Task SendTextAsync(string deviceSerial, string text)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new InvalidOperationException("Nessun device ADB selezionato.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var escapedText = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(" ", "%s", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var result = await RunAdbAsync($"-s {deviceSerial} shell input text \"{escapedText}\"");
        EnsureSuccess(result, "adb shell input text");
    }

    public async Task SendKeyEventAsync(string deviceSerial, int keyCode)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new InvalidOperationException("Nessun device ADB selezionato.");
        }

        var result = await RunAdbAsync($"-s {deviceSerial} shell input keyevent {keyCode}");
        EnsureSuccess(result, "adb shell input keyevent");
    }

    public async Task<string> DumpUiHierarchyXmlAsync(string deviceSerial, string remotePath = "/sdcard/window_dump.xml")
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new InvalidOperationException("Nessun device ADB selezionato.");
        }

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            remotePath = "/sdcard/window_dump.xml";
        }

        var dumpResult = await RunAdbAsync($"-s {deviceSerial} shell uiautomator dump {remotePath}");
        EnsureSuccess(dumpResult, "adb shell uiautomator dump");

        var readResult = await RunAdbAsync($"-s {deviceSerial} shell cat {remotePath}");
        EnsureSuccess(readResult, "adb shell cat window_dump.xml");
        return readResult.StandardOutput;
    }

    public async Task<bool> HasNoSearchResultsAsync(string deviceSerial)
    {
        var xml = await DumpUiHierarchyXmlAsync(deviceSerial);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        return ContainsNodeByTextOrResourceId(
            xml,
            "Nessun risultato trovato",
            "com.whatsapp.w4b:id/search_no_matches");
    }

    public bool ContainsNodeByTextOrResourceId(string xml, string text, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        var normalizedText = (text ?? string.Empty).Trim();
        var normalizedResourceId = (resourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedText) && string.IsNullOrWhiteSpace(normalizedResourceId))
        {
            return false;
        }

        foreach (var node in EnumerateUiNodes(xml))
        {
            if (!string.IsNullOrWhiteSpace(normalizedText) &&
                string.Equals(node.Text, normalizedText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(normalizedResourceId) &&
                string.Equals(node.ResourceId, normalizedResourceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasAnyNodeByResourceId(string xml, params string[] resourceIds)
    {
        if (string.IsNullOrWhiteSpace(xml) || resourceIds is null || resourceIds.Length == 0)
        {
            return false;
        }

        var normalized = new HashSet<string>(
            resourceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0)
        {
            return false;
        }

        foreach (var node in EnumerateUiNodes(xml))
        {
            if (normalized.Contains(node.ResourceId))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryFindNodeByContentDesc(string xml, string contentDesc, out AdbUiNode node)
    {
        var normalizedContentDesc = (contentDesc ?? string.Empty).Trim();
        foreach (var currentNode in EnumerateUiNodes(xml))
        {
            if (string.Equals(currentNode.ContentDescription, normalizedContentDesc, StringComparison.OrdinalIgnoreCase))
            {
                node = currentNode;
                return true;
            }
        }

        node = default;
        return false;
    }

    public bool TryFindNodeByResourceId(string xml, string resourceId, out AdbUiNode node)
    {
        var normalizedResourceId = (resourceId ?? string.Empty).Trim();
        foreach (var currentNode in EnumerateUiNodes(xml))
        {
            if (string.Equals(currentNode.ResourceId, normalizedResourceId, StringComparison.OrdinalIgnoreCase))
            {
                node = currentNode;
                return true;
            }
        }

        node = default;
        return false;
    }

    public (int X, int Y) GetNodeCenter(AdbUiNode node)
    {
        var x = (int)Math.Round((node.Left + node.Right) / 2.0, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round((node.Top + node.Bottom) / 2.0, MidpointRounding.AwayFromZero);
        return (x, y);
    }

    private static IEnumerable<AdbUiNode> EnumerateUiNodes(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch
        {
            yield break;
        }

        foreach (var element in document.Descendants("node"))
        {
            var boundsText = (string?)element.Attribute("bounds") ?? string.Empty;
            if (!TryParseBounds(boundsText, out var left, out var top, out var right, out var bottom))
            {
                continue;
            }

            yield return new AdbUiNode(
                (string?)element.Attribute("text") ?? string.Empty,
                (string?)element.Attribute("content-desc") ?? string.Empty,
                (string?)element.Attribute("resource-id") ?? string.Empty,
                (string?)element.Attribute("class") ?? string.Empty,
                left,
                top,
                right,
                bottom,
                ParseBooleanAttribute(element, "clickable"),
                ParseBooleanAttribute(element, "focused"));
        }
    }

    private static bool ParseBooleanAttribute(XElement element, string name)
    {
        var value = ((string?)element.Attribute(name) ?? string.Empty).Trim();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBounds(string boundsText, out int left, out int top, out int right, out int bottom)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        var match = Regex.Match(boundsText, @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out left) &&
               int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) &&
               int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out right) &&
               int.TryParse(match.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bottom);
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

public readonly record struct AdbUiNode(
    string Text,
    string ContentDescription,
    string ResourceId,
    string ClassName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool Clickable,
    bool Focused);
