using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Remote;

/// <summary>
/// <see cref="IProjectStore"/> over the Raffaello server (HTTP + SignalR). Same contract as the SQLite store: audited writes,
/// RowVersion conflicts as <see cref="ConcurrencyException"/>, atomic batches. Additionally:
/// <list type="bullet">
/// <item>reads are cached per table with ETags (a reload only downloads tables that changed);</item>
/// <item>when the server is unreachable the store goes offline: reads come from the cache, writes are queued (inserts get
/// temporary negative ids) and replayed in order on reconnect; replay problems become <see cref="SyncConflict"/>s;</item>
/// <item><see cref="RemoteChanged"/> fires for other people's changes pushed by the server.</item>
/// </list>
/// </summary>
public sealed class RemoteProjectStore : IProjectStore, IDisposable
{
    private readonly object _gate = new();
    private readonly RemoteSettings _settings;
    private readonly OfflineCache _cache;
    private SyncState _state;
    private HubConnection? _hub;
    private Timer? _reconnect;
    private bool _online;
    private bool _disposed;
    private string _user;

    public RemoteApi Api { get; }
    public string ClientId { get; } = Guid.NewGuid().ToString("N");
    public string Location => Api.BaseUri.ToString().TrimEnd('/');
    public string Machine { get; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public MeDto? Me { get; private set; }
    public ServerInfoDto? ServerInfo { get; private set; }
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The signed-in server user once connected (the server records this name, whatever the client claims).</summary>
    public string User
    {
        get => Me?.UserName ?? _user;
        set => _user = value;
    }

    public bool IsOnline => _online;
    public bool HubConnected => _hub?.State == HubConnectionState.Connected;
    public int PendingWrites { get { lock (_gate) return _state.Queue.Count; } }
    public IReadOnlyList<SyncConflict> Conflicts { get { lock (_gate) return _state.Conflicts.ToList(); } }
    public string? LastError { get; private set; }

    /// <summary>Other people's committed changes (pushed by the server). Raised on a background thread.</summary>
    public event Action<IReadOnlyList<ChangeNotice>>? RemoteChanged;
    /// <summary>Online / offline / queue / conflicts changed. Raised on a background thread.</summary>
    public event Action? StatusChanged;
    /// <summary>Back online after an outage, queue replayed: reload everything.</summary>
    public event Action? Reconnected;

    public RemoteProjectStore(RemoteSettings settings, string user, string? machine = null, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _user = string.IsNullOrWhiteSpace(user) ? Environment.UserName : user;
        Machine = machine ?? Environment.MachineName;
        Api = new RemoteApi(settings.ServerUrl, settings.Token, settings.UseWindowsAuth && string.IsNullOrWhiteSpace(settings.Token), Machine, ClientId,
            TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)), handler);
        _cache = new OfflineCache(settings.EffectiveCacheFolder());
        _state = _cache.ReadJson<SyncState>("sync.json") ?? new SyncState();
    }

    public OfflineCache Cache => _cache;

    // ------------------------------------------------------------------ connection

    /// <summary>
    /// Connects (who am I + server info), replays queued writes and starts live notifications. The schema itself is managed
    /// by the server. With no server and no offline copy this throws; with an offline copy the store opens offline.
    /// </summary>
    public void EnsureSchema()
    {
        if (TryConnect()) return;
        if (!_cache.HasAny)
            throw new ServerUnavailableException($"Cannot reach the Raffaello server at {Location} and there is no offline copy on this PC yet. {LastError}");
        StartReconnectTimer();
    }

    private bool TryConnect()
    {
        try
        {
            Me = Api.Me();
            ServerInfo = Api.Info();
            _cache.SetMeta("__me", RemoteJson.Serialize(Me));
            LastError = null;
        }
        catch (ServerUnavailableException ex)
        {
            LastError = ex.Message;
            if (Me is null && _cache.GetMeta("__me") is { } me) Me = RemoteJson.Deserialize<MeDto>(me);
            SetOnline(false);
            return false;
        }
        Replay();
        if (PendingWrites > 0) { SetOnline(false); return false; }   // replay interrupted: stay offline so the order is kept
        SetOnline(true);
        _ = StartHubAsync();
        return true;
    }

    private void SetOnline(bool online)
    {
        if (_online == online) return;
        _online = online;
        if (online) { _reconnect?.Dispose(); _reconnect = null; }
        else StartReconnectTimer();
        StatusChanged?.Invoke();
    }

    private void GoOffline(Exception ex)
    {
        LastError = ex.Message;
        SetOnline(false);
    }

    private void StartReconnectTimer()
    {
        if (_disposed || _reconnect != null) return;
        _reconnect = new Timer(_ =>
        {
            if (_online || _disposed) return;
            if (!Api.Health()) return;
            if (TryConnect()) Reconnected?.Invoke();
        }, null, ReconnectInterval, ReconnectInterval);
    }

    /// <summary>Checks the connection now (Settings "test connection" / manual retry). Returns true when online.</summary>
    public bool CheckNow()
    {
        if (_online && Api.Health()) return true;
        var was = _online;
        var ok = TryConnect();
        if (ok && !was) Reconnected?.Invoke();
        return ok;
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) => TimeSpan.FromSeconds(ctx.PreviousRetryCount switch { 0 => 0, 1 => 2, 2 => 5, _ => 15 });
    }

    private async Task StartHubAsync()
    {
        try
        {
            if (_hub is null)
            {
                var token = Api.Token;
                _hub = new HubConnectionBuilder()
                    .WithUrl(Api.Url(ApiRoutes.ChangesHub), o =>
                    {
                        if (token != null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                        else o.UseDefaultCredentials = true;
                        o.Headers[ApiRoutes.MachineHeader] = Machine;
                        o.Headers[ApiRoutes.ClientHeader] = ClientId;
                    })
                    .AddJsonProtocol(o => { o.PayloadSerializerOptions.PropertyNamingPolicy = null; o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true; })
                    .WithAutomaticReconnect(new ForeverRetry())
                    .Build();
                _hub.On<ChangeNotice[]>("changed", OnNotices);
                _hub.Reconnected += _ => { StatusChanged?.Invoke(); return Task.CompletedTask; };
                _hub.Reconnecting += _ => { StatusChanged?.Invoke(); return Task.CompletedTask; };
                _hub.Closed += async _ =>
                {
                    StatusChanged?.Invoke();
                    if (_disposed) return;
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    if (!_disposed && _online) await StartHubAsync();
                };
            }
            if (_hub.State == HubConnectionState.Disconnected) await _hub.StartAsync();
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = "Live updates not connected: " + ex.Message;
        }
    }

    /// <summary>Waits until live notifications are connected (tests, Settings test).</summary>
    public async Task<bool> WaitForHubAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (HubConnected) return true;
            if (_hub is null || _hub.State == HubConnectionState.Disconnected) await StartHubAsync();
            await Task.Delay(100);
        }
        return HubConnected;
    }

    private void OnNotices(ChangeNotice[] notices)
    {
        var others = notices.Where(n => n.ClientId != ClientId).ToList();
        if (others.Count > 0) RemoteChanged?.Invoke(others);
    }

    // ------------------------------------------------------------------ reads

    public int Count<T>()
    {
        if (_online)
        {
            try { return Api.Count(EntityMeta.TableOf(typeof(T))); }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        return _cache.Rows(typeof(T)).Count;
    }

    public List<T> All<T>() where T : new()
    {
        var table = EntityMeta.TableOf(typeof(T));
        if (_online)
        {
            try
            {
                var (json, etag) = Api.GetTable(table, _cache.ETag(table));
                if (json is null) return _cache.Rows<T>(table);
                _cache.Put(table, json, etag);
                return RemoteJson.Deserialize<List<T>>(json) ?? new List<T>();
            }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        return _cache.Rows<T>(table);
    }

    public T? Get<T>(long id) where T : Entity, new()
    {
        if (_online)
        {
            try { return Api.GetRow(EntityMeta.TableOf(typeof(T)), id) is { } j ? RemoteJson.Deserialize<T>(j) : null; }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        return _cache.Rows<T>(EntityMeta.TableOf(typeof(T))).FirstOrDefault(e => e.Id == id);
    }

    // ------------------------------------------------------------------ writes

    public T Insert<T>(T entity, string? summary = null) where T : Entity
    {
        Send(new List<(string, Entity)> { (WriteOps.Insert, entity) }, WriteModes.Single, summary);
        return entity;
    }

    public int InsertMany<T>(IEnumerable<T> entities, string? summary = null) where T : Entity
    {
        var ops = entities.Select(e => (WriteOps.Insert, (Entity)e)).ToList();
        if (ops.Count == 0) return 0;
        Send(ops, WriteModes.Many, summary);
        return ops.Count;
    }

    public T Update<T>(T entity, string? summary = null) where T : Entity, new()
    {
        Send(new List<(string, Entity)> { (WriteOps.Update, entity) }, WriteModes.Single, summary);
        return entity;
    }

    public void Delete<T>(T entity, string? summary = null) where T : Entity =>
        Send(new List<(string, Entity)> { (WriteOps.Delete, entity) }, WriteModes.Single, summary);

    public void Batch(Action<IStoreBatch> work, string summary)
    {
        var rec = new RecordingBatch(this);
        work(rec);
        if (rec.Ops.Count == 0) return;
        Send(rec.Ops, WriteModes.Batch, summary, tempIdsAssigned: true);
    }

    private sealed class RecordingBatch : IStoreBatch
    {
        private readonly RemoteProjectStore _s;
        public RecordingBatch(RemoteProjectStore s) => _s = s;
        public List<(string, Entity)> Ops { get; } = new();
        public T Insert<T>(T e) where T : Entity { e.Id = _s.NextTempId(); Ops.Add((WriteOps.Insert, e)); return e; }
        public int InsertMany<T>(IEnumerable<T> rows) where T : Entity { var n = 0; foreach (var e in rows) { Insert(e); n++; } return n; }
        public void Update<T>(T e) where T : Entity, new() => Ops.Add((WriteOps.Update, e));
        public void Delete<T>(T e) where T : Entity => Ops.Add((WriteOps.Delete, e));
    }

    private long NextTempId() { lock (_gate) { var id = _state.NextTempId--; return id; } }

    private static WriteOpDto Op(string op, Entity e) => new()
    {
        Op = op, Table = EntityMeta.TableOf(e.GetType()), Entity = JsonSerializer.SerializeToElement(e, e.GetType(), RemoteJson.Options),
    };

    private void Send(List<(string Op, Entity Entity)> ops, string mode, string? summary, bool tempIdsAssigned = false)
    {
        lock (_gate)
        {
            if (!tempIdsAssigned)
                foreach (var (op, e) in ops) if (op == WriteOps.Insert && e.Id <= 0) e.Id = NextTempId();
            var req = new WriteRequestDto { Mode = mode, Summary = summary, Ops = ops.Select(o => Op(o.Op, o.Entity)).ToList() };
            if (_online)
            {
                try
                {
                    var resp = Api.Write(req);
                    ApplyResponse(ops.Select(o => o.Entity).ToList(), ops.Select(o => o.Op).ToList(), resp);
                    return;
                }
                catch (ServerUnavailableException ex) { GoOffline(ex); }
                catch
                {
                    // the server refused: give the caller its entities back as they were (temporary ids cleared)
                    foreach (var (op, e) in ops) if (op == WriteOps.Insert && e.Id < 0) e.Id = 0;
                    throw;
                }
            }
            Queue(req, ops);
        }
    }

    private static void ApplyResponse(IReadOnlyList<Entity> entities, IReadOnlyList<string> ops, WriteResponseDto resp)
    {
        for (var i = 0; i < entities.Count && i < resp.Results.Count; i++)
        {
            if (ops[i] == WriteOps.Delete) continue;
            var r = resp.Results[i];
            entities[i].Id = r.Id; entities[i].RowVersion = r.RowVersion; entities[i].UpdatedBy = r.UpdatedBy; entities[i].UpdatedAt = r.UpdatedAt;
        }
        RemapForeignKeys(entities, resp.TempIds);
    }

    private static void RemapForeignKeys(IEnumerable<Entity> entities, IReadOnlyDictionary<long, long> map)
    {
        if (map.Count == 0) return;
        foreach (var e in entities)
            foreach (var fk in EntityMeta.ForeignKeys(e.GetType()))
                if (fk.GetValue(e) is long v && v < 0 && map.TryGetValue(v, out var real)) fk.SetValue(e, real);
    }

    // ------------------------------------------------------------------ offline queue

    private void Queue(WriteRequestDto req, List<(string Op, Entity Entity)> ops)
    {
        var unit = new QueuedWrite { Seq = _state.NextSeq++, Kind = QueuedKinds.Write, At = Clock(), User = User, Request = req };
        foreach (var (op, e) in ops)
        {
            string? baseJson = null;
            if (op != WriteOps.Insert)
            {
                var cached = _cache.Rows(e.GetType()).Cast<Entity>().FirstOrDefault(x => x.Id == e.Id);
                baseJson = cached is null ? null : RemoteJson.Serialize(cached);
            }
            unit.Bases.Add(baseJson);
            if (op == WriteOps.Insert) e.RowVersion = 1;
            e.UpdatedBy = User; e.UpdatedAt = Clock();
            _cache.Apply(op, e);
        }
        _state.Queue.Add(unit);
        SaveState();
        StatusChanged?.Invoke();
    }

    private void QueueSimple(string kind, string key, string? value)
    {
        lock (_gate)
        {
            _state.Queue.Add(new QueuedWrite { Seq = _state.NextSeq++, Kind = kind, At = Clock(), User = User, Key = key, Value = value });
            SaveState();
        }
        StatusChanged?.Invoke();
    }

    private void SaveState() { lock (_gate) _cache.WriteJson("sync.json", _state); }

    /// <summary>
    /// Sends the queued writes in order. Each unit keeps its RequestId, so a unit the server already committed (answer lost
    /// on the way back) is not written twice. Refusals become conflicts; a network failure stops the replay.
    /// </summary>
    public int Replay()
    {
        var sent = 0;
        lock (_gate)
        {
            while (_state.Queue.Count > 0)
            {
                var unit = _state.Queue[0];
                try
                {
                    switch (unit.Kind)
                    {
                        case QueuedKinds.Event: Api.LogEvent(unit.Key, unit.Value ?? ""); break;
                        case QueuedKinds.Meta: Api.SetMeta(unit.Key, unit.Value ?? ""); break;
                        default: SendUnit(unit); break;
                    }
                    sent++;
                }
                catch (ServerUnavailableException ex) { LastError = ex.Message; SaveState(); return sent; }
                catch (Exception ex) { _state.Conflicts.Add(ToConflict(unit, ex)); }
                _state.Queue.RemoveAt(0);
                SaveState();
            }
        }
        if (sent > 0) _cache.Invalidate();
        StatusChanged?.Invoke();
        return sent;
    }

    /// <summary>Rewrites temporary ids / stale own versions, then sends the unit.</summary>
    private WriteResponseDto SendUnit(QueuedWrite unit)
    {
        var entities = new List<Entity>();
        var sentVersions = new List<long>();
        foreach (var op in unit.Request.Ops)
        {
            var t = EntityMeta.Find(op.Table) ?? throw new InvalidOperationException($"Unknown table {op.Table} in the offline queue.");
            var e = (Entity)op.Entity.Deserialize(t, RemoteJson.Options)!;
            // inserts keep their temporary id (the server maps it); updates / deletes of rows inserted offline use the real id
            if (e.Id < 0 && op.Op != WriteOps.Insert && _state.IdMap.TryGetValue(e.Id, out var real)) e.Id = real;
            RemapForeignKeys(new[] { e }, _state.IdMap);
            if (op.Op != WriteOps.Insert && _state.VersionMap.TryGetValue($"{op.Table}|{e.Id}", out var vm) && e.RowVersion == vm[0]) e.RowVersion = vm[1];
            op.Entity = JsonSerializer.SerializeToElement(e, t, RemoteJson.Options);
            entities.Add(e);
            sentVersions.Add(e.RowVersion);
        }
        var resp = Api.Write(unit.Request);
        foreach (var kv in resp.TempIds) _state.IdMap[kv.Key] = kv.Value;
        for (var i = 0; i < resp.Results.Count && i < unit.Request.Ops.Count; i++)
        {
            var r = resp.Results[i];
            if (r.Op == WriteOps.Update) _state.VersionMap[$"{r.Table}|{r.Id}"] = new[] { OriginalVersion(unit, i, sentVersions[i]), r.RowVersion };
        }
        return resp;
    }

    /// <summary>The version the user's queued edits of a row are based on (the base row's version, else what was sent).</summary>
    private static long OriginalVersion(QueuedWrite unit, int i, long sent)
    {
        var b = i < unit.Bases.Count ? unit.Bases[i] : null;
        if (b is null) return sent;
        using var doc = JsonDocument.Parse(b);
        return doc.RootElement.TryGetProperty(nameof(Entity.RowVersion), out var v) ? v.GetInt64() : sent;
    }

    private SyncConflict ToConflict(QueuedWrite unit, Exception ex)
    {
        var c = new SyncConflict { At = Clock(), Unit = unit, Message = ex.Message };
        switch (ex)
        {
            case ConcurrencyException ce:
                c.Kind = ConflictKinds.RowVersion;
                c.Table = ce.Table; c.RowId = ce.RowId; c.ChangedBy = ce.ChangedBy;
                c.TheirsJson = ce.Data["current"] as string;
                c.ChangedAt = ce.Data["changedAt"] as DateTime?;
                c.OpIndex = unit.Request.Ops.FindIndex(o => o.Op != WriteOps.Insert && string.Equals(o.Table, ce.Table, StringComparison.OrdinalIgnoreCase) && IdOf(o) == ce.RowId);
                break;
            case RemainingExceededException:
                c.Kind = ConflictKinds.Remaining;
                c.Table = EntityMeta.TableOf(typeof(ClaimLine));
                c.OpIndex = unit.Request.Ops.FindIndex(o => o.Op == WriteOps.Insert && string.Equals(o.Table, c.Table, StringComparison.OrdinalIgnoreCase));
                break;
            default:
                c.Kind = ConflictKinds.Refused;
                c.Table = unit.Request.Ops.FirstOrDefault()?.Table ?? "";
                c.OpIndex = 0;
                break;
        }
        if (c.OpIndex >= 0 && c.OpIndex < unit.Request.Ops.Count)
        {
            var op = unit.Request.Ops[c.OpIndex];
            c.MineJson = op.Entity.GetRawText();
            c.BaseJson = c.OpIndex < unit.Bases.Count ? unit.Bases[c.OpIndex] : null;
            if (c.RowId == 0) c.RowId = IdOf(op);
        }
        return c;
    }

    private static long IdOf(WriteOpDto o) => o.Entity.TryGetProperty(nameof(Entity.Id), out var v) ? v.GetInt64() : 0;

    // ------------------------------------------------------------------ conflict resolution

    /// <summary>Keep theirs: drop my queued change; the server copy stays.</summary>
    public void KeepTheirs(SyncConflict c)
    {
        lock (_gate) { _state.Conflicts.RemoveAll(x => x.Id == c.Id); SaveState(); }
        _cache.Invalidate();
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Keep mine: RowVersion conflict -> my version is written over theirs; remaining conflict -> the claim is posted OVER with
    /// <paramref name="overReason"/>. Throws if the server refuses again (the conflict then stays in the list).
    /// </summary>
    public void KeepMine(SyncConflict c, string? overReason = null)
    {
        if (c.OpIndex < 0) throw new InvalidOperationException("This change cannot be re-sent; keep theirs to drop it.");
        var op = c.Unit.Request.Ops[c.OpIndex];
        var t = EntityMeta.Find(op.Table)!;
        var mine = (Entity)op.Entity.Deserialize(t, RemoteJson.Options)!;
        switch (c.Kind)
        {
            case ConflictKinds.RowVersion:
                var theirs = c.TheirsJson is null ? null : (Entity?)RemoteJson.Deserialize(c.TheirsJson, t);
                mine.RowVersion = theirs?.RowVersion ?? mine.RowVersion;
                break;
            case ConflictKinds.Remaining when mine is ClaimLine cl:
                cl.IsOver = true;
                cl.OverReason = string.IsNullOrWhiteSpace(overReason) ? "Entered offline - remaining was used by others before it reached the server" : overReason.Trim();
                foreach (var o in c.Unit.Request.Ops.Where(o => o.Op == WriteOps.Insert && o.Table == op.Table))
                {
                    var other = (ClaimLine)o.Entity.Deserialize(t, RemoteJson.Options)!;
                    if (other.IsOver) continue;
                    other.IsOver = true; other.OverReason = cl.OverReason;
                    o.Entity = JsonSerializer.SerializeToElement(other, t, RemoteJson.Options);
                }
                break;
            default:
                throw new InvalidOperationException("The server refused this change; it can only be dropped (keep theirs).");
        }
        Resend(c, mine);
    }

    /// <summary>Merge plan for a RowVersion conflict (simple fields only).</summary>
    public List<MergeField> MergePlan(SyncConflict c)
    {
        if (c.Kind != ConflictKinds.RowVersion || c.MineJson is null || c.TheirsJson is null) return new List<MergeField>();
        var t = EntityMeta.Find(c.Table)!;
        var mine = (Entity)RemoteJson.Deserialize(c.MineJson, t)!;
        var theirs = (Entity)RemoteJson.Deserialize(c.TheirsJson, t)!;
        var b = c.BaseJson is null ? null : (Entity?)RemoteJson.Deserialize(c.BaseJson, t);
        return FieldMerge.Plan(b, mine, theirs);
    }

    /// <summary>Writes theirs + the fields chosen as mine.</summary>
    public void Merge(SyncConflict c, IEnumerable<MergeField> fields)
    {
        if (c.Kind != ConflictKinds.RowVersion || c.MineJson is null || c.TheirsJson is null) throw new InvalidOperationException("Only row conflicts can be merged.");
        var t = EntityMeta.Find(c.Table)!;
        var mine = (Entity)RemoteJson.Deserialize(c.MineJson, t)!;
        var theirs = (Entity)RemoteJson.Deserialize(c.TheirsJson, t)!;
        Resend(c, FieldMerge.Apply(mine, theirs, fields));
    }

    private void Resend(SyncConflict c, Entity replacement)
    {
        lock (_gate)
        {
            var unit = c.Unit;
            unit.Request.Ops[c.OpIndex].Entity = JsonSerializer.SerializeToElement(replacement, replacement.GetType(), RemoteJson.Options);
            unit.Request.RequestId = Guid.NewGuid();
            try
            {
                SendUnit(unit);
            }
            catch (Exception ex) when (ex is not ServerUnavailableException)
            {
                var again = ToConflict(unit, ex);
                again.Id = c.Id;
                _state.Conflicts.RemoveAll(x => x.Id == c.Id);
                _state.Conflicts.Add(again);
                SaveState();
                throw;
            }
            _state.Conflicts.RemoveAll(x => x.Id == c.Id);
            SaveState();
        }
        _cache.Invalidate();
        StatusChanged?.Invoke();
    }

    // ------------------------------------------------------------------ admin / audit / presence / meta

    public void ClearAll()
    {
        if (!_online) throw new InvalidOperationException("Clearing the server data needs a connection to the server.");
        Api.ClearAll();
        _cache.Invalidate();
    }

    public void LogEvent(string action, string summary)
    {
        if (_online)
        {
            try { Api.LogEvent(action, summary); return; }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        QueueSimple(QueuedKinds.Event, action, summary);
    }

    public List<AuditEntry> RecentAudit(int take = 50, DateTime? since = null)
    {
        if (_online)
        {
            try
            {
                var rows = Api.Audit(take, since);
                if (since is null) _cache.WriteJson("audit.json", rows);
                return rows;
            }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        var cached = _cache.ReadJson<List<AuditEntry>>("audit.json") ?? new List<AuditEntry>();
        return cached.Where(a => since is null || a.At > since).Take(take).ToList();
    }

    public void Heartbeat(string screen)
    {
        if (!_online) return;
        try
        {
            if (HubConnected) _hub!.InvokeAsync("Heartbeat", screen).Wait(TimeSpan.FromSeconds(10));
            else Api.Heartbeat(screen);
        }
        catch (ServerUnavailableException ex) { GoOffline(ex); }
        catch (Exception) { /* presence is best effort */ }
    }

    public List<PresenceRow> OthersOnline(TimeSpan window)
    {
        if (!_online) return new List<PresenceRow>();
        try { return Api.Presence(window); }
        catch (ServerUnavailableException ex) { GoOffline(ex); return new List<PresenceRow>(); }
    }

    public string? GetMeta(string key)
    {
        if (_online)
        {
            try { var v = Api.GetMeta(key); _cache.SetMeta(key, v); return v; }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        return _cache.GetMeta(key);
    }

    public void SetMeta(string key, string value)
    {
        _cache.SetMeta(key, value);
        if (_online)
        {
            try { Api.SetMeta(key, value); return; }
            catch (ServerUnavailableException ex) { GoOffline(ex); }
        }
        QueueSimple(QueuedKinds.Meta, key, value);
    }

    // ------------------------------------------------------------------ server-only features

    public List<ApprovalStampDto> Stamps(SubInvoice inv) => Api.Stamps(EntityMeta.TableOf(typeof(SubInvoice)), inv.Id);

    /// <summary>Gives the CHECKED / APPROVED stamp to the invoice revision as it is now (its RowVersion).</summary>
    public ApprovalStampDto Stamp(SubInvoice inv, string stage, string note = "") =>
        Api.Stamp(new StampRequest { Table = EntityMeta.TableOf(typeof(SubInvoice)), Id = inv.Id, Stage = stage, RowVersion = inv.RowVersion, Note = note });

    public DocumentInfo UploadDocument(string path, string category = "", string linkedTable = "", long linkedId = 0)
    {
        using var f = File.OpenRead(path);
        return Api.Upload(f, Path.GetFileName(path), category, linkedTable, linkedId);
    }

    public DocumentInfo DownloadDocument(long id, string path) => Api.Download(id, path);

    public void Dispose()
    {
        _disposed = true;
        _reconnect?.Dispose();
        try { _hub?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* closing */ }
        Api.Dispose();
    }
}
