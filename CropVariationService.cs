using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhatJolo;

internal sealed class CropVariationService
{
    private readonly ProjectWorkspaceService _workspaceService;
    private readonly AnnotationCropDbService _annotationCropDbService;
    private readonly ProjectImageBlobService _projectImageBlobService;

    public CropVariationService()
    {
        _workspaceService = new ProjectWorkspaceService();
        _annotationCropDbService = new AnnotationCropDbService();
        _projectImageBlobService = new ProjectImageBlobService();
    }

    public async Task<IReadOnlyList<string>> GenerateVariationsAsync(ProjectCropRecord sourceRecord, int count = 10)
    {
        if (count <= 0)
        {
            return Array.Empty<string>();
        }

        if (!File.Exists(sourceRecord.SourceImagePath))
        {
            throw new FileNotFoundException("Immagine sorgente della crop non trovata.", sourceRecord.SourceImagePath);
        }

        var sourceBitmap = LoadBitmapSource(sourceRecord.SourceImagePath);
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
        var capturesRoot = _workspaceService.GetCapturesPath(sourceRecord.ProjectName);
        var variationSourcesDirectory = Path.Combine(capturesRoot, "Variations");
        var variationCropsDirectory = Path.Combine(_workspaceService.GetSavedCropsPath(sourceRecord.ProjectName), safeClass);
        Directory.CreateDirectory(variationSourcesDirectory);
        Directory.CreateDirectory(variationCropsDirectory);

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
            var sourceOutputPath = Path.Combine(variationSourcesDirectory, $"adb_capture_var_{uniqueSuffix}.png");
            var cropOutputPath = Path.Combine(variationCropsDirectory, $"{safeClass}_var_{uniqueSuffix}.png");

            await SaveBitmapSourceAsPngAsync(transformedBitmap, sourceOutputPath);
            await _projectImageBlobService.SaveImageAsync(sourceRecord.ProjectName, sourceOutputPath, "variation-source");

            var cropBitmap = new CroppedBitmap(transformedBitmap, selectionRect);
            await SaveBitmapSourceAsPngAsync(cropBitmap, cropOutputPath);
            await _projectImageBlobService.SaveImageAsync(sourceRecord.ProjectName, cropOutputPath, "variation-crop");
            await _annotationCropDbService.SaveCropAsync(
                sourceRecord.ProjectName,
                safeClass,
                sourceOutputPath,
                cropOutputPath,
                selectionRect,
                isVariation: true);

            results.Add(cropOutputPath);
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

    private static BitmapImage LoadBitmapSource(string filePath)
    {
        var image = new BitmapImage();
        using var stream = File.OpenRead(filePath);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task SaveBitmapSourceAsPngAsync(BitmapSource bitmap, string outputPath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static double NextDouble(Random random, double minValue, double maxValue)
    {
        return minValue + ((maxValue - minValue) * random.NextDouble());
    }
}
