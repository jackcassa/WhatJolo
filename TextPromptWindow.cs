using System.Windows;
using System.Windows.Controls;

namespace WhatJolo;

internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _textBox;
    public bool IsAccepted { get; private set; }

    public TextPromptWindow(string title, string label, string initialValue = "")
    {
        Title = title;
        Width = 420;
        Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["AppBackgroundBrush"];
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ForegroundPrimaryBrush"];

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textLabel = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold
        };
        root.Children.Add(textLabel);

        _textBox = new TextBox
        {
            Margin = new Thickness(0, 10, 0, 0),
            Text = initialValue
        };
        Grid.SetRow(_textBox, 1);
        root.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Margin = new Thickness(0, 16, 0, 0),
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "Annulla",
            MinWidth = 100,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.Click += (_, _) =>
        {
            IsAccepted = false;
            Close();
        };
        buttons.Children.Add(cancelButton);

        var okButton = new Button
        {
            Content = "Crea progetto",
            MinWidth = 120
        };
        okButton.Click += (_, _) =>
        {
            IsAccepted = true;
            Close();
        };
        buttons.Children.Add(okButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _textBox.Focus();
    }

    public string EnteredText => _textBox.Text.Trim();
}
