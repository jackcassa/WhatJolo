using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace WhatJolo;

internal sealed class NumberPromptWindow : Window
{
    private readonly TextBox _valueTextBox;
    private readonly int _minimum;
    private readonly int _maximum;

    public NumberPromptWindow(string title, string label, int initialValue, int minimum = 1, int maximum = 500, string confirmButtonText = "Conferma")
    {
        _minimum = minimum;
        _maximum = maximum;

        Title = title;
        Width = 360;
        Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["AppBackgroundBrush"];
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ForegroundPrimaryBrush"];

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var inputGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(inputGrid, 1);

        var decreaseButton = new Button
        {
            Content = "-",
            Width = 44,
            Height = 36,
            Margin = new Thickness(0, 0, 8, 0)
        };
        decreaseButton.Click += (_, _) => SetValue(Value - 1);
        inputGrid.Children.Add(decreaseButton);

        _valueTextBox = new TextBox
        {
            Height = 36,
            Text = Math.Clamp(initialValue, minimum, maximum).ToString(CultureInfo.InvariantCulture),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 18,
            FontWeight = FontWeights.Bold
        };
        Grid.SetColumn(_valueTextBox, 1);
        inputGrid.Children.Add(_valueTextBox);

        var increaseButton = new Button
        {
            Content = "+",
            Width = 44,
            Height = 36,
            Margin = new Thickness(8, 0, 0, 0)
        };
        increaseButton.Click += (_, _) => SetValue(Value + 1);
        Grid.SetColumn(increaseButton, 2);
        inputGrid.Children.Add(increaseButton);

        root.Children.Add(inputGrid);

        var buttons = new StackPanel
        {
            Margin = new Thickness(0, 18, 0, 0),
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
            DialogResult = false;
            Close();
        };
        buttons.Children.Add(cancelButton);

        var okButton = new Button
        {
            Content = confirmButtonText,
            MinWidth = 110
        };
        okButton.Click += (_, _) =>
        {
            if (!TryNormalizeValue())
            {
                MessageBox.Show(this, $"Inserisci un numero tra {_minimum} e {_maximum}.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        };
        buttons.Children.Add(okButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _valueTextBox.Focus();
            _valueTextBox.SelectAll();
        };
    }

    public int Value
    {
        get
        {
            return int.TryParse(_valueTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : _minimum;
        }
    }

    private void SetValue(int value)
    {
        _valueTextBox.Text = Math.Clamp(value, _minimum, _maximum).ToString(CultureInfo.InvariantCulture);
        _valueTextBox.SelectAll();
    }

    private bool TryNormalizeValue()
    {
        if (!int.TryParse(_valueTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value < _minimum || value > _maximum)
        {
            return false;
        }

        _valueTextBox.Text = value.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
