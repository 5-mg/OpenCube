using System.Windows;
using System.Windows.Controls;
using OpenCube.Services;

namespace OpenCube.Views;

public sealed class AppRow
{
    public AppRow(InstalledApp app) => App = app;

    public InstalledApp App { get; }
    public string Name => App.Title;
    public bool IsStore => App.Source == AppSource.Store;
    public string SizeText => App.SizeBytes > 0 ? Format.Bytes(App.SizeBytes) : "—";
    public string DateText => App.InstallDate?.ToString("dd/MM/yyyy") ?? "";
    public string Details => string.Join("  ·  ", new[] { App.Version, IsStore ? null : App.Publisher }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public partial class UninstallView : UserControl
{
    private List<InstalledApp> _all = [];
    private InstalledApp? _pending;
    private bool _loaded;

    public UninstallView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ReloadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Leyendo los programas instalados…";
        ConfirmPanel.Visibility = Visibility.Collapsed;

        _all = await Task.Run(() => InstalledAppsService.GetProgramApps().Concat(InstalledAppsService.GetStoreApps()).ToList());

        RefreshButton.IsEnabled = true;
        StatusText.Text = "";
        Subtitle.Text = $"{_all.Count} programas y aplicaciones instalados. Los componentes del sistema y las actualizaciones no se muestran.";
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsLoaded && _all.Count == 0) return;

        IEnumerable<InstalledApp> q = _all;
        var text = Search.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            q = q.Where(a => a.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                             a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                             (a.Publisher?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));

        q = Sort.SelectedIndex switch
        {
            1 => q.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase),
            2 => q.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase),
        };

        var rows = q.Select(a => new AppRow(a)).ToList();
        List.ItemsSource = rows;
        StatusText.Text = rows.Count == _all.Count ? "" : $"{rows.Count} resultados";
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppRow row }) return;
        _pending = row.App;

        ConfirmText.Text = row.IsStore
            ? $"Se va a quitar la aplicación «{row.Name}» de tu usuario. No habrá una ventana de confirmación aparte. ¿Continuar?"
            : $"Se va a abrir el desinstalador oficial de «{row.Name}». La eliminación la confirmas en su ventana. ¿Abrirlo?";
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } app) return;
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = app.Source == AppSource.Store ? "Quitando la aplicación…" : "Abriendo el desinstalador…";

        var error = await Task.Run(() => InstalledAppsService.StartUninstall(app));
        if (error is not null) { StatusText.Text = error; return; }

        if (app.Source == AppSource.Store) await ReloadAsync();
        else StatusText.Text = "Desinstalador abierto. Cuando termines, pulsa Actualizar para refrescar la lista.";
    }
}
