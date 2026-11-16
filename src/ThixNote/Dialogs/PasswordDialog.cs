using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MatDesktop;
using ThixNote.Controls;

namespace ThixNote.Dialogs;

/// <summary>
/// The password for a note: asked twice when a note gets protected (a typo would lock it for good). The password itself
/// is never kept: the caller derives the key from it and forgets it.
/// </summary>
public sealed class PasswordDialog : MatWindow
{
    private string? _result;

    private PasswordDialog(string title, bool askTwice)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Use(BackgroundProperty, "Mat.Surface");

        var first = new TextBox { PasswordChar = '●', Watermark = "Kennwort", Margin = new Thickness(0, 0, 0, 10) };
        var second = new TextBox { PasswordChar = '●', Watermark = "Kennwort wiederholen", IsVisible = askTwice, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(first, "Kennwort");
        AutomationProperties.SetName(second, "Kennwort wiederholen");
        var error = new InfoBar { Margin = new Thickness(0, 0, 0, 8) };

        var confirm = new Button { Content = new IconLabel { Icon = "lock", Text = "Schützen" }, IsDefault = true, Theme = Ui.Theme("MatButton.Primary") };
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(first.Text))
            {
                error.Show("Bitte ein Kennwort eingeben.", InfoSeverity.Error);
                first.Focus();
                return;
            }

            if (askTwice && first.Text != second.Text)
            {
                error.Show("Die beiden Kennwörter stimmen nicht überein.", InfoSeverity.Error);
                second.Focus();
                second.SelectAll();
                return;
            }

            _result = first.Text;
            Close();
        };
        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var heading = new TextBlock { Text = title, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var hint = new TextBlock
        {
            Text = "Ohne das Kennwort lässt sich die Notiz nicht mehr öffnen – niemand kann es wiederherstellen.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");

        Content = new StackPanel { Margin = new Thickness(24, 22), Children = { heading, hint, first, second, error, new ActionBar { Margin = new Thickness(0, 10, 0, 0), Children = { confirm, cancel } } } };
        Opened += (_, _) => first.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    public static async Task<string?> AskAsync(Window owner, string title, bool askTwice)
    {
        var dialog = new PasswordDialog(title, askTwice);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>For the snapshots: the dialog as it opens.</summary>
    internal static PasswordDialog ForSnapshot() => new("Mit Kennwort schützen", askTwice: true);
}
