using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ThixNote.App.Views;

public sealed partial class PasswordDialog : Window
{
    private readonly bool askTwice;

    public PasswordDialog() : this("Kennwort", false)
    {
    }

    public PasswordDialog(string title, bool askTwice)
    {
        InitializeComponent();
        this.askTwice = askTwice;
        TitleText.Text = title;
        RepeatBox.IsVisible = askTwice;
        HintText.IsVisible = askTwice;
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            ShowError("Bitte ein Kennwort eingeben.");
            return;
        }

        if (askTwice && password != RepeatBox.Text)
        {
            ShowError("Die Kennwörter stimmen nicht überein.");
            return;
        }

        Close(password);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
