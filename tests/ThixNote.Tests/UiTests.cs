using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.ViewModels;

[assembly: AvaloniaTestApplication(typeof(ThixNote.Tests.UiTestApp))]

namespace ThixNote.Tests;

/// <summary>The real app (App.axaml, MatDesktop's theme, the pages) rendered headless with Skia: real pixels, no screen needed.</summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The main window driven by the keyboard like a user would, the narrow window (Notepad), and the snapshots of every window
/// in light and dark (THIXNOTE_SCREENSHOTS=&lt;folder&gt; keeps them; docs/images has the ones of the README).
/// </summary>
public sealed class UiTests
{
    private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("THIXNOTE_SCREENSHOTS");

    [AvaloniaFact]
    public void The_title_is_the_tab_and_never_part_of_the_text()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan);
        var notes = window.Notes.ViewModel;

        Assert.Equal("Projektplan Q1", notes.ActiveDocument!.TabTitle);
        Assert.Equal("Projektplan Q1 – ThixNote", window.Title);
        Assert.StartsWith("Ziel:", window.Notes.EditorBox.Text);
        Assert.DoesNotContain("Projektplan", window.Notes.EditorBox.Text);
    }

    [AvaloniaFact]
    public void A_new_note_is_named_after_its_first_line_until_it_is_renamed()
    {
        using var data = new TestData();
        var window = Open(data);
        var notes = window.Notes.ViewModel;

        window.KeyPressQwerty(PhysicalKey.N, CommandModifier);
        Settle(window);
        var document = notes.ActiveDocument!;
        Assert.StartsWith(NoteLibrary.DefaultTitle, document.Title);
        Assert.True(window.Notes.EditorBox.IsFocused, "a new note starts in the text");

        window.KeyTextInput("Einkauf für Samstag\nBrot");
        Settle(window);
        document.Save();
        Assert.Equal("Einkauf für Samstag", document.Title);
        Assert.True(File.Exists(Path.Combine(data.NotesFolder, "Einkauf für Samstag.md")));

        // Renamed by hand, the first line no longer names it.
        document.Rename("Wochenende");
        document.Text = "Ganz anders\n";
        document.Save();
        Assert.Equal("Wochenende", document.Title);
    }

    [AvaloniaFact]
    public void Shortcuts_format_save_switch_tabs_and_zoom()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan, SampleNotes.Shopping);
        var notes = window.Notes.ViewModel;
        var editor = window.Notes.EditorBox;

        // Ctrl+B bolds the selection (the window takes it before the text box does).
        editor.Focus();
        editor.SelectionStart = 0;
        editor.SelectionEnd = 4;
        window.KeyPressQwerty(PhysicalKey.B, CommandModifier);
        Settle(window);
        Assert.StartsWith("**Ziel**:", editor.Text);

        window.KeyPressQwerty(PhysicalKey.S, CommandModifier);
        Settle(window);
        Assert.False(notes.ActiveDocument!.IsDirty);
        Assert.StartsWith("**Ziel**:", File.ReadAllText(data.Notes[SampleNotes.Plan]));

        // Ctrl+Tab: the next tab; a plain text note has no Markdown formatting.
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        Settle(window);
        Assert.Equal("Einkaufsliste", notes.ActiveDocument!.TabTitle);
        Assert.False(notes.ActiveDocument.CanFormat);

        window.KeyPressQwerty(PhysicalKey.Equal, CommandModifier);
        Settle(window);
        Assert.Equal(110, notes.Zoom);
        window.KeyPressQwerty(PhysicalKey.Digit0, CommandModifier);
        Settle(window);
        Assert.Equal(100, notes.Zoom);
    }

    [AvaloniaFact]
    public void Searching_filters_the_list_and_escape_goes_back_to_the_text()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan);
        var notes = window.Notes.ViewModel;
        Assert.Equal(data.Notes.Count, notes.LibraryItems.Count);

        window.KeyPressQwerty(PhysicalKey.F, CommandModifier);
        Settle(window);
        Assert.True(window.Notes.SearchBox.IsFocused);
        window.KeyTextInput("linsen");
        Settle(window);
        Assert.Equal("Rezept Linsensuppe", Assert.Single(notes.LibraryItems).Title);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(string.Empty, notes.SearchText);
        Assert.Equal(data.Notes.Count, notes.LibraryItems.Count);
        Assert.True(window.Notes.EditorBox.IsFocused);
    }

    [AvaloniaFact]
    public void A_protected_note_asks_for_its_password_in_the_place_of_the_text()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Secret);
        var document = window.Notes.ViewModel.ActiveDocument!;
        Assert.True(document.IsLocked);
        Assert.False(window.Notes.EditorBox.IsEffectivelyVisible);
        Assert.True(window.Notes.UnlockBox.IsFocused);

        document.UnlockPassword = "falsch";
        document.UnlockCommand.Execute(null);
        WaitUntil(window, () => !document.IsBusy);
        Assert.True(document.IsLocked);
        Assert.True(document.HasUnlockError);

        document.UnlockPassword = SampleNotes.Password;
        document.UnlockCommand.Execute(null);
        WaitUntil(window, () => !document.IsLocked);
        Assert.Contains("WLAN Gäste", window.Notes.EditorBox.Text);
    }

    [AvaloniaFact]
    public void The_tabs_are_in_the_title_bar_and_the_search_above_the_list()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan, SampleNotes.Shopping);
        Assert.Same(window.Notes.Tabs, window.TitleCenter.Content);
        Assert.True(window.Notes.Tabs.IsEffectivelyVisible);
        Assert.True(window.Notes.SearchBox.IsEffectivelyVisible);

        // Hidden list: Ctrl+F shows it for the search.
        window.Notes.ViewModel.ToggleListCommand.Execute(null);
        Settle(window);
        Assert.False(window.Notes.SearchBox.IsEffectivelyVisible);
        window.KeyPressQwerty(PhysicalKey.F, CommandModifier);
        Settle(window);
        Assert.True(window.Notes.SearchBox.IsFocused);

        // From the settings a click on the chosen tab leads back to the text.
        window.ShowSettings();
        Settle(window);
        window.Notes.Tabs.TabList.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, null!));
        Settle(window);
        Assert.Same(window.Notes, window.PageHost.Content);
    }

    [AvaloniaFact]
    public void A_narrow_window_is_a_small_editor_like_Notepad()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan, SampleNotes.Shopping);
        Assert.False(window.IsCompact);

        window.Width = 640;
        Settle(window);
        Assert.True(window.IsCompact);
        Assert.True(window.Notes.ViewModel.IsNarrow);
        Assert.False(window.Sidebar.IsVisible);
        Assert.Same(window.Notes.Tabs, window.TitleCenter.Content);
        Assert.False(window.Notes.ViewModel.IsListOpen);
        Assert.True(window.Notes.FormatBar.IsVisible);
        Assert.True(window.Notes.StatusBar.IsVisible);

        // Ctrl+F opens the list over the editor with its search; Esc closes it again.
        window.KeyPressQwerty(PhysicalKey.F, CommandModifier);
        Settle(window);
        Assert.True(window.Notes.ViewModel.IsListOpen);
        Assert.True(window.Notes.SearchBox.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.False(window.Notes.ViewModel.IsListOpen);

        window.Width = 1180;
        Settle(window);
        Assert.False(window.IsCompact);
        Assert.True(window.Sidebar.IsVisible);
        Assert.True(window.Notes.ViewModel.IsListOpen);
    }

    [AvaloniaFact]
    public void The_compact_view_is_Notepad_at_any_width_and_is_remembered()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan, SampleNotes.Shopping);
        Assert.False(window.IsCompact);

        window.KeyPressQwerty(PhysicalKey.K, CommandModifier | RawInputModifiers.Shift);
        Settle(window);
        Assert.True(window.IsCompact);
        Assert.False(window.Notes.ViewModel.IsNarrow);
        Assert.False(window.Sidebar.IsVisible);
        Assert.False(window.Notes.ViewModel.IsListOpen);
        Assert.True(window.Notes.EditorBox.IsFocused);
        Assert.True(SettingsService.Get().IsCompactMode);

        // The next start opens compact again.
        window.IsExiting = true;
        window.Close();
        var next = new MainWindow { Width = 1180, Height = 760 };
        next.Show();
        Settle(next);
        Assert.True(next.IsCompact);

        next.KeyPressQwerty(PhysicalKey.K, CommandModifier | RawInputModifiers.Shift);
        Settle(next);
        Assert.False(next.IsCompact);
        Assert.True(next.Sidebar.IsVisible);
        Assert.True(next.Notes.ViewModel.IsListOpen);
        Assert.False(SettingsService.Get().IsCompactMode);
        next.IsExiting = true;
        next.Close();
    }

    [AvaloniaFact]
    public void The_menu_shows_the_folders_and_filters_the_list()
    {
        using var data = new TestData(withSampleNotes: true);
        var window = Open(data, SampleNotes.Plan);
        var notes = window.Notes.ViewModel;
        Assert.Contains("Arbeit", notes.Folders);
        var arbeit = window.Sidebar.Items.Single(item => item.Label == "Arbeit");
        arbeit.Command?.Execute(null);
        arbeit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle(window);
        Assert.Equal(NotesViewKind.Folder, notes.View.Kind);
        Assert.Equal(3, notes.LibraryItems.Count);
        Assert.Equal("Arbeit", notes.ListTitle);
    }

    [AvaloniaFact]
    public void The_cloud_tab_signs_in_in_the_browser_with_the_keyboard()
    {
        using var data = new TestData();
        var cloud = new FakeCloud();
        var opened = new List<string>();
        SyncService.HandlerForTests = () => cloud;
        Ui.OpenExternalForTests = opened.Add;
        try
        {
            var window = Open(data);
            window.ShowSettings(Pages.SettingsPage.CloudTab);
            Settle(window);
            var page = (Pages.SettingsPage)window.PageHost.Content!;
            Assert.True(page.CloudSignIn.IsVisible);
            Assert.Equal(AppInfo.MachineName, page.DeviceNameBox.Text);

            // The address, Enter: the code shows big, the browser opens the cloud's page for it.
            page.ServerUrlBox.Focus();
            window.KeyTextInput("cloud.example.test");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitUntil(window, () => page.CloudCode.IsVisible);
            Assert.Equal(cloud.LastCode, page.LinkCodeText.Text);
            Assert.Equal($"https://cloud.example.test/Connect?code={cloud.LastCode}", Assert.Single(opened));
            Assert.True(page.CancelSignInButton.IsFocused);

            // Confirmed in the browser: connected, the state shows.
            cloud.Approve(cloud.LastCode!);
            WaitUntil(window, () => page.CloudConnected.IsVisible);
            Assert.True(SettingsService.Get().Cloud.IsConnected);
            Assert.Equal($"{FakeCloud.DisplayName} ({FakeCloud.UserName})", page.CloudAccountText.Text);
            Assert.False(page.CloudCode.IsVisible);

            window.IsExiting = true;
            window.Close();
        }
        finally
        {
            Ui.OpenExternalForTests = null;
        }
    }

    [AvaloniaFact]
    public void The_snapshot_draws_every_window_in_light_and_dark()
    {
        using var data = new TestData();
        var folder = ShotFolder ?? Path.Combine(data.Folder, "shots");
        var report = Snapshot.Render(folder);
        Assert.Equal(Snapshot.FileNames.Count, report.Written.Count + report.Skipped.Count);
        foreach (var file in report.Written)
        {
            Assert.True(new FileInfo(Path.Combine(folder, file)).Length > 1000, $"{file} is empty");
        }
    }

    [AvaloniaFact]
    public void The_version_reads_the_same_everywhere()
    {
        using var data = new TestData();
        AppInfo.Pin("1.1.42-20261116", "release", 42);
        try
        {
            var window = Open(data);
            Assert.Equal("Version 1.1.42-20261116 · release", ((Avalonia.Controls.TextBlock)window.VersionButton.Content!).Text);
            window.ShowAbout();
            Settle(window);
            var about = (Pages.AboutPage)window.PageHost.Content!;
            Assert.Equal("Version 1.1.42-20261116 · release", about.VersionText.Text);
        }
        finally
        {
            AppInfo.Pin(null);
        }
    }

    [Fact]
    public void No_window_writes_a_colour_of_its_own()
    {
        // DESK-02: every colour comes from MatDesktop's theme; the one value ThixNote gives is its accent (App.Accent).
        var source = Path.Combine(Repository.Root, "src", "ThixNote");
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}MatDesktop{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var literal = new System.Text.RegularExpressions.Regex(@"#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{8}\b|Color\.(Parse|FromRgb|FromArgb)|Colors\.\w+|Brushes\.\w+");
        var findings = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(item => literal.IsMatch(item.line) && !item.line.Contains("public const string Accent", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(source, item.file)}:{item.index + 1}: {item.line.Trim()}")
            .ToList();
        Assert.Empty(findings);
    }

    [Fact]
    public void The_copy_of_MatDesktop_is_unchanged()
    {
        // MatDesktop is copied from MatDesign (node tools/matdesktop-copy.js src/ThixNote there) and never edited here.
        var folder = Path.Combine(Repository.Root, "src", "ThixNote", "MatDesktop");
        var lines = File.ReadAllLines(Path.Combine(folder, "matdesktop.version"));
        Assert.StartsWith("MatDesktop ", lines[0]);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"MatDesktop/{file} was changed: copy it again from MatDesign instead of editing it");
        }
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    /// <summary>The main window with these sample notes open as tabs (the first one chosen).</summary>
    private static MainWindow Open(TestData data, params string[] tabs)
    {
        var paths = tabs.Select(key => data.Notes[key]).ToList();
        SettingsService.Remember(settings => settings with { OpenFiles = paths, ActiveFile = paths.FirstOrDefault(), WindowWidth = 1180, WindowHeight = 760 });
        var window = new MainWindow { Width = 1180, Height = 760 };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(MainWindow window)
    {
        for (var round = 0; round < 6; round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitUntil(MainWindow window, Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until)
        {
            Settle(window);
            Thread.Sleep(20);
        }

        Settle(window);
    }
}
