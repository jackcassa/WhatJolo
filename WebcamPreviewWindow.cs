using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WhatJolo;

internal sealed class WebcamPreviewWindow : Window
{
    private const double PreviewWidth = 640;
    private const double PreviewHeight = 480;
    private readonly Image _previewImage;
    private readonly Ellipse _selectionCircle;
    private readonly Slider _radiusSlider;
    private Point _circleCenter = new(PreviewWidth / 2, PreviewHeight / 2);

    public WebcamPreviewWindow()
    {
        Title = "WhatJolo Webcam Preview";
        Width = 720;
        Height = 700;
        MinWidth = 700;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24))
        };
        Grid.SetRow(header, 0);
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = "Preview webcam",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0))
        });
        var pathText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            TextWrapping = TextWrapping.Wrap
        };
        pathText.SetBinding(TextBlock.TextProperty, new Binding("LastCapturePath"));
        headerStack.Children.Add(pathText);
        header.Child = headerStack;
        root.Children.Add(header);

        var previewBorder = new Border
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
            Background = Brushes.Black
        };
        Grid.SetRow(previewBorder, 1);

        var previewCanvas = new Canvas
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Focusable = true,
            ClipToBounds = true
        };
        previewCanvas.MouseLeftButtonDown += (_, e) =>
        {
            _circleCenter = e.GetPosition(previewCanvas);
            ClampCircleCenter();
            RenderCircle();
            previewCanvas.Focus();
            e.Handled = true;
        };
        previewCanvas.KeyDown += PreviewCanvas_KeyDown;

        _previewImage = new Image
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Stretch = Stretch.Fill
        };
        _previewImage.SetBinding(Image.SourceProperty, new Binding("LatestScreenshotPreview"));
        previewCanvas.Children.Add(_previewImage);

        _selectionCircle = new Ellipse
        {
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 212, 0)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 212, 0))
        };
        previewCanvas.Children.Add(_selectionCircle);
        previewBorder.Child = previewCanvas;
        root.Children.Add(previewBorder);

        var controls = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(controls, 2);

        controls.Children.Add(new TextBlock
        {
            Text = "Raggio",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0))
        });

        _radiusSlider = new Slider
        {
            Minimum = 8,
            Maximum = 180,
            Value = 48,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _radiusSlider.ValueChanged += (_, _) =>
        {
            ClampCircleCenter();
            RenderCircle();
        };
        Grid.SetColumn(_radiusSlider, 1);
        controls.Children.Add(_radiusSlider);

        var saveButton = new Button
        {
            Content = "Salva selezione",
            Height = 40,
            Padding = new Thickness(16, 6, 16, 6),
            FontWeight = FontWeights.Bold
        };
        saveButton.Click += (_, e) =>
        {
            SaveSelectionRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
        Grid.SetColumn(saveButton, 2);
        controls.Children.Add(saveButton);
        root.Children.Add(controls);

        var status = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 0)),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24))
        };
        Grid.SetRow(status, 3);
        var statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 0)),
            TextWrapping = TextWrapping.Wrap
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding("AdbStatusText"));
        status.Child = statusText;
        root.Children.Add(status);

        Content = root;
        Loaded += (_, _) =>
        {
            RenderCircle();
            previewCanvas.Focus();
        };
    }

    public event EventHandler? SaveSelectionRequested;

    public Int32Rect? SelectedPixelRect
    {
        get
        {
            if (_previewImage.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return null;
            }

            var radius = _radiusSlider.Value;
            var previewLeft = Math.Clamp(_circleCenter.X - radius, 0, PreviewWidth - 1);
            var previewTop = Math.Clamp(_circleCenter.Y - radius, 0, PreviewHeight - 1);
            var previewRight = Math.Clamp(_circleCenter.X + radius, previewLeft + 1, PreviewWidth);
            var previewBottom = Math.Clamp(_circleCenter.Y + radius, previewTop + 1, PreviewHeight);

            var scaleX = source.PixelWidth / PreviewWidth;
            var scaleY = source.PixelHeight / PreviewHeight;
            var pixelLeft = (int)Math.Round(previewLeft * scaleX);
            var pixelTop = (int)Math.Round(previewTop * scaleY);
            var pixelRight = (int)Math.Round(previewRight * scaleX);
            var pixelBottom = (int)Math.Round(previewBottom * scaleY);

            pixelLeft = Math.Clamp(pixelLeft, 0, source.PixelWidth - 1);
            pixelTop = Math.Clamp(pixelTop, 0, source.PixelHeight - 1);
            pixelRight = Math.Clamp(pixelRight, pixelLeft + 1, source.PixelWidth);
            pixelBottom = Math.Clamp(pixelBottom, pixelTop + 1, source.PixelHeight);
            return new Int32Rect(pixelLeft, pixelTop, pixelRight - pixelLeft, pixelBottom - pixelTop);
        }
    }

    public void ResetSelection()
    {
        _circleCenter = new Point(PreviewWidth / 2, PreviewHeight / 2);
        RenderCircle();
    }

    private void PreviewCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) == 0 ? 1 : 10;
        switch (e.Key)
        {
            case Key.Left:
                _circleCenter.X -= step;
                break;
            case Key.Right:
                _circleCenter.X += step;
                break;
            case Key.Up:
                _circleCenter.Y -= step;
                break;
            case Key.Down:
                _circleCenter.Y += step;
                break;
            case Key.OemPlus:
            case Key.Add:
                _radiusSlider.Value = Math.Min(_radiusSlider.Maximum, _radiusSlider.Value + step);
                e.Handled = true;
                return;
            case Key.OemMinus:
            case Key.Subtract:
                _radiusSlider.Value = Math.Max(_radiusSlider.Minimum, _radiusSlider.Value - step);
                e.Handled = true;
                return;
            default:
                return;
        }

        ClampCircleCenter();
        RenderCircle();
        e.Handled = true;
    }

    private void ClampCircleCenter()
    {
        var radius = _radiusSlider.Value;
        _circleCenter.X = Math.Clamp(_circleCenter.X, radius, PreviewWidth - radius);
        _circleCenter.Y = Math.Clamp(_circleCenter.Y, radius, PreviewHeight - radius);
    }

    private void RenderCircle()
    {
        var radius = _radiusSlider.Value;
        _selectionCircle.Width = radius * 2;
        _selectionCircle.Height = radius * 2;
        Canvas.SetLeft(_selectionCircle, _circleCenter.X - radius);
        Canvas.SetTop(_selectionCircle, _circleCenter.Y - radius);
    }
}
