using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Cmux.Views;

public partial class ColorPickerWindow : Window
{
    private bool _suppress;

    public string SelectedHex { get; private set; } = "#FF818CF8";

    public ColorPickerWindow(string? initialHex)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);

        var color = ParseHex(initialHex) ?? (Color)ColorConverter.ConvertFromString("#FF818CF8");
        SetColor(color);
    }

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var value = hex.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;

        if (value.Length == 7)
            value = "#FF" + value[1..];

        if (value.Length != 9)
            return null;

        if (!byte.TryParse(value.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ||
            !byte.TryParse(value.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(value.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(value.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return null;

        return Color.FromArgb(a, r, g, b);
    }

    private void SetColor(Color color)
    {
        _suppress = true;

        RSlider.Value = color.R;
        GSlider.Value = color.G;
        BSlider.Value = color.B;
        RText.Text = ((int)color.R).ToString();
        GText.Text = ((int)color.G).ToString();
        BText.Text = ((int)color.B).ToString();

        SelectedHex = $"#FF{color.R:X2}{color.G:X2}{color.B:X2}";
        HexBox.Text = SelectedHex;
        PreviewBrushHost.Background = new SolidColorBrush(color);

        _suppress = false;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress)
            return;

        var color = Color.FromRgb((byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value);
        SetColor(Color.FromArgb(255, color.R, color.G, color.B));
    }

    private void HexBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppress)
            return;

        var parsed = ParseHex(HexBox.Text);
        if (parsed is null)
            return;

        SetColor(parsed.Value);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
