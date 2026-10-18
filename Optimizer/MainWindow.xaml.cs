using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using OpenCube.Services;
using OpenCube.Views;
using Wpf.Ui.Controls;

namespace OpenCube;

public partial class MainWindow : FluentWindow
{
    private const double ExpandedWidth = 192; // + 18 of margins = the original 210
    private const double CollapsedWidth = 64;
    private const string AdminWarning = "Va a reiniciar OpenCube con permisos de administrador (Windows te pedirá confirmación). " +
        "Con esos permisos la app puede modificar archivos del sistema, así que úsalo solo para limpiar los temporales de Windows " +
        "o crear un punto de restauración, y revisa bien la lista antes de borrar.";

    private sealed record Page(string Key, string Title, SymbolRegular Icon, Func<UserControl> Create);

    private readonly List<Page> _pages;
    private readonly Dictionary<string, UserControl> _created = [];
    private readonly Dictionary<string, Wpf.Ui.Controls.Button> _buttons = [];
    private readonly bool _isAdmin = StartupService.IsAdmin();
    private bool _collapsed;

    public MainWindow()
    {
        InitializeComponent();

        _pages =
        [
            new("panel", "Panel", SymbolRegular.Home24, () => new DashboardView()),
            new("limpieza", "Limpieza", SymbolRegular.Broom24, () => new CleanupView()),
            new("disco", "Disco", SymbolRegular.DataPie24, () => new DiskView()),
            new("programas", "Programas", SymbolRegular.AppsList24, () => new UninstallView()),
            new("innecesario", "Innecesario", SymbolRegular.Warning24, () => new BloatView()),
            new("memoria", "Memoria", SymbolRegular.Flash24, () =>
            {
                var view = new MemoryView();
                view.OpenProcessesRequested += () => NavigateTo("procesos");
                return view;
            }),
            new("procesos", "Procesos", SymbolRegular.TaskListSquareLtr24, () => new ProcessesView()),
            new("ajustes", "Ajustes", SymbolRegular.Options24, () => new TweaksView()),
            new("inicio", "Inicio", SymbolRegular.Rocket24, () => new StartupView()),
        ];

        foreach (var page in _pages)
        {
            var key = page.Key;
            var button = new Wpf.Ui.Controls.Button
            {
                Icon = new SymbolIcon(page.Icon),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4),
            };
            button.Click += (_, _) => NavigateTo(key);
            _buttons[key] = button;
            NavPanel.Children.Add(button);
        }

        NavigateTo("panel");
        SetCollapsed(AppSettings.Load().SidebarCollapsed, animate: false);
    }

    private void NavigateTo(string key)
    {
        var page = _pages.First(p => p.Key == key);
        if (!_created.TryGetValue(key, out var view)) _created[key] = view = page.Create();

        Host.Content = view;
        foreach (var (k, button) in _buttons)
            button.Appearance = k == key ? ControlAppearance.Primary : ControlAppearance.Transparent;
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = !_collapsed;
        SetCollapsed(collapsed, animate: true);
        AppSettings.Update(s => s.SidebarCollapsed = collapsed);
    }

    /// <summary>Icons only (collapsed) or icons with names. Names disappear first when collapsing and come back last when expanding.</summary>
    private void SetCollapsed(bool collapsed, bool animate)
    {
        _collapsed = collapsed;
        ToggleSidebar.Icon = new SymbolIcon(collapsed ? SymbolRegular.PanelLeftExpand24 : SymbolRegular.PanelLeftContract24);

        var target = collapsed ? CollapsedWidth : ExpandedWidth;
        if (!animate)
        {
            Sidebar.Width = target;
            ApplyLabels(collapsed);
            return;
        }

        if (collapsed) ApplyLabels(true);

        var from = Sidebar.ActualWidth > 0 ? Sidebar.ActualWidth : (collapsed ? ExpandedWidth : CollapsedWidth);
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = target;
            if (!collapsed) ApplyLabels(false);
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private void ApplyLabels(bool collapsed)
    {
        var alignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        foreach (var page in _pages)
        {
            var button = _buttons[page.Key];
            button.Content = collapsed ? null : page.Title;
            button.HorizontalContentAlignment = alignment;
            button.ToolTip = collapsed ? page.Title : null; // the name is still one hover away
        }

        AdminButton.Content = collapsed ? null : "Administrador";
        AdminButton.HorizontalContentAlignment = alignment;
        AdminButton.ToolTip = collapsed ? "Modo administrador. " + AdminWarning : null;
        AdminBadge.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        if (_isAdmin)
        {
            AdminRow.Visibility = Visibility.Collapsed;
            AdminLabel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* the user cancelled the UAC prompt */ }
    }
}
