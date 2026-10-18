using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenCube.Services;
using Wpf.Ui.Controls;

namespace OpenCube.Views;

public sealed class TweakRow
{
    public TweakRow(Tweak tweak, TweakState state)
    {
        Tweak = tweak;
        State = state;
    }

    public Tweak Tweak { get; }
    public TweakState State { get; }
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Note => Tweak.Note;
    public Severity Severity => Tweak.Severity;
    public string Warning => $"{Tweak.Description} {Tweak.Note} Se guarda el valor original: puedes revertirlo en cualquier momento.";
    public bool IsOn => State == TweakState.On;
    public bool HasStateText => State == TweakState.Partial;
    public string StateText => "Aplicado solo en parte. Actívalo para completarlo o desactívalo para revertirlo.";
}

public sealed record TweakGroup(string Name, IReadOnlyList<TweakRow> Items);

public partial class TweaksView : UserControl
{
    public TweaksView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        Groups.ItemsSource = TweakService.All
            .GroupBy(t => t.Category)
            .Select(g => new TweakGroup(g.Key, g.Select(t => new TweakRow(t, TweakService.GetState(t))).ToList()))
            .ToList();
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not TweakRow row) return;

        var enable = toggle.IsChecked == true;
        var error = enable ? TweakService.Apply(row.Tweak) : TweakService.Revert(row.Tweak);

        if (error is null)
        {
            var verb = enable ? "aplicado" : "revertido";
            var extra = row.Tweak.NeedsRelogin ? " Cierra sesión y vuelve a entrar para que surta efecto." : "";
            Show($"«{row.Title}» {verb}.{extra}", isError: false);
        }
        else
        {
            Show(error, isError: true);
        }
        Reload();
    }

    private async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        Show("Creando punto de restauración…", isError: false);
        var error = await Task.Run(TweakService.CreateRestorePoint);
        RestoreButton.IsEnabled = true;
        if (error is null) Show("Punto de restauración creado.", isError: false);
        else Show(error, isError: true);
    }

    private void Show(string message, bool isError)
    {
        StatusText.Text = message;
        StatusBar.Background = new SolidColorBrush(isError ? Color.FromArgb(0xB3, 0xC4, 0x2B, 0x1C) : Color.FromArgb(0xB3, 0x2B, 0x6E, 0x3F));
        StatusBar.Visibility = Visibility.Visible;
    }
}
