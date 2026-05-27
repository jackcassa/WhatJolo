using OpenCvSharp;

namespace WhatJolo;

internal sealed class WebcamService
{
    public async Task<byte[]> CapturePngAsync(int cameraIndex, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            using var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                throw new InvalidOperationException($"Webcam {cameraIndex} non disponibile.");
            }

            using var frame = new Mat();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                capture.Read(frame);
                if (!frame.Empty())
                {
                    break;
                }

                Thread.Sleep(80);
            }

            if (frame.Empty())
            {
                throw new InvalidOperationException($"Webcam {cameraIndex}: frame vuoto.");
            }

            Cv2.ImEncode(".png", frame, out var pngBytes);
            return pngBytes;
        }, cancellationToken);
    }
}

public sealed class WebcamTabViewModel : ViewModelBase
{
    private readonly WebcamService _webcamService = new();
    private string _cameraIndexText = "0";
    private string _statusText = "Webcam pronta. Seleziona indice camera e acquisisci un frame.";

    public string CameraIndexText
    {
        get => _cameraIndexText;
        set => SetField(ref _cameraIndexText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public async Task<WebcamCaptureResult> CaptureAsync()
    {
        var cameraIndex = int.TryParse(CameraIndexText, out var parsedIndex)
            ? Math.Max(0, parsedIndex)
            : 0;

        CameraIndexText = cameraIndex.ToString();
        StatusText = $"Acquisizione webcam {cameraIndex} in corso...";
        var pngBytes = await _webcamService.CapturePngAsync(cameraIndex);
        StatusText = $"Webcam {cameraIndex}: frame acquisito ({pngBytes.Length:N0} byte PNG).";
        return new WebcamCaptureResult(cameraIndex, pngBytes);
    }

    public void SetStatusMessage(string message)
    {
        StatusText = message;
    }
}

public sealed record WebcamCaptureResult(int CameraIndex, byte[] PngBytes);
