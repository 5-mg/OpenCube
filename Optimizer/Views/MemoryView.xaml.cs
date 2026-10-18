using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenCube.Services;

namespace OpenCube.Views;

public partial class MemoryView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshing;

    public event Action? OpenProcessesRequested;

    public MemoryView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();

        if (!StartupService.IsAdmin())
        {
            PurgeButton.IsEnabled = false;
            PurgeNote.Text = "Requiere ejecutar como administrador (botón «Administrador» abajo a la izquierda).";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var m = await Task.Run(MemoryService.Read);
            UsedText.Text = $"{m.UsedPercent:0}%";
            UsedDetail.Text = $"{Format.Bytes(m.UsedBytes)} de {Format.Bytes(m.TotalBytes)}";
            UsedBar.Value = m.UsedPercent;
            FreeText.Text = Format.Bytes(m.AvailableBytes);
            StandbyText.Text = m.StandbyBytes > 0 ? Format.Bytes(m.StandbyBytes) : "—";
        }
        finally { _refreshing = false; }
    }

    private async void OnTrim(object sender, RoutedEventArgs e)
    {
        TrimButton.IsEnabled = false;
        Show("Reduciendo la memoria de los programas en segundo plano…", false);

        var result = await Task.Run(MemoryService.TrimBackgroundProcesses);

        TrimButton.IsEnabled = true;
        Show(result.FreedBytes > 0
            ? $"Listo: se liberaron unos {Format.Bytes(result.FreedBytes)} de RAM (afectó a {result.Processes} procesos). Es temporal: los programas recuperarán la memoria que necesiten."
            : $"Listo: se revisaron {result.Processes} procesos, pero no había memoria inactiva significativa que liberar.", false);
        await RefreshAsync();
    }

    private async void OnPurge(object sender, RoutedEventArgs e)
    {
        PurgeButton.IsEnabled = false;
        Show("Vaciando la memoria en espera…", false);

        var before = (await Task.Run(MemoryService.Read)).AvailableBytes;
        var error = await Task.Run(MemoryService.PurgeStandbyList);
        var after = (await Task.Run(MemoryService.Read)).AvailableBytes;

        PurgeButton.IsEnabled = StartupService.IsAdmin();
        if (error is not null) Show(error, true);
        else Show($"Listo: memoria disponible {Format.Bytes(before)} → {Format.Bytes(after)}. Notarás el disco algo más lento un rato mientras Windows vuelve a llenar su caché.", false);
        await RefreshAsync();
    }

    private void OnOpenProcesses(object sender, RoutedEventArgs e) => OpenProcessesRequested?.Invoke();

    private void Show(string message, bool isError)
    {
        ResultText.Text = message;
        ResultBar.Background = new SolidColorBrush(isError ? Color.FromArgb(0xB3, 0xC4, 0x2B, 0x1C) : Color.FromArgb(0xB3, 0x2B, 0x6E, 0x3F));
        ResultBar.Visibility = Visibility.Visible;
    }
}
