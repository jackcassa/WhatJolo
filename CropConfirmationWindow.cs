using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhatJolo;

internal sealed class CropConfirmationWindow : Window
{
    private readonly TextBox _nameTextBox;

    public bool IsAccepted { get; private set; }
    public string EnteredName => _nameTextBox.Text.Trim();

    public CropConfirmationWindow(BitmapSource previewImage, string cropClass, string initialName = "")
    {
        Title = "Conferma acquisizione crop";
        Width = 420;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["AppBackgroundBrush"];
        Foreground = (Brush)Application.Current.Resources["ForegroundPrimaryBrush"];

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "Conferma acquisizione",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"]
        });
        header.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Text = $"Classe selezionata: {cropClass}"
        });
        header.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Text = "Vuoi salvare questa selezione?"
        });
        root.Children.Add(header);

        var namePanel = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(namePanel, 1);
        namePanel.Children.Add(new TextBlock
        {
            Text = "Nome selezione",
            FontWeight = FontWeights.SemiBold
        });
        _nameTextBox = new TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            Text = string.IsNullOrWhiteSpace(initialName) ? cropClass : initialName
        };
        namePanel.Children.Add(_nameTextBox);
        root.Children.Add(namePanel);

        var previewBorder = new Border
        {
            Margin = new Thickness(0, 16, 0, 16),
            Padding = new Thickness(12),
            Background = (Brush)Application.Current.Resources["PanelBackgroundBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentOrangeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        Grid.SetRow(previewBorder, 2);

        var previewImageControl = new Image
        {
            Source = previewImage,
            Stretch = Stretch.Uniform
        };

        previewBorder.Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = previewImageControl
        };
        root.Children.Add(previewBorder);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var noButton = new Button
        {
            Content = "Annulla",
            MinWidth = 100,
            Margin = new Thickness(0, 0, 8, 0)
        };
        noButton.Click += (_, _) =>
        {
            IsAccepted = false;
            Close();
        };
        buttons.Children.Add(noButton);

        var yesButton = new Button
        {
            Content = "Salva",
            MinWidth = 100
        };
        yesButton.Click += (_, _) =>
        {
            IsAccepted = true;
            Close();
        };
        buttons.Children.Add(yesButton);

        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _nameTextBox.Focus();
            _nameTextBox.SelectAll();
        };
    }
}
