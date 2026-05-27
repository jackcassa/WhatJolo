using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WhatJolo;

internal sealed class YoloTrainingMonitorWindow : Window
{
    private static readonly Regex EpochLineRegex = new(
        @"(?<epoch>\d+)\/(?<total>\d+)\s+\S+\s+(?<box>\d+(\.\d+)?)\s+(?<cls>\d+(\.\d+)?)\s+(?<dfl>\d+(\.\d+)?)",
        RegexOptions.Compiled);

    private static readonly Regex MetricsLineRegex = new(
        @"^all\s+(?<images>\d+)\s+(?<instances>\d+)\s+(?<precision>\d+(\.\d+)?)\s+(?<recall>\d+(\.\d+)?)\s+(?<map50>\d+(\.\d+)?)\s+(?<map95>\d+(\.\d+)?)$",
        RegexOptions.Compiled);

    private readonly TextBlock _projectText;
    private readonly TextBlock _modelText;
    private readonly TextBlock _epochText;
    private readonly TextBlock _lossText;
    private readonly TextBlock _metricsText;
    private readonly TextBlock _humanMetricsText;
    private readonly TextBlock _commentText;
    private readonly TextBlock _rawLineText;

    private double? _lastMap50;
    private double? _lastPrecision;
    private double? _lastRecall;

    public YoloTrainingMonitorWindow(string projectName, string modelName)
    {
        Title = "YOLO Monitor";
        Width = 460;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = false;
        Topmost = false;
        Background = (Brush)Application.Current.Resources["AppBackgroundBrush"];
        Foreground = (Brush)Application.Current.Resources["ForegroundPrimaryBrush"];

        var root = new Grid { Margin = new Thickness(16) };
        for (var i = 0; i < 8; i++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _projectText = CreateBlock($"Progetto: {projectName}", 0, FontWeights.SemiBold, 18, "AccentBrush");
        _modelText = CreateBlock($"Modello: {modelName}", 1, FontWeights.Normal, 13, null);
        _epochText = CreateBlock("Epoca: in attesa...", 2, FontWeights.Bold, 22, "AccentOrangeBrush", 12);
        _lossText = CreateBlock("Loss: -", 3, FontWeights.Normal, 13, null, 8);
        _metricsText = CreateBlock("Metriche: -", 4, FontWeights.Normal, 13, null, 8);
        _humanMetricsText = CreateBlock("Riconoscimenti trovati: - | Affidabilita': - | Qualita' box: -", 5, FontWeights.SemiBold, 13, "AccentBrush", 8);
        _commentText = CreateBlock("Commento: training in preparazione.", 6, FontWeights.SemiBold, 13, "AccentBrush", 10);
        _rawLineText = CreateBlock("Ultima riga: -", 7, FontWeights.Normal, 11, "ForegroundMutedBrush", 12);
        _rawLineText.TextWrapping = TextWrapping.Wrap;

        var hint = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources["PanelAltBackgroundBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentOrangeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = "Il commento qualita' usa le ultime metriche YOLO lette dal log.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["ForegroundMutedBrush"],
                FontSize = 11
            }
        };
        Grid.SetRow(hint, 8);
        root.Children.Add(hint);

        Content = root;

        TextBlock CreateBlock(string text, int row, FontWeight weight, double fontSize, string? brushKey, double topMargin = 0)
        {
            var block = new TextBlock
            {
                Margin = new Thickness(0, topMargin, 0, 0),
                Text = text,
                FontWeight = weight,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap
            };

            if (!string.IsNullOrWhiteSpace(brushKey))
            {
                block.Foreground = (Brush)Application.Current.Resources[brushKey];
            }

            Grid.SetRow(block, row);
            root.Children.Add(block);
            return block;
        }
    }

    public void UpdateProgress(YoloTrainingProgress progress)
    {
        _rawLineText.Text = "Ultima riga: " + progress.RawLine;

        var normalizedLine = StripAnsi(progress.RawLine).Trim();
        var epochMatch = EpochLineRegex.Match(normalizedLine);
        if (epochMatch.Success)
        {
            _epochText.Text = $"Epoca: {epochMatch.Groups["epoch"].Value}/{epochMatch.Groups["total"].Value}";
            _lossText.Text =
                $"Loss: box {epochMatch.Groups["box"].Value} | cls {epochMatch.Groups["cls"].Value} | dfl {epochMatch.Groups["dfl"].Value}";
            if (!_lastMap50.HasValue)
            {
                _commentText.Text = "Commento: il modello sta ancora imparando; aspetta le metriche di validazione.";
            }
            return;
        }

        var metricsMatch = MetricsLineRegex.Match(normalizedLine);
        if (metricsMatch.Success)
        {
            _lastPrecision = ParseDouble(metricsMatch.Groups["precision"].Value);
            _lastRecall = ParseDouble(metricsMatch.Groups["recall"].Value);
            _lastMap50 = ParseDouble(metricsMatch.Groups["map50"].Value);
            var map95 = ParseDouble(metricsMatch.Groups["map95"].Value);

            _metricsText.Text =
                $"Metriche: P {_lastPrecision:P1} | R {_lastRecall:P1} | mAP50 {_lastMap50:P1} | mAP50-95 {map95:P1}";
            _humanMetricsText.Text =
                $"Riconoscimenti trovati: {_lastRecall:P1} | " +
                $"Affidabilita': {_lastPrecision:P1} | " +
                $"Qualita' box: {map95:P1}";
            _commentText.Text = "Commento: " + BuildQualityComment(_lastMap50.Value, _lastPrecision.Value, _lastRecall.Value);
        }
    }

    public void MarkCompleted(string message)
    {
        _commentText.Text = "Commento: training completato. " + message;
    }

    public void MarkFailed(string message)
    {
        _commentText.Text = "Commento: training interrotto o fallito. " + message;
    }

    private static string StripAnsi(string text)
    {
        return Regex.Replace(text, @"\x1B\[[0-9;?]*[ -/]*[@-~]", string.Empty);
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;
    }

    private static string BuildQualityComment(double map50, double precision, double recall)
    {
        if (map50 < 0.05 && precision < 0.05 && recall < 0.05)
        {
            return "qualita' ancora molto bassa: il modello non sta ancora riconoscendo bene la classe.";
        }

        if (map50 < 0.30)
        {
            return "qualita' iniziale: il modello inizia a vedere il target ma non e' ancora affidabile.";
        }

        if (map50 < 0.60)
        {
            return "qualita' discreta: il modello sta imparando, ma ci sono ancora errori sensibili.";
        }

        if (map50 < 0.80)
        {
            return "qualita' buona: il detector sta convergendo e inizia a essere utile sul caso reale.";
        }

        return "qualita' alta: il detector sta riconoscendo bene il target nel dataset corrente.";
    }
}
