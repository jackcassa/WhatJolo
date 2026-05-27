#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WhatJolo;

public sealed class YoloIconDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly string[] _labels;

    public YoloIconDetector(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        var input = _session.InputMetadata.First();
        _inputName = input.Key;
        var dimensions = input.Value.Dimensions.ToArray();
        _inputHeight = dimensions.Length > 2 && dimensions[2] > 0 ? dimensions[2] : 1024;
        _inputWidth = dimensions.Length > 3 && dimensions[3] > 0 ? dimensions[3] : 1024;
        _labels = LoadLabels(modelPath);
    }

    public IReadOnlyList<YoloDetection> DetectAll(Bitmap source, float minConfidence = 0.25f)
    {
        return DetectDebug(source, minConfidence).Detections;
    }

    public YoloDetectionDebugResult DetectDebug(Bitmap source, float minConfidence = 0.25f)
    {
        var prep = PrepareImage(source);
        var tensor = new DenseTensor<float>(prep.Input, new[] { 1, 3, _inputHeight, _inputWidth });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();
        var rawDetections = ParseDetections(outputTensor, source.Width, source.Height, prep.Scale, prep.PadX, prep.PadY, minConfidence);
        var detectionsBeforeNms = rawDetections
            .Where(d => d.Confidence >= minConfidence)
            .ToList();
        var finalDetections = detectionsBeforeNms.Count == 0
            ? Array.Empty<YoloDetection>()
            : ApplyNms(detectionsBeforeNms, 0.45f)
                .OrderByDescending(d => d.Confidence)
                .ToArray();

        return new YoloDetectionDebugResult(
            finalDetections,
            outputTensor.Dimensions.ToArray(),
            rawDetections.Count,
            detectionsBeforeNms.Count,
            finalDetections.Length,
            _inputWidth,
            _inputHeight,
            _labels);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private List<YoloDetection> ParseDetections(Tensor<float> outputTensor, int imageWidth, int imageHeight, float scale, float padX, float padY, float minConfidence)
    {
        var dims = outputTensor.Dimensions.ToArray();
        var values = outputTensor.ToArray();
        var detections = new List<YoloDetection>();

        var candidateCount = 0;
        var featureCount = 0;
        var featuresFirst = false;

        if (dims.Length == 3)
        {
            if (dims[1] <= dims[2])
            {
                featureCount = dims[1];
                candidateCount = dims[2];
                featuresFirst = true;
            }
            else
            {
                candidateCount = dims[1];
                featureCount = dims[2];
            }
        }
        else if (dims.Length == 2)
        {
            candidateCount = dims[0];
            featureCount = dims[1];
        }
        else
        {
            throw new InvalidOperationException("Formato output YOLO non supportato.");
        }

        var isEndToEndSixColumns = featureCount == 6;
        var hasObjectness = featureCount == _labels.Length + 5 || (_labels.Length == 0 && featureCount == 6);
        var classOffset = hasObjectness ? 5 : 4;
        var classCount = featureCount - classOffset;

        for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            float ReadValue(int featureIndex)
            {
                if (dims.Length == 2)
                {
                    return values[(candidateIndex * featureCount) + featureIndex];
                }

                if (featuresFirst)
                {
                    return values[(featureIndex * candidateCount) + candidateIndex];
                }

                return values[(candidateIndex * featureCount) + featureIndex];
            }

            if (isEndToEndSixColumns)
            {
                var x1 = ReadValue(0);
                var y1 = ReadValue(1);
                var x2 = ReadValue(2);
                var y2 = ReadValue(3);
                var score = ReadValue(4);
                if (score < 0f || score > 1f)
                {
                    score = Sigmoid(score);
                }

                if (score < minConfidence)
                {
                    continue;
                }

                var left = (x1 - padX) / scale;
                var top = (y1 - padY) / scale;
                var right = (x2 - padX) / scale;
                var bottom = (y2 - padY) / scale;

                var clampedLeft = Math.Clamp((int)Math.Round(left), 0, Math.Max(0, imageWidth - 1));
                var clampedTop = Math.Clamp((int)Math.Round(top), 0, Math.Max(0, imageHeight - 1));
                var clampedRight = Math.Clamp((int)Math.Round(right), clampedLeft + 1, imageWidth);
                var clampedBottom = Math.Clamp((int)Math.Round(bottom), clampedTop + 1, imageHeight);
                var bounds = Rectangle.FromLTRB(clampedLeft, clampedTop, clampedRight, clampedBottom);
                var classValue = ReadValue(5);
                var classIndex = Math.Clamp((int)Math.Round(classValue), 0, Math.Max(0, _labels.Length - 1));

                detections.Add(new YoloDetection
                {
                    Bounds = bounds,
                    Confidence = score,
                    ClassIndex = classIndex,
                    Label = ResolveLabel(classIndex)
                });
                continue;
            }

            var cx = ReadValue(0);
            var cy = ReadValue(1);
            var w = ReadValue(2);
            var h = ReadValue(3);
            if (w <= 1f || h <= 1f)
            {
                continue;
            }

            var objectness = hasObjectness ? ReadValue(4) : 1f;
            if (objectness < 0f || objectness > 1f)
            {
                objectness = Sigmoid(objectness);
            }

            var bestClassScore = 0f;
            var bestClassIndex = 0;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                var classScore = ReadValue(classOffset + classIndex);
                if (classScore < 0f || classScore > 1f)
                {
                    classScore = Sigmoid(classScore);
                }

                if (classScore > bestClassScore)
                {
                    bestClassScore = classScore;
                    bestClassIndex = classIndex;
                }
            }

            var confidence = objectness * bestClassScore;
            if (confidence < minConfidence)
            {
                continue;
            }

            var leftLegacy = ((cx - (w / 2f)) - padX) / scale;
            var topLegacy = ((cy - (h / 2f)) - padY) / scale;
            var rightLegacy = ((cx + (w / 2f)) - padX) / scale;
            var bottomLegacy = ((cy + (h / 2f)) - padY) / scale;

            var clampedLeftLegacy = Math.Clamp((int)Math.Round(leftLegacy), 0, Math.Max(0, imageWidth - 1));
            var clampedTopLegacy = Math.Clamp((int)Math.Round(topLegacy), 0, Math.Max(0, imageHeight - 1));
            var clampedRightLegacy = Math.Clamp((int)Math.Round(rightLegacy), clampedLeftLegacy + 1, imageWidth);
            var clampedBottomLegacy = Math.Clamp((int)Math.Round(bottomLegacy), clampedTopLegacy + 1, imageHeight);
            var legacyBounds = Rectangle.FromLTRB(clampedLeftLegacy, clampedTopLegacy, clampedRightLegacy, clampedBottomLegacy);

            detections.Add(new YoloDetection
            {
                Bounds = legacyBounds,
                Confidence = confidence,
                ClassIndex = bestClassIndex,
                Label = ResolveLabel(bestClassIndex)
            });
        }

        return detections;
    }

    private static float Sigmoid(float value)
    {
        if (value >= 0)
        {
            var z = (float)Math.Exp(-value);
            return 1f / (1f + z);
        }

        var exp = (float)Math.Exp(value);
        return exp / (1f + exp);
    }

    private static List<YoloDetection> ApplyNms(List<YoloDetection> detections, float iouThreshold)
    {
        var ordered = detections.OrderByDescending(d => d.Confidence).ToList();
        var result = new List<YoloDetection>();

        while (ordered.Count > 0)
        {
            var best = ordered[0];
            result.Add(best);
            ordered.RemoveAt(0);

            for (var index = ordered.Count - 1; index >= 0; index--)
            {
                if (ComputeIou(best.Bounds, ordered[index].Bounds) > iouThreshold)
                {
                    ordered.RemoveAt(index);
                }
            }
        }

        return result;
    }

    private static float ComputeIou(Rectangle a, Rectangle b)
    {
        var intersection = Rectangle.Intersect(a, b);
        if (intersection.IsEmpty)
        {
            return 0f;
        }

        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;
        return unionArea <= 0 ? 0f : intersectionArea / (float)unionArea;
    }

    private static string[] LoadLabels(string modelPath)
    {
        var candidates = new[]
        {
            Path.ChangeExtension(modelPath, ".labels.txt"),
            Path.ChangeExtension(modelPath, ".txt")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return File.ReadAllLines(candidate)
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0)
                        .ToArray();
                }
            }
            catch (IOException)
            {
                // If an ONNX export/reset left the labels path temporarily inconsistent,
                // keep detection running with fallback labels instead of crashing the UI.
            }
            catch (UnauthorizedAccessException)
            {
                // Same fallback as above: no labels file is better than a hard crash here.
            }
        }

        return Array.Empty<string>();
    }

    private string ResolveLabel(int classIndex)
    {
        if (classIndex >= 0 && classIndex < _labels.Length)
        {
            return _labels[classIndex];
        }

        return "icon";
    }

    private ImagePreparation PrepareImage(Bitmap source)
    {
        var scale = Math.Min(_inputWidth / (float)source.Width, _inputHeight / (float)source.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        var padX = (_inputWidth - resizedWidth) / 2f;
        var padY = (_inputHeight - resizedHeight) / 2f;
        var input = new float[3 * _inputWidth * _inputHeight];

        using var canvas = new Bitmap(_inputWidth, _inputHeight);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.FromArgb(114, 114, 114));
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, padX, padY, resizedWidth, resizedHeight);
        }

        for (var y = 0; y < _inputHeight; y++)
        {
            for (var x = 0; x < _inputWidth; x++)
            {
                var pixel = canvas.GetPixel(x, y);
                var offset = y * _inputWidth + x;
                input[offset] = pixel.R / 255f;
                input[_inputWidth * _inputHeight + offset] = pixel.G / 255f;
                input[2 * _inputWidth * _inputHeight + offset] = pixel.B / 255f;
            }
        }

        return new ImagePreparation
        {
            Input = input,
            Scale = scale,
            PadX = padX,
            PadY = padY
        };
    }

    private sealed class ImagePreparation
    {
        public float[] Input { get; init; } = Array.Empty<float>();
        public float Scale { get; init; }
        public float PadX { get; init; }
        public float PadY { get; init; }
    }
}

public sealed class YoloDetection
{
    public Rectangle Bounds { get; set; }
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public int ClassIndex { get; set; }
}

public sealed record YoloDetectionDebugResult(
    IReadOnlyList<YoloDetection> Detections,
    int[] OutputDimensions,
    int RawDetectionCount,
    int AboveThresholdCount,
    int FinalDetectionCount,
    int InputWidth,
    int InputHeight,
    IReadOnlyList<string> Labels);
