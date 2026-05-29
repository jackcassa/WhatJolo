using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using WhatJolo;
using WhatJolo.WindowsOcr;

namespace Navigation;

internal sealed class MainWindowViewModel : ViewModelBase
{
    private const string SendModelClassName = "cerca";
    private const string SendBackClassName = "back";
    private const string SendChatClassName = "chat";
    private const string SendInvioClassName = "invio";
    private const string SendFallbackSequence = "3204751139";
    private static readonly string[] RequiredSendModelClasses = ["cerca", "back", "chat", "invio"];
    private const float SendDetectionThreshold = 0.05f;
    private const int AndroidKeyCodeBack = 4;
    private const int AndroidKeyCodeSearch = 84;
    private const int AndroidKeyCodePaste = 279;
    private const int ImageChangeWaitAttempts = 12;
    private const int ImageChangeWaitDelayMs = 500;
    private const int FastUiSettleDelayMs = 200;
    private const string ContactFilterMigration = "migration";
    private const string ContactFilterAgenda = "agenda";
    private const string ContactFilterAgendaOnly = "agenda_only";
    private const string ContactFilterEmptyContactName = "empty_contactname";
    private const string ContactFilterTest = "test";
    private const string ContactFilterAll = "all";
    private const string SendModeFixed = "Fisso";
    private const string SendModeMigration = "Migration";
    private const string SendModeAgenda = "Agenda";
    private const string SendModeAll = "Tutti";
    private const string SendModeAgendaOnly = "Solo agenda";
    private const string SendModeOcr = "OCR nomi vuoti";
    private const string SendModeTest = "Test";

    private readonly AdbService _adbService;
    private readonly ProjectModelBlobService _projectModelBlobService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly ProjectWorkspaceService _workspaceService;
    private readonly WindowsOcrService _windowsOcrService;
    private readonly string _formStatePath;
    private readonly object _workflowLogFileLock = new();
    private string _dbStatusText;
    private string _statusText;
    private string _workflowLogText;
    private string _selectedProjectName;
    private string _currentContactName;
    private long? _currentContactId;
    private int _sentContactsCount;
    private bool _useStandardSearch;
    private bool _useStandardBack;
    private bool _useChatOcr;
    private bool _stopContactOnOcrReject;
    private bool _stopWorkflowOnOcrReject;
    private bool _usePasteAfterChat;
    private bool _useDoubleBack;
    private bool _autoScrollWorkflowLog;
    private bool _isDatabaseConnected;
    private bool _isSendLoopRunning;
    private string _selectedSendListMode;
    private BitmapSource? _lastCapturePreview;
    private CancellationTokenSource? _sendLoopCancellationTokenSource;

    public MainWindowViewModel()
    {
        _adbService = new AdbService();
        _projectModelBlobService = new ProjectModelBlobService();
        _projectImageBlobService = new ProjectImageBlobService();
        _workspaceService = new ProjectWorkspaceService();
        _windowsOcrService = new WindowsOcrService();
        _formStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhatJolo",
            "Navigation",
            "navigation-form-state.json");
        var formState = NavigationFormState.Load(_formStatePath);
        ProjectNames = new ObservableCollection<string>();
        _dbStatusText = "In attesa di autoconnect PostgreSQL...";
        _statusText = "Navigation pronta.";
        _workflowLogText = $"[{DateTime.Now:HH:mm:ss}] Navigation pronta.";
        _selectedProjectName = formState.SelectedProjectName ?? string.Empty;
        _currentContactName = "-";
        _useStandardSearch = formState.UseStandardSearch;
        _useStandardBack = formState.UseStandardBack;
        _useChatOcr = formState.UseChatOcr;
        _stopContactOnOcrReject = formState.StopContactOnOcrReject;
        _stopWorkflowOnOcrReject = formState.StopWorkflowOnOcrReject;
        _usePasteAfterChat = formState.UsePasteAfterChat;
        _useDoubleBack = formState.UseDoubleBack;
        _autoScrollWorkflowLog = formState.AutoScrollWorkflowLog;
        _selectedSendListMode = ResolveInitialSendMode(formState.SendListMode);
        ConnectionPreview = SharedDatabase.GetConnectionPreview();
        MachineName = Environment.MachineName;
        IpSummary = SharedAppBootstrap.BuildMachineIpSummary();
        SendListModes = new ObservableCollection<string>();
        EnsureSendListModes();
    }

    public ObservableCollection<string> ProjectNames { get; }
    public ObservableCollection<string> SendListModes { get; }

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

    public string WorkflowLogText
    {
        get => _workflowLogText;
        private set => SetField(ref _workflowLogText, value);
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
                AppendWorkflowLog(StatusText);
            }
        }
    }

    public string CurrentContactName
    {
        get => _currentContactName;
        private set => SetField(ref _currentContactName, value);
    }

    public long? CurrentContactId
    {
        get => _currentContactId;
        private set => SetField(ref _currentContactId, value);
    }

    public int SentContactsCount
    {
        get => _sentContactsCount;
        private set => SetField(ref _sentContactsCount, value);
    }

    public bool UseStandardSearch
    {
        get => _useStandardSearch;
        set => SetField(ref _useStandardSearch, value);
    }

    public bool UseStandardBack
    {
        get => _useStandardBack;
        set => SetField(ref _useStandardBack, value);
    }

    public bool UseChatOcr
    {
        get => _useChatOcr;
        set
        {
            if (!SetField(ref _useChatOcr, value))
            {
                return;
            }

            if (!_useChatOcr)
            {
                StopContactOnOcrReject = false;
                StopWorkflowOnOcrReject = false;
            }
        }
    }

    public bool StopContactOnOcrReject
    {
        get => _stopContactOnOcrReject;
        set
        {
            if (!SetField(ref _stopContactOnOcrReject, value))
            {
                return;
            }

            if (_stopContactOnOcrReject)
            {
                StopWorkflowOnOcrReject = false;
            }
        }
    }

    public bool StopWorkflowOnOcrReject
    {
        get => _stopWorkflowOnOcrReject;
        set
        {
            if (!SetField(ref _stopWorkflowOnOcrReject, value))
            {
                return;
            }

            if (_stopWorkflowOnOcrReject)
            {
                StopContactOnOcrReject = false;
            }
        }
    }

    public bool AutoScrollWorkflowLog
    {
        get => _autoScrollWorkflowLog;
        set => SetField(ref _autoScrollWorkflowLog, value);
    }

    public bool UsePasteAfterChat
    {
        get => _usePasteAfterChat;
        set => SetField(ref _usePasteAfterChat, value);
    }

    public bool UseDoubleBack
    {
        get => _useDoubleBack;
        set => SetField(ref _useDoubleBack, value);
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

    public string SelectedSendListMode
    {
        get => _selectedSendListMode;
        set => SetField(ref _selectedSendListMode, value);
    }

    public async Task InitializeAsync()
    {
        EnsureSendListModes();
        var settings = SharedDatabase.LoadPostgresSettings();
        if (!settings.Enabled)
        {
            DbStatusText = "Autoconnect non disponibile: configurazione PostgreSQL disabilitata o mancante.";
            StatusText = "Nessuna connessione DB attiva.";
            return;
        }

        await ConnectAndLoadProjectsAsync();
        await RefreshSentContactsCountAsync();
    }

    public async Task RefreshProjectsAsync()
    {
        await LoadProjectsAsync();
        await RefreshSentContactsCountAsync();
    }

    public void SaveFormState()
    {
        var formState = new NavigationFormState
        {
            SelectedProjectName = SelectedProjectName,
            UseStandardSearch = UseStandardSearch,
            UseStandardBack = UseStandardBack,
            UseChatOcr = UseChatOcr,
            StopContactOnOcrReject = StopContactOnOcrReject,
            StopWorkflowOnOcrReject = StopWorkflowOnOcrReject,
            UsePasteAfterChat = UsePasteAfterChat,
            UseDoubleBack = UseDoubleBack,
            AutoScrollWorkflowLog = AutoScrollWorkflowLog,
            SendListMode = SelectedSendListMode
        };

        formState.Save(_formStatePath);
    }

    private void ResetWorkflowLog(string initialMessage)
    {
        WorkflowLogText = string.Empty;
        SetStatusAndLog(initialMessage);
    }

    private void SetStatusAndLog(string message)
    {
        StatusText = message;
        AppendWorkflowLog(message);
    }

    private void AppendWorkflowLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        WorkflowLogText = string.IsNullOrWhiteSpace(WorkflowLogText)
            ? line
            : $"{WorkflowLogText}{Environment.NewLine}{line}";
        AppendWorkflowLogLineToFile(line);
    }

    private void AppendWorkflowLogLineToFile(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedProjectName))
            {
                return;
            }

            var logPath = Path.Combine(_workspaceService.GetProjectPath(SelectedProjectName), "navigation_yolo.log");
            var logDirectory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            lock (_workflowLogFileLock)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Non interrompere il workflow se il log su file fallisce.
        }
    }

    public async Task StartSendLoopAsync()
    {
        if (IsSendLoopRunning)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Workflow Send già in esecuzione.");
            return;
        }

        ResetWorkflowLog($"[{SelectedProjectName}] Avvio workflow Send (ciclo singolo).");
        _sendLoopCancellationTokenSource = new CancellationTokenSource();
        IsSendLoopRunning = true;

        try
        {
            const int cycleNumber = 1;
            await ExecuteSendCycleAsync(cycleNumber, _sendLoopCancellationTokenSource.Token, SendFallbackSequence, "Send");

            if (!_sendLoopCancellationTokenSource.IsCancellationRequested)
            {
                SetStatusAndLog($"[{SelectedProjectName}] Workflow Send completato (1 ciclo).");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Workflow Send fermato.");
        }
        catch (Exception)
        {
            // Lo StatusText viene già valorizzato nel ciclo che ha fallito.
            // Qui fermiamo semplicemente il loop continuo.
        }
        finally
        {
            _sendLoopCancellationTokenSource?.Dispose();
            _sendLoopCancellationTokenSource = null;
            IsSendLoopRunning = false;
        }
    }

    public async Task StartSelectedSendLoopAsync()
    {
        switch (SelectedSendListMode)
        {
            case SendModeFixed:
                await StartSendLoopAsync();
                break;
            case SendModeMigration:
                await StartSend2MigrationLoopAsync();
                break;
            case SendModeAgenda:
                await StartSend3AgendaLoopAsync();
                break;
            case SendModeAll:
                await StartSend4AllLoopAsync();
                break;
            case SendModeAgendaOnly:
                await StartSend5AgendaOnlyLoopAsync();
                break;
            case SendModeOcr:
                await StartSend6OcrLoopAsync();
                break;
            case SendModeTest:
                await StartSendTestLoopAsync();
                break;
            default:
                await StartSendTestLoopAsync();
                break;
        }
    }

    public async Task StartSend2LoopAsync()
    {
        if (IsSendLoopRunning)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Workflow già in esecuzione.");
            return;
        }

        ResetWorkflowLog($"[{SelectedProjectName}] Avvio workflow Send2.");
        _sendLoopCancellationTokenSource = new CancellationTokenSource();
        IsSendLoopRunning = true;

        try
        {
            var cycleNumber = 1;
            while (!_sendLoopCancellationTokenSource.IsCancellationRequested)
            {
                var contacts = await LoadWorkflow2ContactsAsync(_sendLoopCancellationTokenSource.Token);
                if (contacts.Count == 0)
                {
                    SetStatusAndLog($"[{SelectedProjectName}] Nessun contatto con telefono disponibile per Send2.");
                    break;
                }

                foreach (var contact in contacts)
                {
                    _sendLoopCancellationTokenSource.Token.ThrowIfCancellationRequested();
                    var cycleOutcome = await ExecuteSendCycleAsync(
                        cycleNumber,
                        _sendLoopCancellationTokenSource.Token,
                        contact.Telefono,
                        $"Send2/{contact.DisplayName}");

                    if (_sendLoopCancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    if (cycleOutcome == SendCycleOutcome.CompletedMarkSent)
                    {
                        SetStatusAndLog($"[{SelectedProjectName}] Send2 contatto completato: {contact.DisplayName}. Passo al prossimo contatto...");
                    }
                    else if (cycleOutcome == SendCycleOutcome.CompletedNextCycle)
                    {
                        SetStatusAndLog($"[{SelectedProjectName}] Send2 passa al ciclo successivo dopo gestione INVITA per: {contact.DisplayName}. Passo al prossimo contatto...");
                    }
                    else
                    {
                        SetStatusAndLog($"[{SelectedProjectName}] Send2 contatto chiuso senza invio finale: {contact.DisplayName}. Passo al prossimo contatto...");
                    }
                }

                if (_sendLoopCancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                SetStatusAndLog($"[{SelectedProjectName}] Send2 completato su {contacts.Count} contatti. Ripartenza dall'inizio della lista contatti...");
                cycleNumber++;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Workflow Send2 fermato.");
        }
        catch (Exception)
        {
            // Lo StatusText viene già valorizzato nel ciclo che ha fallito.
        }
        finally
        {
            _sendLoopCancellationTokenSource?.Dispose();
            _sendLoopCancellationTokenSource = null;
            IsSendLoopRunning = false;
        }
    }

    public async Task StartSend2MigrationLoopAsync()
    {
        await StartContactSendLoopAsync(
            workflowName: "Send2",
            emptyMessage: "Nessun contatto con telefono disponibile e migration attiva per Send2.",
            contactsLoader: token => LoadFilteredWorkflowContactsAsync(ContactFilterMigration, token));
    }

    public async Task StartSend3AgendaLoopAsync()
    {
        await StartContactSendLoopAsync(
            workflowName: "Send3",
            emptyMessage: "Nessun contatto con telefono disponibile e agenda attiva per Send3.",
            contactsLoader: token => LoadFilteredWorkflowContactsAsync(ContactFilterAgenda, token));
    }

    public async Task StartSend4AllLoopAsync()
    {
        await StartContactSendLoopAsync(
            workflowName: "Send4",
            emptyMessage: "Nessun contatto con telefono disponibile per Send4.",
            contactsLoader: token => LoadFilteredWorkflowContactsAsync(ContactFilterAll, token));
    }

    public async Task StartSend5AgendaOnlyLoopAsync()
    {
        await StartContactSendLoopAsync(
            workflowName: "Send5",
            emptyMessage: "Nessun contatto con telefono disponibile con agenda attiva e migration disattiva per Send5.",
            contactsLoader: token => LoadFilteredWorkflowContactsAsync(ContactFilterAgendaOnly, token));
    }

    public async Task StartSend6OcrLoopAsync()
    {
        await StartContactSendLoopAsync(
            workflowName: "Send6",
            emptyMessage: "Nessun contatto con telefono disponibile e contactname vuoto per Send6.",
            contactsLoader: token => LoadFilteredWorkflowContactsAsync(ContactFilterEmptyContactName, token));
    }

    public async Task StartSendTestLoopAsync()
    {
        await StartContactSendLoopAsync(
            workflowName: "SendTest",
            emptyMessage: "Nessun contatto con telefono disponibile e test=true per SendTest.",
            contactsLoader: token => LoadFilteredWorkflowContactsAsync(ContactFilterTest, token));
    }

    private async Task StartContactSendLoopAsync(
        string workflowName,
        string emptyMessage,
        Func<CancellationToken, Task<IReadOnlyList<Workflow2Contact>>> contactsLoader)
    {
        if (IsSendLoopRunning)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Workflow gia' in esecuzione.");
            return;
        }

        ResetWorkflowLog($"[{SelectedProjectName}] Avvio workflow {workflowName}.");
        _sendLoopCancellationTokenSource = new CancellationTokenSource();
        IsSendLoopRunning = true;

        try
        {
            var cycleNumber = 1;
            while (!_sendLoopCancellationTokenSource.IsCancellationRequested)
            {
                var contacts = await contactsLoader(_sendLoopCancellationTokenSource.Token);
                if (contacts.Count == 0)
                {
                    SetStatusAndLog($"[{SelectedProjectName}] {emptyMessage}");
                    break;
                }

                foreach (var contact in contacts)
                {
                    _sendLoopCancellationTokenSource.Token.ThrowIfCancellationRequested();
                    CurrentContactId = contact.Id;
                    CurrentContactName = contact.DisplayName;
                    SetStatusAndLog($"[{SelectedProjectName}] {workflowName} in corso su contatto {contact.DisplayName} (Id={contact.Id}).");
                    var cycleOutcome = await ExecuteSendCycleAsync(
                        cycleNumber,
                        _sendLoopCancellationTokenSource.Token,
                        contact.Telefono,
                        $"{workflowName}/{contact.DisplayName}",
                        currentContact: contact);

                    if (_sendLoopCancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    if (cycleOutcome == SendCycleOutcome.CompletedMarkSent)
                    {
                        await MarkContactSentAsync(contact.Id, _sendLoopCancellationTokenSource.Token);
                        await RefreshSentContactsCountAsync(_sendLoopCancellationTokenSource.Token);
                        SetStatusAndLog($"[{SelectedProjectName}] {workflowName} contatto completato: {contact.DisplayName}. Passo al prossimo contatto...");
                    }
                    else if (cycleOutcome == SendCycleOutcome.CompletedNextCycle)
                    {
                        SetStatusAndLog($"[{SelectedProjectName}] {workflowName} continua col ciclo successivo dopo gestione INVITA per: {contact.DisplayName}. Passo al prossimo contatto...");
                    }
                    else
                    {
                        SetStatusAndLog($"[{SelectedProjectName}] {workflowName} contatto chiuso senza invio finale: {contact.DisplayName}. Passo al prossimo contatto...");
                    }
                }

                if (_sendLoopCancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                SetStatusAndLog($"[{SelectedProjectName}] {workflowName} completato su {contacts.Count} contatti. Ripartenza dall'inizio della lista contatti...");
                cycleNumber++;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Workflow {workflowName} fermato.");
        }
        catch (Exception)
        {
            // Lo StatusText viene gia' valorizzato nel ciclo che ha fallito.
        }
        finally
        {
            CurrentContactId = null;
            CurrentContactName = "-";
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

        SetStatusAndLog($"[{SelectedProjectName}] Arresto workflow Send richiesto...");
        _sendLoopCancellationTokenSource.Cancel();
    }

    public async Task ResetSentAsync()
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            throw new InvalidOperationException("Database non connesso.");
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Contacts
            SET Sent = 0,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        await command.ExecuteNonQueryAsync();
        await RefreshSentContactsCountAsync();
        SetStatusAndLog($"[{SelectedProjectName}] Flag sent azzerato per tutti i contatti.");
    }

    public async Task ResetExcludeAsync()
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            throw new InvalidOperationException("Database non connesso.");
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Contacts
            SET Exclude = 0,
                UpdatedAtUtc = CURRENT_TIMESTAMP;
            """;
        await command.ExecuteNonQueryAsync();
        SetStatusAndLog($"[{SelectedProjectName}] Flag exclude azzerato per tutti i contatti.");
    }

    private async Task<SendCycleOutcome> ExecuteSendCycleAsync(
        int cycleNumber,
        CancellationToken cancellationToken,
        string fallbackSequence,
        string workflowName,
        Workflow2Contact? currentContact = null)
    {
        // Step 1 del workflow Send:
        // 1. verifica progetto selezionato e disponibilità di ADB
        // 2. ricostruisce la struttura locale per l'inferenza del progetto selezionato
        //    e delle classi presenti nel progetto, ciascuna nella propria directory
        // 3. usa quei path locali per i modelli best.onnx di "cerca", "back" e "chat"
        // 4. avvia il server ADB
        // 5. legge i device collegati
        // 6. acquisisce uno screenshot PNG dal primo device disponibile
        // 7. esegue YOLO alla ricerca della classe "cerca"
        // 8. se trova "cerca" fa il tap, aspetta il cambio schermata e aggiorna la preview
        // 9. se non trova "cerca" salva l'immagine come priva_<timestamp>.png e interrompe il ciclo
        // 10. invia la sequenza 3204751139
        // 11. aspetta il cambio schermata, aggiorna la preview e cerca "chat"
        // 12. se non trova "chat" salva l'immagine come priva_<timestamp>_chat.png e interrompe il ciclo
        // 13. se trova "chat" fa il tap, aspetta il cambio schermata e aggiorna la preview
        // 14. dopo il tap su "chat" invia testo "ch", attende cambio schermata e cerca "invio"
        // 15. se trova "invio" fa tap, attende cambio schermata e aggiorna la preview
        // 16. esegue il passaggio "back" con YOLO, tap e attesa cambio immagine
        cancellationToken.ThrowIfCancellationRequested();

        if (!_adbService.Exists())
        {
            StatusText = "ADB non trovato.";
            return SendCycleOutcome.CompletedNoSent;
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectName))
        {
            StatusText = "Nessun progetto selezionato.";
            return SendCycleOutcome.CompletedNoSent;
        }

        try
        {
            var restoredModelPaths = await PrepareInferenceStructureAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var modelPaths = EnsureSendModels(restoredModelPaths);
            SetStatusAndLog($"[{SelectedProjectName}] {workflowName} ciclo {cycleNumber}: avvio ADB...");
            await _adbService.StartServerAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await _adbService.GetConnectedDevicesAsync();
            if (devices.Count == 0)
            {
                SetStatusAndLog($"[{SelectedProjectName}] Nessun device ADB collegato.");
                return SendCycleOutcome.CompletedNoSent;
            }

            var selectedDevice = devices[0];
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

            SetStatusAndLog($"[{SelectedProjectName}] {workflowName} ciclo {cycleNumber}: lettura immagine da ADB in corso su {selectedDevice}...");
            var pngBytes = await _adbService.CapturePngAsync(selectedDevice);
            cancellationToken.ThrowIfCancellationRequested();
            LastCapturePreview = LoadPreview(pngBytes);

            byte[] currentScreenBytes;
            if (UseStandardSearch)
            {
                SetStatusAndLog($"[{SelectedProjectName}] Uso comando standard per '{SendModelClassName}' (KEYCODE_SEARCH)...");
                await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeSearch);
                await Task.Delay(FastUiSettleDelayMs, cancellationToken);
                SetStatusAndLog($"[{SelectedProjectName}] Attendo cambio schermata dopo KEYCODE_SEARCH...");
                currentScreenBytes = await WaitForImageChangeAsync(selectedDevice, pngBytes, cancellationToken);
                LastCapturePreview = LoadPreview(currentScreenBytes);
            }
            else
            {
                using var imageStream = new MemoryStream(pngBytes);
                using var bitmap = new Bitmap(imageStream);
                SetStatusAndLog($"[{SelectedProjectName}] Esecuzione YOLO alla ricerca di '{SendModelClassName}'...");
                var searchDetection = AnalyzeDetection(bitmap, modelPaths[SendModelClassName], SendModelClassName);
                await AppendYoloLogAsync(BuildYoloLogBlock(
                    phaseName: "cerca",
                    imageName: $"adb_capture_{timestamp}.png",
                    modelPath: modelPaths[SendModelClassName],
                    attempt: searchDetection));
                var bestDetection = searchDetection.BestDetection;

                if (bestDetection is not null)
                {
                    await TapDetectionAsync(selectedDevice, SendModelClassName, bestDetection, cancellationToken);
                    SetStatusAndLog($"[{SelectedProjectName}] Attendo cambio schermata dopo il tap su '{SendModelClassName}'...");
                    currentScreenBytes = await WaitForImageChangeAsync(selectedDevice, pngBytes, cancellationToken);
                    LastCapturePreview = LoadPreview(currentScreenBytes);
                }
                else
                {
                    var privaFileName = $"priva_{timestamp}.png";
                    var privaPath = await SaveWorkflowImageToFileSystemAsync(SendModelClassName, privaFileName, pngBytes);
                    SetStatusAndLog($"[{SelectedProjectName}] '{SendModelClassName}' non trovata. Immagine salvata su file come {privaPath}. Flusso interrotto.");
                    return SendCycleOutcome.CompletedNoSent;
                }
            }

            SetStatusAndLog($"[{SelectedProjectName}] Invio sequenza {fallbackSequence}...");
            await _adbService.SendTextAsync(selectedDevice, fallbackSequence);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(FastUiSettleDelayMs, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] Sequenza inviata. Attendo cambio schermata per cercare '{SendChatClassName}'...");
            var chatScreenBytes = await WaitForImageChangeAsync(selectedDevice, currentScreenBytes, cancellationToken);
            LastCapturePreview = LoadPreview(chatScreenBytes);
            if (currentContact is not null && await HandleNoSearchOrInviteFromUiDumpAsync(selectedDevice, currentContact, cancellationToken))
            {
                return SendCycleOutcome.CompletedNoSent;
            }

            var chatDetection = await DetectAndLogAsync(
                chatScreenBytes,
                modelPaths[SendChatClassName],
                SendChatClassName,
                phaseName: SendChatClassName,
                imageName: $"adb_after_sequence_{timestamp}.png");
            if (chatDetection.BestDetection is null)
            {
                var chatPrivaFileName = $"priva_{timestamp}_chat.png";
                var chatPrivaPath = await SaveWorkflowImageToFileSystemAsync(SendChatClassName, chatPrivaFileName, chatScreenBytes);
                SetStatusAndLog($"[{SelectedProjectName}] '{SendChatClassName}' non trovata. Immagine salvata su file come {chatPrivaPath}. Flusso interrotto.");
                return SendCycleOutcome.CompletedNoSent;
            }

            if (UseChatOcr && currentContact is not null)
            {
                var ocrOutcome = await TryApplyChatOcrAsync(currentContact, chatScreenBytes, chatDetection.BestDetection);
                if (ocrOutcome == OcrFailureAction.StopWorkflow)
                {
                    SetStatusAndLog($"[{SelectedProjectName}] Workflow fermato per OCR non accettato sul contatto {currentContact.Id}.");
                    throw new OcrWorkflowStopException($"OCR non accettato per contatto {currentContact.Id}.");
                }

                if (ocrOutcome == OcrFailureAction.StopCurrentContact)
                {
                    SetStatusAndLog($"[{SelectedProjectName}] Contatto {currentContact.Id} interrotto per OCR non accettato.");
                    return SendCycleOutcome.CompletedNoSent;
                }

                if (ocrOutcome == OcrFailureAction.MarkExcludeAndContinue)
                {
                    await MarkContactExcludeAsync(currentContact.Id, cancellationToken);
                    SetStatusAndLog($"[{SelectedProjectName}] Contatto {currentContact.Id} marcato come exclude=true. Invio KEYCODE_BACK e continuo col prossimo contatto.");
                   
                    await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeBack);
                    await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeBack);
                    await Task.Delay(FastUiSettleDelayMs, cancellationToken);
                    var postExcludeBackBytes = await WaitForImageChangeAsync(selectedDevice, chatScreenBytes, cancellationToken);
                    LastCapturePreview = LoadPreview(postExcludeBackBytes);
                    SetStatusAndLog($"[{SelectedProjectName}] Cambio schermata rilevato dopo gestione EXCLUDE per contatto {currentContact.Id}. Continuo col prossimo contatto.");
                    return SendCycleOutcome.CompletedNextCycle;
                }

                if (ocrOutcome == OcrFailureAction.MarkInvitaAndContinue)
                {
                    await MarkContactInvitaAsync(currentContact.Id, cancellationToken);
                    await MarkContactExcludeAsync(currentContact.Id, cancellationToken);
                    SetStatusAndLog($"[{SelectedProjectName}] Contatto {currentContact.Id} marcato come invita=true ed exclude=true. Invio KEYCODE_BACK e continuo col prossimo contatto.");
                    await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeBack);
                    await Task.Delay(FastUiSettleDelayMs, cancellationToken);
                    var postInviteBackBytes = await WaitForImageChangeAsync(selectedDevice, chatScreenBytes, cancellationToken);
                    LastCapturePreview = LoadPreview(postInviteBackBytes);
                    SetStatusAndLog($"[{SelectedProjectName}] Cambio schermata rilevato dopo gestione INVITA per contatto {currentContact.Id}. Continuo col prossimo contatto.");
                    return SendCycleOutcome.CompletedNextCycle;
                }
            }

            await TapDetectionAsync(selectedDevice, SendChatClassName, chatDetection.BestDetection, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] Attendo cambio schermata dopo il tap su '{SendChatClassName}'...");
            currentScreenBytes = await WaitForImageChangeAsync(selectedDevice, chatScreenBytes, cancellationToken);
            LastCapturePreview = LoadPreview(currentScreenBytes);
            currentScreenBytes = await ExecuteInvioStepAsync(selectedDevice, modelPaths, currentScreenBytes, $"{timestamp}_invio", cancellationToken);
            if (currentScreenBytes is null)
            {
                return SendCycleOutcome.CompletedNoSent;
            }

            var backScreenBytes = await ExecuteBackStepAsync(selectedDevice, modelPaths, currentScreenBytes, $"{timestamp}_back", cancellationToken);
            if (backScreenBytes is null)
            {
                return SendCycleOutcome.CompletedNoSent;
            }

            LastCapturePreview = LoadPreview(backScreenBytes);
            SetStatusAndLog($"[{SelectedProjectName}] {workflowName} ciclo {cycleNumber} completato con passaggio '{SendBackClassName}'.");
            return SendCycleOutcome.CompletedMarkSent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Errore step 1 Send: {ex.Message}");
            throw;
        }
    }

    private async Task<YoloDetectionAttempt> DetectAndLogAsync(
        byte[] imageBytes,
        string modelPath,
        string labelName,
        string phaseName,
        string imageName)
    {
        using var imageStream = new MemoryStream(imageBytes);
        using var bitmap = new Bitmap(imageStream);
        SetStatusAndLog($"[{SelectedProjectName}] Esecuzione YOLO alla ricerca di '{labelName}'...");
        var attempt = AnalyzeDetection(bitmap, modelPath, labelName);
        await AppendYoloLogAsync(BuildYoloLogBlock(
            phaseName,
            imageName,
            modelPath,
            attempt));
        return attempt;
    }

    private async Task<bool> HandleNoSearchOrInviteFromUiDumpAsync(string selectedDevice, Workflow2Contact currentContact, CancellationToken cancellationToken)
    {
        var xml = await _adbService.DumpUiHierarchyXmlAsync(selectedDevice);
        var uiDumpPath = await SaveWorkflowXmlDumpToFileSystemAsync(
            reason: "search_state",
            currentContact,
            xml);
        SetStatusAndLog($"[{SelectedProjectName}] UI dump salvato per controllo ricerca/INVITA: {uiDumpPath}");

        var hasNoResults = _adbService.ContainsNodeByTextOrResourceId(
            xml,
            text: "Nessun risultato trovato",
            resourceId: "com.whatsapp.w4b:id/search_no_matches");

        var hasChatOrContactRows = _adbService.HasAnyNodeByResourceId(
            xml,
            "com.whatsapp.w4b:id/contact_row_container",
            "com.whatsapp.w4b:id/conversations_row_contact_name");

        // Nuova regola: "Nessun risultato trovato" è valido solo se CHAT/CONTATTI sono davvero vuoti.
        var shouldTreatAsNoResults = hasNoResults && !hasChatOrContactRows;
        if (!shouldTreatAsNoResults)
        {
            if (hasNoResults && hasChatOrContactRows)
            {
                SetStatusAndLog($"[{SelectedProjectName}] UI dump: 'Nessun risultato trovato' presente ma CHAT/CONTATTI non vuoti. Non applico exclude. Dump={uiDumpPath}");
            }
            var hasInviteSection = _adbService.ContainsNodeByTextOrResourceId(
                xml,
                text: "INVITA SU WHATSAPP",
                resourceId: string.Empty);
            var hasInviteButton = _adbService.ContainsNodeByTextOrResourceId(
                xml,
                text: "INVITA",
                resourceId: "com.whatsapp.w4b:id/invite_btn");

            if (!hasInviteSection && !hasInviteButton)
            {
                return false;
            }

            await MarkContactInvitaAsync(currentContact.Id, cancellationToken);
            await MarkContactExcludeAsync(currentContact.Id, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] UI dump: rilevato caso INVITA per contatto {currentContact.Id}. invita=true, exclude=true e ritorno indietro. Dump={uiDumpPath}");
            if (_adbService.TryFindNodeByContentDesc(xml, "Indietro", out var inviteBackNode))
            {
                var (tapX, tapY) = _adbService.GetNodeCenter(inviteBackNode);
                SetStatusAndLog($"[{SelectedProjectName}] Tap back da XML @ {tapX},{tapY}.");
                await _adbService.TapAsync(selectedDevice, tapX, tapY);
                await Task.Delay(FastUiSettleDelayMs, cancellationToken);
                SetStatusAndLog($"[{SelectedProjectName}] INVITA: back eseguito, passo al prossimo contatto.");
                return true;
            }

            SetStatusAndLog($"[{SelectedProjectName}] INVITA rilevato ma nodo back non rilevato da XML. Fallback KEYCODE_BACK.");
            await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeBack);
            await Task.Delay(FastUiSettleDelayMs, cancellationToken);
            return true;
        }

        await MarkContactExcludeAsync(currentContact.Id, cancellationToken);
        SetStatusAndLog($"[{SelectedProjectName}] UI dump: rilevato 'Nessun risultato trovato' per contatto {currentContact.Id}. exclude=true e tap su back da XML. Dump={uiDumpPath}");
        if (_adbService.TryFindNodeByContentDesc(xml, "Indietro", out var backNode))
        {
            var (tapX, tapY) = _adbService.GetNodeCenter(backNode);
            SetStatusAndLog($"[{SelectedProjectName}] Tap back da XML @ {tapX},{tapY}.");
            await _adbService.TapAsync(selectedDevice, tapX, tapY);
            await Task.Delay(FastUiSettleDelayMs, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] Nessun risultato: back eseguito, passo al prossimo contatto.");
            return true;
        }

        SetStatusAndLog($"[{SelectedProjectName}] Nessun risultato trovato ma nodo back non rilevato da XML. Fallback KEYCODE_BACK.");
        await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeBack);
        await Task.Delay(FastUiSettleDelayMs, cancellationToken);
        return true;
    }

    private async Task<string> SaveWorkflowXmlDumpToFileSystemAsync(string reason, Workflow2Contact currentContact, string xmlContent)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "state" : reason.Trim();
        var normalizedPhone = string.IsNullOrWhiteSpace(currentContact.Telefono)
            ? "no_phone"
            : new string(currentContact.Telefono.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            normalizedPhone = "no_phone";
        }

        var fileName = $"uidump_{DateTime.Now:yyyyMMdd_HHmmss_fff}_id{currentContact.Id}_{normalizedPhone}_{normalizedReason}.xml";
        var xmlPath = Path.Combine(_workspaceService.GetCapturesPath(SelectedProjectName), "ui_dump");
        Directory.CreateDirectory(xmlPath);
        var outputPath = Path.Combine(xmlPath, fileName);
        await File.WriteAllTextAsync(outputPath, xmlContent ?? string.Empty);
        return outputPath;
    }

    private async Task TapDetectionAsync(
        string selectedDevice,
        string labelName,
        YoloDetection detection,
        CancellationToken cancellationToken)
    {
        var tapX = detection.Bounds.Left + (detection.Bounds.Width / 2);
        var tapY = detection.Bounds.Top + (detection.Bounds.Height / 2);
        SetStatusAndLog($"[{SelectedProjectName}] '{labelName}' riconosciuta ({detection.Confidence:P0}). Tap ADB rapido @ {tapX},{tapY}...");
        await Task.Delay(FastUiSettleDelayMs, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetStatusAndLog($"[{SelectedProjectName}] Tap ADB su '{labelName}' @ {tapX},{tapY}...");
        await _adbService.TapAsync(selectedDevice, tapX, tapY);
        cancellationToken.ThrowIfCancellationRequested();
        SetStatusAndLog($"[{SelectedProjectName}] Tap ADB eseguito su '{labelName}' ({detection.Confidence:P0}) @ {tapX},{tapY}. Breve attesa post-tap...");
        await Task.Delay(FastUiSettleDelayMs, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<byte[]?> ExecuteInvioStepAsync(
        string selectedDevice,
        IReadOnlyDictionary<string, string> modelPaths,
        byte[] baselineBytes,
        string imageSuffix,
        CancellationToken cancellationToken)
    {
        byte[] postCommandBytes;
        if (UsePasteAfterChat)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Invio comando ADB paste (KEYCODE_PASTE) dopo il tap su '{SendChatClassName}'...");
            await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodePaste);
            await Task.Delay(FastUiSettleDelayMs, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] Attendo cambio schermata dopo comando paste...");
            postCommandBytes = await WaitForImageChangeAsync(selectedDevice, baselineBytes, cancellationToken);
        }
        else
        {
            SetStatusAndLog($"[{SelectedProjectName}] Invio (paste) disabilitato da checkbox: salto KEYCODE_PASTE e step '{SendInvioClassName}'. Passo direttamente a '{SendBackClassName}'.");
            LastCapturePreview = LoadPreview(baselineBytes);
            return baselineBytes;
        }
        LastCapturePreview = LoadPreview(postCommandBytes);

        var invioAttempt = await DetectAndLogAsync(
            postCommandBytes,
            modelPaths[SendInvioClassName],
            SendInvioClassName,
            phaseName: SendInvioClassName,
            imageName: $"adb_{imageSuffix}.png");

        if (invioAttempt.BestDetection is null)
        {
            var errorFileName = $"errore_{imageSuffix}.png";
            var errorPath = await SaveWorkflowImageToFileSystemAsync(SendInvioClassName, errorFileName, postCommandBytes);
            SetStatusAndLog($"[{SelectedProjectName}] '{SendInvioClassName}' non riconosciuta. Immagine salvata su file come {errorPath}. Flusso interrotto.");
            return null;
        }

        await TapDetectionAsync(selectedDevice, SendInvioClassName, invioAttempt.BestDetection, cancellationToken);
        SetStatusAndLog($"[{SelectedProjectName}] Attendo nuova immagine dopo il tap su '{SendInvioClassName}'...");
        var postTapBytes = await WaitForImageChangeAsync(selectedDevice, postCommandBytes, cancellationToken);
        LastCapturePreview = LoadPreview(postTapBytes);
        SetStatusAndLog($"[{SelectedProjectName}] Nuova immagine rilevata dopo il tap su '{SendInvioClassName}'.");
        return postTapBytes;
    }

    private async Task<byte[]?> ExecuteBackStepAsync(
        string selectedDevice,
        IReadOnlyDictionary<string, string> modelPaths,
        byte[] baselineBytes,
        string imageSuffix,
        CancellationToken cancellationToken)
    {
        LastCapturePreview = LoadPreview(baselineBytes);
        SetStatusAndLog($"[{SelectedProjectName}] Cambio schermata rilevato. Passo successivo: ricerca di '{SendBackClassName}'.");

        if (UseStandardBack)
        {
            SetStatusAndLog($"[{SelectedProjectName}] Uso comando standard per '{SendBackClassName}' (KEYCODE_BACK)...");
            await Task.Delay(FastUiSettleDelayMs, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] Invio KEYCODE_BACK...");
            await _adbService.SendKeyEventAsync(selectedDevice, AndroidKeyCodeBack);
            await Task.Delay(FastUiSettleDelayMs, cancellationToken);
            SetStatusAndLog($"[{SelectedProjectName}] Attendo nuova immagine dopo KEYCODE_BACK...");
            var postBackImageBytes = await WaitForImageChangeAsync(selectedDevice, baselineBytes, cancellationToken);
            LastCapturePreview = LoadPreview(postBackImageBytes);
            SetStatusAndLog($"[{SelectedProjectName}] Nuova immagine rilevata dopo KEYCODE_BACK.");
            return postBackImageBytes;
        }

        var backAttempt = await DetectAndLogAsync(
            baselineBytes,
            modelPaths[SendBackClassName],
            SendBackClassName,
            phaseName: SendBackClassName,
            imageName: $"adb_{imageSuffix}.png");

        if (backAttempt.BestDetection is null)
        {
            var errorFileName = $"errore_{imageSuffix}.png";
            var errorPath = await SaveWorkflowImageToFileSystemAsync(SendBackClassName, errorFileName, baselineBytes);
            SetStatusAndLog($"[{SelectedProjectName}] '{SendBackClassName}' non riconosciuta. Immagine salvata su file come {errorPath}. Flusso interrotto.");
            return null;
        }

        await TapDetectionAsync(selectedDevice, SendBackClassName, backAttempt.BestDetection, cancellationToken);
        SetStatusAndLog($"[{SelectedProjectName}] Attendo nuova immagine dopo il tap su '{SendBackClassName}'...");
        var postTapImageBytes = await WaitForImageChangeAsync(selectedDevice, baselineBytes, cancellationToken);
        LastCapturePreview = LoadPreview(postTapImageBytes);
        SetStatusAndLog($"[{SelectedProjectName}] Nuova immagine rilevata dopo il tap su '{SendBackClassName}'.");
        return postTapImageBytes;
    }

    private async Task<OcrFailureAction> TryApplyChatOcrAsync(Workflow2Contact contact, byte[] chatScreenBytes, YoloDetection detection)
    {
        try
        {
            var ocrResult = await TryReadChatNameAsync(chatScreenBytes, detection.Bounds);
            var linesText = ocrResult.Lines.Count == 0
                ? "(nessuna riga)"
                : string.Join(" | ", ocrResult.Lines.Select((line, index) => $"r{index + 1}='{line}'"));
            if (!ocrResult.Success)
            {
                var rejectMessage = $"[{SelectedProjectName}] OCR chat non accettato per contatto {contact.Id}: {ocrResult.Reason}. Righe: {linesText}";
                AppendWorkflowLog(rejectMessage);
                var ocrRejectImagePath = await SaveOcrRejectedImageWithBoundsAsync(
                    contact,
                    chatScreenBytes,
                    detection.Bounds,
                    ocrResult.Reason);
                AppendWorkflowLog($"[{SelectedProjectName}] OCR chat immagine non riconosciuta salvata con box OCR su: {ocrRejectImagePath}");
                if (ocrResult.ForceStopWorkflow)
                {
                    return OcrFailureAction.StopWorkflow;
                }

                if (ocrResult.MarkExcludeAndContinue)
                {
                    return OcrFailureAction.MarkExcludeAndContinue;
                }

                if (ocrResult.MarkInvitaAndContinue)
                {
                    return OcrFailureAction.MarkInvitaAndContinue;
                }

                if (StopWorkflowOnOcrReject)
                {
                    return OcrFailureAction.StopWorkflow;
                }

                if (StopContactOnOcrReject)
                {
                    return OcrFailureAction.StopCurrentContact;
                }

                return OcrFailureAction.Continue;
            }

            var recognizedMessage = $"[{SelectedProjectName}] OCR chat ha riconosciuto per contatto {contact.Id} il nome '{ocrResult.ContactName}'. Righe: {linesText}";
            AppendWorkflowLog(recognizedMessage);
            await MarkContactOcrAsync(contact.Id, ocrResult.ContactName);
            var message = string.IsNullOrWhiteSpace(contact.ContactName)
                ? $"[{SelectedProjectName}] OCR chat accettato per contatto {contact.Id}. Nome salvato: {ocrResult.ContactName}."
                : $"[{SelectedProjectName}] OCR chat accettato per contatto {contact.Id}. Flag OCR aggiornato.";
            AppendWorkflowLog(message);
            return OcrFailureAction.Continue;
        }
        catch (Exception ex)
        {
            var errorMessage = $"[{SelectedProjectName}] OCR chat fallito per contatto {contact.Id}: {ex.Message}";
            AppendWorkflowLog(errorMessage);
            if (StopWorkflowOnOcrReject)
            {
                return OcrFailureAction.StopWorkflow;
            }

            if (StopContactOnOcrReject)
            {
                return OcrFailureAction.StopCurrentContact;
            }

            return OcrFailureAction.Continue;
        }
    }

    private async Task<string> SaveOcrRejectedImageWithBoundsAsync(
        Workflow2Contact contact,
        byte[] imageBytes,
        Rectangle originalOcrBounds,
        string reason)
    {
        using var sourceStream = new MemoryStream(imageBytes);
        using var sourceBitmap = new Bitmap(sourceStream);
        using var annotatedBitmap = new Bitmap(sourceBitmap);
        using var graphics = Graphics.FromImage(annotatedBitmap);

        var imageRect = new Rectangle(0, 0, annotatedBitmap.Width, annotatedBitmap.Height);
        var expandedOcrBounds = ExpandOcrBoundsKeepingCenter(originalOcrBounds);
        var normalizedOriginalBounds = Rectangle.Intersect(imageRect, originalOcrBounds);
        var normalizedExpandedBounds = Rectangle.Intersect(imageRect, expandedOcrBounds);
        if (normalizedOriginalBounds.Width > 0 && normalizedOriginalBounds.Height > 0)
        {
            using var originalPen = new Pen(Color.Yellow, 4f);
            graphics.DrawRectangle(originalPen, normalizedOriginalBounds);
        }

        if (normalizedExpandedBounds.Width > 0 && normalizedExpandedBounds.Height > 0)
        {
            using var expandedPen = new Pen(Color.Red, 4f);
            graphics.DrawRectangle(expandedPen, normalizedExpandedBounds);
        }

        var boxText = $"OCR BOX id={contact.Id} tel={contact.Telefono} reason={reason}";
        using var textFont = new System.Drawing.Font(System.Drawing.FontFamily.GenericMonospace, 18f, System.Drawing.FontStyle.Bold);
        var textSize = graphics.MeasureString(boxText, textFont);
        var textRect = new RectangleF(
            8f,
            8f,
            Math.Min(annotatedBitmap.Width - 16f, textSize.Width + 20f),
            textSize.Height + 12f);
        using var textBackground = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var textBrush = new SolidBrush(Color.Lime);
        graphics.FillRectangle(textBackground, textRect);
        graphics.DrawString(boxText, textFont, textBrush, textRect.Left + 8f, textRect.Top + 4f);

        await using var outputStream = new MemoryStream();
        annotatedBitmap.Save(outputStream, System.Drawing.Imaging.ImageFormat.Png);
        var fileName = $"ocr_reject_{DateTime.Now:yyyyMMdd_HHmmss_fff}_id{contact.Id}.png";
        return await SaveWorkflowImageToFileSystemAsync("chat_ocr", fileName, outputStream.ToArray());
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

    private async Task<IReadOnlyList<Workflow2Contact>> LoadWorkflow2ContactsAsync(CancellationToken cancellationToken)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            throw new InvalidOperationException("Database non connesso.");
        }

        var contacts = new List<Workflow2Contact>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,
                   COALESCE(ContactName, ''),
                   COALESCE(Telefono, '')
            FROM Contacts
            WHERE COALESCE(Telefono, '') <> ''
              AND COALESCE(Sent, 0) = 0
              AND COALESCE(Exclude, 0) = 0
            ORDER BY Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var contactName = reader.GetString(1);
            var telefono = reader.GetString(2);
            contacts.Add(new Workflow2Contact(
                id,
                contactName,
                telefono,
                string.IsNullOrWhiteSpace(contactName) ? telefono : $"{contactName} ({telefono})"));
        }

        return contacts;
    }

    private async Task<IReadOnlyList<Workflow2Contact>> LoadFilteredWorkflowContactsAsync(string filterColumn, CancellationToken cancellationToken)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            throw new InvalidOperationException("Database non connesso.");
        }

        if (!string.Equals(filterColumn, ContactFilterMigration, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filterColumn, ContactFilterAgenda, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filterColumn, ContactFilterAgendaOnly, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filterColumn, ContactFilterEmptyContactName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filterColumn, ContactFilterTest, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filterColumn, ContactFilterAll, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Filtro contatti non supportato: {filterColumn}.");
        }

        var extraWhere = string.Equals(filterColumn, ContactFilterAll, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : string.Equals(filterColumn, ContactFilterAgendaOnly, StringComparison.OrdinalIgnoreCase)
                ? "AND COALESCE(Agenda, FALSE) = TRUE AND COALESCE(Migration, FALSE) = FALSE"
                : string.Equals(filterColumn, ContactFilterEmptyContactName, StringComparison.OrdinalIgnoreCase)
                    ? "AND COALESCE(BTRIM(ContactName), '') = ''"
                : $"AND COALESCE({filterColumn}, FALSE) = TRUE";

        var contacts = new List<Workflow2Contact>();
        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT Id,
                   COALESCE(ContactName, ''),
                   COALESCE(Telefono, '')
            FROM Contacts
            WHERE COALESCE(Telefono, '') <> ''
              {extraWhere}
              AND COALESCE(Sent, 0) = 0
              AND COALESCE(Exclude, 0) = 0
            ORDER BY Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var contactName = reader.GetString(1);
            var telefono = reader.GetString(2);
            contacts.Add(new Workflow2Contact(
                id,
                contactName,
                telefono,
                string.IsNullOrWhiteSpace(contactName) ? telefono : $"{contactName} ({telefono})"));
        }

        return contacts;
    }

    private async Task<ChatOcrResult> TryReadChatNameAsync(byte[] imageBytes, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new ChatOcrResult(false, null, "bounding box non valida", [], false);
        }

        using var sourceStream = new MemoryStream(imageBytes);
        using var sourceBitmap = new Bitmap(sourceStream);
        var expandedBounds = ExpandOcrBoundsKeepingCenter(bounds);
        var normalizedBounds = Rectangle.Intersect(new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height), expandedBounds);
        if (normalizedBounds.Width <= 0 || normalizedBounds.Height <= 0)
        {
            return new ChatOcrResult(false, null, "bounding box fuori immagine", [], false);
        }

        using var cropBitmap = sourceBitmap.Clone(normalizedBounds, sourceBitmap.PixelFormat);
        var tempImagePath = Path.Combine(Path.GetTempPath(), $"whatjolo_chat_ocr_{Guid.NewGuid():N}.png");
        cropBitmap.Save(tempImagePath, System.Drawing.Imaging.ImageFormat.Png);

        try
        {
            var cropBytes = await File.ReadAllBytesAsync(tempImagePath);
            var ocrLines = await _windowsOcrService.ReadLinesFromPngBytesAsync(cropBytes, new Rectangle(0, 0, cropBitmap.Width, cropBitmap.Height));
            if (TryFindNonTraHeaderLine(ocrLines, out var nonTraLine))
            {
                return new ChatOcrResult(false, null, $"rilevato testo OCR = '{nonTraLine}'", ocrLines, false, false, true);
            }

            if (TryFindInvitaLine(ocrLines, out var invitaLine))
            {
                return new ChatOcrResult(false, null, $"rilevato testo OCR = '{invitaLine}'", ocrLines, false, true);
            }

            var firstLine = ocrLines.Count > 0 ? (ocrLines[0]?.Trim() ?? string.Empty) : string.Empty;
            if (string.Equals(firstLine, "INVITA SU WHATSAPP", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatOcrResult(false, null, "prima riga OCR = 'INVITA SU WHATSAPP'", ocrLines, false, true);
            }

            var headerMatch = FindAcceptedOcrHeader(ocrLines);
            if (headerMatch is null)
            {
                return new ChatOcrResult(false, null, "intestazione OCR non trovata: atteso 'CHAT' o 'CONTATTI' o 'NON TRA I TUOI CONTATTI' (fuzzy distanza=1 su CHAT/CONTATTI)", ocrLines, false);
            }

            string candidateName;
            var headerIndex = headerMatch.Value.Index;
            var acceptedHeader = headerMatch.Value.Header;
            if (string.Equals(acceptedHeader, "NON TRA I TUOI CONTATTI", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatOcrResult(false, null, "intestazione OCR = 'NON TRA I TUOI CONTATTI'", ocrLines, false, false, true);
            }

            var remainingLines = ocrLines.Count - (headerIndex + 1);
            if (remainingLines <= 0)
            {
                return new ChatOcrResult(false, null, "riga nome OCR non presente dopo intestazione", ocrLines, false);
            }

            if (string.Equals(acceptedHeader, "CONTATTI", StringComparison.OrdinalIgnoreCase) && remainingLines == 2)
            {
                candidateName = ocrLines[headerIndex + 1]?.Trim() ?? string.Empty;
            }
            else if (remainingLines == 3)
            {
                candidateName = ocrLines[headerIndex + 1]?.Trim() ?? string.Empty;
            }
            else if (remainingLines >= 4)
            {
                var secondLine = ocrLines[headerIndex + 1]?.Trim() ?? string.Empty;
                var thirdLine = ocrLines[headerIndex + 2]?.Trim() ?? string.Empty;
                candidateName = string.Join(" ", new[] { secondLine, thirdLine }.Where(line => !string.IsNullOrWhiteSpace(line)));
            }
            else
            {
                candidateName = ocrLines[headerIndex + 1]?.Trim() ?? string.Empty;
            }

            var cleanedName = CleanChatContactName(candidateName);
            if (string.IsNullOrWhiteSpace(cleanedName))
            {
                return new ChatOcrResult(false, null, $"riga nome OCR non valida: '{candidateName}'", ocrLines, false);
            }

            return new ChatOcrResult(true, cleanedName, $"OCR ok: '{cleanedName}'", ocrLines, false);
        }
        finally
        {
            if (File.Exists(tempImagePath))
            {
                File.Delete(tempImagePath);
            }
        }
    }

    private static string CleanChatContactName(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(line.Trim(), @"\s+\d{1,2}:\d{2}$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+\d{1,2}/\d{1,2}/\d{2,4}$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+\d{1,2}/\d{1,2}$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+(oggi|ieri)$", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static string ResolveInitialSendMode(string? mode)
    {
        return mode switch
        {
            SendModeFixed => SendModeFixed,
            SendModeMigration => SendModeMigration,
            SendModeAgenda => SendModeAgenda,
            SendModeAll => SendModeAll,
            SendModeAgendaOnly => SendModeAgendaOnly,
            SendModeOcr => SendModeOcr,
            SendModeTest => SendModeTest,
            _ => SendModeFixed
        };
    }

    private void EnsureSendListModes()
    {
        var expectedModes = new[]
        {
            SendModeFixed,
            SendModeMigration,
            SendModeAgenda,
            SendModeAll,
            SendModeAgendaOnly,
            SendModeOcr,
            SendModeTest
        };

        if (SendListModes.Count == expectedModes.Length &&
            expectedModes.All(mode => SendListModes.Contains(mode)))
        {
            if (!SendListModes.Contains(SelectedSendListMode))
            {
                SelectedSendListMode = SendModeFixed;
            }

            return;
        }

        SendListModes.Clear();
        foreach (var mode in expectedModes)
        {
            SendListModes.Add(mode);
        }

        if (!SendListModes.Contains(SelectedSendListMode))
        {
            SelectedSendListMode = SendModeFixed;
        }
    }

    private static Rectangle ExpandOcrBoundsKeepingCenter(Rectangle bounds)
    {
        var expandedHeight = (int)Math.Round(bounds.Height * 1.30, MidpointRounding.AwayFromZero);
        var verticalPadding = (int)Math.Round((expandedHeight - bounds.Height) / 2.0, MidpointRounding.AwayFromZero);
        return new Rectangle(
            bounds.X,
            bounds.Y - verticalPadding,
            bounds.Width,
            expandedHeight);
    }

    private static string NormalizeOcrHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lettersOnly = new string(value
            .ToUpperInvariant()
            .Where(char.IsLetter)
            .ToArray());
        return lettersOnly;
    }

    private static bool TryFindInvitaLine(IReadOnlyList<string> ocrLines, out string matchedLine)
    {
        foreach (var line in ocrLines)
        {
            var trimmed = line?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var normalized = NormalizeOcrHeader(trimmed);
            if (string.Equals(normalized, "INVITASUWHATSAPP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "INVITA", StringComparison.OrdinalIgnoreCase))
            {
                matchedLine = trimmed;
                return true;
            }
        }

        matchedLine = string.Empty;
        return false;
    }

    private static bool TryFindNonTraHeaderLine(IReadOnlyList<string> ocrLines, out string matchedLine)
    {
        foreach (var line in ocrLines)
        {
            var trimmed = line?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var normalized = NormalizeOcrHeader(trimmed);
            if (ComputeLevenshteinDistance(normalized, "NONTRAITUOICONTATTI") <= 1)
            {
                matchedLine = trimmed;
                return true;
            }
        }

        matchedLine = string.Empty;
        return false;
    }

    private static string? TryResolveAcceptedOcrHeader(string normalizedHeader)
    {
        if (string.IsNullOrWhiteSpace(normalizedHeader))
        {
            return null;
        }

        var nonTraDistance = ComputeLevenshteinDistance(normalizedHeader, "NONTRAITUOICONTATTI");
        if (nonTraDistance <= 1)
        {
            return "NON TRA I TUOI CONTATTI";
        }

        var chatDistance = ComputeLevenshteinDistance(normalizedHeader, "CHAT");
        if (chatDistance <= 1)
        {
            return "CHAT";
        }

        var contattiDistance = ComputeLevenshteinDistance(normalizedHeader, "CONTATTI");
        if (contattiDistance <= 1)
        {
            return "CONTATTI";
        }

        return null;
    }

    private static (int Index, string Header)? FindAcceptedOcrHeader(IReadOnlyList<string> ocrLines)
    {
        for (var i = 0; i < ocrLines.Count; i++)
        {
            var normalized = NormalizeOcrHeader(ocrLines[i]);
            var accepted = TryResolveAcceptedOcrHeader(normalized);
            if (accepted is not null)
            {
                return (i, accepted);
            }
        }

        return null;
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var costs = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            costs[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            var previousDiagonal = costs[0];
            costs[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var previousUp = costs[j];
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                costs[j] = Math.Min(
                    Math.Min(costs[j] + 1, costs[j - 1] + 1),
                    previousDiagonal + substitutionCost);
                previousDiagonal = previousUp;
            }
        }

        return costs[right.Length];
    }

    private static async Task MarkContactOcrAsync(long contactId, string? recognizedName)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Contacts
            SET ContactName = CASE
                    WHEN COALESCE(BTRIM(ContactName), '') = '' AND COALESCE(BTRIM(@name), '') <> '' THEN @name
                    ELSE ContactName
                END,
                Ocr = TRUE,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE Id = @id;
            """;
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "id";
        idParameter.Value = contactId;
        command.Parameters.Add(idParameter);
        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "name";
        nameParameter.Value = string.IsNullOrWhiteSpace(recognizedName) ? DBNull.Value : recognizedName;
        command.Parameters.Add(nameParameter);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MarkContactSentAsync(long contactId, CancellationToken cancellationToken)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Contacts
            SET Sent = 1,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE Id = @id;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = contactId;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkContactInvitaAsync(long contactId, CancellationToken cancellationToken)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Contacts
            SET Invita = TRUE,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE Id = @id;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = contactId;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkContactExcludeAsync(long contactId, CancellationToken cancellationToken)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            return;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Contacts
            SET Exclude = 1,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE Id = @id;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = contactId;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RefreshSentContactsCountAsync(CancellationToken cancellationToken = default)
    {
        if (!SharedDatabase.IsDatabaseConnected())
        {
            SentContactsCount = 0;
            return;
        }

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM Contacts
            WHERE COALESCE(Sent, 0) <> 0;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        SentContactsCount = Convert.ToInt32(result ?? 0);
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

    private Dictionary<string, string> EnsureSendModels(IReadOnlyDictionary<string, string> restoredModelPaths)
    {
        var modelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var className in RequiredSendModelClasses)
        {
            if (!restoredModelPaths.TryGetValue(className, out var localModelPath))
            {
                throw new FileNotFoundException($"best.onnx dal DB non ripristinato per la classe {className}.");
            }

            if (!string.IsNullOrWhiteSpace(localModelPath) && File.Exists(localModelPath))
            {
                EnsureRuntimeLabelFiles(localModelPath, className);
                SetStatusAndLog($"[{SelectedProjectName}] best.onnx sincronizzato dal DB per classe {className}: {localModelPath}");
                modelPaths[className] = localModelPath;
                continue;
            }

            throw new FileNotFoundException($"best.onnx dal DB non disponibile localmente per la classe {className} dopo la sincronizzazione.");
        }

        if (!modelPaths.TryGetValue(SendModelClassName, out var detectionModelPath) || string.IsNullOrWhiteSpace(detectionModelPath))
        {
            throw new FileNotFoundException($"Impossibile trovare un best.onnx utilizzabile per la classe {SendModelClassName}.");
        }

        return modelPaths;
    }

    private async Task<Dictionary<string, string>> PrepareInferenceStructureAsync()
    {
        SetStatusAndLog($"[{SelectedProjectName}] Sincronizzazione modelli dal DB in corso...");
        var restoredModels = await _projectModelBlobService.RestoreAllBestOnnxToProjectAsync(SelectedProjectName);
        var restoredPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var restoredModel in restoredModels)
        {
            restoredPaths[restoredModel.ClassName] = restoredModel.ModelPath;
            SetStatusAndLog($"[{SelectedProjectName}] Modello DB aggiornato: classe={restoredModel.ClassName} hash={restoredModel.ContentHash[..Math.Min(12, restoredModel.ContentHash.Length)]} path={restoredModel.ModelPath}");
        }

        SetStatusAndLog($"[{SelectedProjectName}] Sincronizzazione inferenza completata: {restoredModels.Count} modelli ONNX ripristinati dal DB.");
        return restoredPaths;
    }

    private YoloDetectionAttempt AnalyzeDetection(Bitmap bitmap, string modelPath, string labelName)
    {
        using var detector = new YoloIconDetector(modelPath);
        var debugResult = detector.DetectDebug(bitmap, SendDetectionThreshold);
        var bestDetection = debugResult.Detections
            .Where(d => string.Equals(d.Label, labelName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        // Fallback robusto: quando labels.txt manca, il detector usa label "icon".
        // In questo caso assumiamo modello single-class e prendiamo la detection migliore.
        if (bestDetection is null && debugResult.Labels.Count == 0 && debugResult.Detections.Count > 0)
        {
            var fallback = debugResult.Detections
                .OrderByDescending(d => d.Confidence)
                .First();
            bestDetection = new YoloDetection
            {
                Bounds = fallback.Bounds,
                Confidence = fallback.Confidence,
                ClassIndex = fallback.ClassIndex,
                Label = labelName
            };
        }

        return new YoloDetectionAttempt(labelName, bestDetection, debugResult);
    }

    private static void EnsureRuntimeLabelFiles(string modelPath, string className)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(className))
        {
            return;
        }

        var normalizedClass = className.Trim();
        var labelFileCandidates = new[]
        {
            Path.ChangeExtension(modelPath, ".labels.txt"),
            Path.ChangeExtension(modelPath, ".txt")
        };

        foreach (var labelFile in labelFileCandidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(labelFile))
                {
                    continue;
                }

                var dir = Path.GetDirectoryName(labelFile);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(labelFile) || string.IsNullOrWhiteSpace(File.ReadAllText(labelFile)))
                {
                    File.WriteAllText(labelFile, normalizedClass + Environment.NewLine);
                }
            }
            catch
            {
                // Non bloccare il workflow: il fallback detection gestisce anche labels mancanti.
            }
        }
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

    private async Task<string> SaveWorkflowImageToFileSystemAsync(string className, string fileName, byte[] imageBytes)
    {
        var normalizedClassName = string.IsNullOrWhiteSpace(className) ? "misc" : className.Trim();
        var capturesPath = Path.Combine(_workspaceService.GetCapturesPath(SelectedProjectName), normalizedClassName);
        Directory.CreateDirectory(capturesPath);
        var outputPath = Path.Combine(capturesPath, fileName);
        await File.WriteAllBytesAsync(outputPath, imageBytes);
        return outputPath;
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

internal sealed record Workflow2Contact(
    long Id,
    string ContactName,
    string Telefono,
    string DisplayName);

internal sealed record ChatOcrResult(
    bool Success,
    string? ContactName,
    string Reason,
    IReadOnlyList<string> Lines,
    bool ForceStopWorkflow,
    bool MarkInvitaAndContinue = false,
    bool MarkExcludeAndContinue = false);

internal enum OcrFailureAction
{
    Continue,
    StopCurrentContact,
    StopWorkflow,
    MarkInvitaAndContinue,
    MarkExcludeAndContinue
}

internal sealed class OcrWorkflowStopException(string message) : Exception(message);

internal enum SendCycleOutcome
{
    CompletedNoSent,
    CompletedNextCycle,
    CompletedMarkSent
}
