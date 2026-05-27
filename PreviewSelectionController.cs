using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WhatJolo;

internal sealed class PreviewSelectionController
{
    private readonly FrameworkElement _resourceOwner;
    private readonly Image _previewImage;
    private readonly Canvas _selectionCanvas;
    private readonly Rectangle _selectionRectangle;
    private readonly Action<string> _setStatusMessage;
    private readonly DoubleAnimation _dashAnimation = new()
    {
        From = 0,
        To = -12,
        Duration = TimeSpan.FromSeconds(0.5),
        RepeatBehavior = RepeatBehavior.Forever
    };

    private Point? _selectionStart;
    private Int32Rect? _selectedPixelRect;
    private Rect? _selectedDisplayRect;
    private Int32Rect? _selectionBackupPixelRect;
    private Rect? _selectionBackupDisplayRect;
    private bool _isDraggingSelection;
    private Rect _lastMouseDownDisplayBounds;

    public PreviewSelectionController(
        FrameworkElement resourceOwner,
        Image previewImage,
        Canvas selectionCanvas,
        Rectangle selectionRectangle,
        Action<string> setStatusMessage)
    {
        _resourceOwner = resourceOwner;
        _previewImage = previewImage;
        _selectionCanvas = selectionCanvas;
        _selectionRectangle = selectionRectangle;
        _setStatusMessage = setStatusMessage;
    }

    public Int32Rect? SelectedPixelRect => _selectedPixelRect;

    public void HandleMouseDown(MouseButtonEventArgs e)
    {
        if (_previewImage.Source is not BitmapSource)
        {
            return;
        }

        var startPoint = e.GetPosition(_selectionCanvas);
        _lastMouseDownDisplayBounds = GetDisplayedImageBounds();
        _selectionStart = startPoint;
        _selectionBackupPixelRect = _selectedPixelRect;
        _selectionBackupDisplayRect = _selectedDisplayRect;
        _isDraggingSelection = false;
        _selectedDisplayRect = new Rect(startPoint.X, startPoint.Y, 1, 1);
        ApplySelectionRectangle(_selectedDisplayRect.Value);
        _selectionCanvas.CaptureMouse();
        _setStatusMessage(
            $"Mouse down selezione: X={(int)startPoint.X} Y={(int)startPoint.Y} | " +
            $"imgBounds=({_lastMouseDownDisplayBounds.X:0},{_lastMouseDownDisplayBounds.Y:0},{_lastMouseDownDisplayBounds.Width:0},{_lastMouseDownDisplayBounds.Height:0})");
        e.Handled = true;
    }

    public void HandleMouseMove(MouseEventArgs e)
    {
        if (_selectionStart == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(_selectionCanvas);
        var left = Math.Min(_selectionStart.Value.X, currentPoint.X);
        var top = Math.Min(_selectionStart.Value.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _selectionStart.Value.X);
        var height = Math.Abs(currentPoint.Y - _selectionStart.Value.Y);

        _selectedDisplayRect = new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
        _isDraggingSelection = width >= 4 && height >= 4;
        _selectedPixelRect = null;
        ApplySelectionRectangle(_selectedDisplayRect.Value);
    }

    public void HandleMouseUp(MouseButtonEventArgs e)
    {
        if (_selectionStart == null)
        {
            return;
        }

        _selectionCanvas.ReleaseMouseCapture();
        _selectionStart = null;

        if (!_isDraggingSelection)
        {
            _selectedPixelRect = _selectionBackupPixelRect;
            _selectedDisplayRect = _selectionBackupDisplayRect;
            RenderSelectionFromPixels();
            _setStatusMessage("Click senza drag: selezione precedente mantenuta.");
            return;
        }

        if (_previewImage.Source is not BitmapSource source)
        {
            ClearSelection();
            return;
        }

        if (_selectedDisplayRect == null || _selectedDisplayRect.Value.Width < 4 || _selectedDisplayRect.Value.Height < 4)
        {
            RestorePreviousSelection("Selezione troppo piccola: selezione precedente ripristinata.");
            return;
        }

        var displayBounds = GetDisplayedImageBounds();
        if (displayBounds.IsEmpty)
        {
            RestorePreviousSelection("Area immagine non disponibile: selezione precedente ripristinata.");
            return;
        }

        var clippedRect = Rect.Intersect(_selectedDisplayRect.Value, displayBounds);
        if (clippedRect.IsEmpty || clippedRect.Width < 2 || clippedRect.Height < 2)
        {
            RestorePreviousSelection(
                $"Selezione fuori immagine: down=({_selectionBackupDisplayRect?.X:0},{_selectionBackupDisplayRect?.Y:0}) " +
                $"raw=({_selectedDisplayRect.Value.X:0},{_selectedDisplayRect.Value.Y:0},{_selectedDisplayRect.Value.Width:0},{_selectedDisplayRect.Value.Height:0}) " +
                $"imgDown=({_lastMouseDownDisplayBounds.X:0},{_lastMouseDownDisplayBounds.Y:0},{_lastMouseDownDisplayBounds.Width:0},{_lastMouseDownDisplayBounds.Height:0}) " +
                $"imgUp=({displayBounds.X:0},{displayBounds.Y:0},{displayBounds.Width:0},{displayBounds.Height:0}) " +
                $"clip=({clippedRect.X:0},{clippedRect.Y:0},{clippedRect.Width:0},{clippedRect.Height:0})");
            return;
        }

        _selectedDisplayRect = clippedRect;
        ApplySelectionRectangle(clippedRect);

        var scaleX = source.PixelWidth / displayBounds.Width;
        var scaleY = source.PixelHeight / displayBounds.Height;
        var pixelLeft = (int)Math.Round((clippedRect.X - displayBounds.X) * scaleX);
        var pixelTop = (int)Math.Round((clippedRect.Y - displayBounds.Y) * scaleY);
        var pixelWidth = (int)Math.Round(clippedRect.Width * scaleX);
        var pixelHeight = (int)Math.Round(clippedRect.Height * scaleY);

        pixelLeft = Math.Clamp(pixelLeft, 0, source.PixelWidth - 1);
        pixelTop = Math.Clamp(pixelTop, 0, source.PixelHeight - 1);
        pixelWidth = Math.Clamp(pixelWidth, 1, source.PixelWidth - pixelLeft);
        pixelHeight = Math.Clamp(pixelHeight, 1, source.PixelHeight - pixelTop);

        _selectedPixelRect = new Int32Rect(pixelLeft, pixelTop, pixelWidth, pixelHeight);
        _setStatusMessage($"Selezione pronta: X={pixelLeft} Y={pixelTop} W={pixelWidth} H={pixelHeight}");
    }

    public void HandlePreviewSizeChanged()
    {
        RenderSelectionFromPixels();
    }

    public void ClearSelection()
    {
        _selectionStart = null;
        _isDraggingSelection = false;
        _selectedPixelRect = null;
        _selectedDisplayRect = null;
        _selectionBackupPixelRect = null;
        _selectionBackupDisplayRect = null;
        StopSelectionAnimation();
        _selectionRectangle.Visibility = Visibility.Collapsed;
        _selectionRectangle.Width = 0;
        _selectionRectangle.Height = 0;
    }

    private void RestorePreviousSelection(string message)
    {
        _isDraggingSelection = false;
        _selectedPixelRect = _selectionBackupPixelRect;
        _selectedDisplayRect = _selectionBackupDisplayRect;

        if (_selectedPixelRect == null && _selectedDisplayRect == null)
        {
            ClearSelection();
        }
        else
        {
            RenderSelectionFromPixels();
        }

        _setStatusMessage(message);
    }

    private void RenderSelectionFromPixels()
    {
        if (_selectedPixelRect == null || _previewImage.Source is not BitmapSource source)
        {
            return;
        }

        var displayBounds = GetDisplayedImageBounds();
        if (displayBounds.IsEmpty)
        {
            return;
        }

        var scaleX = displayBounds.Width / source.PixelWidth;
        var scaleY = displayBounds.Height / source.PixelHeight;
        _selectedDisplayRect = new Rect(
            displayBounds.X + (_selectedPixelRect.Value.X * scaleX),
            displayBounds.Y + (_selectedPixelRect.Value.Y * scaleY),
            _selectedPixelRect.Value.Width * scaleX,
            _selectedPixelRect.Value.Height * scaleY);
        ApplySelectionRectangle(_selectedDisplayRect.Value);
    }

    private Rect GetDisplayedImageBounds()
    {
        if (_previewImage.Source is not BitmapSource source)
        {
            return Rect.Empty;
        }

        var containerWidth = _selectionCanvas.ActualWidth;
        var containerHeight = _selectionCanvas.ActualHeight;
        if (containerWidth <= 0 || containerHeight <= 0 || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(containerWidth / source.PixelWidth, containerHeight / source.PixelHeight);
        var renderedWidth = source.PixelWidth * scale;
        var renderedHeight = source.PixelHeight * scale;
        var offsetX = (containerWidth - renderedWidth) / 2.0;
        var offsetY = (containerHeight - renderedHeight) / 2.0;

        return new Rect(offsetX, offsetY, renderedWidth, renderedHeight);
    }

    private void ApplySelectionRectangle(Rect rect)
    {
        _selectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selectionRectangle, rect.X);
        Canvas.SetTop(_selectionRectangle, rect.Y);
        _selectionRectangle.Width = rect.Width;
        _selectionRectangle.Height = rect.Height;
        StartSelectionAnimation();
    }

    private void StartSelectionAnimation()
    {
        _selectionRectangle.BeginAnimation(Shape.StrokeDashOffsetProperty, _dashAnimation);
    }

    private void StopSelectionAnimation()
    {
        _selectionRectangle.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _selectionRectangle.StrokeDashOffset = 0;
    }
}
