using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using MatCloud.Contract;

namespace ThixNote.Tests;

/// <summary>
/// MatCloud in memory, after the rules of <see cref="CloudContract"/> and <see cref="SyncContract"/>: the sign-in with a
/// password and in the browser (a code that a test approves or denies), the account, and the general sync of ThixNote – one
/// revision counter, tombstones, the last writer wins by UpdatedAt (a delete too), and the rules of MatCloud's Note module: a
/// protected note turns plain only with <c>"unprotected":true</c> (<see cref="GuardsProtectedNotes"/>), and that marker stays on
/// the later plain states of the note (<see cref="CarriesUnprotectMarker"/>). Enough to test the desktop side.
/// </summary>
internal sealed class FakeCloud : HttpMessageHandler
{
    public const string UserName = "anna";
    public const string DisplayName = "Anna Berger";
    public const string Password = "geheim-123";

    /// <summary>How MatCloud's Note module writes the data it carries the marker into.</summary>
    private static readonly JsonSerializerOptions StoredJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly object _gate = new();
    private readonly Dictionary<string, (long Id, string Name)> _devices = [];
    private readonly Dictionary<string, Link> _links = [];
    private long _revision;
    private long _lastDeviceId;

    /// <summary>Every item of the account by collection and key (tombstones included).</summary>
    public Dictionary<(string Collection, string Key), SyncItemDto> Items { get; } = [];

    /// <summary>The user agents of every request (the cloud shows the version with the device).</summary>
    public List<string> UserAgents { get; } = [];

    /// <summary>The administrator switched ThixNote off: every call of a device answers 403.</summary>
    public bool IsAppSwitchedOff { get; set; }

    /// <summary>
    /// A push that turns a protected note (bytes, a .txts) into a plain one without <c>"unprotected":true</c> is not applied: the
    /// device gets the protected note as <see cref="SyncPushResult.Current"/>, as for a newer change. False = a cloud before that rule.
    /// </summary>
    public bool GuardsProtectedNotes { get; set; } = true;

    /// <summary>
    /// A plain version without the marker over a plain note that carries <c>"unprotected":true</c> is stored with the marker added
    /// (every other property as pushed): a computer that missed the removal of the protection still sees it was on purpose. The
    /// marker goes when the note is protected again or deleted. False = a cloud that keeps the marker only with the one push.
    /// </summary>
    public bool CarriesUnprotectMarker { get; set; } = true;

    /// <summary>A pull sees nothing above this revision (a change that reached the cloud between a device's pull and its push).</summary>
    public long? HideChangesAbove { get; set; }

    /// <summary>Every item that was pushed, in order (applied or not).</summary>
    public List<SyncItemDto> Pushed { get; } = [];

    public long Revision => _revision;

    /// <summary>The live notes as the cloud has them.</summary>
    public List<SyncItemDto> Notes
    {
        get
        {
            lock (_gate)
            {
                return Items.Values.Where(item => item.Collection == "notes" && !item.IsDeleted).ToList();
            }
        }
    }

    /// <summary>The code of the last sign-in started in the browser.</summary>
    public string? LastCode
    {
        get
        {
            lock (_gate)
            {
                return _links.Values.LastOrDefault()?.Code;
            }
        }
    }

    /// <summary>The person confirms the code in the browser (signed in as <see cref="UserName"/>).</summary>
    public void Approve(string code) => SetLinkState(code, DeviceLinkState.Approved);

    /// <summary>ThixLauncher on this computer asks for a sign-in of ThixNote (already confirmed); returns the poll token it leaves for ThixNote.</summary>
    public string LeaveHandoff(string deviceName)
    {
        lock (_gate)
        {
            var link = new Link("HAND-OVER", deviceName, Guid.NewGuid().ToString("D")) { State = DeviceLinkState.Approved };
            _links[link.PollToken] = link;
            return link.PollToken;
        }
    }

    /// <summary>
    /// The person saves a plain note on the pages of the cloud (MatCloud's Note module): its new data goes in like a change of a
    /// device, without <c>"unprotected"</c> – the pages never remove a protection, they only edit a note that is plain already
    /// (the cloud carries a marker the note has, as for any writer).
    /// </summary>
    public void SaveInBrowser(string key, string path, string content)
    {
        lock (_gate)
        {
            var current = Items[("notes", key)];
            var saved = current with { Data = JsonSerializer.Serialize(new { path, content }, CloudContract.Json) };
            Items[("notes", key)] = current with
            {
                Data = CarriesUnprotectMarker ? WithCarriedMarker(current, saved) : saved.Data,
                ContentHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))),
                UpdatedAt = current.UpdatedAt.AddSeconds(1),
                Source = "Browser",
                Revision = ++_revision,
            };
        }
    }

    /// <summary>The person refuses the code in the browser.</summary>
    public void Deny(string code) => SetLinkState(code, DeviceLinkState.Denied);

    /// <summary>Every device was removed in the browser: their tokens answer 401 from now on.</summary>
    public void RemoveAllDevices()
    {
        lock (_gate)
        {
            _devices.Clear();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        lock (_gate)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
        }

        if (!request.Headers.TryGetValues(CloudContract.ProtocolHeader, out var protocol) || protocol.Single() != "1")
        {
            return Json(HttpStatusCode.BadRequest, new ApiError("Protokoll fehlt."));
        }

        switch (path)
        {
            case CloudContract.DeviceLogin:
            {
                var login = (await request.Content!.ReadFromJsonAsync<DeviceLoginRequest>(CloudContract.Json, cancellationToken))!;
                if (login.App != CloudApps.Note)
                {
                    return Json(HttpStatusCode.BadRequest, new ApiError("Unbekannte App.", "app"));
                }

                return login.UserName != UserName || login.Password != Password
                    ? Json(HttpStatusCode.Unauthorized, new ApiError("Benutzername oder Passwort stimmen nicht."))
                    : Json(HttpStatusCode.OK, NewDevice(login.DeviceName));
            }

            case CloudContract.DeviceLink:
            {
                var start = (await request.Content!.ReadFromJsonAsync<DeviceLinkRequest>(CloudContract.Json, cancellationToken))!;
                lock (_gate)
                {
                    var code = $"CODE-{_links.Count + 1:D4}";
                    var link = new Link(code, start.DeviceName, Guid.NewGuid().ToString("D"));
                    _links[link.PollToken] = link;
                    var baseUrl = request.RequestUri.GetLeftPart(UriPartial.Authority);
                    return Json(HttpStatusCode.OK, new DeviceLinkStart(code, $"{baseUrl}{CloudContract.ConnectPage}?code={code}", link.PollToken, 600, 1));
                }
            }

            case CloudContract.DeviceLinkPoll:
            {
                var poll = (await request.Content!.ReadFromJsonAsync<DeviceLinkPollRequest>(CloudContract.Json, cancellationToken))!;
                lock (_gate)
                {
                    if (!_links.TryGetValue(poll.PollToken, out var link) || link.IsFetched)
                    {
                        return Json(HttpStatusCode.OK, new DeviceLinkPollResponse(DeviceLinkState.Expired));
                    }

                    if (link.State != DeviceLinkState.Approved)
                    {
                        return Json(HttpStatusCode.OK, new DeviceLinkPollResponse(link.State));
                    }

                    link.IsFetched = true;
                    return Json(HttpStatusCode.OK, new DeviceLinkPollResponse(DeviceLinkState.Approved, NewDevice(link.DeviceName)));
                }
            }
        }

        (long Id, string Name) device;
        lock (_gate)
        {
            var token = request.Headers.Authorization?.Parameter;
            if (token is null || !_devices.TryGetValue(token, out device))
            {
                return Json(HttpStatusCode.Unauthorized, new ApiError("Dieses Gerät ist nicht (mehr) angemeldet."));
            }

            if (IsAppSwitchedOff)
            {
                return Json(HttpStatusCode.Forbidden, new ApiError("ThixNote ist in dieser Cloud ausgeschaltet."));
            }

            if (path == CloudContract.DeviceLogout)
            {
                _devices.Remove(token);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        switch (path)
        {
            case CloudContract.Account:
                return Json(HttpStatusCode.OK, new AccountInfo(UserName, DisplayName, device.Name, CloudApps.Note, "test"));

            case SyncContract.Wait:
                lock (_gate)
                {
                    return Json(HttpStatusCode.OK, new WaitResult(_revision));
                }

            case SyncContract.Changes:
            {
                var since = long.Parse(query["since"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                var limit = Math.Clamp(int.Parse(query["limit"] ?? "100", System.Globalization.CultureInfo.InvariantCulture), 1, SyncContract.MaxBatchSize);
                lock (_gate)
                {
                    var newer = Items.Values.Where(item => item.Revision > since && (HideChangesAbove is not { } hidden || item.Revision <= hidden)).OrderBy(item => item.Revision).ToList();
                    var items = newer.Take(limit).ToList();
                    return Json(HttpStatusCode.OK, new SyncChangeSet(items, items.Count > 0 ? items[^1].Revision : since, newer.Count > limit));
                }
            }

            case SyncContract.Push:
            {
                var push = (await request.Content!.ReadFromJsonAsync<SyncPushRequest>(CloudContract.Json, cancellationToken))!;
                if (Validate(push) is { } problem)
                {
                    return Json(HttpStatusCode.BadRequest, problem);
                }

                lock (_gate)
                {
                    var results = new List<SyncPushResult>();
                    foreach (var item in push.Items)
                    {
                        Pushed.Add(item);
                        if (Items.TryGetValue((item.Collection, item.Key), out var current)
                            && (current.UpdatedAt > item.UpdatedAt || (GuardsProtectedNotes && IsUnmarkedUnprotect(current, item))))
                        {
                            results.Add(new SyncPushResult(item.Collection, item.Key, current.Revision, false, current));
                            continue;
                        }

                        var stored = item with
                        {
                            Data = item.IsDeleted ? null : CarriesUnprotectMarker ? WithCarriedMarker(current, item) : item.Data,
                            ContentHash = item.IsDeleted ? null : item.ContentHash,
                            Source = device.Name,
                            Revision = ++_revision,
                        };
                        Items[(item.Collection, item.Key)] = stored;
                        results.Add(new SyncPushResult(item.Collection, item.Key, stored.Revision, true));
                    }

                    return Json(HttpStatusCode.OK, new SyncPushResponse(results, _revision));
                }
            }

            default:
                return Json(HttpStatusCode.NotFound, new ApiError("Nicht gefunden."));
        }
    }

    /// <summary>A live protected note that would become plain without the marker (a delete is not this rule's business).</summary>
    private static bool IsUnmarkedUnprotect(SyncItemDto current, SyncItemDto item)
    {
        if (item.Collection != "notes" || current.IsDeleted || item.IsDeleted || !IsProtected(current.Data))
        {
            return false;
        }

        if (IsProtected(item.Data))
        {
            return false;
        }

        return !HasMarker(item.Data);
    }

    /// <summary>
    /// The data to store: a live plain version without the marker over a live plain note that carries it gets <c>"unprotected":true</c>
    /// added, every other property kept as pushed (the content hash is the content's and stays), written as MatCloud writes it (the
    /// name in any case, umlauts and &lt; &gt; unescaped). The marker may take the data over the length a device may push, by its own
    /// length (as MatCloud's MarkerAllowance: dropped there, no later push would carry it again); beyond that, and anything else, as pushed.
    /// </summary>
    private static string? WithCarriedMarker(SyncItemDto? current, SyncItemDto item)
    {
        if (item.Collection != "notes" || current is null || current.IsDeleted || item.IsDeleted || item.Data is null
            || IsProtected(current.Data) || IsProtected(item.Data) || !HasMarker(current.Data) || HasMarker(item.Data))
        {
            return item.Data;
        }

        var data = JsonNode.Parse(item.Data, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();
        data["unprotected"] = true;
        var marked = data.ToJsonString(StoredJson);
        return marked.Length <= SyncContract.MaxDataLength + ",\"unprotected\":true".Length ? marked : item.Data;
    }

    /// <summary>The note carries <c>"unprotected":true</c>, the name in any case (as MatCloud reads it; the last one counts).</summary>
    private static bool HasMarker(string? data)
    {
        if (data is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(data);
        return document.RootElement.EnumerateObject()
            .LastOrDefault(property => string.Equals(property.Name, "unprotected", StringComparison.OrdinalIgnoreCase))
            .Value.ValueKind == JsonValueKind.True;
    }

    private static bool IsProtected(string? data)
    {
        if (data is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        return (root.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.String)
               || (root.TryGetProperty("path", out var path) && path.GetString()?.EndsWith(".txts", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static ApiError? Validate(SyncPushRequest push)
    {
        if (push.Items.Count > SyncContract.MaxBatchSize)
        {
            return new ApiError("Zu viele Einträge.", "items");
        }

        for (var i = 0; i < push.Items.Count; i++)
        {
            var item = push.Items[i];
            if (!SyncContract.IsValidCollection(item.Collection) || !SyncContract.IsValidKey(item.Key))
            {
                return new ApiError("Sammlung oder Schlüssel ungültig.", $"items[{i}].key");
            }

            if ((!item.IsDeleted && item.Data is null) || item.Data?.Length > SyncContract.MaxDataLength)
            {
                return new ApiError("Inhalt fehlt oder ist zu groß.", $"items[{i}].data");
            }
        }

        return push.Items.GroupBy(item => (item.Collection, item.Key)).Any(group => group.Count() > 1) ? new ApiError("Doppelt.", "items") : null;
    }

    private DeviceLoginResponse NewDevice(string deviceName)
    {
        lock (_gate)
        {
            var token = Guid.NewGuid().ToString("N");
            var id = ++_lastDeviceId;
            _devices[token] = (id, deviceName);
            return new DeviceLoginResponse(token, id, new AccountInfo(UserName, DisplayName, deviceName, CloudApps.Note, "test"));
        }
    }

    private void SetLinkState(string code, DeviceLinkState state)
    {
        lock (_gate)
        {
            _links.Values.Single(link => link.Code == code).State = state;
        }
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body, options: CloudContract.Json) };

    private sealed class Link(string code, string deviceName, string pollToken)
    {
        public string Code { get; } = code;

        public string DeviceName { get; } = deviceName;

        public string PollToken { get; } = pollToken;

        public DeviceLinkState State { get; set; } = DeviceLinkState.Pending;

        public bool IsFetched { get; set; }
    }
}
