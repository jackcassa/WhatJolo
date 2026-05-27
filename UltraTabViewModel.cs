using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WhatJolo;

public sealed class UltraTabViewModel : ViewModelBase
{
    private const float DetectionThreshold = 0.05f;
    private const string ModelSourceLatestLocal = "Ultimo best.onnx locale";
    private const string ModelSourceDatabase = "best.onnx dal DB";
    private readonly AdbService _adbService;
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly ProjectModelBlobService _projectModelBlobService;
    private readonly ProjectWorkspaceService _workspaceService;
    private readonly YoloTrainingService _yoloTrainingService;
    private string _currentProjectName;
    private string _currentClassName;
    private TestImageItem? _selectedTestImage;
    private TestImageItem? _selectedTrainImage;
    private TestImageItem? _selectedValImage;
    private BitmapImage? _detectionPreview;
    private string _statusText;
    private string _modelPath;
    private string _selectedModelSource;
    private string _detectionsSummary;
    private string _lastDetectionImagePath;
    private string _lastDetectionSourceName;
    private IReadOnlyList<YoloDetection> _lastDetections;

    public UltraTabViewModel()
    {
        _adbService = new AdbService();
        _annotationCropDbService = new AnnotationCropDbService();
        _projectImageBlobService = new ProjectImageBlobService();
        _projectModelBlobService = new ProjectModelBlobService();
        _workspaceService = new ProjectWorkspaceService();
        _yoloTrainingService = new YoloTrainingService();
        _currentProjectName = "Default";
        _currentClassName = "cerca";
        _statusText = "Pronto.";
        _modelPath = "Modello ONNX non trovato.";
        _selectedModelSource = ModelSourceLatestLocal;
        _detectionsSummary = "-";
        _lastDetectionImagePath = string.Empty;
        _lastDetectionSourceName = string.Empty;
        _lastDetections = Array.Empty<YoloDetection>();
        TestImages = new ObservableCollection<TestImageItem>();
        TrainImages = new ObservableCollection<TestImageItem>();
        ValImages = new ObservableCollection<TestImageItem>();
        ModelSourceOptions = new ObservableCollection<string>
        {
            ModelSourceLatestLocal,
            ModelSourceDatabase
        };
    }

    public ObservableCollection<TestImageItem> TestImages { get; }
    public ObservableCollection<TestImageItem> TrainImages { get; }
    public ObservableCollection<TestImageItem> ValImages { get; }
    public ObservableCollection<string> ModelSourceOptions { get; }

    public TestImageItem? SelectedTestImage
    {
        get => _selectedTestImage;
        set => SetField(ref _selectedTestImage, value);
    }

    public TestImageItem? SelectedTrainImage
    {
        get => _selectedTrainImage;
        set => SetField(ref _selectedTrainImage, value);
    }

    public TestImageItem? SelectedValImage
    {
        get => _selectedValImage;
        set => SetField(ref _selectedValImage, value);
    }

    public BitmapImage? DetectionPreview
    {
        get => _detectionPreview;
        private set => SetField(ref _detectionPreview, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public void SetStatusMessage(string message)
    {
        StatusText = message;
    }

    public string ModelPath
    {
        get => _modelPath;
        private set => SetField(ref _modelPath, value);
    }

    public string SelectedModelSource
    {
        get => _selectedModelSource;
        set
        {
            var selectedValue = string.IsNullOrWhiteSpace(value) ? ModelSourceLatestLocal : value;
            if (!SetField(ref _selectedModelSource, selectedValue))
            {
                return;
            }

            ModelPath = DetectModelPath();
            StatusText = $"[{_currentProjectName}] Sorgente modello Ultra: {_selectedModelSource}.";
        }
    }

    public string DetectionsSummary
    {
        get => _detectionsSummary;
        private set => SetField(ref _detectionsSummary, value);
    }

    public void SetCurrentProject(string projectName, string className)
    {
        _currentProjectName = projectName;
        _currentClassName = string.IsNullOrWhiteSpace(className) ? "cerca" : className.Trim();
        ModelPath = DetectModelPath();
        LoadAllImages();
        DetectionPreview = null;
        DetectionsSummary = "-";
        _lastDetectionImagePath = string.Empty;
        _lastDetectionSourceName = string.Empty;
        _lastDetections = Array.Empty<YoloDetection>();
        StatusText = $"[{_currentProjectName}/{_currentClassName}] Tab Ultra pronta.";
    }

    public string GetTestFolderPath()
    {
        var folder = _workspaceService.GetYoloTestPath(_currentProjectName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void LoadTestImages()
    {
        LoadItemsInto(TestImages, GetTestFolderPath());
        SelectedTestImage = TestImages.FirstOrDefault();
    }

    public void LoadTrainImages()
    {
        LoadItemsInto(TrainImages, GetTrainImagesFolderPath());
        SelectedTrainImage = TrainImages.FirstOrDefault();
    }

    public void LoadValImages()
    {
        LoadItemsInto(ValImages, GetValImagesFolderPath());
        SelectedValImage = ValImages.FirstOrDefault();
    }

    public void LoadAllImages()
    {
        LoadTestImages();
        LoadTrainImages();
        LoadValImages();
    }

    public string GetTrainImagesFolderPath()
    {
        var folder = Path.Combine(_workspaceService.GetYoloDatasetPath(_currentProjectName), "images", "train");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string GetValImagesFolderPath()
    {
        var folder = Path.Combine(_workspaceService.GetYoloDatasetPath(_currentProjectName), "images", "val");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void LoadItemsInto(ObservableCollection<TestImageItem> items, string folder)
    {
        items.Clear();
        foreach (var filePath in Directory
                     .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsSupportedImagePath)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            items.Add(new TestImageItem(filePath, Path.GetFileName(filePath)));
        }
    }

    public async Task<bool> CaptureTestImageAsync(string? deviceSerial)
    {
        if (!_adbService.Exists())
        {
            StatusText = "ADB non trovato.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            StatusText = "Nessun device ADB selezionato.";
            return false;
        }

        var outputDirectory = GetTestFolderPath();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var outputPath = Path.Combine(outputDirectory, $"adb_test_{timestamp}.png");
        StatusText = $"[{_currentProjectName}] Acquisizione test ADB in corso...";

        var pngBytes = await _adbService.CapturePngAsync(deviceSerial);
        await File.WriteAllBytesAsync(outputPath, pngBytes);
        await _projectImageBlobService.SaveImageAsync(_currentProjectName, outputPath, "test");
        LoadAllImages();
        SelectedTestImage = TestImages.FirstOrDefault(item => string.Equals(item.FilePath, outputPath, StringComparison.OrdinalIgnoreCase));
        SelectedTrainImage = null;
        SelectedValImage = null;
        StatusText = $"[{_currentProjectName}] Immagine test acquisita: {outputPath}";
        return true;
    }

    public async Task CaptureTestImageAndDetectAsync(string? deviceSerial)
    {
        var captured = await CaptureTestImageAsync(deviceSerial);
        if (!captured)
        {
            return;
        }

        await DetectTestImageAsync();
    }

    public async Task CaptureAdbPreviewAndDetectAsync(string? deviceSerial)
    {
        if (!_adbService.Exists())
        {
            StatusText = "ADB non trovato.";
            return;
        }

        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            StatusText = "Nessun device ADB selezionato.";
            return;
        }

        var outputDirectory = Path.Combine(Path.GetTempPath(), "WhatJolo", "UltraPreview", BuildSafeProjectName(_currentProjectName));
        Directory.CreateDirectory(outputDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var outputPath = Path.Combine(outputDirectory, $"adb_preview_{timestamp}.png");

        StatusText = $"[{_currentProjectName}] Acquisizione preview ADB in corso...";
        var pngBytes = await _adbService.CapturePngAsync(deviceSerial);
        await File.WriteAllBytesAsync(outputPath, pngBytes);

        SelectedTestImage = null;
        SelectedTrainImage = null;
        SelectedValImage = null;
        await DetectImageAsync(new TestImageItem(outputPath, Path.GetFileName(outputPath)), "preview adb");
    }

    public async Task TapBestPreviewDetectionAsync(string? deviceSerial)
    {
        if (!_adbService.Exists())
        {
            StatusText = "ADB non trovato.";
            return;
        }

        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            StatusText = "Nessun device ADB selezionato.";
            return;
        }

        if (!string.Equals(_lastDetectionSourceName, "preview adb", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"[{_currentProjectName}] Esegui prima 'Acquisisci ADB e riconosci' nella preview.";
            return;
        }

        var bestDetection = _lastDetections
            .Where(d => !string.IsNullOrWhiteSpace(d.Label))
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        if (bestDetection == null)
        {
            StatusText = $"[{_currentProjectName}] Nessun oggetto riconosciuto da toccare.";
            return;
        }

        var tapX = bestDetection.Bounds.Left + (bestDetection.Bounds.Width / 2);
        var tapY = bestDetection.Bounds.Top + (bestDetection.Bounds.Height / 2);
        StatusText = $"[{_currentProjectName}] Tap ADB in corso su {bestDetection.Label} ({bestDetection.Confidence:P0}) @ {tapX},{tapY}...";
        await _adbService.TapAsync(deviceSerial, tapX, tapY);
        StatusText = $"[{_currentProjectName}] Tap ADB eseguito su {bestDetection.Label} ({bestDetection.Confidence:P0}) @ {tapX},{tapY}.";
    }

    public async Task DetectSelectedImageAsync()
    {
        if (SelectedTestImage != null)
        {
            await DetectImageAsync(SelectedTestImage, "test");
            return;
        }

        if (SelectedTrainImage != null)
        {
            await DetectImageAsync(SelectedTrainImage, "train");
            return;
        }

        if (SelectedValImage != null)
        {
            await DetectImageAsync(SelectedValImage, "val");
            return;
        }

        StatusText = $"[{_currentProjectName}] Nessuna immagine selezionata.";
    }

    public async Task DetectTestImageAsync()
    {
        if (SelectedTestImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine test selezionata.";
            return;
        }

        SelectedTrainImage = null;
        SelectedValImage = null;
        await DetectImageAsync(SelectedTestImage, "test");
    }

    public async Task DetectTrainImageAsync()
    {
        if (SelectedTrainImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine train selezionata.";
            return;
        }

        SelectedTestImage = null;
        SelectedValImage = null;
        await DetectImageAsync(SelectedTrainImage, "train");
    }

    public async Task DetectValImageAsync()
    {
        if (SelectedValImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine val selezionata.";
            return;
        }

        SelectedTestImage = null;
        SelectedTrainImage = null;
        await DetectImageAsync(SelectedValImage, "val");
    }

    public async Task DeleteSelectedTestImageAsync()
    {
        if (SelectedTestImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine test selezionata.";
            return;
        }

        await DeleteImageAsync(SelectedTestImage, "test", deleteLabel: false);
    }

    public async Task DeleteSelectedTrainImageAsync()
    {
        if (SelectedTrainImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine train selezionata.";
            return;
        }

        await DeleteImageAsync(SelectedTrainImage, "train", deleteLabel: true);
    }

    public async Task DeleteSelectedValImageAsync()
    {
        if (SelectedValImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine val selezionata.";
            return;
        }

        await DeleteImageAsync(SelectedValImage, "val", deleteLabel: true);
    }

    public async Task<int> GetPromotableDetectionCountAsync(float threshold)
    {
        if (SelectedTestImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine test selezionata.";
            return 0;
        }

        if (!string.Equals(_lastDetectionSourceName, "test", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_lastDetectionImagePath, SelectedTestImage.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await DetectTestImageAsync();
        }

        return _lastDetections.Count(d => d.Confidence >= threshold && !string.IsNullOrWhiteSpace(d.Label));
    }

    public async Task<int> PromoteDetectedCropsAsync(float threshold)
    {
        if (SelectedTestImage == null)
        {
            StatusText = $"[{_currentProjectName}] Nessuna immagine test selezionata.";
            return 0;
        }

        if (!string.Equals(_lastDetectionSourceName, "test", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_lastDetectionImagePath, SelectedTestImage.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await DetectTestImageAsync();
        }

        var detections = _lastDetections
            .Where(d => d.Confidence >= threshold && !string.IsNullOrWhiteSpace(d.Label))
            .ToList();

        if (detections.Count == 0)
        {
            StatusText = $"[{_currentProjectName}] Nessuna detection sopra soglia da promuovere.";
            return 0;
        }

        var promotedSourceImagePath = MoveTestImageToCaptures(SelectedTestImage.FilePath);
        var savedCount = 0;
        using var sourceBitmap = new Bitmap(promotedSourceImagePath);
        foreach (var detection in detections)
        {
            var safeLabel = detection.Label.Trim().ToLowerInvariant();
            if (safeLabel.Length == 0)
            {
                continue;
            }

            var bounds = Rectangle.Intersect(detection.Bounds, new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            var outputDirectory = Path.Combine(_workspaceService.GetSavedCropsPath(_currentProjectName), safeLabel);
            Directory.CreateDirectory(outputDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var outputPath = Path.Combine(outputDirectory, $"{safeLabel}_auto_{timestamp}_{savedCount + 1}.png");

            using (var cropBitmap = sourceBitmap.Clone(bounds, sourceBitmap.PixelFormat))
            {
                cropBitmap.Save(outputPath, ImageFormat.Png);
            }

            await _projectImageBlobService.SaveImageAsync(_currentProjectName, outputPath, "crop");

            await _annotationCropDbService.SaveCropAsync(
                _currentProjectName,
                safeLabel,
                promotedSourceImagePath,
                outputPath,
                new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));

            savedCount++;
        }

        LoadAllImages();
        SelectedTestImage = TestImages.FirstOrDefault();
        StatusText = $"[{_currentProjectName}] Crop promosse da test al training: {savedCount} | soglia {threshold:P0} | sorgente spostata in Captures.";
        return savedCount;
    }

    private async Task DetectImageAsync(TestImageItem selectedImage, string sourceName)
    {
        string modelPath;
        try
        {
            modelPath = await ResolveDetectionModelPathAsync();
        }
        catch (Exception ex)
        {
            DetectionPreview = LoadBitmapImage(selectedImage.FilePath);
            DetectionsSummary = $"Modello ONNX dal DB non disponibile.{Environment.NewLine}{ex.Message}";
            StatusText = $"[{_currentProjectName}] Errore caricamento modello Ultra dal DB: {ex.Message}";
            return;
        }

        if (!File.Exists(modelPath) && !IsDatabaseModelSourceSelected())
        {
            var exportedModelPath = await TryExportMissingOnnxAsync();
            modelPath = !string.IsNullOrWhiteSpace(exportedModelPath) ? exportedModelPath : modelPath;
        }

        ModelPath = modelPath;
        if (!File.Exists(modelPath))
        {
            DetectionPreview = LoadBitmapImage(selectedImage.FilePath);
            DetectionsSummary = $"Modello ONNX non trovato.{Environment.NewLine}Path atteso: {modelPath}";
            StatusText = $"[{_currentProjectName}] Modello ONNX non trovato: {modelPath}";
            return;
        }

        StatusText = $"[{_currentProjectName}] Detection YOLO in corso su {selectedImage.FileName} ({sourceName}) | modello: {Path.GetFileName(modelPath)} | soglia: {DetectionThreshold:0.00}";

        var detectionResult = await Task.Run(() =>
        {
            using var bitmap = new Bitmap(selectedImage.FilePath);
            using var detector = new YoloIconDetector(modelPath);
            var debugResult = detector.DetectDebug(bitmap, DetectionThreshold);
            var groundTruthBoxes = LoadGroundTruthBoxes(selectedImage.FilePath, bitmap.Width, bitmap.Height);
            var validationMetrics = ComputeValidationMetrics(debugResult.Detections, groundTruthBoxes);
            using var annotated = DrawDetections(bitmap, debugResult.Detections, groundTruthBoxes);
            var lines = new List<string>
            {
                $"Progetto: {_currentProjectName}",
                $"Sorgente: {sourceName}",
                $"Immagine: {selectedImage.FileName}",
                $"Modello: {modelPath}",
                $"Soglia confidence: {DetectionThreshold:0.00}",
                $"Input modello: {debugResult.InputWidth}x{debugResult.InputHeight}",
                $"Output tensor: {string.Join("x", debugResult.OutputDimensions)}",
                $"Classi modello: {(debugResult.Labels.Count == 0 ? "(nessuna labels.txt trovata)" : string.Join(", ", debugResult.Labels))}",
                $"Ground truth boxes: {groundTruthBoxes.Count}",
                $"Detection grezze: {debugResult.RawDetectionCount}",
                $"Sopra soglia: {debugResult.AboveThresholdCount}",
                $"Dopo NMS: {debugResult.FinalDetectionCount}",
                $"Match GT: {validationMetrics.MatchedGroundTruth}/{groundTruthBoxes.Count}",
                $"Recall test immagine: {validationMetrics.Recall:P1}",
                $"IoU medio match: {validationMetrics.MeanIou:P1}",
                $"Confidence media match: {validationMetrics.MeanConfidence:P1}"
            };

            if (groundTruthBoxes.Count > 0)
            {
                lines.Add("Ground truth:");
                lines.AddRange(groundTruthBoxes.Select(b =>
                    $"{b.Label} [{b.Bounds.X},{b.Bounds.Y},{b.Bounds.Width},{b.Bounds.Height}]"));
            }

            if (debugResult.Detections.Count == 0)
            {
                lines.Add("Esito: nessun oggetto rilevato.");
            }
            else
            {
                lines.Add("Dettaglio detection:");
                lines.AddRange(debugResult.Detections.Select(d =>
                    $"{d.Label} | conf={d.Confidence:P1} | x={d.Bounds.X} y={d.Bounds.Y} w={d.Bounds.Width} h={d.Bounds.Height}"));
            }

            return new UltraDetectionResult(
                ToBitmapImage(annotated),
                lines.ToArray(),
                debugResult.FinalDetectionCount,
                debugResult.Detections);
        });

        DetectionPreview = detectionResult.Preview;
        DetectionsSummary = string.Join(Environment.NewLine, detectionResult.Lines);
        _lastDetectionImagePath = selectedImage.FilePath;
        _lastDetectionSourceName = sourceName;
        _lastDetections = detectionResult.Detections;
        StatusText = $"[{_currentProjectName}] Detection completata su {selectedImage.FileName} ({sourceName}): {detectionResult.FinalDetectionCount} oggetti.";
    }

    private async Task DeleteImageAsync(TestImageItem selectedImage, string sourceName, bool deleteLabel)
    {
        var imagePath = selectedImage.FilePath;
        var labelPath = deleteLabel ? ResolveLabelPath(imagePath) : null;
        var linkedSourceImagePath = ResolveLinkedSourceImagePath(imagePath, sourceName);
        var deletedSelectionCount = 0;

        if (!string.IsNullOrWhiteSpace(linkedSourceImagePath))
        {
            deletedSelectionCount = await _annotationCropDbService.DeleteCropsBySourceImageAsync(_currentProjectName, linkedSourceImagePath);
        }

        await Task.Run(() =>
        {
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }

            if (deleteLabel && !string.IsNullOrWhiteSpace(labelPath) && File.Exists(labelPath))
            {
                File.Delete(labelPath);
            }
        });

        await _projectImageBlobService.DeleteImageAsync(_currentProjectName, imagePath);
        if (deleteLabel && !string.IsNullOrWhiteSpace(labelPath))
        {
            await _projectImageBlobService.DeleteImageAsync(_currentProjectName, labelPath);
        }

        if (string.Equals(_lastDetectionImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
        {
            DetectionPreview = null;
            DetectionsSummary = "-";
            _lastDetectionImagePath = string.Empty;
            _lastDetectionSourceName = string.Empty;
            _lastDetections = Array.Empty<YoloDetection>();
        }

        LoadAllImages();

        switch (sourceName)
        {
            case "test":
                SelectedTestImage = TestImages.FirstOrDefault();
                SelectedTrainImage = null;
                SelectedValImage = null;
                break;
            case "train":
                SelectedTrainImage = TrainImages.FirstOrDefault();
                SelectedTestImage = null;
                SelectedValImage = null;
                break;
            case "val":
                SelectedValImage = ValImages.FirstOrDefault();
                SelectedTestImage = null;
                SelectedTrainImage = null;
                break;
        }

        StatusText = deleteLabel
            ? $"[{_currentProjectName}] File {sourceName} eliminato con label: {Path.GetFileName(imagePath)} | selezioni rimosse: {deletedSelectionCount}"
            : $"[{_currentProjectName}] File {sourceName} eliminato: {Path.GetFileName(imagePath)} | selezioni rimosse: {deletedSelectionCount}";
    }

    private string DetectModelPath()
    {
        if (IsDatabaseModelSourceSelected())
        {
            return $"DB: best.onnx salvato nel database remoto per classe '{_currentClassName}' (scompattato in temp alla detection).";
        }

        var latestOnnxPath = _workspaceService.FindLatestYoloOnnxPath(_currentProjectName);
        if (!string.IsNullOrWhiteSpace(latestOnnxPath))
        {
            return latestOnnxPath;
        }

        var latestRunPath = _workspaceService.FindLatestYoloRunPath(_currentProjectName);
        if (!string.IsNullOrWhiteSpace(latestRunPath))
        {
            return Path.Combine(latestRunPath, "weights", "best.onnx");
        }

        return Path.Combine(_workspaceService.GetYoloRunsPath(_currentProjectName), BuildSafeProjectName(_currentProjectName), "weights", "best.onnx");
    }

    private async Task<string> ResolveDetectionModelPathAsync()
    {
        if (!IsDatabaseModelSourceSelected())
        {
            return DetectModelPath();
        }

        var restoreResult = await _projectModelBlobService.RestoreLatestBestOnnxToTempAsync(_currentProjectName, _currentClassName);
        ModelPath =
            $"DB temp: {restoreResult.ModelPath} | Classe: {restoreResult.ClassName} | Run: {restoreResult.RunName} | " +
            $"{restoreResult.ByteLength:N0} byte -> {restoreResult.CompressedLength:N0} byte compressi";
        return restoreResult.ModelPath;
    }

    private bool IsDatabaseModelSourceSelected()
    {
        return string.Equals(_selectedModelSource, ModelSourceDatabase, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> TryExportMissingOnnxAsync()
    {
        var latestRunPath = _workspaceService.FindLatestYoloRunPath(_currentProjectName);
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            return null;
        }

        var bestPtPath = Path.Combine(latestRunPath, "weights", "best.pt");
        if (!File.Exists(bestPtPath))
        {
            return null;
        }

        StatusText = $"[{_currentProjectName}] best.onnx mancante, provo export da best.pt...";
        var datasetYamlPath = Path.Combine(_workspaceService.GetYoloDatasetPath(_currentProjectName), "data.yaml");
        var exportedPath = await _yoloTrainingService.ExportOnnxAsync(bestPtPath, _workspaceService.GetYoloProjectPath(_currentProjectName), datasetYamlPath);
        if (!string.IsNullOrWhiteSpace(exportedPath) && File.Exists(exportedPath))
        {
            StatusText = $"[{_currentProjectName}] Export ONNX automatico completato: {exportedPath}";
            return exportedPath;
        }

        StatusText = $"[{_currentProjectName}] Export ONNX automatico fallito.";
        return null;
    }

    private List<GroundTruthBox> LoadGroundTruthBoxes(string imagePath, int imageWidth, int imageHeight)
    {
        var labelPath = ResolveLabelPath(imagePath);
        if (string.IsNullOrWhiteSpace(labelPath) || !File.Exists(labelPath))
        {
            return new List<GroundTruthBox>();
        }

        var classesPath = Path.Combine(_workspaceService.GetYoloDatasetPath(_currentProjectName), "classes.txt");
        var classes = File.Exists(classesPath)
            ? File.ReadAllLines(classesPath)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0)
                .ToArray()
            : Array.Empty<string>();

        var boxes = new List<GroundTruthBox>();
        foreach (var line in File.ReadAllLines(labelPath))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 5 ||
                !int.TryParse(parts[0], out var classIndex) ||
                !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cxNorm) ||
                !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cyNorm) ||
                !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wNorm) ||
                !float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hNorm))
            {
                continue;
            }

            var width = Math.Max(1, (int)Math.Round(wNorm * imageWidth));
            var height = Math.Max(1, (int)Math.Round(hNorm * imageHeight));
            var centerX = cxNorm * imageWidth;
            var centerY = cyNorm * imageHeight;
            var left = Math.Clamp((int)Math.Round(centerX - (width / 2f)), 0, Math.Max(0, imageWidth - 1));
            var top = Math.Clamp((int)Math.Round(centerY - (height / 2f)), 0, Math.Max(0, imageHeight - 1));
            var right = Math.Clamp(left + width, left + 1, imageWidth);
            var bottom = Math.Clamp(top + height, top + 1, imageHeight);
            var label = classIndex >= 0 && classIndex < classes.Length ? classes[classIndex] : $"class_{classIndex}";

            boxes.Add(new GroundTruthBox(label, Rectangle.FromLTRB(left, top, right, bottom)));
        }

        return boxes;
    }

    private static DetectionValidationMetrics ComputeValidationMetrics(
        IReadOnlyList<YoloDetection> detections,
        IReadOnlyList<GroundTruthBox> groundTruthBoxes)
    {
        if (groundTruthBoxes.Count == 0)
        {
            return new DetectionValidationMetrics(0, 0, 0, 0);
        }

        var usedDetectionIndexes = new HashSet<int>();
        var matchedCount = 0;
        var iouSum = 0d;
        var confidenceSum = 0d;

        foreach (var groundTruth in groundTruthBoxes)
        {
            var bestDetectionIndex = -1;
            var bestIou = 0d;

            for (var index = 0; index < detections.Count; index++)
            {
                if (usedDetectionIndexes.Contains(index))
                {
                    continue;
                }

                var detection = detections[index];
                if (!string.Equals(detection.Label, groundTruth.Label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var iou = ComputeIou(detection.Bounds, groundTruth.Bounds);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestDetectionIndex = index;
                }
            }

            if (bestDetectionIndex < 0 || bestIou < 0.5d)
            {
                continue;
            }

            usedDetectionIndexes.Add(bestDetectionIndex);
            matchedCount++;
            iouSum += bestIou;
            confidenceSum += detections[bestDetectionIndex].Confidence;
        }

        return new DetectionValidationMetrics(
            matchedCount,
            matchedCount / (double)groundTruthBoxes.Count,
            matchedCount == 0 ? 0 : iouSum / matchedCount,
            matchedCount == 0 ? 0 : confidenceSum / matchedCount);
    }

    private static double ComputeIou(Rectangle a, Rectangle b)
    {
        var intersection = Rectangle.Intersect(a, b);
        if (intersection.IsEmpty)
        {
            return 0d;
        }

        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;
        return unionArea <= 0 ? 0d : intersectionArea / (double)unionArea;
    }

    private string? ResolveLabelPath(string imagePath)
    {
        var trainFolder = GetTrainImagesFolderPath();
        var valFolder = GetValImagesFolderPath();
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(imagePath);

        if (imagePath.StartsWith(trainFolder, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(_workspaceService.GetYoloDatasetPath(_currentProjectName), "labels", "train", fileNameWithoutExtension + ".txt");
        }

        if (imagePath.StartsWith(valFolder, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(_workspaceService.GetYoloDatasetPath(_currentProjectName), "labels", "val", fileNameWithoutExtension + ".txt");
        }

        return null;
    }

    private static Bitmap DrawDetections(Bitmap source, IReadOnlyList<YoloDetection> detections, IReadOnlyList<GroundTruthBox> groundTruthBoxes)
    {
        var copy = new Bitmap(source);
        using var graphics = Graphics.FromImage(copy);
        using var predictionPen = new Pen(Color.Gold, 3f);
        using var predictionBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        using var predictionTextBrush = new SolidBrush(Color.Gold);
        using var groundTruthPen = new Pen(Color.Lime, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var groundTruthBrush = new SolidBrush(Color.FromArgb(180, 0, 32, 0));
        using var groundTruthTextBrush = new SolidBrush(Color.Lime);
        using var font = new System.Drawing.Font("Segoe UI", 12, System.Drawing.FontStyle.Bold);

        foreach (var groundTruth in groundTruthBoxes)
        {
            graphics.DrawRectangle(groundTruthPen, groundTruth.Bounds);
            var labelText = $"GT {groundTruth.Label}";
            var textSize = graphics.MeasureString(labelText, font);
            var textRect = new RectangleF(
                groundTruth.Bounds.Left,
                Math.Max(0, groundTruth.Bounds.Top - textSize.Height - 4),
                textSize.Width + 10,
                textSize.Height + 4);
            graphics.FillRectangle(groundTruthBrush, textRect);
            graphics.DrawString(labelText, font, groundTruthTextBrush, textRect.Left + 4, textRect.Top + 2);
        }

        foreach (var detection in detections)
        {
            graphics.DrawRectangle(predictionPen, detection.Bounds);
            var labelText = $"PRED {detection.Label} {detection.Confidence:P0}";
            var textSize = graphics.MeasureString(labelText, font);
            var textRect = new RectangleF(
                detection.Bounds.Left,
                Math.Max(0, detection.Bounds.Top - textSize.Height - 4),
                textSize.Width + 10,
                textSize.Height + 4);
            graphics.FillRectangle(predictionBrush, textRect);
            graphics.DrawString(labelText, font, predictionTextBrush, textRect.Left + 4, textRect.Top + 2);
        }

        return copy;
    }

    private static BitmapImage LoadBitmapImage(string path)
    {
        var image = new BitmapImage();
        using var stream = File.OpenRead(path);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapImage ToBitmapImage(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool IsSupportedImagePath(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSafeProjectName(string projectName)
    {
        var safeName = System.Text.RegularExpressions.Regex.Replace(projectName, "[^a-zA-Z0-9_-]+", "_").Trim('_');
        return safeName.Length == 0 ? "default" : safeName;
    }

    private string MoveTestImageToCaptures(string testImagePath)
    {
        var capturesFolder = _workspaceService.GetCapturesPath(_currentProjectName);
        Directory.CreateDirectory(capturesFolder);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var destinationPath = Path.Combine(capturesFolder, $"adb_capture_{timestamp}.png");
        var counter = 1;
        while (File.Exists(destinationPath))
        {
            destinationPath = Path.Combine(capturesFolder, $"adb_capture_{timestamp}_{counter}.png");
            counter++;
        }

        File.Move(testImagePath, destinationPath);
        _projectImageBlobService.DeleteImageAsync(_currentProjectName, testImagePath).GetAwaiter().GetResult();
        _projectImageBlobService.SaveImageAsync(_currentProjectName, destinationPath, "capture").GetAwaiter().GetResult();
        return destinationPath;
    }

    private string? ResolveLinkedSourceImagePath(string imagePath, string sourceName)
    {
        if (string.Equals(sourceName, "test", StringComparison.OrdinalIgnoreCase))
        {
            return imagePath;
        }

        var datasetFolder = _workspaceService.GetYoloDatasetPath(_currentProjectName);
        var imageMapPath = Path.Combine(datasetFolder, "image_map.tsv");
        if (!File.Exists(imageMapPath))
        {
            return null;
        }

        var normalizedImagePath = Path.GetFullPath(imagePath);
        foreach (var line in File.ReadLines(imageMapPath).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            if (string.Equals(Path.GetFullPath(parts[1]), normalizedImagePath, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(parts[2]);
            }
        }

        return null;
    }
}

public sealed record TestImageItem(string FilePath, string FileName);

internal sealed record UltraDetectionResult(BitmapImage Preview, string[] Lines, int FinalDetectionCount, IReadOnlyList<YoloDetection> Detections);

internal sealed record GroundTruthBox(string Label, Rectangle Bounds);

internal sealed record DetectionValidationMetrics(int MatchedGroundTruth, double Recall, double MeanIou, double MeanConfidence);
