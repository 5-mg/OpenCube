using System.Windows;
using System.Windows.Controls;
using OpenCube.Services;
using Wpf.Ui.Controls;

namespace OpenCube.Views;

public sealed class StartupRow
{
    public StartupRow(StartupEntry entry) => Entry = entry;

    public StartupEntry Entry { get; set; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Source => Entry.Source;
    public bool IsEnabled => Entry.IsEnabled;
    public bool CanToggle => !Entry.NeedsAdmin;
}

public partial class StartupView : UserControl
{
    public StartupView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        var entries = StartupService.GetEntries();
        List.ItemsSource = entries.Select(x => new StartupRow(x)).ToList();

        var active = entries.Count(x => x.IsEnabled);
        Summary.Text = $"{active} de {entries.Count} se ejecutan al iniciar Windows. " +
            "Desactivar un programa no lo borra, solo evita que arranque; puedes volver a activarlo cuando quieras." +
            (StartupService.IsAdmin() ? "" : " Los del sistema requieren ejecutar como administrador.");
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not StartupRow row) return;

        ErrorBar.Visibility = Visibility.Collapsed;
        var enable = toggle.IsChecked == true;
        var error = StartupService.SetEnabled(row.Entry, enable);
        if (error is null)
        {
            row.Entry = row.Entry with { IsEnabled = enable };
            Reload();
            return;
        }

        toggle.IsChecked = !enable;
        ErrorText.Text = error;
        ErrorBar.Visibility = Visibility.Visible;
    }
}
