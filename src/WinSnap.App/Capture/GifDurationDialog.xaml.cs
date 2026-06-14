using System.Windows;
using System.Windows.Input;

namespace WinSnap.App.Capture;

public partial class GifDurationDialog : Window
{
    public GifDurationDialog(int defaultDurationSeconds)
    {
        InitializeComponent();
        DurationSeconds = Math.Clamp(defaultDurationSeconds, 1, 60);
        DurationTextBox.Text = DurationSeconds.ToString();
        DurationTextBox.SelectAll();
        DurationTextBox.Focus();
    }

    public int DurationSeconds { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DurationTextBox.Text.Trim(), out int seconds) || seconds < 1 || seconds > 60)
        {
            MessageBox.Show(this,
                "录制秒数需要填写 1 到 60 之间的整数。",
                "WinSnap",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            DurationTextBox.SelectAll();
            DurationTextBox.Focus();
            return;
        }

        DurationSeconds = seconds;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
