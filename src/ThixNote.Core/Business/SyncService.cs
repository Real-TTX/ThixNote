using System.Net;
using System.Text.Json;
using MatCloud.Contract;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

public enum CloudState
{
    Off,
    Connecting,
    Online,
    Syncing,
    Offline,
    SignedOut,
}

/// <summary>What the cloud connection does right now (settings page, the foot of the menu).</summary>
public sealed record SyncStatus(CloudState State, string Message, DateTimeOffset? LastSync = null);

/// <summary>A started sign-in in the browser: the code to show and the address to open; <see cref="SyncService.FinishBrowserSignInAsync"/> waits for it.</summary>
public sealed record BrowserSignIn(string ServerUrl, string DeviceName, DeviceLinkStart Link)
{
    public string Code => Link.Code;

    public string VerificationUrl => Link.VerificationUrl;
}

/// <summary>
/// The connection to MatCloud, the cloud of the Thix apps (see <see cref="SyncContract"/>; ThixNote has no module there and
/// keeps its notes in the general sync, app <see cref="CloudApps.Note"/>): signing this computer in (in the browser with a
/// code, or with user name and password) and out, and one background loop that keeps the notes the same on every computer:
///   changes of the cloud come down -> local changes go up (<see cref="NoteSync"/>) -> long poll.
/// A local change (a save, rename, new note, the trash) wakes the loop (<see cref="NotifyLocalChange"/>), a change elsewhere
/// ends the long poll. Without network the loop waits and tries again (2 s, 4 s … 60 s); a removed device (401) signs out,
/// an app the administrator switched off (403) waits. The device token is kept protected (<see cref="TokenStore"/>); the
/// password is used once and never stored. Nothing here logs a title, a text, a password or a token.
/// </summary>
public static class SyncService
{
    private const int LongPollSeconds = 25;
    private const string NotConnected = "Nicht verbunden";

    private static readonly TimeSpan KickSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SwitchedOffWait = TimeSpan.FromMinutes(5);
    private static readonly object Gate = new();
    private static readonly HashSet<string> Unsaved = new(EncryptionSession.PathComparer);

    private static CancellationTokenSource? _stop;
    private static CancellationTokenSource _kick = new();
    private static Task? _loop;
    private static SyncStatus _status = new(CloudState.Off, NotConnected);

    /// <summary>Raised when <see cref="Status"/> changes (any thread).</summary>
    public static event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>Raised when the sync changed notes in the folder (any thread): open tabs follow, the list is read again.</summary>
    public static event EventHandler<IReadOnlyList<NoteChange>>? NotesChanged;

    public static SyncStatus Status => _status;

    public static bool IsConnected => SettingsService.Get().Cloud.IsConnected;

    /// <summary>How ThixNote introduces itself to the cloud ("ThixNote/1.1.42-20261117 (Windows)"); the cloud shows the version with the device.</summary>
    public static string UserAgent => $"ThixNote/{AppInfo.Version} ({AppInfo.PlatformName})";

    /// <summary>Tests talk to a cloud in memory.</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    /// <summary>Tests run the rounds themselves (<see cref="SyncOnceAsync"/>): no background loop after a sign-in.</summary>
    internal static bool LoopDisabledForTests { get; set; }

    // ---- signing in and out ----

    /// <summary>
    /// Signs this computer in with the user name and password of a local account of the cloud. The password is used once
    /// and never stored. Then the notes are compared with the cloud: what is new on either side goes to the other.
    /// </summary>
    public static async Task ConnectAsync(string serverUrl, string userName, string password, string deviceName, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var url = NormalizeServerUrl(serverUrl);
        var device = CheckDeviceName(deviceName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new BusinessException("Bitte den Benutzernamen angeben.", nameof(CloudSettings.UserName));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new BusinessException("Bitte das Passwort angeben.", "Password");
        }

        await DisconnectIfConnectedAsync();
        SetStatus(new SyncStatus(CloudState.Connecting, "Verbinde …"));
        DeviceLoginResponse login;
        try
        {
            using var client = CreateClient(url, null);
            login = await client.LoginAsync(new DeviceLoginRequest(userName.Trim(), password, device, AppInfo.PlatformName, AppInfo.Version, CloudApps.Note), cancellationToken);
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            SetStatus(new SyncStatus(CloudState.Off, NotConnected));
            throw exception is CloudException { IsUnauthorized: true }
                ? new BusinessException("Benutzername oder Passwort stimmen nicht.", "Password")
                : Explain(exception, url);
        }
        catch (OperationCanceledException)
        {
            SetStatus(new SyncStatus(CloudState.Off, NotConnected));
            throw;
        }

        SignedIn(url, login);
    }

    /// <summary>
    /// Starts the sign-in in the browser (it works with every account of the cloud, also one of MatIdentity): the answer has
    /// the code to show and the address to open. <see cref="FinishBrowserSignInAsync"/> then waits for the confirmation.
    /// </summary>
    public static async Task<BrowserSignIn> StartBrowserSignInAsync(string serverUrl, string deviceName, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var url = NormalizeServerUrl(serverUrl);
        var device = CheckDeviceName(deviceName);
        try
        {
            using var client = CreateClient(url, null);
            var link = await client.StartLinkAsync(new DeviceLinkRequest(device, AppInfo.PlatformName, AppInfo.Version, CloudApps.Note), cancellationToken);
            return new BrowserSignIn(url, device, link);
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            throw Explain(exception, url);
        }
    }

    /// <summary>Waits until the code is confirmed in the browser and signs this computer in; cancelling ends the wait (Abbrechen).</summary>
    public static async Task FinishBrowserSignInAsync(BrowserSignIn signIn, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        DeviceLoginResponse login;
        try
        {
            using var client = CreateClient(signIn.ServerUrl, null);
            login = await client.WaitForLinkAsync(signIn.Link, cancellationToken);
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            throw Explain(exception, signIn.ServerUrl);
        }

        await DisconnectIfConnectedAsync();
        SignedIn(signIn.ServerUrl, login);
    }

    /// <summary>
    /// At start: the sign-in ThixLauncher left when it installed ThixNote (<see cref="CloudHandoff"/>). While this computer is not connected,
    /// ThixNote fetches its own token with it and is connected without the browser; the hand-over is removed when it was used or had expired, and
    /// kept when the cloud did not answer (the next start tries again). Returns the account it signed in with, or null. Never throws.
    /// </summary>
    public static async Task<string?> ConnectFromLauncherAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected || AccessPolicy.IsCloudDisabled)
        {
            CloudHandoff.Remove(CloudApps.Note);
            return null;
        }

        if (CloudHandoff.Find(CloudApps.Note) is not { } handoff)
        {
            return null;
        }

        string url;
        DeviceLoginResponse? login;
        try
        {
            url = NormalizeServerUrl(handoff.ServerUrl);
            using var client = CreateClient(url, null);
            login = await client.RedeemHandoffAsync(handoff, cancellationToken);
        }
        catch (BusinessException)
        {
            CloudHandoff.Remove(CloudApps.Note);
            return null;
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            Log.Warning($"The sign-in of ThixLauncher could not be fetched ({exception.GetType().Name}); trying again at the next start");
            return null;
        }

        CloudHandoff.Remove(CloudApps.Note);
        if (login is null || IsConnected)
        {
            Log.Info("The sign-in of ThixLauncher was used or had expired");
            return null;
        }

        SignedIn(url, login);
        Log.Info("Connected with the sign-in of ThixLauncher");
        return login.Account.DisplayName.Length > 0 ? login.Account.DisplayName : login.Account.UserName;
    }

    /// <summary>Signs this computer out (the cloud forgets the device); the notes stay, and so does the index for a later sign-in.</summary>
    public static async Task DisconnectAsync()
    {
        var cloud = SettingsService.Get().Cloud;
        Stop();
        if (cloud.IsConnected && ReadToken(cloud) is { } token)
        {
            try
            {
                using var client = CreateClient(cloud.ServerUrl, token);
                await client.LogoutAsync(CancellationToken.None);
            }
            catch (Exception exception) when (IsCloudFailure(exception, CancellationToken.None))
            {
                Log.Warning($"Logout at the cloud failed ({exception.GetType().Name}); signing out locally");
            }
        }

        Forget(cloud);
        SetStatus(new SyncStatus(CloudState.Off, NotConnected));
    }

    /// <summary>The address as it is stored: http(s), "https://" added when missing, no trailing slash.</summary>
    public static string NormalizeServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new BusinessException("Bitte die Adresse der Cloud angeben.", nameof(CloudSettings.ServerUrl));
        }

        try
        {
            return MatCloudClient.NormalizeServerUrl(serverUrl);
        }
        catch (UriFormatException)
        {
            throw new BusinessException("Das ist keine gültige Adresse (z. B. https://cloud.example.com).", nameof(CloudSettings.ServerUrl));
        }
    }

    // ---- the loop ----

    /// <summary>Starts the background loop when this computer is signed in (and the cloud is allowed).</summary>
    public static void Start()
    {
        var cloud = SettingsService.Get().Cloud;
        if (!cloud.IsConnected || AccessPolicy.IsCloudDisabled)
        {
            SetStatus(new SyncStatus(CloudState.Off, AccessPolicy.IsCloudDisabled ? "Durch Richtlinie deaktiviert" : NotConnected));
            return;
        }

        lock (Gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    public static void Stop()
    {
        Task? loop;
        lock (Gate)
        {
            _stop?.Cancel();
            _stop = null;
            _kick.Cancel();
            loop = _loop;
            _loop = null;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>"Jetzt synchronisieren", or the network is back: sync now instead of waiting.</summary>
    public static void Kick()
    {
        lock (Gate)
        {
            _kick.Cancel();
        }
    }

    /// <summary>A note was saved, created, renamed or put into the trash here: sync soon (nothing happens when not connected).</summary>
    internal static void NotifyLocalChange()
    {
        if (IsConnected)
        {
            Kick();
        }
    }

    /// <summary>ThixNote moved a note (rename, protect, unprotect): it keeps its key in the cloud.</summary>
    internal static void NoteMoved(string oldPath, string newPath)
    {
        var cloud = SettingsService.Get().Cloud;
        if (!cloud.IsConnected)
        {
            return;
        }

        try
        {
            NoteSync.Moved(AccountKey(cloud), oldPath, newPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning($"Sync: a move could not be noted ({exception.GetType().Name}); it is found by its content");
        }

        Kick();
    }

    /// <summary>
    /// The editor tells which notes are open with changes it has not saved yet: the sync never writes, moves or deletes such a
    /// note – a change of the cloud for it waits until it is saved (and then becomes a conflict copy where both changed).
    /// </summary>
    public static void SetUnsavedChanges(string path, bool hasUnsavedChanges)
    {
        var full = Path.GetFullPath(path);
        lock (Unsaved)
        {
            if (hasUnsavedChanges)
            {
                Unsaved.Add(full);
            }
            else
            {
                Unsaved.Remove(full);
            }
        }
    }

    internal static bool HasUnsavedChanges(string path)
    {
        var full = Path.GetFullPath(path);
        lock (Unsaved)
        {
            return Unsaved.Contains(full);
        }
    }

    /// <summary>One round without the long poll (tests): what the loop does, with its handling of 401 and 403.</summary>
    internal static async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var cloud = SettingsService.Get().Cloud;
        var token = ReadToken(cloud) ?? throw new InvalidOperationException("This computer is not connected.");
        try
        {
            using var client = CreateClient(cloud.ServerUrl, token);
            await RoundAsync(client, AccountKey(cloud), cancellationToken);
            SetStatus(new SyncStatus(CloudState.Online, "Synchronisiert", AppClock.Now));
        }
        catch (CloudException exception) when (exception.IsUnauthorized)
        {
            SignedOut(cloud, "Dieses Gerät wurde in der Cloud entfernt – bitte neu anmelden.");
        }
        catch (CloudException exception) when (exception.IsForbidden)
        {
            SetStatus(new SyncStatus(CloudState.Offline, exception.Message, _status.LastSync));
        }
    }

    /// <summary>Tests start disconnected and with nothing in memory.</summary>
    internal static void ResetForTests()
    {
        Stop();
        NoteSync.Reset();
        lock (Unsaved)
        {
            Unsaved.Clear();
        }

        _status = new SyncStatus(CloudState.Off, NotConnected);
    }

    /// <summary>The snapshots show a state without a cloud (null: not connected again).</summary>
    internal static void ShowStatusForSnapshot(SyncStatus? status) => SetStatus(status ?? new SyncStatus(CloudState.Off, NotConnected));

    private static async Task RunAsync(CancellationToken stopToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!stopToken.IsCancellationRequested)
        {
            CancellationToken kickToken;
            lock (Gate)
            {
                if (_kick.IsCancellationRequested)
                {
                    _kick.Dispose();
                    _kick = new CancellationTokenSource();
                }

                kickToken = _kick.Token;
            }

            var cloud = SettingsService.Get().Cloud;
            var token = ReadToken(cloud);
            if (!cloud.IsConnected || token is null)
            {
                SignedOut(cloud, "Der Zugang zur Cloud fehlt – bitte neu anmelden.");
                return;
            }

            var wait = delay;
            var settleOnly = false;
            try
            {
                using var client = CreateClient(cloud.ServerUrl, token);
                SetStatus(_status with { State = CloudState.Syncing, Message = "Synchronisiere …" });
                var account = AccountKey(cloud);
                await RoundAsync(client, account, stopToken);
                SetStatus(new SyncStatus(CloudState.Online, "Synchronisiert", AppClock.Now));
                delay = TimeSpan.FromSeconds(2);

                using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(stopToken, kickToken);
                await client.WaitAsync(NoteSync.LastRevision(account), LongPollSeconds, waitToken.Token);
                continue;
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (kickToken.IsCancellationRequested)
            {
                (wait, settleOnly) = (KickSettle, true); // a local change: a moment for the next saves, then the next round
            }
            catch (CloudException exception) when (exception.IsUnauthorized)
            {
                SignedOut(cloud, "Dieses Gerät wurde in der Cloud entfernt – bitte neu anmelden.");
                return;
            }
            catch (CloudException exception) when (exception.IsForbidden)
            {
                Log.Warning("Sync: the cloud refused ThixNote (403)");
                SetStatus(new SyncStatus(CloudState.Offline, exception.Message, _status.LastSync));
                wait = SwitchedOffWait;
            }
            catch (Exception exception) when (IsCloudFailure(exception, stopToken))
            {
                Log.Warning($"Sync: {exception.GetType().Name}: {exception.Message}");
                SetStatus(new SyncStatus(CloudState.Offline, exception is CloudException ? exception.Message : "Cloud nicht erreichbar – neuer Versuch folgt", _status.LastSync));
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The notes folder could not be read or written (or something unforeseen): the loop goes on. The message of a file
                // error names the note, so only its kind and where it came from go into the log.
                Log.Warning($"Sync: {exception.GetType().Name} {exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                SetStatus(new SyncStatus(CloudState.Offline, "Der Notizordner ließ sich nicht abgleichen – neuer Versuch folgt", _status.LastSync));
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }

            try
            {
                // A kick ends the waiting after an error at once; after a kick it only lets the next saves gather.
                using var waitToken = settleOnly ? CancellationTokenSource.CreateLinkedTokenSource(stopToken) : CancellationTokenSource.CreateLinkedTokenSource(stopToken, KickToken());
                await Task.Delay(wait, waitToken.Token);
            }
            catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The token a kick cancels from now on (a fresh one when the last kick has been taken).</summary>
    private static CancellationToken KickToken()
    {
        lock (Gate)
        {
            if (_kick.IsCancellationRequested)
            {
                _kick.Dispose();
                _kick = new CancellationTokenSource();
            }

            return _kick.Token;
        }
    }

    /// <summary>changes of the cloud come down (and those that waited for an editor) -> local changes go up.</summary>
    private static async Task RoundAsync(MatCloudClient client, string account, CancellationToken cancellationToken)
    {
        Publish(NoteSync.RetryDeferred(account));
        await PullAsync(client, account, cancellationToken);
        await PushAsync(client, account, cancellationToken);
    }

    private static async Task PullAsync(MatCloudClient client, string account, CancellationToken cancellationToken)
    {
        while (true)
        {
            var since = NoteSync.LastRevision(account);
            var changes = await client.GetChangesAsync(since, SyncContract.MaxBatchSize, cancellationToken);
            Publish(NoteSync.ApplyRemote(account, changes.Items, changes.Latest));
            if (!changes.HasMore || changes.Items.Count == 0)
            {
                return;
            }
        }
    }

    private static async Task PushAsync(MatCloudClient client, string account, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 50; round++)
        {
            var pending = NoteSync.PendingChanges(account, SyncContract.MaxBatchSize);
            if (pending.Count == 0)
            {
                return;
            }

            try
            {
                var response = await client.PushAsync(new SyncPushRequest(pending.Select(item => item.Item).ToList()), cancellationToken);
                Publish(NoteSync.ConfirmPushed(account, pending, response));
            }
            catch (CloudException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
            {
                // The cloud refuses a whole push for one item it does not take: push one by one, so that one note never
                // holds up the others; the refused one stays on this computer only (until it changes again).
                await PushOneByOneAsync(client, account, pending, cancellationToken);
            }
        }
    }

    private static async Task PushOneByOneAsync(MatCloudClient client, string account, List<PendingItem> pending, CancellationToken cancellationToken)
    {
        foreach (var item in pending)
        {
            try
            {
                var response = await client.PushAsync(new SyncPushRequest([item.Item]), cancellationToken);
                Publish(NoteSync.ConfirmPushed(account, [item], response));
            }
            catch (CloudException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
            {
                Log.Warning($"Sync: the cloud refused item {item.Item.Key} ({exception.Field ?? "no field"}); it stays on this computer");
                NoteSync.KeepLocalOnly(account, item);
            }
        }
    }

    private static void Publish(List<NoteChange> changes)
    {
        if (changes.Count > 0)
        {
            NotesChanged?.Invoke(null, changes);
        }
    }

    private static void SignedIn(string url, DeviceLoginResponse login)
    {
        var cloud = new CloudSettings
        {
            ServerUrl = url,
            UserName = login.Account.UserName,
            DisplayName = login.Account.DisplayName,
            DeviceName = login.Account.DeviceName,
            DeviceId = login.DeviceId,
        };
        var protectedToken = TokenStore.Save(AccountKey(cloud), login.Token);
        SettingsService.UpdateCloud(_ => cloud with { ProtectedToken = protectedToken });
        NoteSync.Reset();
        Log.Info($"Connected to MatCloud at {url} as device {login.DeviceId}");
        SetStatus(new SyncStatus(CloudState.Connecting, "Verbunden – gleiche die Notizen ab …"));
        if (!LoopDisabledForTests)
        {
            Start();
        }
    }

    private static async Task DisconnectIfConnectedAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
    }

    private static string CheckDeviceName(string? deviceName)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? AppInfo.MachineName : deviceName.Trim();
        if (name.Length > CloudContract.MaxDeviceNameLength)
        {
            throw new BusinessException($"Der Gerätename ist zu lang (höchstens {CloudContract.MaxDeviceNameLength} Zeichen).", nameof(CloudSettings.DeviceName));
        }

        return name;
    }

    /// <summary>The message of the cloud at its field (user name, password, device name), or "not reachable" at the address.</summary>
    private static BusinessException Explain(Exception exception, string url)
    {
        if (exception is not CloudException cloud)
        {
            return new BusinessException($"Die Cloud ist unter {url} nicht erreichbar.", nameof(CloudSettings.ServerUrl));
        }

        var field = cloud.Field?.ToLowerInvariant() switch
        {
            "username" => nameof(CloudSettings.UserName),
            "password" => "Password",
            "devicename" => nameof(CloudSettings.DeviceName),
            _ => cloud.StatusCode == HttpStatusCode.NotFound ? nameof(CloudSettings.ServerUrl) : null,
        };
        return new BusinessException(cloud.Message, field);
    }

    /// <summary>The cloud answered with an error, could not be reached or took too long (and nobody cancelled on purpose).</summary>
    private static bool IsCloudFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is CloudException or HttpRequestException or JsonException or UriFormatException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static MatCloudClient CreateClient(string serverUrl, string? token) => new(serverUrl, token, UserAgent, HandlerForTests?.Invoke());

    private static void SignedOut(CloudSettings cloud, string message)
    {
        Log.Warning("Sync: signed out");
        Forget(cloud);
        SetStatus(new SyncStatus(CloudState.SignedOut, message));
    }

    private static void Forget(CloudSettings cloud)
    {
        if (cloud.ServerUrl.Length > 0)
        {
            TokenStore.Delete(AccountKey(cloud));
        }

        SettingsService.UpdateCloud(current => new CloudSettings { ServerUrl = current.ServerUrl, UserName = current.UserName, DeviceName = current.DeviceName });
    }

    private static string? ReadToken(CloudSettings cloud) =>
        cloud.IsConnected ? TokenStore.Read(AccountKey(cloud), cloud.ProtectedToken) : null;

    /// <summary>user@server: the Keychain entry and the account the sync index belongs to.</summary>
    private static string AccountKey(CloudSettings cloud) => $"{cloud.UserName}@{cloud.ServerUrl}";

    private static void SetStatus(SyncStatus status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(null, status);
    }
}
