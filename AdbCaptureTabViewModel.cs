using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WhatJolo;

public sealed class AdbCaptureTabViewModel : ViewModelBase
{
    private const string DebugScope = "ADB/Selection";
    private readonly AdbService _adbService;
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly CropVariationService _cropVariationService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly ProjectWorkspaceService _workspaceService;
    private string _adbStatusText;
    private string _lastCapturePath;
    private string? _lastCaptureImageKey;
    private BitmapImage? _latestScreenshotPreview;
    private BitmapImage? _selectedObjectPreview;
    private string? _selectedDeviceSerial;
    private CapturedImageItem? _selectedCapturedImage;
    private SavedCropItem? _selectedSavedCrop;
    private SavedCropItem? _selectedVariationCrop;
    private string _currentProjectName;
    private string _currentCropClass = "crop";

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
        CapturedImages = new ObservableCollection<CapturedImageItem>();
        SavedCrops = new ObservableCollection<SavedCropItem>();
        VariationCrops = new ObservableCollection<SavedCropItem>();
        StatusLog = new ObservableCollection<string>();
        AdbExecutablePath = _adbService.AdbExecutablePath;
        AppendLog(_adbStatusText);
    }

    public string AdbExecutablePath { get; }

    public ObservableCollection<string> ConnectedDevices { get; }
    public ObservableCollection<CapturedImageItem> CapturedImages { get; }
    public ObservableCollection<SavedCropItem> SavedCrops { get; }
    public ObservableCollection<SavedCropItem> VariationCrops { get; }
    public ObservableCollection<string> StatusLog { get; }
    public int CapturedImageCount => CapturedImages.Count;
    public int SavedCropCount => SavedCrops.Count;
    public int VariationCropCount => VariationCrops.Count;
    public string StatusLogText => string.Join(Environment.NewLine, StatusLog);
    public bool IsLoadingCapturedImages { get; private set; }
    public bool IsLoadingSavedCrops { get; private set; }
    public bool IsLoadingVariationCrops { get; private set; }

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

    public BitmapImage? SelectedObjectPreview
    {
        get => _selectedObjectPreview;
        private set
        {
            if (SetField(ref _selectedObjectPreview, value))
            {
                OnPropertyChanged(nameof(SelectedObjectPreviewPlaceholderVisibility));
            }
        }
    }

    public string? SelectedDeviceSerial
    {
        get => _selectedDeviceSerial;
        set => SetField(ref _selectedDeviceSerial, value);
    }

    public CapturedImageItem? SelectedCapturedImage
    {
        get => _selectedCapturedImage;
        set
        {
            if (SetField(ref _selectedCapturedImage, value))
            {
                AppDebugLog.Debug(DebugScope,
                    $"SelectedCapturedImage => {DescribeCapture(value)} | loadingCaptured={IsLoadingCapturedImages}");
                OnPropertyChanged(nameof(CanMoveToPreviousCapture));
                OnPropertyChanged(nameof(CanMoveToNextCapture));
            }
        }
    }

    public SavedCropItem? SelectedSavedCrop
    {
        get => _selectedSavedCrop;
        set
        {
            if (SetField(ref _selectedSavedCrop, value))
            {
                AppDebugLog.Debug(DebugScope,
                    $"SelectedSavedCrop => {DescribeCrop(value)} | loadingSaved={IsLoadingSavedCrops}");
            }
        }
    }

    public SavedCropItem? SelectedVariationCrop
    {
        get => _selectedVariationCrop;
        set
        {
            if (SetField(ref _selectedVariationCrop, value))
            {
                AppDebugLog.Debug(DebugScope,
                    $"SelectedVariationCrop => {DescribeCrop(value)} | loadingVariation={IsLoadingVariationCrops}");
            }
        }
    }

    public string CurrentProjectName
    {
        get => _currentProjectName;
        private set => SetField(ref _currentProjectName, value);
    }

    public Visibility PreviewPlaceholderVisibility =>
        LatestScreenshotPreview == null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SelectedObjectPreviewPlaceholderVisibility =>
        SelectedObjectPreview == null ? Visibility.Visible : Visibility.Collapsed;

    public bool CanMoveToPreviousCapture => GetSelectedCaptureIndex() > 0;

    public bool CanMoveToNextCapture
    {
        get
        {
            var selectedIndex = GetSelectedCaptureIndex();
            return selectedIndex >= 0 && selectedIndex < CapturedImages.Count - 1;
        }
    }

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

    public async Task CaptureAdbScreenshotAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
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

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"adb_capture_{timestamp}.png";

        AdbStatusText = $"[{CurrentProjectName}] Acquisizione ADB in corso su {SelectedDeviceSerial} (PNG lossless)...";

        try
        {
            var pngBytes = await _adbService.CapturePngAsync(SelectedDeviceSerial);
            await _projectImageBlobService.SaveImageBytesAsync(CurrentProjectName, fileName, "capture", pngBytes);
            _lastCaptureImageKey = $"capture|{fileName}";
            LastCapturePath = fileName;
            LatestScreenshotPreview = CreateBitmapImage(pngBytes);
            SelectedObjectPreview = LatestScreenshotPreview;
            await LoadCapturedImagesAsync();
            SelectedCapturedImage = CapturedImages.FirstOrDefault(item => string.Equals(item.ImageKey, _lastCaptureImageKey, StringComparison.OrdinalIgnoreCase));
            await LoadSelectedCaptureAsync(cropClass);
            AdbStatusText = $"[{CurrentProjectName}] Acquisizione completata su {SelectedDeviceSerial}: {pngBytes.Length:N0} byte PNG lossless.";
        }
        catch (Exception ex)
        {
            AdbStatusText = "Errore acquisizione ADB: " + ex.Message;
        }
    }

    public async Task ImportCapturePngAsync(byte[] pngBytes, string sourceLabel, string filePrefix, string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        if (pngBytes.Length == 0)
        {
            AdbStatusText = $"[{CurrentProjectName}] {sourceLabel}: frame vuoto.";
            return;
        }

        var safePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "capture" : filePrefix.Trim().ToLowerInvariant();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{safePrefix}_{timestamp}.png";

        await _projectImageBlobService.SaveImageBytesAsync(CurrentProjectName, fileName, "capture", pngBytes);
        _lastCaptureImageKey = $"capture|{fileName}";
        LastCapturePath = fileName;
        LatestScreenshotPreview = CreateBitmapImage(pngBytes);
        SelectedObjectPreview = LatestScreenshotPreview;
        await LoadCapturedImagesAsync();
        SelectedCapturedImage = CapturedImages.FirstOrDefault(item => string.Equals(item.ImageKey, _lastCaptureImageKey, StringComparison.OrdinalIgnoreCase));
        await LoadSelectedCaptureAsync(cropClass);
        AdbStatusText = $"[{CurrentProjectName}] {sourceLabel}: acquisizione completata ({pngBytes.Length:N0} byte PNG).";
    }

    public async Task ImportCaptureFileAsync(byte[] imageBytes, string sourceLabel, string fileName, string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        if (imageBytes.Length == 0)
        {
            AdbStatusText = $"[{CurrentProjectName}] {sourceLabel}: file vuoto.";
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var safeFileName = BuildImportedCaptureFileName(fileName, timestamp);

        await _projectImageBlobService.SaveImageBytesAsync(CurrentProjectName, safeFileName, "capture", imageBytes);
        _lastCaptureImageKey = $"capture|{safeFileName}";
        LastCapturePath = safeFileName;
        LatestScreenshotPreview = CreateBitmapImage(imageBytes);
        SelectedObjectPreview = LatestScreenshotPreview;
        await LoadCapturedImagesAsync();
        SelectedCapturedImage = CapturedImages.FirstOrDefault(item => string.Equals(item.ImageKey, _lastCaptureImageKey, StringComparison.OrdinalIgnoreCase));
        await LoadSelectedCaptureAsync(cropClass);
        AdbStatusText = $"[{CurrentProjectName}] {sourceLabel}: immagine importata nel DB ({imageBytes.Length:N0} byte).";
    }

    public string GetCapturesFolderPath()
    {
        return _workspaceService.GetCapturesPath(CurrentProjectName);
    }

    public async Task LoadCapturedImagesAsync()
    {
        AppDebugLog.Info(DebugScope,
            $"LoadCapturedImagesAsync START | project={CurrentProjectName} | class={_currentCropClass} | lastKey={_lastCaptureImageKey ?? "<null>"}");
        IsLoadingCapturedImages = true;
        try
        {
            CapturedImages.Clear();

            if (!SharedDatabase.IsDatabaseConnected())
            {
                AppDebugLog.Warn(DebugScope, "LoadCapturedImagesAsync skipped: database not connected.");
                SelectedCapturedImage = null;
                OnPropertyChanged(nameof(CapturedImageCount));
                OnPropertyChanged(nameof(CanMoveToPreviousCapture));
                OnPropertyChanged(nameof(CanMoveToNextCapture));
                return;
            }

            var storedCaptures = await _projectImageBlobService.GetProjectImagesByKindsAsync(CurrentProjectName, "capture");
            var labeledSourceKeys = await _annotationCropDbService.GetSourceImageKeysForLabelAsync(CurrentProjectName, _currentCropClass);
            var annotatedSourceKeys = await _annotationCropDbService.GetAnnotatedSourceImageKeysAsync(CurrentProjectName);
            AppDebugLog.Debug(DebugScope,
                $"LoadCapturedImagesAsync fetched | captures={storedCaptures.Count} | labeled={labeledSourceKeys.Count} | annotated={annotatedSourceKeys.Count}");

            foreach (var storedCapture in storedCaptures)
            {
                var hasSelectedClass = labeledSourceKeys.Contains(storedCapture.ImageKey);
                var isUnannotated = !annotatedSourceKeys.Contains(storedCapture.ImageKey);
                if (!hasSelectedClass && !isUnannotated)
                {
                    continue;
                }

                var resolvedPath = ProjectAssetKey.ResolveProjectImagePath(_workspaceService, CurrentProjectName, storedCapture.ImageKey, storedCapture.StorageKind);
                CapturedImages.Add(new CapturedImageItem(
                    storedCapture.ImageKey,
                    resolvedPath,
                    Path.GetFileName(resolvedPath),
                    CreateBitmapImage(storedCapture.OriginalBytes),
                    storedCapture.OriginalBytes));
            }

            var selectedCapture = CapturedImages.FirstOrDefault(item =>
                string.Equals(item.ImageKey, _lastCaptureImageKey, StringComparison.OrdinalIgnoreCase))
                ?? CapturedImages.FirstOrDefault();
            AppDebugLog.Debug(DebugScope,
                $"LoadCapturedImagesAsync choose selection | chosen={DescribeCapture(selectedCapture)} | items={CapturedImages.Count}");

            SelectedCapturedImage = null;
            SelectedCapturedImage = selectedCapture;

            OnPropertyChanged(nameof(CapturedImageCount));
            OnPropertyChanged(nameof(CanMoveToPreviousCapture));
            OnPropertyChanged(nameof(CanMoveToNextCapture));
        }
        finally
        {
            IsLoadingCapturedImages = false;
            AppDebugLog.Info(DebugScope,
                $"LoadCapturedImagesAsync END | selected={DescribeCapture(SelectedCapturedImage)} | count={CapturedImages.Count}");
        }
    }

    public async Task<bool> LoadSelectedCaptureAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var selectedItem = SelectedCapturedImage;
        AppDebugLog.Info(DebugScope,
            $"LoadSelectedCaptureAsync START | class={_currentCropClass} | selected={DescribeCapture(selectedItem)}");
        if (selectedItem == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna immagine acquisita selezionata.";
            SavedCrops.Clear();
            VariationCrops.Clear();
            SelectedObjectPreview = null;
            OnPropertyChanged(nameof(SavedCropCount));
            OnPropertyChanged(nameof(VariationCropCount));
            AppDebugLog.Warn(DebugScope, "LoadSelectedCaptureAsync aborted: no selected capture.");
            return false;
        }

        _lastCaptureImageKey = selectedItem.ImageKey;
        LastCapturePath = selectedItem.FileName;
        LatestScreenshotPreview = CreateBitmapImage(selectedItem.ImageBytes);
        SelectedObjectPreview = selectedItem.PreviewImage;
        SelectedSavedCrop = null;
        SelectedVariationCrop = null;
        await LoadSavedCropsAsync(cropClass);
        AdbStatusText = $"[{CurrentProjectName}] Capture caricata per la selezione: {selectedItem.FileName}";
        AppDebugLog.Info(DebugScope,
            $"LoadSelectedCaptureAsync END | selected={DescribeCapture(selectedItem)} | savedCrops={SavedCrops.Count} | variationCrops={VariationCrops.Count}");
        return true;
    }

    public Task<bool> LoadSelectedCaptureAsync()
    {
        return LoadSelectedCaptureAsync(_currentCropClass);
    }

    public async Task<bool> LoadPreviousCaptureAsync(string cropClass)
    {
        var selectedIndex = GetSelectedCaptureIndex();
        if (selectedIndex <= 0)
        {
            return false;
        }

        SelectedCapturedImage = CapturedImages[selectedIndex - 1];
        return await LoadSelectedCaptureAsync(cropClass);
    }

    public Task<bool> LoadPreviousCaptureAsync()
    {
        return LoadPreviousCaptureAsync(_currentCropClass);
    }

    public async Task<bool> LoadNextCaptureAsync(string cropClass)
    {
        var selectedIndex = GetSelectedCaptureIndex();
        if (selectedIndex < 0 || selectedIndex >= CapturedImages.Count - 1)
        {
            return false;
        }

        SelectedCapturedImage = CapturedImages[selectedIndex + 1];
        return await LoadSelectedCaptureAsync(cropClass);
    }

    public Task<bool> LoadNextCaptureAsync()
    {
        return LoadNextCaptureAsync(_currentCropClass);
    }

    public async Task<bool> DeleteSelectedCaptureAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var selectedItem = SelectedCapturedImage;
        if (selectedItem == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna capture selezionata da eliminare.";
            return false;
        }

        var selectedIndex = GetSelectedCaptureIndex();
        await _projectImageBlobService.DeleteImagesWithCleanupAsync(CurrentProjectName, new[] { selectedItem.ImageKey });
        await LoadCapturedImagesAsync();

        if (CapturedImages.Count == 0)
        {
            LatestScreenshotPreview = null;
            SelectedObjectPreview = null;
            LastCapturePath = "Nessuna cattura eseguita.";
            _lastCaptureImageKey = null;
            SelectedCapturedImage = null;
            SavedCrops.Clear();
            VariationCrops.Clear();
            SelectedSavedCrop = null;
            SelectedVariationCrop = null;
            OnPropertyChanged(nameof(SavedCropCount));
            OnPropertyChanged(nameof(VariationCropCount));
        }
        else
        {
            var fallbackIndex = Math.Min(selectedIndex, CapturedImages.Count - 1);
            SelectedCapturedImage = CapturedImages[fallbackIndex];
            await LoadSelectedCaptureAsync(cropClass);
        }

        AdbStatusText = $"[{CurrentProjectName}] Capture eliminata: {selectedItem.FileName}";
        return true;
    }

    public Task<bool> DeleteSelectedCaptureAsync()
    {
        return DeleteSelectedCaptureAsync(_currentCropClass);
    }

    public void SetPreviewFromSavedCropSelection()
    {
        SelectedObjectPreview = SelectedSavedCrop?.PreviewImage;
        AppDebugLog.Debug(DebugScope,
            $"SetPreviewFromSavedCropSelection | preview={(SelectedObjectPreview == null ? "<null>" : "crop")} | crop={DescribeCrop(SelectedSavedCrop)}");
    }

    public void SetPreviewFromCapturedImageSelection()
    {
        if (SelectedCapturedImage == null)
        {
            SelectedObjectPreview = null;
            AppDebugLog.Debug(DebugScope, "SetPreviewFromCapturedImageSelection | preview=<null> | no selected capture");
            return;
        }

        SelectedObjectPreview = SelectedCapturedImage.PreviewImage;
        LatestScreenshotPreview = SelectedCapturedImage.PreviewImage;
        LastCapturePath = SelectedCapturedImage.FileName;
        _lastCaptureImageKey = SelectedCapturedImage.ImageKey;
        AppDebugLog.Debug(DebugScope,
            $"SetPreviewFromCapturedImageSelection | preview=image | capture={DescribeCapture(SelectedCapturedImage)}");
    }

    public async Task LoadVariationsForSelectedCropAsync()
    {
        AppDebugLog.Info(DebugScope,
            $"LoadVariationsForSelectedCropAsync START | crop={DescribeCrop(SelectedSavedCrop)}");
        IsLoadingVariationCrops = true;
        try
        {
            VariationCrops.Clear();

            var selectedCrop = SelectedSavedCrop;
            if (!SharedDatabase.IsDatabaseConnected() || selectedCrop == null)
            {
                AppDebugLog.Warn(DebugScope,
                    $"LoadVariationsForSelectedCropAsync skipped | dbConnected={SharedDatabase.IsDatabaseConnected()} | crop={DescribeCrop(selectedCrop)}");
                SelectedVariationCrop = null;
                OnPropertyChanged(nameof(VariationCropCount));
                return;
            }

            var records = await _annotationCropDbService.GetVariationsByOriginalCropImageKeyAsync(CurrentProjectName, selectedCrop.ImageKey);
            foreach (var record in records)
            {
                VariationCrops.Add(CreateSavedCropItem(record));
            }

            SelectedVariationCrop = VariationCrops.FirstOrDefault(item =>
                string.Equals(item.ImageKey, SelectedVariationCrop?.ImageKey, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(VariationCropCount));
            AppDebugLog.Info(DebugScope,
                $"LoadVariationsForSelectedCropAsync END | count={VariationCrops.Count} | selectedVariation={DescribeCrop(SelectedVariationCrop)}");
        }
        finally
        {
            IsLoadingVariationCrops = false;
        }
    }

    public void SetPreviewFromVariationSelection()
    {
        SelectedObjectPreview = SelectedVariationCrop?.PreviewImage;
        AppDebugLog.Debug(DebugScope,
            $"SetPreviewFromVariationSelection | preview={(SelectedObjectPreview == null ? "<null>" : "variation")} | variation={DescribeCrop(SelectedVariationCrop)}");
    }

    public async Task LoadSavedCropsAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        AppDebugLog.Info(DebugScope,
            $"LoadSavedCropsAsync START | class={_currentCropClass} | capture={DescribeCapture(SelectedCapturedImage)}");
        IsLoadingSavedCrops = true;
        try
        {
            SavedCrops.Clear();
            VariationCrops.Clear();
            SelectedSavedCrop = null;
            SelectedVariationCrop = null;

            if (!SharedDatabase.IsDatabaseConnected() || SelectedCapturedImage == null)
            {
                AppDebugLog.Warn(DebugScope,
                    $"LoadSavedCropsAsync skipped | dbConnected={SharedDatabase.IsDatabaseConnected()} | capture={DescribeCapture(SelectedCapturedImage)}");
                OnPropertyChanged(nameof(SavedCropCount));
                OnPropertyChanged(nameof(VariationCropCount));
                return;
            }

            var safeClass = _currentCropClass;
            var normalRecords = await _annotationCropDbService.GetProjectCropsByLabelAndSourceImageAsync(
                CurrentProjectName,
                safeClass,
                SelectedCapturedImage.ImageKey);

            foreach (var record in normalRecords)
            {
                SavedCrops.Add(CreateSavedCropItem(record));
            }

            OnPropertyChanged(nameof(SavedCropCount));
            await LoadVariationsForSelectedCropAsync();
            AppDebugLog.Info(DebugScope,
                $"LoadSavedCropsAsync END | count={SavedCrops.Count} | selectedCrop={DescribeCrop(SelectedSavedCrop)}");
        }
        finally
        {
            IsLoadingSavedCrops = false;
        }
    }

    public async Task<SavedCropItem?> SaveSelectionAsync(Int32Rect selectionRect, string cropClass, string? selectionName = null)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        if (LatestScreenshotPreview == null || string.IsNullOrWhiteSpace(_lastCaptureImageKey))
        {
            AdbStatusText = "Nessuna immagine disponibile per il salvataggio.";
            return null;
        }

        if (selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            AdbStatusText = "Selezione non valida.";
            return null;
        }

        var safeClass = _currentCropClass;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = BuildCropFileName(selectionName, safeClass, timestamp);
        var cropImageKey = $"crop|{safeClass}|{fileName}";

        var crop = new CroppedBitmap(LatestScreenshotPreview, selectionRect);
        var cropBytes = SaveBitmapSourceAsPngBytes(crop);
        await _annotationCropDbService.SaveCropAsync(CurrentProjectName, safeClass, _lastCaptureImageKey, cropImageKey, cropBytes, selectionRect);

        await LoadSavedCropsAsync(safeClass);
        SelectedSavedCrop = SavedCrops.FirstOrDefault(item => string.Equals(item.ImageKey, cropImageKey, StringComparison.OrdinalIgnoreCase));
        SetPreviewFromSavedCropSelection();
        AdbStatusText = $"[{CurrentProjectName}] Crop salvato: {fileName} | Classe YOLO: {safeClass} | Totale immagine: {SavedCropCount}";
        return SelectedSavedCrop;
    }

    public async Task<bool> DeleteSelectedCropAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var item = SelectedSavedCrop;
        if (item == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna crop selezionata da eliminare.";
            return false;
        }

        var safeClass = _currentCropClass;
        var deleted = await _annotationCropDbService.DeleteCropAsync(CurrentProjectName, safeClass, item.ImageKey);
        if (!deleted)
        {
            AdbStatusText = $"[{CurrentProjectName}] Crop non trovata: {item.FileName}";
            return false;
        }

        await LoadSavedCropsAsync(safeClass);
        AdbStatusText = $"[{CurrentProjectName}] Crop eliminata: {item.FileName}";
        return true;
    }

    public async Task<bool> DeleteSelectedVariationCropAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var item = SelectedVariationCrop;
        if (item == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna variazione selezionata da eliminare.";
            return false;
        }

        var deleted = await _annotationCropDbService.DeleteVariationAsync(CurrentProjectName, item.ImageKey);
        if (!deleted)
        {
            AdbStatusText = $"[{CurrentProjectName}] Variazione non trovata: {item.FileName}";
            return false;
        }

        await LoadVariationsForSelectedCropAsync();
        if (SelectedVariationCrop != null)
        {
            SetPreviewFromVariationSelection();
        }
        else
        {
            SetPreviewFromSavedCropSelection();
        }
        AdbStatusText = $"[{CurrentProjectName}] Variazione eliminata: {item.FileName}";
        return true;
    }

    public async Task<int> DeleteAllVariationCropsAsync(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var selectedCrop = SelectedSavedCrop;
        if (selectedCrop == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna crop selezionata per l'eliminazione delle variazioni.";
            return 0;
        }

        var deletedCount = await _annotationCropDbService.DeleteVariationsByOriginalCropImageKeyAsync(CurrentProjectName, selectedCrop.ImageKey);
        await LoadVariationsForSelectedCropAsync();
        SetPreviewFromSavedCropSelection();
        AdbStatusText = $"[{CurrentProjectName}] Variazioni eliminate per {selectedCrop.FileName}: {deletedCount}";
        return deletedCount;
    }

    public async Task<int> GenerateVariationsAsync(string cropClass, int count = 10)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var selectedItem = SelectedSavedCrop;
        if (selectedItem == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna crop selezionata per generare variazioni.";
            return 0;
        }

        var sourceRecord = await _annotationCropDbService.GetProjectCropByImageKeyAsync(CurrentProjectName, selectedItem.ImageKey);
        if (sourceRecord == null)
        {
            AdbStatusText = $"[{CurrentProjectName}] Record DB della crop selezionata non trovato.";
            return 0;
        }

        var createdCropKeys = await _cropVariationService.GenerateVariationsAsync(sourceRecord, count);
        await LoadVariationsForSelectedCropAsync();
        SelectedVariationCrop = createdCropKeys.Count > 0
            ? VariationCrops.FirstOrDefault(item => string.Equals(item.ImageKey, createdCropKeys[0], StringComparison.OrdinalIgnoreCase))
            : VariationCrops.FirstOrDefault();
        SetPreviewFromVariationSelection();
        AdbStatusText = $"[{CurrentProjectName}] Variazioni generate: {createdCropKeys.Count} | Classe: {sourceRecord.LabelName}.";
        return createdCropKeys.Count;
    }

    public async Task<int> GenerateVariationsForCropPathsAsync(string cropClass, IEnumerable<string> cropPaths, int countPerCrop = 10)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        var safePaths = cropPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (safePaths.Length == 0)
        {
            AdbStatusText = $"[{CurrentProjectName}] Nessuna crop selezionata per generare variazioni multiple.";
            return 0;
        }

        var totalGenerated = 0;
        foreach (var cropKey in safePaths)
        {
            var sourceRecord = await _annotationCropDbService.GetProjectCropByImageKeyAsync(CurrentProjectName, cropKey);
            if (sourceRecord == null || sourceRecord.IsVariation)
            {
                continue;
            }

            var createdCropKeys = await _cropVariationService.GenerateVariationsAsync(sourceRecord, countPerCrop);
            totalGenerated += createdCropKeys.Count;
        }

        await LoadVariationsForSelectedCropAsync();
        SetPreviewFromVariationSelection();
        AdbStatusText = $"[{CurrentProjectName}] Variazioni generate su {safePaths.Length} crop: {totalGenerated} | Classe: {cropClass}.";
        return totalGenerated;
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

    public async Task SetCurrentProjectAsync(string projectName, string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        CurrentProjectName = _workspaceService.EnsureProject(projectName);
        LastCapturePath = "Nessuna cattura eseguita.";
        _lastCaptureImageKey = null;
        LatestScreenshotPreview = null;
        SelectedObjectPreview = null;
        await LoadCapturedImagesAsync();
        if (SelectedCapturedImage != null)
        {
            await LoadSelectedCaptureAsync(cropClass);
        }
        else
        {
            SavedCrops.Clear();
            VariationCrops.Clear();
            OnPropertyChanged(nameof(SavedCropCount));
            OnPropertyChanged(nameof(VariationCropCount));
        }

        StatusLog.Clear();
        AdbStatusText = $"Progetto corrente: {CurrentProjectName}";
    }

    public void SetCurrentCropClass(string cropClass)
    {
        _currentCropClass = ProjectAssetKey.NormalizeLabel(cropClass);
        AppDebugLog.Debug(DebugScope, $"SetCurrentCropClass => {_currentCropClass}");
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

    private static byte[] SaveBitmapSourceAsPngBytes(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string BuildImportedCaptureFileName(string fileName, string timestamp)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"import_{timestamp}.png";
        }

        var normalizedFileName = ProjectAssetKey.NormalizeFileName(fileName);
        var extension = Path.GetExtension(normalizedFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var baseName = Path.GetFileNameWithoutExtension(normalizedFileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "import";
        }

        return $"{baseName}_{timestamp}{extension}";
    }

    private static string BuildCropFileName(string? selectionName, string fallbackClass, string timestamp)
    {
        var requestedName = string.IsNullOrWhiteSpace(selectionName)
            ? fallbackClass
            : selectionName.Trim();

        var normalizedName = ProjectAssetKey.NormalizeFileName(requestedName);
        var extension = Path.GetExtension(normalizedName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var baseName = Path.GetFileNameWithoutExtension(normalizedName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = fallbackClass;
        }

        return $"{baseName}_{timestamp}{extension}";
    }

    private static SavedCropItem CreateSavedCropItem(ProjectCropRecord record)
    {
        var cropPath = ProjectAssetKey.ResolveCropImagePath(new ProjectWorkspaceService(), record.ProjectName, record.CropImageKey);
        return new SavedCropItem(
            record.CropImageKey,
            cropPath,
            Path.GetFileName(cropPath),
            record.LabelName,
            CreateBitmapImage(record.CropImageBytes),
            record.CropImageBytes,
            record.IsVariation,
            record.SourceImageKey,
            record.OriginalCropImageKey);
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

    private int GetSelectedCaptureIndex()
    {
        return SelectedCapturedImage == null ? -1 : CapturedImages.IndexOf(SelectedCapturedImage);
    }

    private static string DescribeCapture(CapturedImageItem? item)
    {
        return item == null ? "<null>" : $"{item.FileName} [{item.ImageKey}]";
    }

    private static string DescribeCrop(SavedCropItem? item)
    {
        return item == null ? "<null>" : $"{item.FileName} [{item.ImageKey}]";
    }

}

public sealed record SavedCropItem(
    string ImageKey,
    string FilePath,
    string FileName,
    string LabelName,
    BitmapImage PreviewImage,
    byte[] ImageBytes,
    bool IsVariation,
    string SourceImageKey,
    string? OriginalCropImageKey);

public sealed record CapturedImageItem(string ImageKey, string FilePath, string FileName, BitmapImage PreviewImage, byte[] ImageBytes);
