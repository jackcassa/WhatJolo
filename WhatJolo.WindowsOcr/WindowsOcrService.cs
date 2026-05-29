using System.Drawing;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WhatJolo.WindowsOcr;

public sealed class WindowsOcrService
{
    public async Task<IReadOnlyList<string>> ReadLinesFromPngBytesAsync(byte[] imageBytes, Rectangle cropBounds)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (cropBounds.Width <= 0 || cropBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cropBounds), "Bounding box OCR non valida.");
        }

        using var sourceStream = new MemoryStream(imageBytes);
        using var sourceBitmap = new Bitmap(sourceStream);
        var normalizedBounds = Rectangle.Intersect(new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height), cropBounds);
        if (normalizedBounds.Width <= 0 || normalizedBounds.Height <= 0)
        {
            throw new InvalidOperationException("Bounding box OCR fuori dall'immagine.");
        }

        using var cropBitmap = sourceBitmap.Clone(normalizedBounds, sourceBitmap.PixelFormat);
        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(cropBitmap);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Language("it-IT"))
            ?? throw new InvalidOperationException("OCR locale Windows non disponibile.");

        var result = await engine.RecognizeAsync(softwareBitmap);
        return result?.Lines?
            .Select(line => line.Text?.Trim() ?? string.Empty)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList() ?? [];
    }

    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
        pngStream.Position = 0;

        using var randomAccessStream = new InMemoryRandomAccessStream();
        await randomAccessStream.WriteAsync(pngStream.ToArray().AsBuffer());
        randomAccessStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
