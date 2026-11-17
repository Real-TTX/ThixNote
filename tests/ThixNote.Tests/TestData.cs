using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixNote.Tests;

/// <summary>
/// Every test gets its own data folder (settings, logs and the notes in "Notizen" inside it), no machine policies, no
/// cloud and a trash of its own. Nothing a test does touches the real notes, settings, registry values, Keychain or recycle bin.
/// </summary>
internal sealed class TestData : IDisposable
{
    public TestData(bool withSampleNotes = false)
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixnote-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        AppPaths.UseDirectoryForTests(Folder);
        SettingsStore.Reset();
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        UpdateService.SetStateForTests(null);
        UseNoCloud();
        NoteLibrary.UseTrashForTests(TrashFolder);
        Notes = withSampleNotes ? SampleNotes.Create(AppPaths.DefaultNotesFolder) : new Dictionary<string, string>();
    }

    public string Folder { get; }

    public string NotesFolder => AppPaths.DefaultNotesFolder;

    /// <summary>The sample notes by their keys (SampleNotes.Plan …), when asked for.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; }

    /// <summary>What goes into the trash (NoteLibrary.MoveToTrash): a folder of the test, never the recycle bin of the system.</summary>
    public string TrashFolder => Path.Combine(Folder, "Papierkorb");

    public void Dispose()
    {
        foreach (var path in Notes.Values)
        {
            EncryptionSession.Forget(path);
        }

        UseNoCloud();
        NoteLibrary.UseTrashForTests(null);
        UpdateService.SetStateForTests(null);
        AccessPolicy.UsePolicyReaderForTests(null);
        SettingsStore.Reset();
        AppPaths.UseDirectoryForTests(null);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>No cloud: no loop, no handler, tokens only in the settings of the test (never DPAPI or the Keychain).</summary>
    private static void UseNoCloud()
    {
        SyncService.ResetForTests();
        SyncService.HandlerForTests = null;
        SyncService.LoopDisabledForTests = true;
        TokenStore.UseMemoryForTests(true);
    }
}
