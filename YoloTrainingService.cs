using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatJolo;

internal sealed class YoloTrainingService
{
    private static readonly Regex EpochRegex = new(@"^\s*(\d+)\s*/\s*(\d+)\b", RegexOptions.Compiled);

    public async Task<string?> ExportOnnxAsync(
        string bestPtPath,
        string workingDirectory,
        string? datasetYamlPath = null,
        int imageSize = 640,
        IProgress<YoloTrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(bestPtPath))
        {
            return null;
        }

        var command = DetectPreferredYoloCommand();
        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new InvalidOperationException("Non trovo yolo/ultralytics installato. Verifica il runtime YoloRuntime.");
        }

        var exportArguments =
            command.ArgumentsPrefix +
            " export" +
            " model=" + QuoteArg(bestPtPath) +
            " format=onnx imgsz=" + imageSize;

        Report(progress, new YoloTrainingProgress("Avvio export ONNX on-demand...", null, null, "info"));
        Report(progress, new YoloTrainingProgress("Export model: " + bestPtPath, null, null, "info"));

        using var exportProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = exportArguments.Trim(),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        exportProcess.Start();
        using var exportCancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!exportProcess.HasExited)
                {
                    exportProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });
        var exportStdOut = await exportProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var exportStdErr = await exportProcess.StandardError.ReadToEndAsync(cancellationToken);
        await exportProcess.WaitForExitAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(exportStdOut))
        {
            Report(progress, new YoloTrainingProgress(exportStdOut.Trim(), null, null, "stdout"));
        }

        if (!string.IsNullOrWhiteSpace(exportStdErr))
        {
            Report(progress, new YoloTrainingProgress(exportStdErr.Trim(), null, null, "stderr"));
        }

        if (exportProcess.ExitCode != 0)
        {
            Report(progress, new YoloTrainingProgress("Export ONNX on-demand fallito.", null, null, "stderr"));
            return null;
        }

        var onnxModelPath = Path.Combine(Path.GetDirectoryName(bestPtPath)!, "best.onnx");
        if (!string.IsNullOrWhiteSpace(datasetYamlPath))
        {
            var classesPath = Path.Combine(Path.GetDirectoryName(datasetYamlPath)!, "classes.txt");
            var labelsPath = Path.Combine(Path.GetDirectoryName(bestPtPath)!, "best.labels.txt");
            if (File.Exists(classesPath))
            {
                var labelsDirectory = Path.GetDirectoryName(labelsPath);
                if (!string.IsNullOrWhiteSpace(labelsDirectory))
                {
                    Directory.CreateDirectory(labelsDirectory);
                }

                File.Copy(classesPath, labelsPath, overwrite: true);
            }
        }

        Report(progress, new YoloTrainingProgress("Export ONNX on-demand completato: " + onnxModelPath, null, null, "info"));
        return File.Exists(onnxModelPath) ? onnxModelPath : null;
    }

    public async Task<YoloTrainingResult> TrainAsync(
        string projectName,
        string datasetYamlPath,
        string workingDirectory,
        string modelName,
        int epochs,
        int imageSize,
        int batch,
        IProgress<YoloTrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var command = DetectPreferredYoloCommand();
        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new InvalidOperationException("Non trovo yolo/ultralytics installato. Verifica il runtime YoloRuntime.");
        }

        var trainingDevice = await DetectTrainingDeviceAsync(command, cancellationToken);

        var runsFolder = Path.Combine(workingDirectory, "runs");
        Directory.CreateDirectory(runsFolder);
        var safeProjectName = BuildSafeProjectName(projectName);
        var latestRunPath = FindLatestRunPath(runsFolder, safeProjectName);
        var effectiveRunName = !string.IsNullOrWhiteSpace(latestRunPath)
            ? Path.GetFileName(latestRunPath)
            : safeProjectName;
        var runFolder = latestRunPath ?? Path.Combine(runsFolder, effectiveRunName);
        var lastCheckpointPath = latestRunPath == null ? string.Empty : Path.Combine(latestRunPath, "weights", "last.pt");
        var trainLogPath = Path.Combine(workingDirectory, "train_yolo.log");
        var resumeTraining = !string.IsNullOrWhiteSpace(lastCheckpointPath) && File.Exists(lastCheckpointPath);
        var arguments = resumeTraining
            ? command.ArgumentsPrefix +
              " detect train" +
              " data=" + QuoteArg(datasetYamlPath) +
              " model=" + QuoteArg(lastCheckpointPath) +
              " epochs=" + epochs +
              " imgsz=" + imageSize +
              " batch=" + batch +
              " device=" + trainingDevice +
              " project=" + QuoteArg(runsFolder) +
              " name=" + QuoteArg(effectiveRunName) +
              " exist_ok=true"
            : command.ArgumentsPrefix +
              " detect train" +
              " data=" + QuoteArg(datasetYamlPath) +
              " model=" + QuoteArg(modelName) +
              " epochs=" + epochs +
              " imgsz=" + imageSize +
              " batch=" + batch +
              " device=" + trainingDevice +
              " project=" + QuoteArg(runsFolder) +
              " name=" + QuoteArg(effectiveRunName);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var lineLog = new StringBuilder();
        Report(progress, new YoloTrainingProgress("Preparazione training YOLO...", null, null, "info"));
        Report(progress, new YoloTrainingProgress("Runtime: " + command.FileName, null, null, "info"));
        Report(progress, new YoloTrainingProgress("Modello: " + modelName, null, null, "info"));
        Report(progress, new YoloTrainingProgress($"Device training scelto: {trainingDevice}", null, null, "info"));
        Report(progress, new YoloTrainingProgress($"Parametri: epochs={epochs} imgsz={imageSize} batch={batch} device={trainingDevice}", null, null, "info"));
        Report(progress, new YoloTrainingProgress(resumeTraining
            ? "Continua training da last.pt come pesi iniziali"
            : "Training nuovo da zero", null, null, "info"));
        Report(progress, new YoloTrainingProgress("RunName: " + effectiveRunName, null, null, "info"));
        Report(progress, new YoloTrainingProgress("Argomenti: " + arguments.Trim(), null, null, "info"));
        Report(progress, new YoloTrainingProgress("WorkingDir: " + workingDirectory, null, null, "info"));
        Report(progress, new YoloTrainingProgress("DatasetYaml: " + datasetYamlPath, null, null, "info"));
        Report(progress, new YoloTrainingProgress("RunFolder: " + runFolder, null, null, "info"));
        if (resumeTraining)
        {
            Report(progress, new YoloTrainingProgress("Checkpoint: " + lastCheckpointPath, null, null, "info"));
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = arguments.Trim(),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        File.WriteAllText(
            trainLogPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] YOLO TRAIN START{Environment.NewLine}" +
            $"Project: {projectName}{Environment.NewLine}" +
            $"Runtime: {command.FileName}{Environment.NewLine}" +
            $"Model: {modelName}{Environment.NewLine}" +
            $"Epochs: {epochs}{Environment.NewLine}" +
            $"ImageSize: {imageSize}{Environment.NewLine}" +
            $"Batch: {batch}{Environment.NewLine}" +
            $"Device: {trainingDevice}{Environment.NewLine}" +
            $"Resume: {resumeTraining}{Environment.NewLine}" +
            $"Checkpoint: {lastCheckpointPath}{Environment.NewLine}" +
            $"RunName: {effectiveRunName}{Environment.NewLine}" +
            $"Arguments: {arguments.Trim()}{Environment.NewLine}" +
            $"WorkingDir: {workingDirectory}{Environment.NewLine}" +
            $"DatasetYaml: {datasetYamlPath}{Environment.NewLine}" +
            $"RunFolder: {runFolder}{Environment.NewLine}{Environment.NewLine}",
            new UTF8Encoding(false));

        process.Start();
        using var trainCancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });
        Report(progress, new YoloTrainingProgress($"Processo YOLO avviato. PID={process.Id}", null, null, "info"));

        var stdOutTask = ReadStreamAsync(process.StandardOutput, line =>
        {
            standardOutput.AppendLine(line);
            lineLog.AppendLine("[STDOUT] " + line);
            Report(progress, ParseProgress(line, "stdout"));
        }, cancellationToken);

        var stdErrTask = ReadStreamAsync(process.StandardError, line =>
        {
            standardError.AppendLine(line);
            lineLog.AppendLine("[STDERR] " + line);
            Report(progress, ParseProgress(line, "stderr"));
        }, cancellationToken);

        Report(progress, new YoloTrainingProgress("Attendo l'uscita del processo YOLO...", null, null, "info"));
        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync(cancellationToken));
        Report(progress, new YoloTrainingProgress($"Processo YOLO terminato. ExitCode={process.ExitCode}", null, null, "info"));

        File.AppendAllText(
            trainLogPath,
            lineLog.ToString() +
            $"{Environment.NewLine}ExitCode: {process.ExitCode}{Environment.NewLine}" +
            $"Finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
            new UTF8Encoding(false));

        var onnxModelPath = string.Empty;
        if (process.ExitCode == 0)
        {
            var bestPtPath = Path.Combine(runFolder, "weights", "best.pt");
            if (File.Exists(bestPtPath))
            {
                var exportedPath = await ExportOnnxAsync(bestPtPath, workingDirectory, datasetYamlPath, imageSize, progress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(exportedPath))
                {
                    onnxModelPath = exportedPath;
                }
                else
                {
                    Report(progress, new YoloTrainingProgress("Export ONNX fallito.", null, null, "stderr"));
                }
            }
        }

        return new YoloTrainingResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            runFolder,
            trainLogPath,
            onnxModelPath);
    }

    public async Task<YoloTrainingResult> TestAsync(
        string projectName,
        string datasetYamlPath,
        string workingDirectory,
        int imageSize,
        IProgress<YoloTrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var command = DetectPreferredYoloCommand();
        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new InvalidOperationException("Non trovo yolo/ultralytics installato. Verifica il runtime YoloRuntime.");
        }

        var trainingDevice = await DetectTrainingDeviceAsync(command, cancellationToken);

        var runsFolder = Path.Combine(workingDirectory, "runs");
        Directory.CreateDirectory(runsFolder);
        var safeProjectName = BuildSafeProjectName(projectName);
        var latestRunPath = FindLatestRunPath(runsFolder, safeProjectName);
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            throw new InvalidOperationException("Nessuna run YOLO trovata per il progetto corrente.");
        }

        var bestCheckpointPath = Path.Combine(latestRunPath, "weights", "best.pt");
        if (!File.Exists(bestCheckpointPath))
        {
            throw new InvalidOperationException("Checkpoint best.pt non trovato per il progetto corrente.");
        }

        var runName = Path.GetFileName(latestRunPath);
        var testLogPath = Path.Combine(workingDirectory, "test_yolo.log");
        var arguments =
            command.ArgumentsPrefix +
            " detect val" +
            " data=" + QuoteArg(datasetYamlPath) +
            " model=" + QuoteArg(bestCheckpointPath) +
            " split=test" +
            " imgsz=" + imageSize +
            " device=" + trainingDevice +
            " project=" + QuoteArg(runsFolder) +
            " name=" + QuoteArg(runName + "_test") +
            " exist_ok=true";

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var lineLog = new StringBuilder();
        Report(progress, new YoloTrainingProgress("Preparazione test YOLO...", null, null, "info"));
        Report(progress, new YoloTrainingProgress("Runtime: " + command.FileName, null, null, "info"));
        Report(progress, new YoloTrainingProgress("Device test scelto: " + trainingDevice, null, null, "info"));
        Report(progress, new YoloTrainingProgress("Checkpoint test: " + bestCheckpointPath, null, null, "info"));
        Report(progress, new YoloTrainingProgress("Argomenti: " + arguments.Trim(), null, null, "info"));
        Report(progress, new YoloTrainingProgress("WorkingDir: " + workingDirectory, null, null, "info"));
        Report(progress, new YoloTrainingProgress("DatasetYaml: " + datasetYamlPath, null, null, "info"));

        File.WriteAllText(
            testLogPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] YOLO TEST START{Environment.NewLine}" +
            $"Project: {projectName}{Environment.NewLine}" +
            $"Runtime: {command.FileName}{Environment.NewLine}" +
            $"Device: {trainingDevice}{Environment.NewLine}" +
            $"Checkpoint: {bestCheckpointPath}{Environment.NewLine}" +
            $"ImageSize: {imageSize}{Environment.NewLine}" +
            $"Arguments: {arguments.Trim()}{Environment.NewLine}" +
            $"WorkingDir: {workingDirectory}{Environment.NewLine}" +
            $"DatasetYaml: {datasetYamlPath}{Environment.NewLine}{Environment.NewLine}",
            new UTF8Encoding(false));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = arguments.Trim(),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.Start();
        using var testCancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });
        Report(progress, new YoloTrainingProgress($"Processo YOLO test avviato. PID={process.Id}", null, null, "info"));

        var stdOutTask = ReadStreamAsync(process.StandardOutput, line =>
        {
            standardOutput.AppendLine(line);
            lineLog.AppendLine("[STDOUT] " + line);
            Report(progress, ParseProgress(line, "stdout"));
        }, cancellationToken);

        var stdErrTask = ReadStreamAsync(process.StandardError, line =>
        {
            standardError.AppendLine(line);
            lineLog.AppendLine("[STDERR] " + line);
            Report(progress, ParseProgress(line, "stderr"));
        }, cancellationToken);

        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync(cancellationToken));
        Report(progress, new YoloTrainingProgress($"Processo YOLO test terminato. ExitCode={process.ExitCode}", null, null, "info"));

        File.AppendAllText(
            testLogPath,
            lineLog.ToString() +
            $"{Environment.NewLine}ExitCode: {process.ExitCode}{Environment.NewLine}" +
            $"Finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
            new UTF8Encoding(false));

        return new YoloTrainingResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            latestRunPath,
            testLogPath,
            string.Empty);
    }

    public string DetectRuntimeDescription()
    {
        var command = DetectPreferredYoloCommand();
        return string.IsNullOrWhiteSpace(command.FileName)
            ? "YOLO runtime non trovato"
            : $"{command.FileName} {command.ArgumentsPrefix}".Trim();
    }

    private static async Task ReadStreamAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var charBuffer = new char[1];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var charsRead = await reader.ReadAsync(charBuffer.AsMemory(0, 1), cancellationToken);
            if (charsRead == 0)
            {
                FlushBuffer(buffer, onLine);
                break;
            }

            var ch = charBuffer[0];
            if (ch == '\r' || ch == '\n')
            {
                FlushBuffer(buffer, onLine);
                continue;
            }

            buffer.Append(ch);
        }
    }

    private static void FlushBuffer(StringBuilder buffer, Action<string> onLine)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var line = buffer.ToString().Trim();
        buffer.Clear();
        if (line.Length == 0)
        {
            return;
        }

        onLine(line);
    }

    private static YoloTrainingProgress ParseProgress(string line, string source)
    {
        var match = EpochRegex.Match(line);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var currentEpoch) &&
            int.TryParse(match.Groups[2].Value, out var totalEpochs))
        {
            return new YoloTrainingProgress(line, currentEpoch, totalEpochs, source);
        }

        return new YoloTrainingProgress(line, null, null, source);
    }

    private static void Report(IProgress<YoloTrainingProgress>? progress, YoloTrainingProgress update)
    {
        progress?.Report(update);
    }

    private static string BuildSafeProjectName(string projectName)
    {
        var safeName = Regex.Replace(projectName, "[^a-zA-Z0-9_-]+", "_").Trim('_');
        return safeName.Length == 0 ? "default" : safeName;
    }

    private static string? FindLatestRunPath(string runsFolder, string runName)
    {
        if (!Directory.Exists(runsFolder))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(runsFolder, runName + "*", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Stamp = GetRunTimestamp(path)
            })
            .OrderByDescending(item => item.Stamp)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static DateTime GetRunTimestamp(string runPath)
    {
        var weightsFolder = Path.Combine(runPath, "weights");
        var lastCheckpoint = Path.Combine(weightsFolder, "last.pt");
        var bestCheckpoint = Path.Combine(weightsFolder, "best.pt");

        if (File.Exists(lastCheckpoint))
        {
            return File.GetLastWriteTimeUtc(lastCheckpoint);
        }

        if (File.Exists(bestCheckpoint))
        {
            return File.GetLastWriteTimeUtc(bestCheckpoint);
        }

        return Directory.GetLastWriteTimeUtc(runPath);
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static async Task<string> DetectTrainingDeviceAsync(YoloCommand command, CancellationToken cancellationToken)
    {
        var pythonExe = ResolvePythonForCommand(command);
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
        {
            return "cpu";
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-c \"import torch; print('0' if torch.cuda.is_available() else 'cpu')\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var allTask = Task.WhenAll(outputTask, errorTask, waitTask);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var completed = await Task.WhenAny(allTask, timeoutTask);
        if (completed == timeoutTask)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            return "cpu";
        }

        await allTask;
        var output = (await outputTask).Trim();
        return string.Equals(output, "0", StringComparison.Ordinal) ? "0" : "cpu";
    }

    private static string? ResolvePythonForCommand(YoloCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            return null;
        }

        if (Path.GetFileName(command.FileName).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
        {
            return command.FileName;
        }

        var directory = Path.GetDirectoryName(command.FileName);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, "python.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static YoloCommand DetectPreferredYoloCommand()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(SharedDatabase.GetHidRootPath(), "ScrcpyKeyboardClient", "YoloRuntime", ".venv", "Scripts", "yolo.exe")),
            Path.GetFullPath(Path.Combine(SharedDatabase.GetHidRootPath(), "HID", "ScrcpyKeyboardClient", "YoloRuntime", ".venv", "Scripts", "yolo.exe")),
            Path.GetFullPath(Path.Combine(SharedDatabase.GetProjectDirectoryPath(), "YoloRuntime", ".venv", "Scripts", "yolo.exe"))
        };

        var yoloExe = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(yoloExe))
        {
            return new YoloCommand(yoloExe, string.Empty);
        }

        var pythonCandidates = new[]
        {
            Path.GetFullPath(Path.Combine(SharedDatabase.GetHidRootPath(), "ScrcpyKeyboardClient", "YoloRuntime", ".venv", "Scripts", "python.exe")),
            Path.GetFullPath(Path.Combine(SharedDatabase.GetHidRootPath(), "HID", "ScrcpyKeyboardClient", "YoloRuntime", ".venv", "Scripts", "python.exe")),
            Path.GetFullPath(Path.Combine(SharedDatabase.GetProjectDirectoryPath(), "YoloRuntime", ".venv", "Scripts", "python.exe"))
        };

        var pythonExe = pythonCandidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(pythonExe))
        {
            return new YoloCommand(pythonExe, "-m ultralytics");
        }

        return new YoloCommand(string.Empty, string.Empty);
    }

    private sealed record YoloCommand(string FileName, string ArgumentsPrefix);
}

public sealed record YoloTrainingProgress(string RawLine, int? CurrentEpoch, int? TotalEpochs, string Source);

internal sealed record YoloTrainingResult(int ExitCode, string StandardOutput, string StandardError, string RunFolder, string TrainLogPath, string OnnxModelPath);
