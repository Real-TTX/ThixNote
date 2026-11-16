using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ThixNote.ViewModels;

namespace ThixNote.Pages;

/// <summary>The tabs of the open notes. A double click on a tab renames the note (its title is its file name).</summary>
public partial class TabStrip : UserControl
{
    public TabStrip()
    {
        InitializeComponent();
        ToolTip.SetTip(NewTabButton, $"Neue Notiz ({Ui.Shortcut("N")})");
        Tabs.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: DocumentViewModel } && DataContext is NotesViewModel notes)
            {
                notes.RenameActiveDocumentCommand.Execute(null);
            }
        };

        // The chosen tab is always in sight (a new one, Strg+Tab).
        Tabs.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (Tabs.SelectedItem is { } item)
            {
                Tabs.ContainerFromItem(item)?.BringIntoView();
            }
        }, DispatcherPriority.Background);

        // The wheel scrolls the tabs sideways.
        Scroller.PointerWheelChanged += (_, e) =>
        {
            Scroller.Offset = Scroller.Offset.WithX(Math.Max(0, Scroller.Offset.X - e.Delta.Y * 40));
            e.Handled = true;
        };
    }

    /// <summary>The list of tabs (tests).</summary>
    public ListBox TabList => Tabs;
}
