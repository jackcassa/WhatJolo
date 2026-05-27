using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using WhatJolo;

namespace Navigation;

internal sealed class MainWindowViewModel : ViewModelBase
{
    private const string SendModelClassName = "cerca";
    private const float SendDetectionThreshold = 0.05f;

    private readonly AdbService _adbService;
    private readonly ProjectModelBlobService _projectModelBlobService;
    private readonly ProjectWorkspaceService _workspaceService;
    private string _dbStatusText;
    private string _statusText;
    private string _selectedProjectName;
    private bool _isDatabaseConnected;
    private BitmapSource? _lastCapturePreview;

    public MainWindowViewModel()
    {
        _adbService = new AdbService();
        _projectModelBlobService = new ProjectModelBlobService();
        _workspaceService = new ProjectWorkspaceService();
        ProjectNames = new ObservableCollection<string>();
        _dbStatusText = "In attesa di autoconnect PostgreSQL...";
        _statusText = "Navigation pronta.";
        _selectedProjectName = string.Empty;
        ConnectionPreview = SharedDatabase.GetConnectionPreview();
        MachineName = Environment.MachineName;
        IpSummary = SharedAppBootstrap.BuildMachineIpSummary();
    }

    public ObservableCollection<string> ProjectNames { get; }

    public string ConnectionPreview { get; }

    public string MachineName { get; }

    public string IpSummary { get; }

    public string DbStatusText
    {
        get => _dbStatusText;
        private set => SetField(ref _dbStatusText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SelectedProjectName
    {
        get => _selectedProjectName;
        set
        {
            if (!SetField(ref _selectedProjectName, value))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_selectedProjectName))
            {
                var ensuredName = _workspaceService.EnsureProject(_selectedProjectName);
                if (!string.Equals(ensuredName, _selectedProjectName, StringComparison.Ordinal))
                {
                    _selectedProjectName = ensuredName;
                    OnPropertyChanged(nameof(SelectedProjectName));
                }

                StatusText = $"Progetto selezionato: {_selectedProjectName}";
            }
        }
    }

    public bool IsDatabaseConnected
    {
        get => _isDatabaseConnected;
        private set => SetField(ref _isDatabaseConnected, value);
    }

    public BitmapSource? LastCapturePreview
    {
        get => _lastCapturePreview;
        private set => SetField(ref _lastCapturePreview, value);
    }

    public async Task InitializeAsync()
    {
        var settings = SharedDatabase.LoadPostgresSettings();
        if (!settings.Enabled)
        {
            DbStatusText = "Autoconnect non disponibile: configurazione PostgreSQL disabilitata o mancante.";
            StatusText = "Nessuna connessione DB attiva.";
            return;
        }

        await ConnectAndLoadProjectsAsync();
    }

    public async Task RefreshProjectsAsync()
    {
        await LoadProjectsAsync();
    }

    public async Task ExecuteSendStep1Async()
    {
        // Step 1 del workflow Send:
        // 1. verifica progetto selezionato e disponibilità di ADB
        // 2. controlla il best.onnx del progetto corrente per la classe "cerca"
        // 3. se il best.onnx locale manca, lo ripristina dal DB scompattandolo in temp
        // 4. avvia il server ADB
        // 5. legge i device collegati
        // 6. acquisisce uno screenshot PNG dal primo device disponibile
        // 7. se trova "cerca" esegue il tap ADB sul bounding box migliore
        // 8. se non trova "cerca" salva l'immagine come errore_<timestamp>.png
        if (!_adbService.Exists())
        {
            StatusText = "ADB non trovato.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectName))
        {
            StatusText = "Nessun progetto selezionato.";
            return;
        }

        try
        {
            var modelPath = await EnsureSendModelAsync();
            StatusText = $"[{SelectedProjectName}] Avvio ADB...";
            await _adbService.StartServerAsync();
            var devices = await _adbService.GetConnectedDevicesAsync();
            if (devices.Count == 0)
            {
                StatusText = $"[{SelectedProjectName}] Nessun device ADB collegato.";
                return;
            }

            var selectedDevice = devices[0];
            var capturesPath = _workspaceService.GetCapturesPath(SelectedProjectName);
            Directory.CreateDirectory(capturesPath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

            StatusText = $"[{SelectedProjectName}] Lettura immagine da ADB in corso su {selectedDevice}...";
            var pngBytes = await _adbService.CapturePngAsync(selectedDevice);
            LastCapturePreview = LoadPreview(pngBytes);

            using var imageStream = new MemoryStream(pngBytes);
            using var bitmap = new Bitmap(imageStream);
            using var detector = new YoloIconDetector(modelPath);
            var bestDetection = detector
                .DetectAll(bitmap, SendDetectionThreshold)
                .Where(d => string.Equals(d.Label, SendModelClassName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.Confidence)
                .FirstOrDefault();

            if (bestDetection is not null)
            {
                var tapX = bestDetection.Bounds.Left + (bestDetection.Bounds.Width / 2);
                var tapY = bestDetection.Bounds.Top + (bestDetection.Bounds.Height / 2);
                StatusText = $"[{SelectedProjectName}] 'cerca' riconosciuta ({bestDetection.Confidence:P0}). Tap ADB @ {tapX},{tapY}...";
                await _adbService.TapAsync(selectedDevice, tapX, tapY);
                StatusText = $"[{SelectedProjectName}] Tap ADB eseguito su 'cerca' ({bestDetection.Confidence:P0}) @ {tapX},{tapY}.";
                return;
            }

            var errorPath = Path.Combine(capturesPath, $"errore_{timestamp}.png");
            await File.WriteAllBytesAsync(errorPath, pngBytes);
            StatusText = $"[{SelectedProjectName}] 'cerca' non riconosciuta. Immagine salvata in {errorPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"[{SelectedProjectName}] Errore step 1 Send: {ex.Message}";
        }
    }

    private async Task ConnectAndLoadProjectsAsync()
    {
        try
        {
            DbStatusText = "Autoconnect PostgreSQL in corso...";
            await SharedAppBootstrap.EnsureConfiguredPostgresReadyAsync(message => DbStatusText = message);
            IsDatabaseConnected = true;
            DbStatusText = "PostgreSQL connesso. Lettura progetti dal DB...";
            await LoadProjectsAsync();
            DbStatusText = "PostgreSQL connesso con autoconnect.";
        }
        catch (Exception ex)
        {
            IsDatabaseConnected = false;
            DbStatusText = $"Autoconnect PostgreSQL fallito: {ex.Message}";
            StatusText = "Impossibile leggere i progetti dal DB.";
        }
    }

    private async Task LoadProjectsAsync()
    {
        var snapshot = await SharedAppBootstrap.LoadProjectCatalogAsync(_workspaceService, SelectedProjectName);
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProjectNames.Clear();
            foreach (var projectName in snapshot.ProjectNames)
            {
                ProjectNames.Add(projectName);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.SelectedProjectName))
            {
                SelectedProjectName = snapshot.SelectedProjectName;
                StatusText = $"Progetti letti dal DB: {ProjectNames.Count}. Corrente: {SelectedProjectName}";
            }
            else
            {
                SelectedProjectName = string.Empty;
                StatusText = "Nessun progetto trovato nel DB.";
            }
        });
    }

    private static BitmapSource LoadPreview(byte[] pngBytes)
    {
        using var memoryStream = new MemoryStream(pngBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = memoryStream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task<string> EnsureSendModelAsync()
    {
        var localModelPath = _workspaceService.FindLatestYoloOnnxPath(SelectedProjectName);
        if (!string.IsNullOrWhiteSpace(localModelPath) && File.Exists(localModelPath))
        {
            StatusText = $"[{SelectedProjectName}] best.onnx locale trovato per classe {SendModelClassName}: {localModelPath}";
            return localModelPath;
        }

        StatusText = $"[{SelectedProjectName}] best.onnx locale mancante. Ripristino dal DB per classe {SendModelClassName}...";
        var restoredModel = await _projectModelBlobService.RestoreLatestBestOnnxToTempAsync(SelectedProjectName, SendModelClassName);
        StatusText = $"[{SelectedProjectName}] best.onnx scompattato dal DB in {restoredModel.ModelPath}";
        return restoredModel.ModelPath;
    }
}
