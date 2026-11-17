# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixNote is

A deliberately simple note app for **Windows and macOS**: every note is a plain file (`.md` Markdown, `.txt` text, `.txts`
password-protected), the file name is the title, sub folders are folders. Laid out like Notepad: the TABS in the title bar,
ONE formatting bar right below, the text alone, the status bar; beside it a small library (menu, note list with the search).
The compact view (Ctrl/Cmd+Shift+K, remembered; always below 900 px) is Notepad itself: no menu, the list over the editor.
The title is never part of the text (F2 / double click on the tab renames; a new note is named after its first line until renamed).
UI language German; code, comments and identifiers English. Formerly called **QuickNote** (`QuickNoteMigration`, the
encryption magic `QNSEC1` stays for compatibility).

Stack: **.NET 10**, C#, **Avalonia 11.3** with **MatDesktop 0.2.0** (the desktop toolkit of MatDesign), CommunityToolkit.Mvvm,
Markdig (export), Markdown.Avalonia (preview). The sister project **ThixClip** (`../ThixClip`) is the reference for structure,
setup, update and tests; MatDesign (`../MatDesign`, docs/DESKTOP.md) holds the rules DESK and SETUP that ThixNote follows
(no tray: TRAY does not apply, closing the window ends ThixNote).

## Build / run / test

- `dotnet build ThixNote.slnx` – expected: 0 warnings, 0 errors.
- `dotnet test tests/ThixNote.Tests` – business layer (notes, encryption, settings, policies, QuickNote takeover), the
  installation in test locations (Windows), the update rules, and the **UI rendered headless** with Avalonia + Skia
  (`UiTests`: keyboard flows, the narrow window, the snapshots; `THIXNOTE_SCREENSHOTS=<folder>` keeps the pictures – look at
  them). Tests never touch the real notes, settings or registry values (`TestData`, `InstallService.UseLocationsForTests`).
- Run: `dotnet run --project src/ThixNote`; with `THIXNOTE_DATA_DIR=<folder>` an instance of its own (settings, logs and the
  notes in `<folder>/Notizen`), which never meets the installed one.
- **Snapshots:** `dotnet run --project src/ThixNote -- --snapshot docs/images` draws every window headless from the sample
  notes (`SampleNotes`), pinned clock 16.11.2026 14:32, version 1.1.0, ANNA-PC, light and dark: `setup-*`,
  `main-{notes,preview,locked,compact (the compact view),folded,settings,about}`, `dialog-{password,rename}`, `toast`. The README uses them; a
  new window or page goes into `Snapshot.cs`. No real path or note may show in a picture.
- The Windows publish (win-x64) is a **trimmed** single file (`TrimMode=partial`, about 35 MB). `ThixNote.exe --self-test
  <report>` checks the published program (JSON, encryption, export, the embedded document icon, COM, every window); CI runs it.
- `scripts/package-macos.sh` builds `ThixNote.app` (Info.plist with the document types, icns, ad-hoc signature), the update
  zip and the dmg. `scripts/Capture-Window.ps1` takes a picture of a real window (needs a screen).
- Icons: `dotnet run --project tools/IconGen -- .` renders `assets/logo/*.svg` into `assets/icons` (PNG, ICO, ICNS); copy the
  app icon and logo into `src/ThixNote/Assets` when they change.
- **MatDesktop** (`src/ThixNote/MatDesktop/`) is a COPY from MatDesign (branch `feature/desktop-and-services`,
  `src/MatDesktop`): never edit it here. Change it in MatDesign and copy again with
  `node tools/matdesktop-copy.js <path>/ThixNote/src/ThixNote`; `UiTests.The_copy_of_MatDesktop_is_unchanged` holds the
  files to `matdesktop.version`. An icon the sprite lacks is added to MatDesign first.

## Map

```
src/ThixNote.Core       (no UI) models, business layer (static classes), infrastructure
  Models/               NoteFile, AppSettings (record: notes folder, tabs, favourites, window, compact view, theme, zoom, word
                        wrap, updates),
                        Exceptions (BusinessException with Field, InvalidPassword, InvalidNoteFile), ExportFormat, EncryptionKey
  Business/             NoteLibrary (files, folders, rename, trash), NoteContent (read/write/protect/unprotect), NoteEncryption,
                        EncryptionSession (keys of unlocked notes in memory), AccessGuard (permission checks), NoteExporter,
                        LegacyNotes (GUID-named notes of the very first build), QuickNoteMigration, SampleNotes (tests, snapshots),
                        SettingsService (Update: checked + LockConfiguration; Remember: what the app remembers), AccessPolicy
                        (LockConfiguration, DisableUpdates), InstallService (Windows self-installing exe, file types, QuickNote
                        removal), UpdateService + MacAppBundle (OTA from the rolling releases; install on exit or "now")
  Infrastructure/       AppPaths (data folder, THIXNOTE_DATA_DIR), AppInfo (version, channel, build; Pin for snapshots), AppClock,
                        SettingsStore (settings.json), Log, ProblemReports (crash / hang reports, shown at the next start)
  Platform/Windows/     ShellLink (.lnk through generated COM)
src/ThixNote            (Avalonia app)
  Program / App         start-up: ProblemReports, self test, snapshot, Windows setup mode (--update [--restart], --quiet,
                        --uninstall), single instance (MatSingleInstance hands files to the running one), UpdateCoordinator;
                        ExitApplication saves and starts a downloaded update; InstallUpdateNowAsync
  MainWindow            MatWindow: MatTitleBar (logo + word mark, the TABS of NotesPage), MatNav (Alle Notizen,
                        Favoriten, the folders, Settings; foot: new folder, notes folder; version = About), page host; ALL shortcuts
                        (OnPreviewKeyDown); ApplyLayout switches to the Notepad layout (compact view, or below 900 px)
  Pages/                NotesPage (search + list of MatCardRow cards; editor: format bar – it sheds groups when narrow –, text,
                        status bar; the Markdown helpers),
                        TabStrip (shown in the title bar), SettingsPage (Allgemein | Updates | Erweitert),
                        AboutPage
  ViewModels/           NotesViewModel (list, view, search, tabs, commands), DocumentViewModel (one tab: autosave, title from the
                        first line, caret for the status bar, lock/unlock), LibraryItemViewModel
  Dialogs/              ConfirmDialog, TextInputDialog (rename, new folder), PasswordDialog, ProblemReportDialog
  Setup/                ThixNoteSetup: MatSetupWindow for install (Welcome → Options → Progress → Finish), update and uninstall
  Controls/             IconLabel, ActionBar, InfoBar, FormErrors (from ThixClip)
  Themes/               ThixNoteControls.axaml: tabs, the frameless editor, the bars – every value a MatDesktop resource
tests/ThixNote.Tests    see above
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, human-readable names, early returns. Core keeps its
  existing camelCase-without-underscore fields where they are; new UI code follows ThixClip (`_camelCase`).
- **Business layer = static classes.** UI and view models never touch files, the registry or HTTP directly; validation and
  permission checks (AccessGuard, AccessPolicy) live in the business layer. Errors are `BusinessException(message, field)`;
  the settings page shows them at the `MatFormField` whose `controls:FormErrors.Field` is that field.
- **Colours only from MatDesktop's theme** (DESK-02): `{DynamicResource Mat.Surface}` … in XAML, `control.Use(property, key)` in
  code; the only literal is `App.Accent` (#DB4C3F, the red of the mark). `UiTests.No_window_writes_a_colour_of_its_own` checks it.
  Icons are MatDesktop's (`<mat:MatIcon Icon="lock"/>`); B, I, S and "1." in the format bar are letters on purpose.
- The version reads `Version x · channel` everywhere (MatVersion, SETUP-07). Icon-only buttons get `AutomationProperties.Name`
  and a tooltip naming the shortcut (`Ui.Shortcut`).
- An editor never restarts under the user's hands: automatic updates install when ThixNote closes.
- Never log note contents, titles or passwords.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor (1.1). CI sets `Channel`, `BuildNumber` (run number of `desktop.yml`), `BuildDate`.
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no
  other names, no "generated with" lines.**
- `.github/workflows/desktop.yml`: Windows (test, publish win-x64 single file, self test, `ThixNoteSetup.exe`), macOS (test,
  publish osx-arm64 + osx-x64, `package-macos.sh`), publish (manifest `thixnote-update.json`, rolling releases
  `release-channel` / `nightly-channel`, versioned release `v<version>` on main). GitHub Actions must be enabled for the
  repository (Settings › Actions › General) before the first push builds anything.
