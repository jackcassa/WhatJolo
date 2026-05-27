using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WhatJolo;

internal sealed class UltraPreviewWindow : Window
{
    private readonly ScrollViewer _scrollViewer;
    private readonly Border _previewBorder;
    private readonly Image _previewImage;
    private readonly ScaleTransform _scaleTransform;
    private readonly TranslateTransform _translateTransform;
    private double _fitZoom = 1.0;
    private int _zoomLevel;
    private bool _isAdjustingWindowSize;
    private double _imageAspectRatio = 1.0;
    private double _lastWindowWidth;
    private double _lastWindowHeight;

    public event EventHandler? CaptureAndDetectRequested;
    public event EventHandler? TapRequested;

    public UltraPreviewWindow()
    {
        Title = "WhatJolo Ultra Preview";
        Width = 1200;
        Height = 900;
        MinWidth = 420;
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

        var tapButton = new Button
        {
            Margin = new Thickness(14, 10, 0, 0),
            Padding = new Thickness(16, 9, 16, 9),
            MinWidth = 190,
            Content = "Tap ADB",
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(255, 212, 0)),
            Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 0))
        };
        tapButton.Click += (_, _) => TapRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(tapButton, 1);
        Grid.SetRow(tapButton, 1);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        buttonsPanel.Children.Add(captureButton);
        buttonsPanel.Children.Add(tapButton);
        Grid.SetColumn(buttonsPanel, 1);

        headerGrid.Children.Add(headerStack);
        headerGrid.Children.Add(buttonsPanel);
        headerBorder.Child = headerGrid;
        root.Children.Add(headerBorder);

        _previewBorder = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 16))
        };
        Grid.SetRow(_previewBorder, 1);
        _previewBorder.SizeChanged += PreviewBorder_SizeChanged;

        _scaleTransform = new ScaleTransform(1.0, 1.0);
        _translateTransform = new TranslateTransform(0, 0);
        _previewImage = new Image
        {
            Stretch = Stretch.None,
            RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    _scaleTransform,
                    _translateTransform
                }
            },
            RenderTransformOrigin = new Point(0, 0)
        };
        _previewImage.SetBinding(Image.SourceProperty, new Binding("DetectionPreview"));
        _previewImage.SizeChanged += PreviewImage_SizeChanged;
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
        _previewBorder.Child = _scrollViewer;
        root.Children.Add(_previewBorder);

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
        detectionsText.SetBinding(
            TextBox.TextProperty,
            new Binding("DetectionsSummary")
            {
                Mode = BindingMode.OneWay
            });
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

        SizeChanged += UltraPreviewWindow_SizeChanged;
        Loaded += UltraPreviewWindow_Loaded;
        MouseWheel += PreviewImage_PreviewMouseWheel;
        Content = root;
        _lastWindowWidth = Width;
        _lastWindowHeight = Height;

        DependencyPropertyDescriptor
            .FromProperty(Image.SourceProperty, typeof(Image))
            ?.AddValueChanged(_previewImage, (_, _) => HandleImageSourceChanged());
    }

    private void PreviewImage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var previousEffectiveZoom = GetEffectiveZoom();
        var previousZoomLevel = _zoomLevel;
        _zoomLevel = e.Delta > 0 ? Math.Min(18, _zoomLevel + 1) : Math.Max(-10, _zoomLevel - 1);
        if (_zoomLevel == previousZoomLevel)
        {
            return;
        }

        var mousePosition = e.GetPosition(_scrollViewer);
        var relativeX = (_scrollViewer.HorizontalOffset + mousePosition.X) / Math.Max(1.0, _previewImage.ActualWidth * previousEffectiveZoom);
        var relativeY = (_scrollViewer.VerticalOffset + mousePosition.Y) / Math.Max(1.0, _previewImage.ActualHeight * previousEffectiveZoom);

        ApplyZoom();
        UpdateLayout();

        var effectiveZoom = GetEffectiveZoom();
        _scrollViewer.ScrollToHorizontalOffset(Math.Max(0, (_previewImage.ActualWidth * effectiveZoom * relativeX) - mousePosition.X));
        _scrollViewer.ScrollToVerticalOffset(Math.Max(0, (_previewImage.ActualHeight * effectiveZoom * relativeY) - mousePosition.Y));
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
        _zoomLevel = 0;
        UpdateFitZoom();
        ApplyZoom();
        UpdateLayout();
        _scrollViewer.ScrollToHorizontalOffset(0);
        _scrollViewer.ScrollToVerticalOffset(0);
    }

    private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFitZoom();
    }

    private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFitZoom();
    }

    private void UltraPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            HandleImageSourceChanged();
            UpdateFitZoom();
            ResetWindowToImageAspect();
        }), DispatcherPriority.Loaded);
    }

    private void UltraPreviewWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isAdjustingWindowSize || _previewBorder.ActualWidth <= 0 || _previewBorder.ActualHeight <= 0 || _imageAspectRatio <= 0)
        {
            _lastWindowWidth = ActualWidth;
            _lastWindowHeight = ActualHeight;
            return;
        }

        var widthDelta = Math.Abs(ActualWidth - _lastWindowWidth);
        var heightDelta = Math.Abs(ActualHeight - _lastWindowHeight);
        var chromeWidth = Math.Max(0, ActualWidth - _previewBorder.ActualWidth);
        var chromeHeight = Math.Max(0, ActualHeight - _previewBorder.ActualHeight);

        _isAdjustingWindowSize = true;
        try
        {
            if (widthDelta >= heightDelta)
            {
                var previewWidth = Math.Max(200, ActualWidth - chromeWidth);
                var targetHeight = chromeHeight + (previewWidth / _imageAspectRatio);
                if (!double.IsNaN(targetHeight) && !double.IsInfinity(targetHeight))
                {
                    Height = Math.Max(MinHeight, targetHeight);
                }
            }
            else
            {
                var previewHeight = Math.Max(120, ActualHeight - chromeHeight);
                var targetWidth = chromeWidth + (previewHeight * _imageAspectRatio);
                if (!double.IsNaN(targetWidth) && !double.IsInfinity(targetWidth))
                {
                    Width = Math.Max(MinWidth, targetWidth);
                }
            }
        }
        finally
        {
            _isAdjustingWindowSize = false;
            _lastWindowWidth = ActualWidth;
            _lastWindowHeight = ActualHeight;
        }
    }

    private void UpdateFitZoom()
    {
        if (_previewImage.Source is not ImageSource source)
        {
            _fitZoom = 1.0;
            ApplyZoom();
            return;
        }

        var viewportWidth = Math.Max(1.0, _previewBorder.ActualWidth - 24);
        var viewportHeight = Math.Max(1.0, _previewBorder.ActualHeight - 24);
        var contentWidth = Math.Max(1.0, GetSourceWidth(source));
        var contentHeight = Math.Max(1.0, GetSourceHeight(source));

        _fitZoom = Math.Min(viewportWidth / contentWidth, viewportHeight / contentHeight);
        if (double.IsNaN(_fitZoom) || double.IsInfinity(_fitZoom) || _fitZoom <= 0)
        {
            _fitZoom = 1.0;
        }

        ApplyZoom();
    }

    private double GetEffectiveZoom()
    {
        return _fitZoom * Math.Pow(1.15, _zoomLevel);
    }

    private void ApplyZoom()
    {
        var zoom = GetEffectiveZoom();
        _scaleTransform.ScaleX = zoom;
        _scaleTransform.ScaleY = zoom;
        UpdateCenteringOffset();
    }

    private void HandleImageSourceChanged()
    {
        if (_previewImage.Source is not ImageSource source)
        {
            return;
        }

        var width = GetSourceWidth(source);
        var height = GetSourceHeight(source);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _imageAspectRatio = width / height;
        _previewImage.Width = width;
        _previewImage.Height = height;

        UpdateFitZoom();
        ResetZoom();
        ResetWindowToImageAspect();
    }

    private void UpdateCenteringOffset()
    {
        if (_previewImage.Source is not ImageSource source)
        {
            _translateTransform.X = 0;
            _translateTransform.Y = 0;
            return;
        }

        var viewportWidth = Math.Max(1.0, _previewBorder.ActualWidth - 24);
        var viewportHeight = Math.Max(1.0, _previewBorder.ActualHeight - 24);
        var contentWidth = GetSourceWidth(source) * GetEffectiveZoom();
        var contentHeight = GetSourceHeight(source) * GetEffectiveZoom();

        _translateTransform.X = contentWidth < viewportWidth ? (viewportWidth - contentWidth) / 2.0 : 0;
        _translateTransform.Y = contentHeight < viewportHeight ? (viewportHeight - contentHeight) / 2.0 : 0;
    }

    private void ResetWindowToImageAspect()
    {
        if (_previewBorder.ActualWidth <= 0 || _previewBorder.ActualHeight <= 0 || _imageAspectRatio <= 0)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var chromeWidth = Math.Max(0, ActualWidth - _previewBorder.ActualWidth);
        var chromeHeight = Math.Max(0, ActualHeight - _previewBorder.ActualHeight);
        var maxWindowWidth = Math.Max(MinWidth, workArea.Width * 0.92);
        var maxWindowHeight = Math.Max(MinHeight, workArea.Height * 0.92);
        var availablePreviewWidth = Math.Max(220, maxWindowWidth - chromeWidth);
        var availablePreviewHeight = Math.Max(220, maxWindowHeight - chromeHeight);

        var previewWidth = availablePreviewWidth;
        var previewHeight = previewWidth / _imageAspectRatio;
        if (previewHeight > availablePreviewHeight)
        {
            previewHeight = availablePreviewHeight;
            previewWidth = previewHeight * _imageAspectRatio;
        }

        var targetWidth = chromeWidth + previewWidth;
        var targetHeight = chromeHeight + previewHeight;

        _isAdjustingWindowSize = true;
        try
        {
            Width = Math.Max(MinWidth, targetWidth);
            Height = Math.Max(MinHeight, targetHeight);
        }
        finally
        {
            _isAdjustingWindowSize = false;
            _lastWindowWidth = ActualWidth;
            _lastWindowHeight = ActualHeight;
        }
    }

    private static double GetSourceWidth(ImageSource source)
    {
        return source switch
        {
            System.Windows.Media.Imaging.BitmapSource bitmap when bitmap.PixelWidth > 0 => bitmap.PixelWidth,
            _ => source.Width
        };
    }

    private static double GetSourceHeight(ImageSource source)
    {
        return source switch
        {
            System.Windows.Media.Imaging.BitmapSource bitmap when bitmap.PixelHeight > 0 => bitmap.PixelHeight,
            _ => source.Height
        };
    }
}
