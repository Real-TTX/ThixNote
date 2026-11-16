using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ThixNote.App.Views;

public sealed partial class MessageDialog : Window
{
    public MessageDialog() : this("ThixNote", string.Empty, false)
    {
    }

    public MessageDialog(string title, string message, bool showCancel)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        CancelButton.IsVisible = showCancel;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
