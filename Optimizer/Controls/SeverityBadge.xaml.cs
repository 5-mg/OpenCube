using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenCube.Services;
using Wpf.Ui.Controls;

namespace OpenCube.Controls;

/// <summary>Small icon that explains on hover what an action will do and how serious it is.</summary>
public partial class SeverityBadge : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(Severity), typeof(SeverityBadge),
        new PropertyMetadata(Severity.Low, (d, _) => ((SeverityBadge)d).Update()));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(SeverityBadge),
        new PropertyMetadata("", (d, _) => ((SeverityBadge)d).Update()));

    public SeverityBadge()
    {
        InitializeComponent();
        Update();
    }

    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private void Update()
    {
        if (Icon is null) return;

        var (symbol, color) = Severity switch
        {
            Severity.Low => (SymbolRegular.Info24, Color.FromRgb(0x5A, 0xB0, 0xFF)),
            Severity.Medium => (SymbolRegular.Warning24, Color.FromRgb(0xFF, 0xB0, 0x20)),
            _ => (SymbolRegular.ErrorCircle24, Color.FromRgb(0xFF, 0x5A, 0x4D)),
        };

        var brush = new SolidColorBrush(color);
        Icon.Symbol = symbol;
        Icon.Foreground = brush;
        Header.Text = SeverityInfo.Label(Severity);
        Header.Foreground = brush;
        Body.Text = Message;
        System.Windows.Automation.AutomationProperties.SetName(this, Header.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(this, Message);
        Visibility = string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
