using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenCube.Services;

namespace OpenCube.Views;

public sealed class ProcessRowVm : INotifyPropertyChanged
{
    private bool _isSelected;

    public ProcessRowVm(ProcGroup group) => Group = group;

    public ProcGroup Group { get; }
    public string Title => Group.Items.Count > 1 ? $"{Group.Name}  ×{Group.Items.Count}" : Group.Name;
    public string MemoryText => Format.Bytes(Group.MemoryBytes);
    public string TagText => Group.Tag == ProcTag.Suggested ? "SUGERIDO" : "OTRO";
    public Brush TagBrush => new SolidColorBrush(Group.Tag == ProcTag.Suggested ? Color.FromRgb(0x4C, 0xC3, 0x8A) : Color.FromRgb(0x9A, 0x9A, 0x9A));
    public string Reason => Group.Reason ?? "Proceso en segundo plano que no está en la lista de sugeridos. Ciérralo solo si sabes qué es.";
    public string Detail => (Group.HasWindow ? "Con ventana abierta" : "Sin ventana (segundo plano)") + (Group.Items[0].Path is { } p ? "  ·  " + p : "");

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ProcessesView : UserControl
{
    private List<ProcessRowVm> _rows = [];
    private List<ProcInfo> _retry = [];   // windows that ignored the normal close request
    private bool _forceMode;

    public ProcessesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) await ReloadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        StatusText.Text = "Leyendo procesos…";
        ConfirmPanel.Visibility = Visibility.Collapsed;
        TotalBar.Visibility = Visibility.Visible;

        var groups = await Task.Run(ProcessService.GetGroups);
        var showOthers = ShowOthers.IsChecked == true;

        foreach (var old in _rows) old.PropertyChanged -= OnRowChanged;
        _rows = groups.Where(g => showOthers || g.Tag == ProcTag.Suggested).Select(g => new ProcessRowVm(g)).ToList();
        foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
        List.ItemsSource = _rows;

        var suggested = groups.Count(g => g.Tag == ProcTag.Suggested);
        StatusText.Text = suggested == 0 && !showOthers
            ? "No hay procesos claramente innecesarios ahora mismo. Activa «Mostrar también otros procesos» para ver más."
            : $"{suggested} sugerido(s)" + (showOthers ? $", {groups.Count - suggested} otros" : "");
        UpdateTotals();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateTotals();

    private void OnSelectSuggested(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = r.Group.Tag == ProcTag.Suggested;
    }

    private IEnumerable<ProcessRowVm> Selected => _rows.Where(r => r.IsSelected);

    private void UpdateTotals()
    {
        var picked = Selected.ToList();
        var count = picked.Sum(r => r.Group.Items.Count);
        TotalText.Text = count == 0 ? "Nada seleccionado" : $"Seleccionado: {count} proceso(s), {Format.Bytes(picked.Sum(r => r.Group.MemoryBytes))} de RAM";
        CloseButton.IsEnabled = count > 0;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var picked = Selected.ToList();
        var count = picked.Sum(r => r.Group.Items.Count);
        ConfirmText.Text = $"Se van a cerrar {count} proceso(s) de {picked.Count} programa(s) (≈ {Format.Bytes(picked.Sum(r => r.Group.MemoryBytes))}). " +
                           "Si alguno tenía trabajo sin guardar, se perderá. ¿Continuar?";
        _forceMode = false;
        ConfirmYes.Content = "Sí, cerrar";
        TotalBar.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _forceMode = false;
        _retry = [];
        ConfirmPanel.Visibility = Visibility.Collapsed;
        TotalBar.Visibility = Visibility.Visible;
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var force = _forceMode;
        var items = force ? _retry : Selected.SelectMany(r => r.Group.Items).ToList();
        _forceMode = false;
        _retry = [];
        ConfirmPanel.Visibility = Visibility.Collapsed;
        TotalBar.Visibility = Visibility.Visible;
        StatusText.Text = force ? "Forzando el cierre…" : "Cerrando…";

        var result = await Task.Run(() => ProcessService.Close(items, force));
        await ReloadAsync();

        var message = $"Cerrados {result.Closed} proceso(s), unos {Format.Bytes(result.FreedBytes)} de RAM.";
        if (result.Failed > 0) message += $" {result.Failed} no se cerraron de forma normal.";
        if (result.Skipped > 0) message += $" {result.Skipped} se omitieron por seguridad.";
        StatusText.Text = message;

        // Many programs (Epic Games, Discord, Steam...) only hide in the tray when asked to close: offer to force it.
        if (result.Retry is { Count: > 0 } retry)
        {
            _retry = retry.ToList();
            _forceMode = true;
            var names = string.Join(", ", retry.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
            ConfirmText.Text = $"No se cerraron de forma normal: {names}. Muchos programas solo se minimizan a la bandeja al pedirles que cierren, " +
                               "o están preguntando algo en su ventana. ¿Forzar el cierre? Si tenían trabajo sin guardar, se perderá.";
            ConfirmYes.Content = "Sí, forzar cierre";
            TotalBar.Visibility = Visibility.Collapsed;
            ConfirmPanel.Visibility = Visibility.Visible;
        }
    }
}
