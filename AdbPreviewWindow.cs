using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WhatJolo;

internal sealed class AdbPreviewWindow : Window
{
    private readonly ScrollViewer _scrollViewer;
    private readonly Grid _previewHost;
    private readonly Image _previewImage;
    private readonly Canvas _selectionCanvas;
    private readonly Rectangle _selectionRectangle;
    private readonly ScaleTransform _scaleTransform;
    private readonly PreviewSelectionController _selectionController;
    private double _zoom = 1.0;

    public AdbPreviewWindow()
    {
        Title = "WhatJolo ADB Preview";
        Width = 1200;
        Height = 900;
        MinWidth = 720;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));

        var root = new Grid
        {
            Margin = new Thickness(12)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerBorder = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24))
        };
        Grid.SetRow(headerBorder, 0);

        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = "Preview ADB",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0))
        });

        var capturePathText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            TextWrapping = TextWrapping.Wrap
        };
        capturePathText.SetBinding(TextBlock.TextProperty, new Binding("LastCapturePath"));
        headerStack.Children.Add(capturePathText);
        headerBorder.Child = headerStack;
        root.Children.Add(headerBorder);

        var previewBorder = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 16))
        };
        Grid.SetRow(previewBorder, 1);

        _previewHost = new Grid();
        _previewHost.SizeChanged += PreviewHost_SizeChanged;

        _scaleTransform = new ScaleTransform(1.0, 1.0);
        _previewHost.RenderTransform = _scaleTransform;
        _previewHost.RenderTransformOrigin = new Point(0, 0);

        _previewImage = new Image
        {
            Stretch = Stretch.None
        };
        _previewImage.SetBinding(Image.SourceProperty, new Binding("LatestScreenshotPreview"));
        _previewImage.PreviewMouseWheel += PreviewSurface_PreviewMouseWheel;
        _previewImage.MouseLeftButtonDown += PreviewImage_MouseLeftButtonDown;
        _previewHost.Children.Add(_previewImage);

        _selectionCanvas = new Canvas
        {
            Background = Brushes.Transparent
        };
        _selectionCanvas.PreviewMouseWheel += PreviewSurface_PreviewMouseWheel;
        _selectionCanvas.MouseLeftButtonDown += SelectionCanvas_MouseLeftButtonDown;
        _selectionCanvas.MouseMove += SelectionCanvas_MouseMove;
        _selectionCanvas.MouseLeftButtonUp += SelectionCanvas_MouseLeftButtonUp;
        _previewHost.Children.Add(_selectionCanvas);

        _selectionRectangle = new Rectangle
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            Fill = new SolidColorBrush(Color.FromArgb(48, 255, 212, 0))
        };
        _selectionCanvas.Children.Add(_selectionRectangle);

        _selectionController = new PreviewSelectionController(
            this,
            _previewImage,
            _selectionCanvas,
            _selectionRectangle,
            message =>
            {
                if (DataContext is AdbCaptureTabViewModel vm)
                {
                    vm.SetStatusMessage(message);
                }
            });

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Content = _previewHost,
            Padding = new Thickness(12)
        };
        previewBorder.Child = _scrollViewer;
        root.Children.Add(previewBorder);

        var statusBorder = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24))
        };
        Grid.SetRow(statusBorder, 2);

        var statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0)),
            TextWrapping = TextWrapping.Wrap
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding("AdbStatusText"));
        statusBorder.Child = statusText;
        root.Children.Add(statusBorder);

        MouseWheel += PreviewSurface_PreviewMouseWheel;
        Content = root;
    }

    public Int32Rect? SelectedPixelRect => _selectionController.SelectedPixelRect;

    public void ClearSelection()
    {
        _selectionController.ClearSelection();
    }

    private void PreviewSurface_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            if (e.Delta > 0)
            {
                _scrollViewer.LineUp();
            }
            else
            {
                _scrollViewer.LineDown();
            }

            e.Handled = true;
            return;
        }

        var previousZoom = _zoom;
        _zoom = e.Delta > 0 ? Math.Min(8.0, _zoom * 1.15) : Math.Max(0.2, _zoom / 1.15);
        if (Math.Abs(_zoom - previousZoom) < 0.0001)
        {
            return;
        }

        var mousePosition = e.GetPosition(_scrollViewer);
        var relativeX = (_scrollViewer.HorizontalOffset + mousePosition.X) / Math.Max(1.0, _previewHost.ActualWidth * previousZoom);
        var relativeY = (_scrollViewer.VerticalOffset + mousePosition.Y) / Math.Max(1.0, _previewHost.ActualHeight * previousZoom);

        _scaleTransform.ScaleX = _zoom;
        _scaleTransform.ScaleY = _zoom;
        UpdateLayout();

        _scrollViewer.ScrollToHorizontalOffset(Math.Max(0, (_previewHost.ActualWidth * _zoom * relativeX) - mousePosition.X));
        _scrollViewer.ScrollToVerticalOffset(Math.Max(0, (_previewHost.ActualHeight * _zoom * relativeY) - mousePosition.Y));
        e.Handled = true;
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        ResetZoom();
        e.Handled = true;
    }

    private void ResetZoom()
    {
        _zoom = 1.0;
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        UpdateLayout();
        _scrollViewer.ScrollToHorizontalOffset(0);
        _scrollViewer.ScrollToVerticalOffset(0);
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _selectionController.HandlePreviewSizeChanged();
    }

    private void SelectionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _selectionController.HandleMouseDown(e);
    }

    private void SelectionCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        _selectionController.HandleMouseMove(e);
    }

    private void SelectionCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _selectionController.HandleMouseUp(e);
    }
}
