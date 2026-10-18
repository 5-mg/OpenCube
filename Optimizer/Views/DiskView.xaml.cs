using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenCube.Services;

namespace OpenCube.Views;

public sealed record LocationItem(string Label, string Path);

public sealed class FolderRow
{
    public FolderRow(DirNode node, long parentSize)
    {
        Node = node;
        Percent = parentSize == 0 ? 0 : node.Size / (double)parentSize * 100;
    }

    public DirNode Node { get; }
    public string Name => Node.Name;
    public string SizeText => Format.Bytes(Node.Size);
    public double Percent { get; }
    public string PercentText => Percent < 1 ? "<1%" : $"{Percent:0}%";
    public string FilesText => $"{Node.FileCount:N0} archivos";
}

public partial class DiskView : UserControl
{
    private readonly List<LocationItem> _locations = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DiskScanner? _scanner;
    private CancellationTokenSource? _cts;
    private DirNode? _current;

    public DiskView()
    {
        InitializeComponent();

        _locations.Add(new LocationItem($"Mi carpeta de usuario ({Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)})",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            _locations.Add(new LocationItem($"{d.Name}  ({Format.Bytes(d.TotalSize - d.AvailableFreeSpace)} usados de {Format.Bytes(d.TotalSize)})", d.Name));

        Locations.ItemsSource = _locations;
        Locations.SelectedIndex = 0;
        _timer.Tick += (_, _) => StatusText.Text = $"Analizando… {_scanner?.FilesScanned:N0} archivos revisados";
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Elige la carpeta que quieres analizar" };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        var item = new LocationItem($"Carpeta elegida ({dialog.FolderName})", dialog.FolderName);
        _locations.Add(item);
        Locations.Items.Refresh();
        Locations.SelectedItem = item;
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if (Locations.SelectedItem is not LocationItem loc) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _scanner = new DiskScanner();
        var scanner = _scanner;

        AnalyzeButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "Analizando… (en discos grandes puede tardar varios minutos)";
        _timer.Start();

        try
        {
            var root = await Task.Run(() => scanner.Scan(loc.Path, ct), ct);
            _timer.Stop();
            StatusText.Text = $"Listo: {root.FileCount:N0} archivos analizados.";
            Show(root);
        }
        catch (OperationCanceledException)
        {
            _timer.Stop();
            StatusText.Text = "Análisis cancelado.";
        }
        finally
        {
            _timer.Stop();
            AnalyzeButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Show(DirNode node)
    {
        _current = node;
        Crumb.Visibility = Visibility.Visible;
        FoldersHeader.Visibility = Visibility.Visible;
        PathText.Text = node.Path;
        TotalText.Text = $"{Format.Bytes(node.Size)} en {node.FileCount:N0} archivos";
        UpButton.IsEnabled = node.Parent is not null;

        Folders.ItemsSource = node.Children.Where(c => c.Size > 0).Take(200).Select(c => new FolderRow(c, node.Size)).ToList();
        OwnFilesText.Text = node.OwnFileCount > 0
            ? $"+ {node.OwnFileCount:N0} archivos sueltos en esta carpeta ({Format.Bytes(node.OwnFilesSize)})"
            : "";

        var big = node.BigFilesInTree().OrderByDescending(f => f.Size).Take(30).ToList();
        BigFiles.ItemsSource = big.Select(f => new FileRow(f.Path, Format.Bytes(f.Size))).ToList();
        BigHeader.Visibility = big.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderRow row }) Show(row.Node);
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent is { } parent) Show(parent);
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileRow row } || !File.Exists(row.Path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = true });
    }
}
