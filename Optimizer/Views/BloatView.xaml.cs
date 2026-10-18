using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenCube.Services;

namespace OpenCube.Views;

public sealed class BloatRow
{
    public BloatRow(BloatFinding finding) => Finding = finding;

    public BloatFinding Finding { get; }
    public string Name => Finding.App.Title == Finding.Rule.Title ? Finding.Rule.Title : $"{Finding.Rule.Title}  ({Finding.App.Title})";
    public string Reason => Finding.Rule.Reason;
    public string Source => Finding.App.Source == AppSource.Store ? "Aplicación de la Tienda" : "Programa instalado";

    public string ConfidenceText => Finding.Rule.Confidence switch
    {
        Confidence.High => "Casi siempre sobra",
        Confidence.Medium => "Depende de si lo usas",
        _ => "Solo si estás seguro",
    };

    public Brush ConfidenceBrush => new SolidColorBrush(Finding.Rule.Confidence switch
    {
        Confidence.High => Color.FromRgb(0x4C, 0xC3, 0x8A),
        Confidence.Medium => Color.FromRgb(0xFF, 0xB0, 0x20),
        _ => Color.FromRgb(0x9A, 0x9A, 0x9A),
    });
}

public partial class BloatView : UserControl
{
    private InstalledApp? _pending;

    public BloatView() => InitializeComponent();

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Revisando programas y aplicaciones…";

        var (findings, total) = await Task.Run(() =>
        {
            var apps = InstalledAppsService.GetProgramApps().Concat(InstalledAppsService.GetStoreApps()).ToList();
            return (BloatCatalog.Detect(apps), apps.Count);
        });

        List.ItemsSource = findings.Select(f => new BloatRow(f)).ToList();
        StatusText.Text = findings.Count == 0
            ? $"No se encontró nada sospechoso entre {total} elementos revisados."
            : $"{findings.Count} sugerencia(s) entre {total} elementos revisados.";
        ScanButton.IsEnabled = true;
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BloatRow row }) return;
        _pending = row.Finding.App;

        ConfirmText.Text = row.Finding.App.Source == AppSource.Store
            ? $"Se va a quitar la aplicación «{row.Finding.App.Title}» de tu usuario, sin ventana de confirmación aparte. ¿Continuar?"
            : $"Se va a abrir el desinstalador oficial de «{row.Finding.App.Title}». La eliminación la confirmas en su ventana. ¿Abrirlo?";
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

        StatusText.Text = app.Source == AppSource.Store
            ? "Aplicación quitada. Pulsa «Buscar» otra vez para actualizar la lista."
            : "Desinstalador abierto. Cuando termines, pulsa «Buscar» otra vez para actualizar la lista.";
    }
}
