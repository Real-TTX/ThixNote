using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixNote.Tests;

/// <summary>
/// Every test gets its own data folder (settings, logs and the notes in "Notizen" inside it) and no machine policies.
/// Nothing a test does touches the real notes, settings or registry values.
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
        Notes = withSampleNotes ? SampleNotes.Create(AppPaths.DefaultNotesFolder) : new Dictionary<string, string>();
    }

    public string Folder { get; }

    public string NotesFolder => AppPaths.DefaultNotesFolder;

    /// <summary>The sample notes by their keys (SampleNotes.Plan …), when asked for.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; }

    public void Dispose()
    {
        foreach (var path in Notes.Values)
        {
            EncryptionSession.Forget(path);
        }

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
}
