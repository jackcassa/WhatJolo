using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WhatJolo;

internal sealed class UltraPreviewWindow : Window
{
    private readonly ScrollViewer _scrollViewer;
    private readonly Image _previewImage;
    private readonly ScaleTransform _scaleTransform;
    private double _zoom = 1.0;

    public event EventHandler? CaptureAndDetectRequested;

    public UltraPreviewWindow()
    {
        Title = "WhatJolo Ultra Preview";
        Width = 1200;
        Height = 900;
        MinWidth = 720;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));

        var root = new Grid
        {
            Margin = new Thickness(12)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
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

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = "Preview detection",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0))
        });

        var modelPathText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            TextWrapping = TextWrapping.Wrap
        };
        modelPathText.SetBinding(TextBlock.TextProperty, new Binding("ModelPath"));
        headerStack.Children.Add(modelPathText);

        var captureButton = new Button
        {
            Margin = new Thickness(14, 0, 0, 0),
            Padding = new Thickness(16, 9, 16, 9),
            MinWidth = 190,
            Content = "Acquisisci ADB e riconosci",
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 212, 0))
        };
        captureButton.Click += (_, _) => CaptureAndDetectRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(captureButton, 1);

        headerGrid.Children.Add(headerStack);
        headerGrid.Children.Add(captureButton);
        headerBorder.Child = headerGrid;
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

        _scaleTransform = new ScaleTransform(1.0, 1.0);
        _previewImage = new Image
        {
            Stretch = Stretch.None,
            RenderTransform = _scaleTransform,
            RenderTransformOrigin = new Point(0, 0)
        };
        _previewImage.SetBinding(Image.SourceProperty, new Binding("DetectionPreview"));
        _previewImage.PreviewMouseWheel += PreviewImage_PreviewMouseWheel;
        _previewImage.MouseLeftButtonDown += PreviewImage_MouseLeftButtonDown;

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Content = _previewImage,
            Padding = new Thickness(12)
        };
        previewBorder.Child = _scrollViewer;
        root.Children.Add(previewBorder);

        var detectionsBorder = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 16))
        };
        Grid.SetRow(detectionsBorder, 2);

        var detectionsText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 16)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        detectionsText.SetBinding(TextBox.TextProperty, new Binding("DetectionsSummary"));
        detectionsBorder.Child = detectionsText;
        root.Children.Add(detectionsBorder);

        var statusBorder = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24))
        };
        Grid.SetRow(statusBorder, 3);

        var statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0)),
            TextWrapping = TextWrapping.Wrap
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
        statusBorder.Child = statusText;
        root.Children.Add(statusBorder);

        MouseWheel += PreviewImage_PreviewMouseWheel;
        Content = root;
    }

    private void PreviewImage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var previousZoom = _zoom;
        _zoom = e.Delta > 0 ? Math.Min(8.0, _zoom * 1.15) : Math.Max(0.2, _zoom / 1.15);
        if (Math.Abs(_zoom - previousZoom) < 0.0001)
        {
            return;
        }

        var mousePosition = e.GetPosition(_scrollViewer);
        var relativeX = (_scrollViewer.HorizontalOffset + mousePosition.X) / Math.Max(1.0, _previewImage.ActualWidth * previousZoom);
        var relativeY = (_scrollViewer.VerticalOffset + mousePosition.Y) / Math.Max(1.0, _previewImage.ActualHeight * previousZoom);

        _scaleTransform.ScaleX = _zoom;
        _scaleTransform.ScaleY = _zoom;
        UpdateLayout();

        _scrollViewer.ScrollToHorizontalOffset(Math.Max(0, (_previewImage.ActualWidth * _zoom * relativeX) - mousePosition.X));
        _scrollViewer.ScrollToVerticalOffset(Math.Max(0, (_previewImage.ActualHeight * _zoom * relativeY) - mousePosition.Y));
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
}
