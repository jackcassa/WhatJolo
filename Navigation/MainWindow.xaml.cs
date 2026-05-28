using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Navigation;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void RefreshProjects_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshProjectsAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartSendLoopAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.StopSendLoop();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainWindowViewModel.LastCapturePreview), StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AdjustWindowToPreview));
    }

    private void AdjustWindowToPreview()
    {
        if (_viewModel.LastCapturePreview is not BitmapSource bitmap)
        {
            return;
        }

        UpdateLayout();

        var workArea = SystemParameters.WorkArea;
        var chromeWidth = Math.Max(0, ActualWidth - RootGrid.ActualWidth - RootGrid.Margin.Left - RootGrid.Margin.Right);
        var chromeHeight = Math.Max(0, ActualHeight - RootGrid.ActualHeight - RootGrid.Margin.Top - RootGrid.Margin.Bottom);
        var fixedHeight =
            MeasureBlockHeight(HeaderBorder) +
            MeasureBlockHeight(ConnectionBorder) +
            MeasureBlockHeight(ProjectBorder) +
            MeasureBlockHeight(StatusBorder) +
            RootGrid.Margin.Top +
            RootGrid.Margin.Bottom;

        var maxPreviewHeight = Math.Max(220, workArea.Height * 0.62);
        var maxPreviewWidth = Math.Max(MinWidth, workArea.Width * 0.70);

        var aspectRatio = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        var previewHeight = Math.Min(maxPreviewHeight, bitmap.PixelHeight);
        var previewWidth = previewHeight * aspectRatio;

        var horizontalPadding =
            PreviewBorder.Padding.Left +
            PreviewBorder.Padding.Right +
            PreviewBorder.Margin.Left +
            PreviewBorder.Margin.Right +
            RootGrid.Margin.Left +
            RootGrid.Margin.Right +
            40;

        if (previewWidth + horizontalPadding > maxPreviewWidth)
        {
            previewWidth = maxPreviewWidth - horizontalPadding;
            previewHeight = previewWidth / aspectRatio;
        }

        previewWidth = Math.Max(280, previewWidth);
        previewHeight = Math.Max(220, previewHeight);

        Width = Math.Max(MinWidth, Math.Min(workArea.Width * 0.85, previewWidth + horizontalPadding + chromeWidth));
        Height = Math.Max(MinHeight, Math.Min(workArea.Height * 0.90, fixedHeight + previewHeight + chromeHeight + 28));
    }

    private static double MeasureBlockHeight(FrameworkElement element)
    {
        return element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
    }
}
