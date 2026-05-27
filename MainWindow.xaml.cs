using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Npgsql;

namespace WhatJolo;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainWindowViewModel _viewModel;
    private AdbPreviewWindow? _adbPreviewWindow;
    private UltraPreviewWindow? _ultraPreviewWindow;
    private bool _suppressDatabaseTableAutoLoad;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (RootTabControl.Items.Contains(DbInstanceTab))
        {
            RootTabControl.Items.Remove(DbInstanceTab);
            RootTabControl.Items.Insert(0, DbInstanceTab);
            RootTabControl.SelectedItem = DbInstanceTab;
        }
        await _viewModel.InitializeProjectWorkspaceAsync();
        await _viewModel.AdbCaptureTab.InitializeAdbAsync();
    }

    private async void SaveSelection_Click(object sender, RoutedEventArgs e)
    {
        var selectedRect = _adbPreviewWindow?.SelectedPixelRect;
        if (selectedRect == null)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage("Nessuna selezione da salvare.");
            return;
        }

        var preview = _viewModel.AdbCaptureTab.CreateSelectionPreview(selectedRect.Value);
        if (preview == null)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage("Impossibile creare l'anteprima della selezione.");
            return;
        }

        var confirmationWindow = new CropConfirmationWindow(preview, _viewModel.SelectedCropClass)
        {
            Owner = this
        };

        confirmationWindow.Show();
        var accepted = await WaitForWindowCloseAsync(confirmationWindow, () => confirmationWindow.IsAccepted);
        if (!accepted)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage("Salvataggio crop annullato.");
            return;
        }

        var savedItem = await _viewModel.AdbCaptureTab.SaveSelectionAsync(selectedRect.Value, _viewModel.SelectedCropClass);
        if (savedItem == null)
        {
            return;
        }

        _adbPreviewWindow?.ClearSelection();
    }

    private async void GenerateVariations_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AdbCaptureTab.SelectedSavedCrop == null && _viewModel.AdbCaptureTab.SelectedVariationCrop == null)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage("Seleziona prima una crop per generare le variazioni.");
            return;
        }

        try
        {
            var generatedCount = await _viewModel.AdbCaptureTab.GenerateVariationsAsync(_viewModel.SelectedCropClass, 10);
            if (generatedCount <= 0)
            {
                return;
            }

            MessageBox.Show(
                this,
                $"Variazioni generate: {generatedCount}",
                "Variazione crop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Errore generazione variazioni: {ex.Message}");
        }
    }

    private async void CaptureAdbScreenshot_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await _viewModel.AdbCaptureTab.CaptureAdbScreenshotAsync();
        EnsureAdbPreviewWindow();
        _adbPreviewWindow?.ClearSelection();
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AdbCaptureTab.RefreshConnectedDevicesAsync();
    }

    private async void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var restoredProject = await _viewModel.RestoreCurrentProjectFromDatabaseAsync();
            _viewModel.AdbCaptureTab.SetStatusMessage(
                $"[{restoredProject.ProjectName}] Progetto ricreato dal DB: {restoredProject.RestoredImageCount} immagini ripristinate.");
            MessageBox.Show(
                this,
                "Progetto caricato",
                "WhatJolo",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Errore ripristino progetto da DB: {ex.Message}");
        }
    }

    private async void CreateDataset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var datasetPath = await _viewModel.CreateDatasetStructureAsync();
            _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Dataset YOLO creato: {datasetPath}");
        }
        catch (Exception ex)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Errore creazione dataset: {ex.Message}");
        }
    }

    private async void TrainYolo_Click(object sender, RoutedEventArgs e)
    {
        var monitorWindow = new YoloTrainingMonitorWindow(_viewModel.SelectedProjectName, _viewModel.SelectedYoloModel)
        {
            Owner = this
        };
        monitorWindow.Show();

        await _viewModel.TrainYoloAsync(
            progress => Dispatcher.Invoke(() => monitorWindow.UpdateProgress(progress)),
            completedMessage => Dispatcher.Invoke(() => monitorWindow.MarkCompleted(completedMessage)),
            failedMessage => Dispatcher.Invoke(() => monitorWindow.MarkFailed(failedMessage)));
    }

    private void ResetTraining_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SelectedProjectName))
        {
            _viewModel.AdbCaptureTab.SetStatusMessage("Nessun progetto selezionato.");
            return;
        }

        var latestRunPath = _viewModel.FindLatestYoloRunPath();
        if (string.IsNullOrWhiteSpace(latestRunPath))
        {
            MessageBox.Show(
                this,
                $"Nessuna run YOLO trovata per il progetto '{_viewModel.SelectedProjectName}'.",
                "Reset Training",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Vuoi eliminare l'ultima run di training?{Environment.NewLine}{Environment.NewLine}{latestRunPath}",
            "Reset Training",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Reset training annullato.");
            return;
        }

        var deletedRunPath = _viewModel.ResetLatestYoloRun();
        if (string.IsNullOrWhiteSpace(deletedRunPath))
        {
            _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Nessuna run da eliminare.");
            return;
        }

        _viewModel.AdbCaptureTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Run eliminata: {deletedRunPath}");
    }

    private async void CaptureUltraTest_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.UltraTab.CaptureTestImageAsync(_viewModel.AdbCaptureTab.SelectedDeviceSerial);
    }

    private void OpenUltraTestFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenInExplorer(_viewModel.UltraTab.GetTestFolderPath());
    }

    private void OpenUltraPreview_Click(object sender, RoutedEventArgs e)
    {
        EnsureUltraPreviewWindow();
    }

    private async void PromoteUltraDetections_Click(object sender, RoutedEventArgs e)
    {
        const float threshold = 0.30f;
        var candidateCount = await _viewModel.UltraTab.GetPromotableDetectionCountAsync(threshold);
        if (candidateCount <= 0)
        {
            MessageBox.Show(
                this,
                $"Nessuna detection sopra la soglia del {threshold:P0} per l'immagine test selezionata.",
                "Promuovi detection",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Immagine test corrente: {_viewModel.UltraTab.SelectedTestImage?.FileName}{Environment.NewLine}{Environment.NewLine}" +
            $"Soglia: {threshold:P0}{Environment.NewLine}" +
            $"Detection da promuovere: {candidateCount}{Environment.NewLine}{Environment.NewLine}" +
            "Vuoi salvarle nelle classi trovate da YOLO e registrarle nel DB del progetto corrente?",
            "Promuovi detection",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            _viewModel.UltraTab.SetStatusMessage($"[{_viewModel.SelectedProjectName}] Promozione detection annullata.");
            return;
        }

        var savedCount = await _viewModel.UltraTab.PromoteDetectedCropsAsync(threshold);
        await _viewModel.AdbCaptureTab.LoadSavedCropsAsync(_viewModel.SelectedCropClass);
        MessageBox.Show(
            this,
            $"Crop salvate nel training del progetto corrente: {savedCount}",
            "Promuovi detection",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OpenInExplorer(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"",
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    private async void CropClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        await _viewModel.AdbCaptureTab.LoadSavedCropsAsync(_viewModel.SelectedCropClass);
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Nuovo progetto", "Nome progetto:", _viewModel.SelectedProjectName)
        {
            Owner = this
        };

        prompt.Show();
        var accepted = await WaitForWindowCloseAsync(prompt, () => prompt.IsAccepted);
        if (!accepted)
        {
            return;
        }

        await _viewModel.CreateOrSelectProjectAsync(prompt.EnteredText);
        CloseAdbPreviewWindow();
        await _viewModel.AlignProjectImageBlobsAsync();
        await _viewModel.LoadDatabaseTableAsync(_viewModel.SelectedDatabaseTable);
    }

    private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (sender is ComboBox comboBox && comboBox.SelectedItem is string projectName && !string.IsNullOrWhiteSpace(projectName))
        {
            _viewModel.SelectedProjectName = projectName;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.SelectedProjectName))
        {
            return;
        }

        await _viewModel.ApplySelectedProjectAsync();
        CloseAdbPreviewWindow();
        await _viewModel.AlignProjectImageBlobsAsync();
        await _viewModel.LoadDatabaseTableAsync(_viewModel.SelectedDatabaseTable);
    }

    private async void SavedCropsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.AdbCaptureTab.DeleteSelectedCropAsync(_viewModel.SelectedCropClass);
    }

    private async void VariationCropsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.AdbCaptureTab.DeleteSelectedVariationCropAsync(_viewModel.SelectedCropClass);
    }

    private async void UltraTestImages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel?.UltraTab.SelectedTestImage == null)
        {
            return;
        }

        await _viewModel.UltraTab.DetectTestImageAsync();
    }

    private async void UltraTrainImages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel?.UltraTab.SelectedTrainImage == null)
        {
            return;
        }

        await _viewModel.UltraTab.DetectTrainImageAsync();
    }

    private async void UltraValImages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel?.UltraTab.SelectedValImage == null)
        {
            return;
        }

        await _viewModel.UltraTab.DetectValImageAsync();
    }

    private async void UltraTestImages_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _viewModel?.UltraTab.SelectedTestImage == null)
        {
            return;
        }

        e.Handled = true;
        if (MessageBox.Show(this,
                $"Eliminare il file test selezionato?{Environment.NewLine}{_viewModel.UltraTab.SelectedTestImage.FileName}",
                "Elimina file test",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.UltraTab.DeleteSelectedTestImageAsync();
    }

    private async void UltraTrainImages_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _viewModel?.UltraTab.SelectedTrainImage == null)
        {
            return;
        }

        e.Handled = true;
        if (MessageBox.Show(this,
                $"Eliminare il file train selezionato e la sua label?{Environment.NewLine}{_viewModel.UltraTab.SelectedTrainImage.FileName}",
                "Elimina file train",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.UltraTab.DeleteSelectedTrainImageAsync();
    }

    private async void UltraValImages_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _viewModel?.UltraTab.SelectedValImage == null)
        {
            return;
        }

        e.Handled = true;
        if (MessageBox.Show(this,
                $"Eliminare il file val selezionato e la sua label?{Environment.NewLine}{_viewModel.UltraTab.SelectedValImage.FileName}",
                "Elimina file val",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.UltraTab.DeleteSelectedValImageAsync();
    }

    private async void DatabaseTables_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatabaseTableAutoLoad)
        {
            return;
        }

        await _viewModel.LoadDatabaseTableAsync(_viewModel.SelectedDatabaseTable);
    }

    private async void RefreshDatabase_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadDatabaseTableAsync(_viewModel.SelectedDatabaseTable);
    }

    private async void SaveDbInstance_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.SaveDatabaseInstanceSettings();
        }
        catch (Exception ex)
        {
            _viewModel.DbInstanceStatus = $"Errore salvataggio impostazioni DB: {ex.Message}";
        }
    }

    private async void TestDbInstance_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.TestDatabaseInstanceConnectionAsync();
    }

    private async void ReloadDbInstance_Click(object sender, RoutedEventArgs e)
    {
        _suppressDatabaseTableAutoLoad = true;
        try
        {
            await _viewModel.ReloadDatabaseConnectionAsync();
        }
        finally
        {
            _suppressDatabaseTableAutoLoad = false;
        }
    }

    private async void ConnectDbInstance_Click(object sender, RoutedEventArgs e)
    {
        _suppressDatabaseTableAutoLoad = true;
        try
        {
            await _viewModel.ConnectDatabaseInstanceAsync();
        }
        finally
        {
            _suppressDatabaseTableAutoLoad = false;
        }
    }

    private async void DatabaseGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;

        if (sender is not DataGrid dataGrid || string.IsNullOrWhiteSpace(_viewModel.SelectedDatabaseTable))
        {
            _viewModel.DatabaseTableStatus = "Nessuna riga DB selezionata.";
            return;
        }

        var selectedRows = dataGrid.SelectedItems
            .OfType<DataRowView>()
            .ToList();

        if (selectedRows.Count == 0)
        {
            _viewModel.DatabaseTableStatus = "Nessuna riga DB selezionata.";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Vuoi eliminare {selectedRows.Count} righe da '{_viewModel.SelectedDatabaseTable}'?",
            "Elimina righe DB",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.DeleteSelectedDatabaseRowsAsync(selectedRows);
    }

    private void EnsureUltraPreviewWindow()
    {
        if (_ultraPreviewWindow == null || !_ultraPreviewWindow.IsLoaded)
        {
            _ultraPreviewWindow = new UltraPreviewWindow
            {
                Owner = this,
                DataContext = _viewModel.UltraTab,
                Left = Left + Math.Max(40, Width - 1240),
                Top = Top + 40
            };
            _ultraPreviewWindow.Closed += (_, _) => _ultraPreviewWindow = null;
            _ultraPreviewWindow.Show();
            return;
        }

        if (!_ultraPreviewWindow.IsVisible)
        {
            _ultraPreviewWindow.Show();
        }

        _ultraPreviewWindow.Activate();
    }

    private void EnsureAdbPreviewWindow()
    {
        if (_adbPreviewWindow == null || !_adbPreviewWindow.IsLoaded)
        {
            _adbPreviewWindow = new AdbPreviewWindow
            {
                DataContext = _viewModel.AdbCaptureTab,
                Left = Left + Math.Max(30, Width - 1280),
                Top = Top + 20
            };
            _adbPreviewWindow.Closed += (_, _) => _adbPreviewWindow = null;
            _adbPreviewWindow.Show();
            return;
        }

        if (!_adbPreviewWindow.IsVisible)
        {
            _adbPreviewWindow.Show();
        }
    }

    private void CloseAdbPreviewWindow()
    {
        if (_adbPreviewWindow?.IsLoaded == true)
        {
            _adbPreviewWindow.Close();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_adbPreviewWindow?.IsLoaded == true)
        {
            _adbPreviewWindow.Close();
        }

        if (_ultraPreviewWindow?.IsLoaded == true)
        {
            _ultraPreviewWindow.Close();
        }
    }

    private static Task<bool> WaitForWindowCloseAsync(Window window, Func<bool> acceptedAccessor)
    {
        var completion = new TaskCompletionSource<bool>();

        void OnClosed(object? sender, EventArgs args)
        {
            window.Closed -= OnClosed;
            completion.TrySetResult(acceptedAccessor());
        }

        window.Closed += OnClosed;
        return completion.Task;
    }
}

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ProjectWorkspaceService _workspaceService;
    private readonly ProjectImageBlobService _projectImageBlobService;
    private readonly YoloDatasetBuilderService _yoloDatasetBuilderService;
    private bool _isApplyingProjectClasses;

    private string _databasePath;
    private string _databaseBackendName;
    public string DatabasePath
    {
        get => _databasePath;
        private set => SetField(ref _databasePath, value);
    }
    public string DatabaseBackendName
    {
        get => _databaseBackendName;
        private set => SetField(ref _databaseBackendName, value);
    }
    public ObservableCollection<string> Tables { get; } = new();
    public ObservableCollection<string> ProjectNames { get; } = new();
    public ObservableCollection<string> CropClasses { get; } = new()
    {
        "cerca",
        "back",
        "freccia",
        "invio",
        "microfono"
    };
    public ObservableCollection<string> YoloModelOptions { get; } = new()
    {
        "yolo11n.pt",
        "yolo11s.pt",
        "yolo11m.pt",
        "yolo11l.pt"
    };
    public ObservableCollection<YoloTrainingProfile> YoloTrainingProfiles { get; } = new();
    public ObservableCollection<ProjectClassOption> ProjectClassOptions { get; } = new();
    public AdbCaptureTabViewModel AdbCaptureTab { get; }
    public UltraTabViewModel UltraTab { get; }

    private string _statusText;
    private string _selectedCropClass;
    private string _selectedProjectName;
    private string _yoloEpochInfo;
    private string _yoloStatusText;
    private string _selectedYoloModel;
    private YoloTrainingProfile? _selectedYoloTrainingProfile;
    private readonly YoloTrainingService _yoloTrainingService;
    private DataView? _databaseRowsView;
    private string _databaseTableStatus;
    private string? _selectedDatabaseTable;
    private DataRowView? _selectedDatabaseRow;
    private bool _dbInstanceEnabled;
    private string _dbInstanceHost;
    private string _dbInstancePort;
    private string _dbInstanceDatabase;
    private string _dbInstanceUsername;
    private string _dbInstancePassword;
    private string _remoteAccessAddresses;
    private string _dbInstanceStatus;
    private bool _isDatabaseConnected;

    public MainWindowViewModel()
    {
        _workspaceService = new ProjectWorkspaceService();
        _projectImageBlobService = new ProjectImageBlobService();
        _yoloDatasetBuilderService = new YoloDatasetBuilderService();
        _yoloTrainingService = new YoloTrainingService();
        AdbCaptureTab = new AdbCaptureTabViewModel();
        UltraTab = new UltraTabViewModel();
        SharedDatabase.DeactivatePostgres();
        _databasePath = SharedDatabase.GetConnectionDisplayString();
        _databaseBackendName = "Backend attivo: non connesso";
        _statusText = string.Empty;
        _selectedCropClass = "cerca";
        _selectedYoloModel = "yolo11s.pt";
        _yoloEpochInfo = "Epoca corrente: -";
        _yoloStatusText = _yoloTrainingService.DetectRuntimeDescription();
        _databaseTableStatus = "Seleziona una tabella per vedere i dati.";
        _dbInstanceHost = GetDefaultDbInstanceHost();
        _dbInstancePort = "5432";
        _dbInstanceDatabase = "whatjolo";
        _dbInstanceUsername = "postgres";
        _dbInstancePassword = "postgres";
        _remoteAccessAddresses = BuildRemoteAccessAddresses();
        _dbInstanceStatus = $"Istanza DB inizializzata con i dati locali di {Environment.MachineName}.";
        _isDatabaseConnected = false;
        YoloTrainingProfiles.Add(new YoloTrainingProfile
        {
            Name = "Leggero",
            RecommendedModel = "yolo11n.pt",
            Epochs = 60,
            ImageSize = 640,
            Batch = 8
        });
        YoloTrainingProfiles.Add(new YoloTrainingProfile
        {
            Name = "Medio",
            RecommendedModel = "yolo11s.pt",
            Epochs = 100,
            ImageSize = 960,
            Batch = 6
        });
        YoloTrainingProfiles.Add(new YoloTrainingProfile
        {
            Name = "Forte",
            RecommendedModel = "yolo11m.pt",
            Epochs = 160,
            ImageSize = 1280,
            Batch = 4
        });
        _selectedYoloTrainingProfile = YoloTrainingProfiles.Skip(1).FirstOrDefault() ?? YoloTrainingProfiles.FirstOrDefault();
        if (_selectedYoloTrainingProfile is not null)
        {
            _selectedYoloModel = _selectedYoloTrainingProfile.RecommendedModel;
        }
        foreach (var cropClass in CropClasses)
        {
            var option = new ProjectClassOption(cropClass);
            option.PropertyChanged += ProjectClassOption_PropertyChanged;
            ProjectClassOptions.Add(option);
        }

        var existingProjects = _workspaceService.GetProjectNames();
        foreach (var projectName in existingProjects)
        {
            ProjectNames.Add(projectName);
        }

        _selectedProjectName = ProjectNames.FirstOrDefault() ?? _workspaceService.EnsureProject("Default");
        if (ProjectNames.Count == 0)
        {
            ProjectNames.Add(_selectedProjectName);
        }

        LoadDatabaseInstanceSettings();
        _statusText = "PostgreSQL remoto non connesso. Premi Connetti.";
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SelectedCropClass
    {
        get => _selectedCropClass;
        set => SetField(ref _selectedCropClass, value);
    }

    public string SelectedProjectName
    {
        get => _selectedProjectName;
        set => SetField(ref _selectedProjectName, value);
    }

    public string YoloEpochInfo
    {
        get => _yoloEpochInfo;
        private set => SetField(ref _yoloEpochInfo, value);
    }

    public string YoloStatusText
    {
        get => _yoloStatusText;
        private set => SetField(ref _yoloStatusText, value);
    }

    public string SelectedYoloModel
    {
        get => _selectedYoloModel;
        set
        {
            if (SetField(ref _selectedYoloModel, value))
            {
                OnPropertyChanged(nameof(SelectedYoloModelInfo));
            }
        }
    }

    public string SelectedYoloModelInfo
    {
        get
        {
            return SelectedYoloModel switch
            {
                "yolo11n.pt" => "Nano: il piu leggero e veloce. Ideale per prove rapide e GPU piccole.",
                "yolo11s.pt" => "Small: buon equilibrio tra velocita e accuratezza. Scelta consigliata per uso generale.",
                "yolo11m.pt" => "Medium: piu accurato ma piu lento e pesante. Utile se il dataset cresce.",
                "yolo11l.pt" => "Large: modello pesante, richiede piu VRAM e tempo. Da usare solo se serve massima qualita.",
                _ => "Seleziona un modello YOLO per vedere le informazioni."
            };
        }
    }

    public YoloTrainingProfile? SelectedYoloTrainingProfile
    {
        get => _selectedYoloTrainingProfile;
        set
        {
            if (!SetField(ref _selectedYoloTrainingProfile, value))
            {
                return;
            }

            if (value is not null && !string.Equals(SelectedYoloModel, value.RecommendedModel, StringComparison.Ordinal))
            {
                SelectedYoloModel = value.RecommendedModel;
            }

            OnPropertyChanged(nameof(SelectedYoloTrainingProfileInfo));
        }
    }

    public string SelectedYoloTrainingProfileInfo
    {
        get
        {
            var profile = SelectedYoloTrainingProfile;
            if (profile is null)
            {
                return "Seleziona un profilo YOLO per usare un preset di training.";
            }

            return $"{profile.Name}: {profile.Epochs} epoche | imgsz {profile.ImageSize} | batch {profile.Batch} | modello consigliato {profile.RecommendedModel}.";
        }
    }

    public string ActiveProjectClassesSummary
    {
        get
        {
            var selectedNames = ProjectClassOptions
                .Where(option => option.IsSelected)
                .Select(option => option.Name)
                .ToList();

            return selectedNames.Count switch
            {
                0 => "Nessuna classe",
                1 => selectedNames[0],
                _ => string.Join(", ", selectedNames)
            };
        }
    }

    public string? SelectedDatabaseTable
    {
        get => _selectedDatabaseTable;
        set => SetField(ref _selectedDatabaseTable, value);
    }

    public DataView? DatabaseRowsView
    {
        get => _databaseRowsView;
        private set => SetField(ref _databaseRowsView, value);
    }

    public string DatabaseTableStatus
    {
        get => _databaseTableStatus;
        set => SetField(ref _databaseTableStatus, value);
    }

    public DataRowView? SelectedDatabaseRow
    {
        get => _selectedDatabaseRow;
        set => SetField(ref _selectedDatabaseRow, value);
    }

    public bool DbInstanceEnabled
    {
        get => _dbInstanceEnabled;
        set
        {
            if (SetField(ref _dbInstanceEnabled, value))
            {
                OnPropertyChanged(nameof(DbConnectionPreview));
            }
        }
    }

    public string DbInstanceHost
    {
        get => _dbInstanceHost;
        set
        {
            if (SetField(ref _dbInstanceHost, value))
            {
                OnPropertyChanged(nameof(DbConnectionPreview));
            }
        }
    }

    public string DbInstancePort
    {
        get => _dbInstancePort;
        set
        {
            if (SetField(ref _dbInstancePort, value))
            {
                OnPropertyChanged(nameof(DbConnectionPreview));
            }
        }
    }

    public string DbInstanceDatabase
    {
        get => _dbInstanceDatabase;
        set
        {
            if (SetField(ref _dbInstanceDatabase, value))
            {
                OnPropertyChanged(nameof(DbConnectionPreview));
            }
        }
    }

    public string DbInstanceUsername
    {
        get => _dbInstanceUsername;
        set
        {
            if (SetField(ref _dbInstanceUsername, value))
            {
                OnPropertyChanged(nameof(DbConnectionPreview));
            }
        }
    }

    public string DbInstancePassword
    {
        get => _dbInstancePassword;
        set
        {
            if (SetField(ref _dbInstancePassword, value))
            {
                OnPropertyChanged(nameof(DbConnectionPreview));
            }
        }
    }

    public string RemoteAccessAddresses
    {
        get => _remoteAccessAddresses;
        private set => SetField(ref _remoteAccessAddresses, value);
    }

    public string DbConnectionPreview =>
        DbInstanceEnabled
            ? $"Host={DbInstanceHost};Port={(int.TryParse(DbInstancePort, out var port) ? port : 5432)};Database={DbInstanceDatabase};Username={DbInstanceUsername};Password={DbInstancePassword}"
            : "PostgreSQL remoto disabilitato.";

    public string DbInstanceStatus
    {
        get => _dbInstanceStatus;
        set => SetField(ref _dbInstanceStatus, value);
    }

    public bool IsDatabaseConnected
    {
        get => _isDatabaseConnected;
        private set => SetField(ref _isDatabaseConnected, value);
    }

    public async Task InitializeProjectWorkspaceAsync()
    {
        if (ProjectNames.Count == 0)
        {
            RefreshProjects();
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectName))
        {
            SelectedProjectName = ProjectNames.FirstOrDefault() ?? _workspaceService.EnsureProject("Default");
        }

        if (!ProjectNames.Contains(SelectedProjectName))
        {
            ProjectNames.Add(SelectedProjectName);
        }

        if (IsDatabaseConnected)
        {
            await ApplySelectedProjectAsync();
            return;
        }

        await AdbCaptureTab.SetCurrentProjectAsync(SelectedProjectName, SelectedCropClass);
        UltraTab.SetCurrentProject(SelectedProjectName);
        StatusText = $"Progetto corrente: {SelectedProjectName} | PostgreSQL non connesso";
    }

    public void RefreshProjects()
    {
        var previousSelection = SelectedProjectName;
        ProjectNames.Clear();
        foreach (var projectName in _workspaceService.GetProjectNames())
        {
            ProjectNames.Add(projectName);
        }

        if (ProjectNames.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(previousSelection) && ProjectNames.Contains(previousSelection))
        {
            SelectedProjectName = previousSelection;
            return;
        }

        SelectedProjectName = ProjectNames.First();
    }

    public async Task CreateOrSelectProjectAsync(string projectName)
    {
        var ensuredName = _workspaceService.EnsureProject(projectName);
        if (!ProjectNames.Contains(ensuredName))
        {
            ProjectNames.Add(ensuredName);
        }

        SelectedProjectName = ensuredName;
        await ApplySelectedProjectAsync();
    }

    public async Task ApplySelectedProjectAsync()
    {
        var ensuredName = _workspaceService.EnsureProject(SelectedProjectName);
        SelectedProjectName = ensuredName;
        if (!ProjectNames.Contains(ensuredName))
        {
            ProjectNames.Add(ensuredName);
        }

        if (IsDatabaseConnected)
        {
            ApplyProjectClasses(ensuredName);
        }
        await AdbCaptureTab.SetCurrentProjectAsync(ensuredName, SelectedCropClass);
        UltraTab.SetCurrentProject(ensuredName);
        StatusText = IsDatabaseConnected
            ? $"Tabelle lette: {Tables.Count} | Progetto corrente: {ensuredName} | Classi: {ActiveProjectClassesSummary}"
            : $"Progetto corrente: {ensuredName} | PostgreSQL non connesso";
    }

    public async Task AlignProjectImageBlobsAsync()
    {
        if (!IsDatabaseConnected)
        {
            return;
        }

        var imageCount = await _projectImageBlobService.SyncProjectAsync(SelectedProjectName);
        StatusText = $"Tabelle lette: {Tables.Count} | Progetto corrente: {SelectedProjectName} | Classi: {ActiveProjectClassesSummary} | Blob immagini allineati: {imageCount}";
    }

    public string GetCurrentProjectPath()
    {
        return _workspaceService.GetProjectPath(SelectedProjectName);
    }

    internal Task<ProjectRestoreResult> RestoreCurrentProjectFromDatabaseAsync()
    {
        return _projectImageBlobService.RestoreProjectAsync(SelectedProjectName);
    }

    public string? FindLatestYoloRunPath()
    {
        return _workspaceService.FindLatestYoloRunPath(SelectedProjectName);
    }

    public string? ResetLatestYoloRun()
    {
        return _workspaceService.ResetLatestYoloRun(SelectedProjectName);
    }

    public async Task<string> CreateDatasetStructureAsync()
    {
        var activeClasses = ProjectClassOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Name)
            .ToArray();

        var result = await _yoloDatasetBuilderService.BuildAsync(SelectedProjectName, activeClasses);
        await AlignProjectImageBlobsAsync();
        StatusText = $"Tabelle lette: {Tables.Count} | Progetto corrente: {SelectedProjectName} | Classi: {ActiveProjectClassesSummary} | Dataset: {result.ImageCount} immagini / {result.ClassCount} classi";
        return result.DatasetFolder;
    }

    public async Task LoadDatabaseTableAsync(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            DatabaseRowsView = null;
            DatabaseTableStatus = "Seleziona una tabella per vedere i dati.";
            return;
        }

        var dataTable = new DataTable(tableName);
        var filterMode = "nessun filtro progetto";

        await using var connection = SharedDatabase.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        switch (tableName)
        {
            case "ProjectCropLink":
                command.CommandText =
                    """
                    SELECT pcl.Id, pcl.ProjectName, pcl.LabelName, pcl.IsVariation, pcl.CropAssetId, ca.CropImagePath, pcl.CreatedAtUtc, pcl.UpdatedAtUtc
                    FROM ProjectCropLink pcl
                    INNER JOIN CropAsset ca ON ca.Id = pcl.CropAssetId
                    WHERE pcl.ProjectName = @ProjectName
                    ORDER BY pcl.UpdatedAtUtc DESC;
                    """;
                AddParameter(command, "@ProjectName", SelectedProjectName);
                filterMode = $"filtrato per progetto '{SelectedProjectName}'";
                break;

            case "CropAsset":
                command.CommandText =
                    """
                    SELECT
                        pcl.ProjectName,
                        pcl.LabelName,
                        pcl.IsVariation,
                        ca.Id,
                        ca.SourceImagePath,
                        ca.CropImagePath,
                        ca.CropHash,
                        ca.X,
                        ca.Y,
                        ca.Width,
                        ca.Height,
                        ca.CreatedAtUtc,
                        ca.UpdatedAtUtc
                    FROM CropAsset ca
                    INNER JOIN ProjectCropLink pcl ON pcl.CropAssetId = ca.Id
                    WHERE pcl.ProjectName = @ProjectName
                    ORDER BY pcl.UpdatedAtUtc DESC, ca.Id DESC;
                    """;
                AddParameter(command, "@ProjectName", SelectedProjectName);
                filterMode = $"filtrato per progetto '{SelectedProjectName}'";
                break;

            case "ProjectActiveClass":
                command.CommandText =
                    """
                    SELECT Id, ProjectName, ClassName, CreatedAtUtc, UpdatedAtUtc
                    FROM ProjectActiveClass
                    WHERE ProjectName = @ProjectName
                    ORDER BY ClassName;
                    """;
                AddParameter(command, "@ProjectName", SelectedProjectName);
                filterMode = $"filtrato per progetto '{SelectedProjectName}'";
                break;

            case "ProjectInfo":
                command.CommandText =
                    """
                    SELECT Id, ProjectName, ProjectRootPath, MachineName, CreatedAtUtc, UpdatedAtUtc
                    FROM ProjectInfo
                    WHERE ProjectName = @ProjectName
                    ORDER BY UpdatedAtUtc DESC;
                    """;
                AddParameter(command, "@ProjectName", SelectedProjectName);
                filterMode = $"filtrato per progetto '{SelectedProjectName}'";
                break;

            case "ProjectImageBlob":
            case "projectimageblob":
                command.CommandText = SharedDatabase.IsPostgresConfigured()
                    ? """
                      SELECT Id, ProjectName, ImagePath, ImageKind, ContentHash, ByteLength, octet_length(CompressedBytes) AS CompressedLength, CreatedAtUtc, UpdatedAtUtc
                      FROM ProjectImageBlob
                      WHERE ProjectName = @ProjectName
                      ORDER BY UpdatedAtUtc DESC, Id DESC;
                      """
                    : """
                      SELECT Id, ProjectName, ImagePath, ImageKind, ContentHash, ByteLength, length(CompressedBytes) AS CompressedLength, CreatedAtUtc, UpdatedAtUtc
                      FROM ProjectImageBlob
                      WHERE ProjectName = @ProjectName
                      ORDER BY UpdatedAtUtc DESC, Id DESC;
                      """;
                AddParameter(command, "@ProjectName", SelectedProjectName);
                filterMode = $"filtrato per progetto '{SelectedProjectName}'";
                break;

            case "Contacts":
                command.CommandText =
                    """
                    SELECT *
                    FROM Contacts
                    ORDER BY UpdatedAtUtc DESC, Id DESC;
                    """;
                break;

            case "IncomingMessages":
                command.CommandText =
                    """
                    SELECT *
                    FROM IncomingMessages
                    ORDER BY MessageTimestampUtc DESC, Id DESC;
                    """;
                break;

            case "MessageNotifications":
                command.CommandText =
                    """
                    SELECT *
                    FROM MessageNotifications
                    ORDER BY CreatedAtUtc DESC, Id DESC;
                    """;
                break;

            default:
                DatabaseRowsView = null;
                DatabaseTableStatus = $"Tabella non gestita: {tableName}";
                return;
        }

        await using var reader = await command.ExecuteReaderAsync();
        for (var index = 0; index < reader.FieldCount; index++)
        {
            dataTable.Columns.Add(reader.GetName(index), typeof(string));
        }

        while (await reader.ReadAsync())
        {
            var row = dataTable.NewRow();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index)) ?? string.Empty;
            }

            dataTable.Rows.Add(row);
        }

        DatabaseRowsView = dataTable.DefaultView;
        DatabaseTableStatus = $"Righe: {dataTable.Rows.Count} | {filterMode}";
    }

    public async Task DeleteSelectedDatabaseRowsAsync(IReadOnlyList<DataRowView> rowViews)
    {
        if (string.IsNullOrWhiteSpace(SelectedDatabaseTable) || rowViews.Count == 0)
        {
            return;
        }

        var deletedCount = 0;

        foreach (var rowView in rowViews)
        {
            switch (SelectedDatabaseTable)
            {
                case "ProjectCropLink":
                case "CropAsset":
                {
                    var projectName = GetRowValue(rowView, "ProjectName");
                    var labelName = GetRowValue(rowView, "LabelName");
                    var cropImagePath = ResolveDbPath(GetRowValue(rowView, "CropImagePath"));

                    if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(labelName) || string.IsNullOrWhiteSpace(cropImagePath))
                    {
                        continue;
                    }

                    var cropDbService = new AnnotationCropDbService();
                    var deleted = await cropDbService.DeleteCropAsync(projectName, labelName, cropImagePath);
                    if (deleted)
                    {
                        deletedCount++;
                    }

                    break;
                }

                case "ProjectActiveClass":
                {
                    var projectName = GetRowValue(rowView, "ProjectName");
                    var className = GetRowValue(rowView, "ClassName");

                    await using var connection = SharedDatabase.CreateConnection();
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        DELETE FROM ProjectActiveClass
                        WHERE ProjectName = @ProjectName
                          AND ClassName = @ClassName;
                        """;
                    AddParameter(command, "@ProjectName", projectName);
                    AddParameter(command, "@ClassName", className);
                    deletedCount += await command.ExecuteNonQueryAsync();
                    break;
                }

                case "ProjectInfo":
                {
                    var projectName = GetRowValue(rowView, "ProjectName");

                    await using var connection = SharedDatabase.CreateConnection();
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        DELETE FROM ProjectInfo
                        WHERE ProjectName = @ProjectName;
                        """;
                    AddParameter(command, "@ProjectName", projectName);
                    deletedCount += await command.ExecuteNonQueryAsync();
                    break;
                }

                case "ProjectImageBlob":
                case "projectimageblob":
                {
                    var idText = GetRowValue(rowView, "Id");
                    if (!long.TryParse(idText, out var id))
                    {
                        continue;
                    }

                    await using var connection = SharedDatabase.CreateConnection();
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM ProjectImageBlob WHERE Id = @Id;";
                    AddParameter(command, "@Id", id);
                    deletedCount += await command.ExecuteNonQueryAsync();
                    break;
                }

                case "Contacts":
                case "IncomingMessages":
                case "MessageNotifications":
                {
                    var idText = GetRowValue(rowView, "Id");
                    if (!long.TryParse(idText, out var id))
                    {
                        continue;
                    }

                    await using var connection = SharedDatabase.CreateConnection();
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"DELETE FROM {SelectedDatabaseTable} WHERE Id = @Id;";
                    AddParameter(command, "@Id", id);
                    deletedCount += await command.ExecuteNonQueryAsync();
                    break;
                }

                default:
                    DatabaseTableStatus = $"Delete non supportato per {SelectedDatabaseTable}.";
                    return;
            }
        }

        SelectedDatabaseRow = null;
        await LoadDatabaseTableAsync(SelectedDatabaseTable);
        DatabaseTableStatus = deletedCount > 0
            ? $"Righe eliminate da {SelectedDatabaseTable}: {deletedCount}."
            : $"Nessuna riga eliminata da {SelectedDatabaseTable}.";
    }

    private static string GetRowValue(DataRowView rowView, string columnName)
    {
        return rowView.Row.Table.Columns.Contains(columnName)
            ? Convert.ToString(rowView.Row[columnName]) ?? string.Empty
            : string.Empty;
    }

    private static string ResolveDbPath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || Path.IsPathRooted(storedPath))
        {
            return storedPath;
        }

        var root = SharedDatabase.GetProjectDirectoryPath();
        return Path.GetFullPath(Path.Combine(root, storedPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void SaveDatabaseInstanceSettings()
    {
        SharedDatabase.SavePostgresSettings(new PostgresConnectionSettings
        {
            Enabled = DbInstanceEnabled,
            Host = DbInstanceHost.Trim(),
            Port = int.TryParse(DbInstancePort, out var port) ? port : 5432,
            Database = DbInstanceDatabase.Trim(),
            Username = DbInstanceUsername.Trim(),
            Password = DbInstancePassword
        });

        if (!DbInstanceEnabled)
        {
            SharedDatabase.DeactivatePostgres();
            IsDatabaseConnected = false;
            DbInstanceStatus = "Configurazione PostgreSQL disabilitata. Premi Connetti dopo averla riattivata.";
            return;
        }

        DbInstanceStatus = $"Configurazione PostgreSQL salvata per {DbInstanceHost}:{DbInstancePort}/{DbInstanceDatabase}. Premi Connetti.";
    }

    public async Task TestDatabaseInstanceConnectionAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = DbInstanceHost.Trim(),
                Port = int.TryParse(DbInstancePort, out var port) ? port : 5432,
                Database = DbInstanceDatabase.Trim(),
                Username = DbInstanceUsername.Trim(),
                Password = DbInstancePassword
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version();";
            var version = Convert.ToString(await command.ExecuteScalarAsync()) ?? "Connessione OK";
            DbInstanceStatus = $"Connessione OK | {version}";
        }
        catch (Exception ex)
        {
            DbInstanceStatus = $"Test connessione fallito: {ex.Message}";
        }
    }

    public async Task ConnectDatabaseInstanceAsync()
    {
        try
        {
            DbInstanceStatus = "Salvataggio configurazione PostgreSQL...";
            await Task.Yield();
            SaveDatabaseInstanceSettings();

            DbInstanceStatus = $"Attivazione connessione PostgreSQL verso {DbInstanceHost}:{DbInstancePort}/{DbInstanceDatabase}...";
            await Task.Yield();
            SharedDatabase.ActivateConfiguredPostgres();

            DbInstanceStatus = "Inizializzazione schema e verifica connessione in corso...";
            await Task.Yield();
            await ReloadDatabaseConnectionAsync();

            IsDatabaseConnected = true;
            RefreshProjects();
            if (ProjectNames.Count > 0 && !ProjectNames.Contains(SelectedProjectName))
            {
                SelectedProjectName = ProjectNames.First();
            }

            DbInstanceStatus = $"Connessione riuscita. Applicazione progetto {SelectedProjectName}...";
            await Task.Yield();
            await ApplySelectedProjectAsync();

            DbInstanceStatus = $"Allineamento blob immagini del progetto {SelectedProjectName}...";
            await Task.Yield();
            await AlignProjectImageBlobsAsync();

            DbInstanceStatus = $"Connesso a PostgreSQL su {DbInstanceHost}:{DbInstancePort}/{DbInstanceDatabase}.";
        }
        catch (Exception ex)
        {
            SharedDatabase.DeactivatePostgres();
            IsDatabaseConnected = false;
            DbInstanceStatus = $"Connessione PostgreSQL fallita: {ex.Message}";
        }
    }

    public async Task ReloadDatabaseConnectionAsync()
    {
        DbInstanceStatus = "Preparazione connessione PostgreSQL...";
        await Task.Yield();

        IProgress<string> progress = new Progress<string>(message => DbInstanceStatus = message);
        var tableNames = await Task.Run(() =>
        {
            progress.Report("Reset inizializzazione backend PostgreSQL...");
            SharedDatabase.ResetInitialization();
            SharedDatabase.EnsureDatabaseReady(message => progress.Report(message));

            progress.Report("Apertura connessione PostgreSQL...");
            var names = new List<string>();
            using var connection = SharedDatabase.CreateConnection();
            connection.Open();

            progress.Report("Lettura elenco tabelle PostgreSQL...");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            progress.Report($"Tabelle PostgreSQL lette: {names.Count}.");
            return names;
        });

        DatabasePath = SharedDatabase.GetConnectionDisplayString();
        DatabaseBackendName = SharedDatabase.IsPostgresConfigured() ? "Backend attivo: PostgreSQL" : "Backend attivo: non connesso";
        LoadDatabaseInstanceSettings();
        IsDatabaseConnected = true;

        Tables.Clear();
        foreach (var tableName in tableNames)
        {
            Tables.Add(tableName);
        }

        DatabaseRowsView = null;
        DatabaseTableStatus = string.IsNullOrWhiteSpace(SelectedDatabaseTable)
            ? $"Tabelle disponibili: {Tables.Count}. Apri la tab Database per caricare i dati."
            : $"Tabelle disponibili: {Tables.Count}. Seleziona '{SelectedDatabaseTable}' nella tab Database per caricare i dati.";

        StatusText = $"Tabelle lette: {Tables.Count} | {DatabaseBackendName}";
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private void LoadDatabaseInstanceSettings()
    {
        var settings = SharedDatabase.LoadPostgresSettings();
        DbInstanceEnabled = settings.Enabled;
        DbInstanceHost = string.IsNullOrWhiteSpace(settings.Host) ? GetDefaultDbInstanceHost() : settings.Host;
        DbInstancePort = (settings.Port <= 0 ? 5432 : settings.Port).ToString();
        DbInstanceDatabase = string.IsNullOrWhiteSpace(settings.Database) ? "whatjolo" : settings.Database;
        DbInstanceUsername = string.IsNullOrWhiteSpace(settings.Username) ? "postgres" : settings.Username;
        DbInstancePassword = string.IsNullOrWhiteSpace(settings.Password) ? "postgres" : settings.Password;
        RemoteAccessAddresses = BuildRemoteAccessAddresses();
        DatabasePath = SharedDatabase.GetConnectionDisplayString();
        DatabaseBackendName = SharedDatabase.IsPostgresConfigured() ? "Backend attivo: PostgreSQL" : "Backend attivo: non connesso";
        if (SharedDatabase.IsPostgresConfigured())
        {
            DbInstanceStatus = $"Connesso a PostgreSQL su {DbInstanceHost}:{DbInstancePort}/{DbInstanceDatabase}.";
        }
        else if (string.IsNullOrWhiteSpace(settings.Host) && string.IsNullOrWhiteSpace(settings.Password))
        {
            DbInstanceStatus = $"Istanza DB inizializzata con i dati locali di {Environment.MachineName}.";
        }
        else
        {
            DbInstanceStatus = $"Configurazione pronta per {DbInstanceHost}:{DbInstancePort}/{DbInstanceDatabase}. Premi Connetti.";
        }
        OnPropertyChanged(nameof(DbConnectionPreview));
    }

    private static string BuildRemoteAccessAddresses()
    {
        var lines = new List<string> { $"MachineName: {Environment.MachineName}" };

        try
        {
            var ipAddresses = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            lines.Add(ipAddresses.Length > 0
                ? "IPv4: " + string.Join(", ", ipAddresses)
                : "IPv4: non disponibile");
        }
        catch
        {
            lines.Add("IPv4: non disponibile");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetDefaultDbInstanceHost()
    {
        try
        {
            var firstIpv4 = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

            if (firstIpv4 != null)
            {
                return firstIpv4.ToString();
            }
        }
        catch
        {
        }

        return Environment.MachineName;
    }

    public async Task TrainYoloAsync(
        Action<YoloTrainingProgress>? onProgress = null,
        Action<string>? onCompleted = null,
        Action<string>? onFailed = null)
    {
        var datasetPath = _workspaceService.GetYoloDatasetPath(SelectedProjectName);
        var dataYamlPath = Path.Combine(datasetPath, "data.yaml");
        if (!File.Exists(dataYamlPath))
        {
            AdbCaptureTab.SetStatusMessage($"[{SelectedProjectName}] Dataset YOLO non trovato. Crea prima il dataset.");
            YoloStatusText = "Dataset non pronto";
            onFailed?.Invoke("Dataset non pronto.");
            return;
        }

        YoloEpochInfo = "Epoca corrente: avvio...";
        var trainingProfile = SelectedYoloTrainingProfile ?? new YoloTrainingProfile
        {
            Name = "Medio",
            RecommendedModel = SelectedYoloModel,
            Epochs = 100,
            ImageSize = 960,
            Batch = 6
        };
        YoloStatusText = $"Training YOLO su {SelectedProjectName} | modello {SelectedYoloModel} | profilo {trainingProfile.Name}";
        AdbCaptureTab.SetStatusMessage($"[{SelectedProjectName}] Training YOLO avviato con modello {SelectedYoloModel} | profilo {trainingProfile.Name}...");

        var progress = new Progress<YoloTrainingProgress>(progressUpdate =>
        {
            if (progressUpdate.CurrentEpoch.HasValue && progressUpdate.TotalEpochs.HasValue)
            {
                YoloEpochInfo = $"Epoca corrente: {progressUpdate.CurrentEpoch.Value}/{progressUpdate.TotalEpochs.Value}";
            }

            YoloStatusText = $"[{progressUpdate.Source}] {progressUpdate.RawLine}";
            AdbCaptureTab.SetStatusMessage($"[{SelectedProjectName}] YOLO/{progressUpdate.Source}: {progressUpdate.RawLine}");
            onProgress?.Invoke(progressUpdate);
        });

        try
        {
            var result = await _yoloTrainingService.TrainAsync(
                SelectedProjectName,
                dataYamlPath,
                _workspaceService.GetYoloProjectPath(SelectedProjectName),
                SelectedYoloModel,
                trainingProfile.Epochs,
                trainingProfile.ImageSize,
                trainingProfile.Batch,
                progress);

            if (result.ExitCode != 0)
            {
                YoloStatusText = $"Training YOLO fallito | Log: {result.TrainLogPath}";
                AdbCaptureTab.SetStatusMessage($"[{SelectedProjectName}] Training YOLO fallito. Log: {result.TrainLogPath}");
                onFailed?.Invoke($"Log: {result.TrainLogPath}");
                return;
            }

            YoloStatusText = string.IsNullOrWhiteSpace(result.OnnxModelPath)
                ? $"Training completato: {result.RunFolder}"
                : $"Training completato: {result.OnnxModelPath}";
            AdbCaptureTab.SetStatusMessage(
                string.IsNullOrWhiteSpace(result.OnnxModelPath)
                    ? $"[{SelectedProjectName}] Training YOLO completato: {result.RunFolder} | Log: {result.TrainLogPath}"
                    : $"[{SelectedProjectName}] Training YOLO completato: {result.OnnxModelPath} | Log: {result.TrainLogPath}");
            onCompleted?.Invoke(string.IsNullOrWhiteSpace(result.OnnxModelPath)
                ? $"Run: {result.RunFolder}"
                : $"ONNX: {result.OnnxModelPath}");
        }
        catch (Exception ex)
        {
            YoloStatusText = ex.Message;
            AdbCaptureTab.SetStatusMessage($"[{SelectedProjectName}] Errore training YOLO: {ex.Message}");
            onFailed?.Invoke(ex.Message);
        }
    }

    private void ApplyProjectClasses(string projectName)
    {
        _isApplyingProjectClasses = true;
        try
        {
            var activeClasses = _workspaceService.LoadActiveClasses(projectName, CropClasses);
            var activeSet = new HashSet<string>(activeClasses, StringComparer.OrdinalIgnoreCase);

            foreach (var option in ProjectClassOptions)
            {
                option.IsSelected = activeSet.Contains(option.Name);
            }
        }
        finally
        {
            _isApplyingProjectClasses = false;
        }

        OnPropertyChanged(nameof(ActiveProjectClassesSummary));
    }

    private void ProjectClassOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectClassOption.IsSelected))
        {
            return;
        }

        if (_isApplyingProjectClasses)
        {
            return;
        }

        if (ProjectClassOptions.All(option => !option.IsSelected))
        {
            _isApplyingProjectClasses = true;
            try
            {
                var fallbackOption = sender as ProjectClassOption ?? ProjectClassOptions.First();
                fallbackOption.IsSelected = true;
            }
            finally
            {
                _isApplyingProjectClasses = false;
            }
        }

        _workspaceService.SaveActiveClasses(
            SelectedProjectName,
            ProjectClassOptions.Where(option => option.IsSelected).Select(option => option.Name));

        OnPropertyChanged(nameof(ActiveProjectClassesSummary));
        StatusText = $"Tabelle lette: {Tables.Count} | Progetto corrente: {SelectedProjectName} | Classi: {ActiveProjectClassesSummary}";
    }
}
