using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhatJolo;

internal sealed class CropVariationService
{
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly ProjectImageBlobService _projectImageBlobService;

    public CropVariationService()
    {
        _annotationCropDbService = new AnnotationCropDbService();
        _projectImageBlobService = new ProjectImageBlobService();
    }

    public async Task<IReadOnlyList<string>> GenerateVariationsAsync(ProjectCropRecord sourceRecord, int count = 10)
    {
        if (count <= 0)
        {
            return Array.Empty<string>();
        }

        var sourceBytes = await _projectImageBlobService.GetImageBytesByKeyAsync(sourceRecord.ProjectName, sourceRecord.SourceImageKey);
        if (sourceBytes == null || sourceBytes.Length == 0)
        {
            throw new InvalidOperationException("Immagine sorgente della crop non trovata nel DB.");
        }

        var sourceBitmap = LoadBitmapSource(sourceBytes);
        var sourceWidth = sourceBitmap.PixelWidth;
        var sourceHeight = sourceBitmap.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidOperationException("Immagine sorgente non valida per generare variazioni.");
        }

        var selectionRect = new Int32Rect(sourceRecord.X, sourceRecord.Y, sourceRecord.Width, sourceRecord.Height);
        if (selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            throw new InvalidOperationException("Bounding box originale non valida.");
        }

        if (selectionRect.X < 0 ||
            selectionRect.Y < 0 ||
            selectionRect.X + selectionRect.Width > sourceWidth ||
            selectionRect.Y + selectionRect.Height > sourceHeight)
        {
            throw new InvalidOperationException("Bounding box originale fuori immagine.");
        }

        var safeClass = sourceRecord.LabelName.Trim().ToLowerInvariant();
        var centerX = sourceRecord.X + (sourceRecord.Width / 2d);
        var centerY = sourceRecord.Y + (sourceRecord.Height / 2d);
        var seed = Guid.NewGuid().GetHashCode() ^ Environment.TickCount ^ Random.Shared.Next();
        var random = new Random(seed);
        var results = new List<string>(count);

        for (var index = 0; index < count; index++)
        {
            var rotationDegrees = NextDouble(random, -5.0, 5.0);
            var scaleFactor = NextDouble(random, 0.94, 1.06);
            var transformedBitmap = TransformAroundSelectionCenter(sourceBitmap, centerX, centerY, rotationDegrees, scaleFactor);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var uniqueSuffix = $"{timestamp}_{index + 1:D2}_{Math.Abs(random.Next())}";
            var sourceFileName = $"adb_capture_var_{uniqueSuffix}.png";
            var cropFileName = $"{safeClass}_var_{uniqueSuffix}.png";
            var sourceImageKey = $"variation-source|{sourceFileName}";
            var cropImageKey = $"variation-crop|{safeClass}|{cropFileName}";
            var variationSourceBytes = SaveBitmapSourceAsPngBytes(transformedBitmap);

            var cropBitmap = new CroppedBitmap(transformedBitmap, selectionRect);
            var cropBytes = SaveBitmapSourceAsPngBytes(cropBitmap);
            await _annotationCropDbService.SaveVariationAsync(
                sourceRecord.ProjectName,
                safeClass,
                sourceRecord.CropImageKey,
                sourceImageKey,
                cropImageKey,
                variationSourceBytes,
                cropBytes,
                selectionRect);

            results.Add(cropImageKey);
        }

        return results;
    }

    private static BitmapSource TransformAroundSelectionCenter(
        BitmapSource sourceBitmap,
        double centerX,
        double centerY,
        double rotationDegrees,
        double scaleFactor)
    {
        var width = sourceBitmap.PixelWidth;
        var height = sourceBitmap.PixelHeight;
        var dpiX = sourceBitmap.DpiX > 0 ? sourceBitmap.DpiX : 96.0;
        var dpiY = sourceBitmap.DpiY > 0 ? sourceBitmap.DpiY : 96.0;

        var visual = new DrawingVisual();
        using (var drawingContext = visual.RenderOpen())
        {
            var transformMatrix = Matrix.Identity;
            transformMatrix.Translate(-centerX, -centerY);
            transformMatrix.Scale(scaleFactor, scaleFactor);
            transformMatrix.Rotate(rotationDegrees);
            transformMatrix.Translate(centerX, centerY);

            drawingContext.PushTransform(new MatrixTransform(transformMatrix));
            drawingContext.DrawImage(sourceBitmap, new Rect(0, 0, width, height));
            drawingContext.Pop();
        }

        var renderTarget = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
        renderTarget.Render(visual);
        renderTarget.Freeze();
        return renderTarget;
    }

    private static BitmapImage LoadBitmapSource(byte[] imageBytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(imageBytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static byte[] SaveBitmapSourceAsPngBytes(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static double NextDouble(Random random, double minValue, double maxValue)
    {
        return minValue + ((maxValue - minValue) * random.NextDouble());
    }
}
