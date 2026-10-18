using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenCube.Services;

namespace OpenCube.Views;

public sealed record ProcessRow(string Name, string MemoryText, double Relative);

public partial class DashboardView : UserControl
{
    private readonly SystemStats _stats = new();
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(4) };

    public DashboardView()
    {
        InitializeComponent();
        _fast.Tick += (_, _) => RefreshFast();
        _slow.Tick += async (_, _) => await RefreshProcessesAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFast();
        _fast.Start();
        _slow.Start();
        await RefreshProcessesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _fast.Stop();
        _slow.Stop();
    }

    private void RefreshFast()
    {
        var cpu = _stats.ReadCpuPercent();
        CpuText.Text = $"{cpu:0}%";
        CpuBar.Value = cpu;

        var mem = _stats.ReadMemory();
        RamText.Text = $"{mem.Percent:0}%";
        RamDetail.Text = $"{mem.UsedGb:0.0} de {mem.TotalGb:0.0} GB";
        RamBar.Value = mem.Percent;

        if (_stats.ReadSystemDisk() is { } disk)
        {
            DiskText.Text = $"{disk.Percent:0}%";
            DiskDetail.Text = $"{disk.UsedGb:0} de {disk.TotalGb:0} GB";
            DiskBar.Value = disk.Percent;
        }

        var up = SystemStats.Uptime;
        UptimeText.Text = $"Encendido hace {(int)up.TotalDays} d {up.Hours} h {up.Minutes} min";
    }

    private async Task RefreshProcessesAsync()
    {
        var top = await Task.Run(() => SystemStats.TopProcessesByMemory(8));
        var max = top.Count > 0 ? top.Max(p => p.MemoryMb) : 1;
        ProcList.ItemsSource = top
            .Select(p => new ProcessRow(
                p.Name,
                p.MemoryMb >= 1024
                    ? (p.MemoryMb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
                    : p.MemoryMb.ToString("0", CultureInfo.CurrentCulture) + " MB",
                p.MemoryMb / max * 100))
            .ToList();
    }
}
