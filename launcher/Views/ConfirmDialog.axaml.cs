using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClassIntraOps.Launcher.Views;

public partial class ConfirmDialog : Window
{
    private bool _result;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message, string okText = "确定")
    {
        var dlg = new ConfirmDialog
        {
            Title = title,
            MsgText = { Text = message },
            BtnOk = { Content = okText }
        };
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnOk(object? sender, RoutedEventArgs e) { _result = true; Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) { _result = false; Close(); }
}
