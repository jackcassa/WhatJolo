using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using WhatJolo;

namespace Navigation;

internal sealed class MainWindowViewModel : ViewModelBase
{
    private const string SendModelClassName = "cerca";
    private const string SendBackClassName = "back";
    private static readonly string[] RequiredSendModelClasses = ["cerca", "back"];
    private const float SendDetectionThreshold = 0.05f;
    private const int ImageChangeWaitAttempts = 12;
    private const int ImageChangeWaitDelayMs = 500;
    private const int TapPauseMs = 3_000;
    private const int SendLoopDelayMs = 10_000;

    private readonly AdbService _adbService;
    private readonly ProjectModelBlobService _projectModelBlobService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly ProjectWorkspaceService _workspaceService;
    private string _dbStatusText;
    private string _statusText;
    private string _selectedProjectName;
    private bool _isDatabaseConnected;
    private bool _isSendLoopRunning;
    private BitmapSource? _lastCapturePreview;
    private CancellationTokenSource? _sendLoopCancellationTokenSource;

    public MainWindowViewModel()
    {
        _adbService = new AdbService();
        _projectModelBlobService = new ProjectModelBlobService();
        _projectImageBlobService = new ProjectImageBlobService();
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

    public bool IsSendLoopRunning
    {
        get => _isSendLoopRunning;
        private set => SetField(ref _isSendLoopRunning, value);
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

    public async Task StartSendLoopAsync()
    {
        if (IsSendLoopRunning)
        {
            StatusText = $"[{SelectedProjectName}] Workflow Send già in esecuzione.";
            return;
        }

        _sendLoopCancellationTokenSource = new CancellationTokenSource();
        IsSendLoopRunning = true;

        try
        {
            var cycleNumber = 1;
            while (!_sendLoopCancellationTokenSource.IsCancellationRequested)
            {
                await ExecuteSendCycleAsync(cycleNumber, _sendLoopCancellationTokenSource.Token);

                if (_sendLoopCancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                StatusText = $"[{SelectedProjectName}] Ciclo Send {cycleNumber} completato. Pausa di 10 secondi prima del prossimo ciclo...";
                await Task.Delay(SendLoopDelayMs, _sendLoopCancellationTokenSource.Token);
                cycleNumber++;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = $"[{SelectedProjectName}] Workflow Send fermato.";
        }
        finally
        {
            _sendLoopCancellationTokenSource?.Dispose();
            _sendLoopCancellationTokenSource = null;
            IsSendLoopRunning = false;
        }
    }

    public void StopSendLoop()
    {
        if (!IsSendLoopRunning || _sendLoopCancellationTokenSource is null)
        {
            StatusText = "Workflow Send non in esecuzione.";
            return;
        }

        StatusText = $"[{SelectedProjectName}] Arresto workflow Send richiesto...";
        _sendLoopCancellationTokenSource.Cancel();
    }

    private async Task ExecuteSendCycleAsync(int cycleNumber, CancellationToken cancellationToken)
    {
        // Step 1 del workflow Send:
        // 1. verifica progetto selezionato e disponibilità di ADB
        // 2. ricostruisce la struttura locale per l'inferenza del progetto selezionato
        //    e delle classi presenti nel progetto, ciascuna nella propria directory
        // 3. usa quei path locali per i modelli best.onnx di "cerca" e "back"
        // 4. avvia il server ADB
        // 5. legge i device collegati
        // 6. acquisisce uno screenshot PNG dal primo device disponibile
        // 7. esegue YOLO alla ricerca della classe "cerca"
        // 8. se trova "cerca" fa il tap e passa al passo successivo
        // 9. se non trova "cerca" salva l'immagine come priva_<timestamp>.png e ferma il flusso
        // 10. aspetta che l'immagine cambi
        // 11. aggiorna la preview con la nuova immagine
        // 12. esegue YOLO alla ricerca della classe "back"
        // 13. se trova "back" esegue il tap ADB sul bounding box migliore
        // 14. se non trova "back" salva l'immagine come errore_<timestamp>.png e ferma il flusso
        // 15. dopo il tap su "back" aspetta che arrivi una nuova immagine
        // 16. aggiorna ancora la preview con la nuova immagine
        cancellationToken.ThrowIfCancellationRequested();

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
            await PrepareInferenceStructureAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var modelPaths = await EnsureSendModelsAsync();
            StatusText = $"[{SelectedProjectName}] Ciclo Send {cycleNumber}: avvio ADB...";
            await _adbService.StartServerAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await _adbService.GetConnectedDevicesAsync();
            if (devices.Count == 0)
            {
                StatusText = $"[{SelectedProjectName}] Nessun device ADB collegato.";
                return;
            }

            var selectedDevice = devices[0];
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

            StatusText = $"[{SelectedProjectName}] Ciclo Send {cycleNumber}: lettura immagine da ADB in corso su {selectedDevice}...";
            var pngBytes = await _adbService.CapturePngAsync(selectedDevice);
            cancellationToken.ThrowIfCancellationRequested();
            LastCapturePreview = LoadPreview(pngBytes);

            using var imageStream = new MemoryStream(pngBytes);
            using var bitmap = new Bitmap(imageStream);
            StatusText = $"[{SelectedProjectName}] Esecuzione YOLO alla ricerca di '{SendModelClassName}'...";
            var searchDetection = AnalyzeDetection(bitmap, modelPaths[SendModelClassName], SendModelClassName);
            await AppendYoloLogAsync(BuildYoloLogBlock(
                phaseName: "cerca",
                imageName: $"adb_capture_{timestamp}.png",
                modelPath: modelPaths[SendModelClassName],
                attempt: searchDetection));
            var bestDetection = searchDetection.BestDetection;

            if (bestDetection is not null)
            {
                var tapX = bestDetection.Bounds.Left + (bestDetection.Bounds.Width / 2);
                var tapY = bestDetection.Bounds.Top + (bestDetection.Bounds.Height / 2);
                StatusText = $"[{SelectedProjectName}] 'cerca' riconosciuta ({bestDetection.Confidence:P0}). Pausa di 3 secondi prima del tap ADB @ {tapX},{tapY}...";
                await Task.Delay(TapPauseMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"[{SelectedProjectName}] Tap ADB su 'cerca' @ {tapX},{tapY}...";
                await _adbService.TapAsync(selectedDevice, tapX, tapY);
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"[{SelectedProjectName}] Tap ADB eseguito su 'cerca' ({bestDetection.Confidence:P0}) @ {tapX},{tapY}. Pausa di 3 secondi dopo il tap...";
                await Task.Delay(TapPauseMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"[{SelectedProjectName}] Attendo cambio schermata dopo il tap su 'cerca'...";
            }
            else
            {
                var privaFileName = $"priva_{timestamp}.png";
                await _projectImageBlobService.SaveImageBytesAsync(SelectedProjectName, privaFileName, "capture", pngBytes);
                StatusText = $"[{SelectedProjectName}] 'cerca' non trovata. Immagine salvata nel DB come {privaFileName}. Flusso interrotto.";
                return;
            }

            var arrowSearchBytes = await WaitForImageChangeAsync(selectedDevice, pngBytes, cancellationToken);
            LastCapturePreview = LoadPreview(arrowSearchBytes);
            StatusText = $"[{SelectedProjectName}] Cambio schermata rilevato. Passo successivo: ricerca di 'back'.";

            using var arrowImageStream = new MemoryStream(arrowSearchBytes);
            using var arrowBitmap = new Bitmap(arrowImageStream);
            StatusText = $"[{SelectedProjectName}] Esecuzione YOLO alla ricerca di 'back'...";
            var arrowAttempt = AnalyzeDetection(arrowBitmap, modelPaths[SendBackClassName], SendBackClassName);
            await AppendYoloLogAsync(BuildYoloLogBlock(
                phaseName: "back",
                imageName: $"adb_after_cerca_{timestamp}.png",
                modelPath: modelPaths[SendBackClassName],
                attempt: arrowAttempt));
            var arrowDetection = arrowAttempt.BestDetection;
            if (arrowDetection is not null)
            {
                var tapX = arrowDetection.Bounds.Left + (arrowDetection.Bounds.Width / 2);
                var tapY = arrowDetection.Bounds.Top + (arrowDetection.Bounds.Height / 2);
                StatusText = $"[{SelectedProjectName}] 'back' riconosciuta ({arrowDetection.Confidence:P0}). Pausa di 3 secondi prima del tap ADB @ {tapX},{tapY}...";
                await Task.Delay(TapPauseMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"[{SelectedProjectName}] Tap ADB su 'back' @ {tapX},{tapY}...";
                await _adbService.TapAsync(selectedDevice, tapX, tapY);
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"[{SelectedProjectName}] Tap ADB eseguito su 'back' ({arrowDetection.Confidence:P0}) @ {tapX},{tapY}. Pausa di 3 secondi dopo il tap...";
                await Task.Delay(TapPauseMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"[{SelectedProjectName}] Attendo nuova immagine dopo il tap su 'back'...";
                var postTapImageBytes = await WaitForImageChangeAsync(selectedDevice, arrowSearchBytes, cancellationToken);
                LastCapturePreview = LoadPreview(postTapImageBytes);
                StatusText = $"[{SelectedProjectName}] Nuova immagine rilevata dopo il tap su 'back'.";
                return;
            }

            var errorFileName = $"errore_{timestamp}.png";
            await _projectImageBlobService.SaveImageBytesAsync(SelectedProjectName, errorFileName, "capture", arrowSearchBytes);
            StatusText = $"[{SelectedProjectName}] 'back' non riconosciuta. Immagine salvata nel DB come {errorFileName}. Flusso interrotto.";
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private async Task<Dictionary<string, string>> EnsureSendModelsAsync()
    {
        var modelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var className in RequiredSendModelClasses)
        {
            var localModelPath = _workspaceService.FindLatestYoloOnnxPath(SelectedProjectName, className);
            if (!string.IsNullOrWhiteSpace(localModelPath) && File.Exists(localModelPath))
            {
                StatusText = $"[{SelectedProjectName}] best.onnx locale trovato per classe {className}: {localModelPath}";
                modelPaths[className] = localModelPath;
                continue;
            }

            throw new FileNotFoundException($"best.onnx locale non trovato per la classe {className} dopo la ricostruzione della struttura di inferenza.");
        }

        if (!modelPaths.TryGetValue(SendModelClassName, out var detectionModelPath) || string.IsNullOrWhiteSpace(detectionModelPath))
        {
            throw new FileNotFoundException($"Impossibile trovare un best.onnx utilizzabile per la classe {SendModelClassName}.");
        }

        return modelPaths;
    }

    private async Task PrepareInferenceStructureAsync()
    {
        StatusText = $"[{SelectedProjectName}] Ricostruzione struttura locale per l'inferenza in corso...";
        var restoredModels = await _projectModelBlobService.RestoreAllBestOnnxToProjectAsync(SelectedProjectName);
        StatusText = $"[{SelectedProjectName}] Struttura inferenza pronta: {restoredModels.Count} modelli ONNX ripristinati nelle directory di classe.";
    }

    private YoloDetectionAttempt AnalyzeDetection(Bitmap bitmap, string modelPath, string labelName)
    {
        using var detector = new YoloIconDetector(modelPath);
        var debugResult = detector.DetectDebug(bitmap, SendDetectionThreshold);
        var bestDetection = debugResult.Detections
            .Where(d => string.Equals(d.Label, labelName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();
        return new YoloDetectionAttempt(labelName, bestDetection, debugResult);
    }

    private async Task<byte[]> WaitForImageChangeAsync(string deviceSerial, byte[] baselineBytes, CancellationToken cancellationToken)
    {
        var baselineHash = Convert.ToHexString(SHA256.HashData(baselineBytes));

        for (var attempt = 1; attempt <= ImageChangeWaitAttempts; attempt++)
        {
            await Task.Delay(ImageChangeWaitDelayMs, cancellationToken);
            var candidateBytes = await _adbService.CapturePngAsync(deviceSerial);
            cancellationToken.ThrowIfCancellationRequested();
            var candidateHash = Convert.ToHexString(SHA256.HashData(candidateBytes));
            if (!string.Equals(candidateHash, baselineHash, StringComparison.Ordinal))
            {
                return candidateBytes;
            }
        }

        throw new TimeoutException("L'immagine ADB non e' cambiata entro il tempo atteso.");
    }

    private async Task AppendYoloLogAsync(string logBlock)
    {
        var logPath = Path.Combine(_workspaceService.GetProjectPath(SelectedProjectName), "navigation_yolo.log");
        var logDirectory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        await File.AppendAllTextAsync(logPath, logBlock + Environment.NewLine + Environment.NewLine);
    }

    private string BuildYoloLogBlock(string phaseName, string imageName, string modelPath, YoloDetectionAttempt attempt)
    {
        var labels = attempt.DebugResult.Labels.Count > 0
            ? string.Join(", ", attempt.DebugResult.Labels)
            : "(nessuna labels.txt trovata)";
        var bestDetectionText = attempt.BestDetection is null
            ? "nessuna"
            : $"{attempt.BestDetection.Label} conf={attempt.BestDetection.Confidence:P2} bbox={attempt.BestDetection.Bounds.Left},{attempt.BestDetection.Bounds.Top},{attempt.BestDetection.Bounds.Width},{attempt.BestDetection.Bounds.Height}";

        return
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Workflow Send YOLO" + Environment.NewLine +
            $"Progetto: {SelectedProjectName}" + Environment.NewLine +
            $"Fase: {phaseName}" + Environment.NewLine +
            $"Immagine: {imageName}" + Environment.NewLine +
            $"Modello: {modelPath}" + Environment.NewLine +
            $"Soglia confidence: {SendDetectionThreshold:0.00}" + Environment.NewLine +
            $"Input modello: {attempt.DebugResult.InputWidth}x{attempt.DebugResult.InputHeight}" + Environment.NewLine +
            $"Output tensor: {string.Join('x', attempt.DebugResult.OutputDimensions)}" + Environment.NewLine +
            $"Classi modello: {labels}" + Environment.NewLine +
            $"Detection grezze: {attempt.DebugResult.RawDetectionCount}" + Environment.NewLine +
            $"Sopra soglia: {attempt.DebugResult.AboveThresholdCount}" + Environment.NewLine +
            $"Dopo NMS: {attempt.DebugResult.FinalDetectionCount}" + Environment.NewLine +
            $"Migliore match '{attempt.LabelName}': {bestDetectionText}";
    }
}

internal sealed record YoloDetectionAttempt(
    string LabelName,
    YoloDetection? BestDetection,
    YoloDetectionDebugResult DebugResult);
