using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MatDesktop;
using ThixNote.Controls;

namespace ThixNote.Dialogs;

/// <summary>One line of text: rename a note (its title is its file name), name a new folder. Enter takes it, Esc cancels.</summary>
public sealed class TextInputDialog : MatWindow
{
    private string? _result;

    private TextInputDialog(string title, string label, string initial)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Use(BackgroundProperty, "Mat.Surface");

        var input = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 22) };
        AutomationProperties.SetName(input, label);

        var confirm = new Button { Content = new IconLabel { Icon = "check", Text = "Übernehmen" }, IsDefault = true, Theme = Ui.Theme("MatButton.Primary") };
        confirm.Click += (_, _) =>
        {
            _result = input.Text;
            Close();
        };
        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var heading = new TextBlock { Text = title, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        var caption = new TextBlock { Text = label, Theme = Ui.Theme("MatText.FieldLabel") };
        Content = new StackPanel { Margin = new Thickness(24, 22), Children = { heading, caption, input, new ActionBar { Children = { confirm, cancel } } } };
        Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>The text the user confirmed, or null (cancelled).</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string label, string initial)
    {
        var dialog = new TextInputDialog(title, label, initial);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
