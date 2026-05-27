using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace WhatJolo;

public sealed class AdbCaptureTabViewModel : ViewModelBase
{
    private readonly AdbService _adbService;
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly CropVariationService _cropVariationService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly ProjectWorkspaceService _workspaceService;
    private string _adbStatusText;
    private string _lastCapturePath;
    private BitmapImage? _latestScreenshotPreview;
    private string? _selectedDeviceSerial;
    private SavedCropItem? _selectedSavedCrop;
    private SavedCropItem? _selectedVariationCrop;
    private string _currentProjectName;

    public AdbCaptureTabViewModel()
    {
        _adbService = new AdbService();
        _annotationCropDbService = new AnnotationCropDbService();
        _cropVariationService = new CropVariationService();
        _projectImageBlobService = new ProjectImageBlobService();
        _workspaceService = new ProjectWorkspaceService();
        _adbStatusText = "Pronto. Formato acquisizione: PNG lossless.";
        _lastCapturePath = "Nessuna cattura eseguita.";
        _currentProjectName = _workspaceService.EnsureProject("Default");
        ConnectedDevices = new ObservableCollection<string>();
        SavedCrops = new ObservableCollection<SavedCropItem>();
        VariationCrops = new ObservableCollection<SavedCropItem>();
        StatusLog = new ObservableCollection<string>();
        AdbExecutablePath = _adbService.AdbExecutablePath;
        AppendLog(_adbStatusText);
    }

    public string AdbExecutablePath { get; }

    public ObservableCollection<string> ConnectedDevices { get; }
    public ObservableCollection<SavedCropItem> SavedCrops { get; }
    public ObservableCollection<SavedCropItem> VariationCrops { get; }
    public ObservableCollection<string> StatusLog { get; }
    public int SavedCropCount => SavedCrops.Count;
    public int VariationCropCount => VariationCrops.Count;
    public string StatusLogText => string.Join(Environment.NewLine, StatusLog);

    public string AdbStatusText
    {
        get => _adbStatusText;
        private set
        {
            if (SetField(ref _adbStatusText, value))
            {
                AppendLog(value);
            }
        }
    }

    public string LastCapturePath
    {
        get => _lastCapturePath;
        private set => SetField(ref _lastCapturePath, value);
    }

    public BitmapImage? LatestScreenshotPreview
    {
        get => _latestScreenshotPreview;
        private set
        {
            if (SetField(ref _latestScreenshotPreview, value))
            {
                OnPropertyChanged(nameof(PreviewPlaceholderVisibility));
            }
        }
    }

    public string? SelectedDeviceSerial
    {
        get => _selectedDeviceSerial;
        set => SetField(ref _selectedDeviceSerial, value);
    }

    public SavedCropItem? SelectedSavedCrop
    {
        get => _selectedSavedCrop;
        set => SetField(ref _selectedSavedCrop, value);
    }

    public SavedCropItem? SelectedVariationCrop
    {
        get => _selectedVariationCrop;
        set => SetField(ref _selectedVariationCrop, value);
    }

    public string CurrentProjectName
    {
        get => _currentProjectName;
        private set => SetField(ref _currentProjectName, value);
    }

    public Visibility PreviewPlaceholderVisibility =>
        LatestScreenshotPreview == null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public async Task InitializeAdbAsync()
    {
        if (!_adbService.Exists())
        {
            AdbStatusText = "ADB non trovato.";
            return;
        }

        AdbStatusText = "Connessione automatica ad ADB in corso...";

        try
        {
            await _adbService.StartServerAsync();
            await RefreshConnectedDevicesAsync();
        }
        catch (Exception ex)
        {
            AdbStatusText = "Errore inizializzazione ADB: " + ex.Message;
        }
    }

    public async Task RefreshConnectedDevicesAsync()
    {
        ConnectedDevices.Clear();

        if (!_adbService.Exists())
        {
            AdbStatusText = "ADB non trovato.";
            return;
        }

        try
        {
            var devices = await _adbService.GetConnectedDevicesAsync();
            foreach (var device in devices)
            {
                ConnectedDevices.Add(device);
            }

            if (devices.Count == 0)
            {
                SelectedDeviceSerial = null;
                AdbStatusText = "Nessun device ADB collegato.";
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedDeviceSerial) || !devices.Contains(SelectedDeviceSerial))
            {
                SelectedDeviceSerial = devices[0];
            }

            AdbStatusText = $"Device pronti: {devices.Count}. Selezionato: {SelectedDeviceSerial}.";
        }
        catch (Exception ex)
        {
            AdbStatusText = "Errore lettura device ADB: " + ex.Message;
        }
    }

    public async Task CaptureAdbScreenshotAsync()
    {
        if (!_adbService.Exists())
        {
            AdbStatusText = "ADB non trovato.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedDeviceSerial))
        {
            await RefreshConnectedDevicesAsync();
        }

        if (string.IsNullOrWhiteSpace(SelectedDeviceSerial))
        {
            AdbStatusText = "Nessun device ADB disponibile.";
            return;
        }

        var outputDirectory = GetCapturesFolderPath();
        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var outputPath = Path.Combine(outputDirectory, $"adb_capture_{timestamp}.png");

        AdbStatusText = $"[{CurrentProjectName}] Acquisizione ADB in corso su {SelectedDeviceSerial} (PNG lossless)...";

        try
        {
            var pngBytes = await _adbService.CapturePngAsync(SelectedDeviceSerial);
            await File.WriteAllBytesAsync(outputPath, pngBytes);
            await _projectImageBlobService.SaveImageAsync(CurrentProjectName, outputPath, "capture");
            await AlignCurrentProjectAsync("capture");
            LastCapturePath = outputPath;
            LatestScreenshotPreview = CreateBitmapImage(pngBytes);
            AdbStatusText = $"[{CurrentProjectName}] Acquisizione completata su {SelectedDeviceSerial}: {pngBytes.Length:N0} byte PNG lossless.";
        }
        catch (Exception ex)
        {
            AdbStatusText = "Errore acquisizione ADB: " + ex.Message;
        }
    }

    public string GetCapturesFolderPath()
    {
        return _workspaceService.GetCapturesPath(CurrentProjectName);
    }

    public void LoadSavedCrops(string cropClass)
    {
        SavedCrops.Clear();
        VariationCrops.Clear();

        var safeClass = string.IsNullOrWhiteSpace(cropClass) ? "crop" : cropClass.Trim().ToLowerInvariant();
        var normalRecords = _annotationCropDbService
            .GetProjectCropsByLabelAsync(CurrentProjectName, safeClass, isVariation: false)
            .GetAwaiter()
            .GetResult();
        foreach (var record in normalRecords.Where(record => File.Exists(record.CropImagePath)))
        {
            SavedCrops.Add(CreateSavedCropItem(record));
        }

        var variationRecords = _annotationCropDbService
            .GetProjectCropsByLabelAsync(CurrentProjectName, safeClass, isVariation: true)
            .GetAwaiter()
            .GetResult();
        foreach (var record in variationRecords.Where(record => File.Exists(record.CropImagePath)))
        {
            VariationCrops.Add(CreateSavedCropItem(record));
        }

        SelectedSavedCrop = SavedCrops.FirstOrDefault();
        SelectedVariationCrop = VariationCrops.FirstOrDefault();
        OnPropertyChanged(nameof(SavedCropCount));
        OnPropertyChanged(nameof(VariationCropCount));
    }

    public async Task<SavedCropItem?> SaveSelectionAsync(Int32Rect selectionRect, string cropClass)
    {
        if (LatestScreenshotPreview == null)
        {
            AdbStatusText = "Nessuna immagine disponibile per il salvataggio.";
            return null;
        }

        if (selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            AdbStatusText = "Selezione non valida.";
            return null;
        }

        var safeClass = string.IsNullOrWhiteSpace(cropClass) ? "crop" : cropClass.Trim().ToLowerInvariant();
        var outputDirectory = Path.Combine(_workspaceService.GetSavedCropsPath(CurrentProjectName), safeClass);
        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var outputPath = Path.Combine(outputDirectory, $"{safeClass}_{timestamp}.png");

        var crop = new CroppedBitmap(LatestScreenshotPreview, selectionRect);
        await SaveBitmapSourceAsPngAsync(crop, outputPath);
        await _projectImageBlobService.SaveImageAsync(CurrentProjectName, outputPath, "crop");
        var sourceImagePath = LastCapturePath;
        if (!string.IsNullOrWhiteSpace(sourceImagePath) && !sourceImagePath.Equals("Nessuna cattura eseguita.", StringComparison.OrdinalIgnoreCase))
        {
            await _annotationCropDbService.SaveCropAsync(CurrentProjectName, safeClass, sourceImagePath, outputPath, selectionRect);
        }
        await AlignCurrentProjectAsync("crop");

        var savedItem = new SavedCropItem(
            outputPath,
            Path.GetFileName(outputPath),
            safeClass,
            CreateBitmapImage(File.ReadAllBytes(outputPath)),
            IsVariation: false);

        LoadSavedCrops(safeClass);
        SelectedSavedCrop = SavedCrops.FirstOrDefault(item => string.Equals(item.FilePath, outputPath, StringComparison.OrdinalIgnoreCase)) ?? savedItem;
        SelectedVariationCrop = VariationCrops.FirstOrDefault();
        AdbStatusText = $"[{CurrentProjectName}] Crop salvato: {outputPath} | Classe YOLO: {safeClass} | Totale classe {safeClass}: {SavedCropCount}";
        return SelectedSavedCrop;
    }

    public async Task<bool> DeleteSelectedCropAsync(string cropClass)
    {
        var item = SelectedSavedCrop;
        if (item == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna crop selezionata da eliminare.";
            return false;
        }

        var safeClass = string.IsNullOrWhiteSpace(cropClass) ? "crop" : cropClass.Trim().ToLowerInvariant();
        await _annotationCropDbService.DeleteCropAsync(CurrentProjectName, safeClass, item.FilePath);

        if (File.Exists(item.FilePath))
        {
            File.Delete(item.FilePath);
        }

        await _projectImageBlobService.DeleteImageAsync(CurrentProjectName, item.FilePath);
        await AlignCurrentProjectAsync("delete-crop");

        LoadSavedCrops(safeClass);
        AdbStatusText = $"[{CurrentProjectName}] Crop eliminata: {item.FileName}";
        return true;
    }

    public async Task<bool> DeleteSelectedVariationCropAsync(string cropClass)
    {
        var item = SelectedVariationCrop;
        if (item == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna variazione selezionata da eliminare.";
            return false;
        }

        var safeClass = string.IsNullOrWhiteSpace(cropClass) ? "crop" : cropClass.Trim().ToLowerInvariant();
        await _annotationCropDbService.DeleteCropAsync(CurrentProjectName, safeClass, item.FilePath);

        if (File.Exists(item.FilePath))
        {
            File.Delete(item.FilePath);
        }

        await _projectImageBlobService.DeleteImageAsync(CurrentProjectName, item.FilePath);
        await AlignCurrentProjectAsync("delete-variation");

        LoadSavedCrops(safeClass);
        AdbStatusText = $"[{CurrentProjectName}] Variazione eliminata: {item.FileName}";
        return true;
    }

    public async Task<int> GenerateVariationsAsync(string cropClass, int count = 10)
    {
        var selectedItem = SelectedSavedCrop ?? SelectedVariationCrop;
        if (selectedItem == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna crop selezionata per generare variazioni.";
            return 0;
        }

        var sourceRecord = await _annotationCropDbService.GetProjectCropByImagePathAsync(CurrentProjectName, selectedItem.FilePath);
        if (sourceRecord == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Record DB della crop selezionata non trovato.";
            return 0;
        }

        var createdCropPaths = await _cropVariationService.GenerateVariationsAsync(sourceRecord, count);
        await AlignCurrentProjectAsync("variation");
        LoadSavedCrops(string.IsNullOrWhiteSpace(cropClass) ? sourceRecord.LabelName : cropClass);
        SelectedVariationCrop = createdCropPaths.Count > 0
            ? VariationCrops.FirstOrDefault(item => string.Equals(item.FilePath, createdCropPaths[0], StringComparison.OrdinalIgnoreCase))
            : VariationCrops.FirstOrDefault();
        AdbStatusText = $"[{CurrentProjectName}] Variazioni generate: {createdCropPaths.Count} | Classe: {sourceRecord.LabelName} | Seed casuale nuovo.";
        return createdCropPaths.Count;
    }

    public BitmapSource? CreateSelectionPreview(Int32Rect selectionRect)
    {
        if (LatestScreenshotPreview == null || selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            return null;
        }

        return new CroppedBitmap(LatestScreenshotPreview, selectionRect);
    }

    public void SetStatusMessage(string message)
    {
        AdbStatusText = message;
    }

    public void SetCurrentProject(string projectName, string cropClass)
    {
        CurrentProjectName = _workspaceService.EnsureProject(projectName);
        LastCapturePath = "Nessuna cattura eseguita.";
        LoadSavedCrops(cropClass);
        StatusLog.Clear();
        AdbStatusText = $"Progetto corrente: {CurrentProjectName}";
    }

    private async Task AlignCurrentProjectAsync(string reason)
    {
        var alignedImages = await _projectImageBlobService.SyncProjectAsync(CurrentProjectName);
        AppendLog($"[{CurrentProjectName}] DB allineato dopo {reason}: {alignedImages} immagini indicizzate.");
    }

    private static BitmapImage CreateBitmapImage(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task SaveBitmapSourceAsPngAsync(BitmapSource bitmap, string outputPath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static SavedCropItem CreateSavedCropItem(ProjectCropRecord record)
    {
        return new SavedCropItem(
            record.CropImagePath,
            Path.GetFileName(record.CropImagePath),
            record.LabelName,
            CreateBitmapImage(File.ReadAllBytes(record.CropImagePath)),
            record.IsVariation);
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        StatusLog.Insert(0, line);
        while (StatusLog.Count > 200)
        {
            StatusLog.RemoveAt(StatusLog.Count - 1);
        }

        OnPropertyChanged(nameof(StatusLogText));
    }
}

public sealed record SavedCropItem(string FilePath, string FileName, string LabelName, BitmapImage PreviewImage, bool IsVariation);
