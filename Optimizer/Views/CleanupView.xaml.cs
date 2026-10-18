using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OpenCube.Services;

namespace OpenCube.Views;

public sealed record FileRow(string Path, string SizeText);

public sealed class CleanupRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _showFiles;

    public CleanupRow(CategoryScan scan, bool canSelect)
    {
        Scan = scan;
        CanSelect = canSelect;
        _isSelected = canSelect && scan.Files.Count > 0;
        TopFiles = scan.Files.OrderByDescending(f => f.Size).Take(100)
                       .Select(f => new FileRow(f.Path, Format.Bytes(f.Size))).ToList();
    }

    public CategoryScan Scan { get; }
    public bool CanSelect { get; }
    public Severity Severity => Scan.Category.Severity;
    public string Warning => Scan.Category.Warning;
    public string Title => Scan.Category.Name;
    public string Description => CanSelect ? Scan.Category.Description : Scan.Category.Description + " (desactivado: falta ejecutar como administrador)";
    public string SizeText => Format.Bytes(Scan.TotalBytes);
    public string CountText => $"{Scan.Files.Count:N0} archivos";
    public bool HasFiles => Scan.Files.Count > 0;
    public IReadOnlyList<FileRow> TopFiles { get; }
    public string ToggleText => _showFiles
        ? "Ocultar archivos"
        : Scan.Files.Count > TopFiles.Count ? $"Ver los {TopFiles.Count} más grandes" : "Ver archivos";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Raise(nameof(IsSelected)); }
    }

    public bool ShowFiles
    {
        get => _showFiles;
        set { _showFiles = value; Raise(nameof(ShowFiles)); Raise(nameof(ToggleText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record ExclusionRow(string Path, bool IsBuiltIn)
{
    public bool CanRemove => !IsBuiltIn;
}

public partial class CleanupView : UserControl
{
    private List<CleanupRow> _rows = [];
    private readonly AppSettings _settings = AppSettings.Load();

    public CleanupView()
    {
        InitializeComponent();
        RefreshExclusions();
    }

    private void RefreshExclusions()
    {
        ExclusionList.ItemsSource = AppSettings.BuiltInExclusions.Select(p => new ExclusionRow(p, true))
            .Concat(_settings.Exclusions.Select(p => new ExclusionRow(p, false))).ToList();
        ExclusionsButton.Content = $"Exclusiones ({_settings.AllExclusions().Count})";
    }

    private void OnToggleExclusions(object sender, RoutedEventArgs e) =>
        ExclusionsPanel.Visibility = ExclusionsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnAddExclusion(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Elige una carpeta que nunca se debe limpiar" };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        if (!_settings.Exclusions.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Exclusions.Add(dialog.FolderName);
            var exclusions = _settings.Exclusions.ToList();
            AppSettings.Update(s => s.Exclusions = exclusions);
            RefreshExclusions();
            StatusText.Text = "Exclusión añadida. Vuelve a analizar para actualizar la lista.";
        }
    }

    private void OnRemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExclusionRow { IsBuiltIn: false } row }) return;
        _settings.Exclusions.RemoveAll(p => string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase));
        var exclusions = _settings.Exclusions.ToList();
        AppSettings.Update(s => s.Exclusions = exclusions);
        RefreshExclusions();
        StatusText.Text = "Exclusión quitada. Vuelve a analizar para actualizar la lista.";
    }

    private IEnumerable<CleanupRow> Selected => _rows.Where(r => r.IsSelected && r.HasFiles);

    private async void OnAnalyze(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync(string? doneMessage = null)
    {
        SetBusy(true);
        StatusText.Text = "Analizando…";
        TotalBar.Visibility = Visibility.Collapsed;

        var admin = StartupService.IsAdmin();
        var exclusions = _settings.AllExclusions();
        var scans = await Task.Run(() => CleanupService.DefaultCategories().Select(c => CleanupService.Scan(c, exclusions)).ToList());

        foreach (var old in _rows) old.PropertyChanged -= OnRowChanged;
        _rows = scans.Select(s => new CleanupRow(s, canSelect: admin || !s.Category.NeedsAdmin)).ToList();
        foreach (var row in _rows) row.PropertyChanged += OnRowChanged;
        List.ItemsSource = _rows;

        SetBusy(false);
        StatusText.Text = doneMessage ?? "Análisis completo. Revisa la lista y elige qué limpiar.";
        TotalBar.Visibility = Visibility.Visible;
        UpdateTotals();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupRow.IsSelected)) UpdateTotals();
    }

    private void UpdateTotals()
    {
        var picked = Selected.ToList();
        var count = picked.Sum(r => r.Scan.Files.Count);
        var bytes = picked.Sum(r => r.Scan.TotalBytes);
        TotalText.Text = count == 0 ? "Nada seleccionado" : $"Seleccionado: {Format.Bytes(bytes)} en {count:N0} archivos";
        DeleteButton.IsEnabled = count > 0;
    }

    private void OnToggleFiles(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CleanupRow row }) row.ShowFiles = !row.ShowFiles;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var picked = Selected.ToList();
        var count = picked.Sum(r => r.Scan.Files.Count);
        var bytes = picked.Sum(r => r.Scan.TotalBytes);
        ConfirmText.Text = $"Se van a eliminar {count:N0} archivos ({Format.Bytes(bytes)}) de {picked.Count} " +
                           $"{(picked.Count == 1 ? "categoría" : "categorías")}. Esta acción no se puede deshacer. ¿Continuar?";
        TotalBar.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        TotalBar.Visibility = Visibility.Visible;
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var scans = Selected.Select(r => r.Scan).ToList(); // exactly what the preview showed
        ConfirmPanel.Visibility = Visibility.Collapsed;
        SetBusy(true);
        StatusText.Text = "Eliminando…";

        var exclusions = _settings.AllExclusions();
        var result = await Task.Run(() => CleanupService.Clean(scans, exclusions));

        var message = $"Listo: liberaste {Format.Bytes(result.FreedBytes)} ({result.Deleted:N0} archivos).";
        if (result.Skipped > 0) message += $" {result.Skipped:N0} se omitieron porque estaban en uso.";
        await AnalyzeAsync(message);
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AnalyzeButton.IsEnabled = !busy;
    }
}
