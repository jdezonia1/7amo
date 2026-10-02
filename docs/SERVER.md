# Raffaello.Server - developer notes (Phase 5)

```
 Raffaello.App (WPF)                              office server PC (Windows service "RaffaelloServer")
 ProjectService ── IProjectStore                  Raffaello.Server (ASP.NET Core minimal APIs, Kestrel :5180)
                    ├─ Db (SQLite file)            ├─ /api/v1/*    REST (JSON, PascalCase)
                    └─ RemoteProjectStore ──HTTP──►├─ /hubs/changes SignalR "changed" notices
                         ├─ OfflineCache (ETag)    ├─ PgStore : IProjectStore  (+ guards, audit, versions)
                         ├─ SyncState (queue,      ├─ UserStore (BCrypt app accounts, Windows mapping, tokens)
                         │   conflicts)            ├─ DocumentStore (UNC share + SHA-256 metadata)
                         └─ HubConnection ◄──push──┤─ NightlyBackupService (pg_dump, retention)
                                                   └─ PostgreSQL 16
```

## Choices

* **Npgsql with a small reflection mapper, no EF Core / Dapper.** The entities are plain POCOs already mapped by
  reflection in the SQLite `Db`; the server uses the same rule (public get/set properties = columns, table = type name + "s").
  New properties or entity types added by any phase appear on the server with no DbContext, no migration class and no
  mapping code: tables are created / extended additively at start-up (like SQLite). Hand-written numbered migrations
  exist only for the server's own tables (`PgSchema.Migrations`). EF Core would need every phase to edit one DbContext
  and generate migrations (merge conflicts); Dapper would add little over the existing mapper.
* **Concurrency.** Updates/deletes: `SELECT ... FOR UPDATE` + `UPDATE ... WHERE RowVersion = @old` (409 with the current
  row and who changed it). Ledger claims: `pg_advisory_xact_lock(ledger|ROOM|STAGE|ITEM)` then the remaining check
  (`LedgerRules.Balance/Check`, the same code as the desktop) inside the transaction - claims on one key are serialised,
  other keys run in parallel. READ COMMITTED is enough because the lock is taken before the balance is read.
* **Idempotent writes.** Every write carries a RequestId; the server stores the answer in `__requests` in the same
  transaction, so a replay after a lost answer never writes twice.
* **Temporary ids.** Batches (and offline inserts) use negative ids; the server replaces them and remaps every `long`/`long?`
  column named `*Id` that carries one (e.g. `SubInvoiceLine.SubInvoiceId`). Keep foreign keys named `XxxId`.
* **Read cache.** `GET /api/v1/tables/{table}` returns an ETag (`epoch-version`, the version bumps in every write
  transaction that touches the table); a reload downloads only changed tables. The epoch changes on each server start,
  so a restored database can never be confused with a cached one.

## API (all JSON; everything except health and login needs a Windows or bearer sign-in)

| Route | |
|---|---|
| `GET /api/v1/health` | anonymous liveness + DB check |
| `POST /api/v1/auth/login` / `logout` | app-account token (30 days); Windows users use Negotiate instead |
| `GET /api/v1/me`, `/info` | who am I (role, permissions), server version, tables |
| `GET /api/v1/tables/{t}` · `/{t}/{id}` · `/{t}/count` | reads (ETag / If-None-Match) |
| `POST /api/v1/write` | `{RequestId, Mode: SINGLE/MANY/BATCH, Summary, Ops:[{Op, Table, Entity}]}` atomically |
| `POST /api/v1/admin/clear-all` | ADMIN |
| `GET /api/v1/audit` · `POST /events` · `GET/POST /presence` · `GET/PUT /meta/{key}` | audit, presence, meta |
| `GET/POST /api/v1/approvals` | invoice stamps CHECKED / APPROVED (SUBMITTED / status stamps are automatic) |
| `POST /api/v1/documents?fileName=&category=&linkedTable=&linkedId=` (raw body) · `GET /documents[/{id}[/info,/verify]]` | documents |
| `GET/POST/PUT /api/v1/users` | ADMIN |
| `POST /api/v1/migrate/import` · `/migrate/audit` | ADMIN, used by "Copy local data to server" |
| `/hubs/changes` | SignalR: server sends `changed(ChangeNotice[])` |

Errors: `ErrorDto {Code, Message, Table, RowId, ChangedBy, ChangedAt, Current}` with codes `conflict` (409),
`remaining_exceeded` (422), `approval_required` (422), `append_only` / `locked` / `invalid_transition` (409),
`forbidden` (403), `not_found` (404), `too_large` (413). The client maps them to `ConcurrencyException`,
`RemainingExceededException`, `PermissionDeniedException`, `RemoteRejectedException`.

## Plug-in pattern: giving another store server support

Phases that add their own store (e.g. `IMaterialsStore`, `IAconexStore`, `IVariationStore`) get the server, the offline
queue, conflicts, audit and live notifications by following four steps. Nothing in `IProjectStore` has to change.

1. **Entities.** Derive from `Raffaello.Core.Domain.Entity` (Id, UpdatedBy, UpdatedAt, RowVersion), public get/set
   properties, parameterless constructor. Foreign keys are `long`/`long?` named `XxxId`. Supported column types: string,
   int, long, double, decimal, bool, DateTime(?), byte[], enums, Guid.
2. **Server module.** Add `src/Raffaello.Server/Modules/MaterialsServerModule.cs`:
   ```csharp
   public sealed class MaterialsServerModule : IServerModule
   {
       public string Name => "Materials";
       public IEnumerable<Type> EntityTypes => new[] { typeof(MaterialReceipt), typeof(MaterialReceiptLine) };
       public IEnumerable<IWriteGuard> Guards(IServiceProvider sp) => new IWriteGuard[] { new OverPoGuard() }; // optional rules
       public void MapEndpoints(IEndpointRouteBuilder api) { /* optional non-CRUD endpoints, e.g. api.MapGet("/api/v1/materials/dn-lookup", ...) */ }
   }
   ```
   and add one line `new MaterialsServerModule(),` to `ServerModules.All` (`src/Raffaello.Server/Data/EntityRegistry.cs`).
   The tables are created on the next server start; `/api/v1/tables/MaterialReceipts` and `/api/v1/write` serve them with
   RowVersion checks, audit rows, change notices and ETags. A guard (`IWriteGuard.Check` before the row is written,
   `AfterWrite` after, same transaction, `c.Tx.Lock(key)` for a serialising lock, `c.Tx.Query<T>(sql)` to read) is where
   server-side business rules go; throw `WriteRejectedException(status, code, message)` to refuse.
   Writes to a new table need `EDIT_DATA` by default; for another permission add the type to `PermissionGuard.Required`
   (one line) or add your own guard.
   Types added straight to `Db.EntityTypes` need no module at all (they are registered automatically on both sides).
3. **Client.** Register the types once (e.g. in a static constructor of your remote store) with
   `EntityMeta.Register(typeof(MaterialReceipt))`, then implement the remote variant of your interface on top of the
   generic methods of `RemoteProjectStore` (`DataSourceFactory.Current`), which work for any registered entity type -
   including the offline queue, temporary ids and sync conflicts:
   ```csharp
   public sealed class RemoteMaterialsStore : IMaterialsStore
   {
       private readonly RemoteProjectStore _r;
       static RemoteMaterialsStore() { EntityMeta.Register(typeof(MaterialReceipt)); EntityMeta.Register(typeof(MaterialReceiptLine)); }
       public RemoteMaterialsStore(RemoteProjectStore r) => _r = r;
       public List<MaterialReceipt> Receipts() => _r.All<MaterialReceipt>();
       public void Save(MaterialReceipt h, List<MaterialReceiptLine> lines) =>
           _r.Batch(w => { w.Insert(h); foreach (var l in lines) l.ReceiptId = h.Id; w.InsertMany(lines); }, $"MIR {h.MirNo} saved");
   }
   ```
   Choose the implementation where the SQLite one is created today:
   `DataSourceFactory.Current is { } remote ? new RemoteMaterialsStore(remote) : new SqliteMaterialsStore(...)`.
   For non-entity calls (reports, look-ups) map an endpoint in the module and call it through `remote.Api`.
4. **Migration + tests.** `LocalToServerMigrator` copies every type in `EntityMeta.All` - registered types are included
   automatically (their SQLite tables must be readable through `IProjectStore.All<T>()`; if your SQLite store uses its own
   file, add a call in your phase that runs `LocalToServerMigrator`'s pattern on it). Copy the integration-test pattern
   from `tests/Raffaello.Server.Tests` (`TestServer.StartAsync()`, `srv.Client(user, role)`, `[PgFact]`).

## Tests

`dotnet test tests/Raffaello.Server.Tests` - unit tests always run; integration tests (`[PgFact]`) need PostgreSQL:
role `raffaello` / password `raffaello_test` with CREATEDB on 127.0.0.1:5432, or `RAFFAELLO_TEST_PG` set to a
connection string without `Database`. Each test creates and drops its own database and starts a real server on a free port.
Without PostgreSQL the integration tests are reported as skipped with that reason.

Not testable on Linux: Windows (Negotiate) sign-in, the Windows service and SETUP_SERVER.bat - verify on the office server.

## Sign-in rate limit (phase 6)

`POST /api/v1/auth/login`: after `LoginMaxFailures` (5) wrong passwords for the same user name, or from the same address,
within `LoginWindowMinutes` (15), sign-in is refused with **429 `too_many_attempts`** and a `Retry-After` header for
`LoginLockoutMinutes` (5). A correct password clears the user's counter. Windows (Negotiate) sign-in is not affected.
Settings live in `appsettings.json` section `Raffaello`.

## Optional HTTPS with a certificate from IT

Kestrel serves HTTPS when the URL starts with `https` and a certificate is configured. Ask IT for a certificate (PFX) issued to
the server PC's name (e.g. `raffaello.mobco.local`), copy it next to the service (e.g. `C:\Raffaello\Server\raffaello.pfx`) and set,
in `appsettings.json`:

```json
{
  "Raffaello": { "Urls": "https://0.0.0.0:5443" },
  "Kestrel": {
    "Certificates": {
      "Default": { "Path": "C:\\Raffaello\\Server\\raffaello.pfx", "Password": "<from IT>" }
    }
  }
}
```

Open the firewall port (5443), restart the service (`sc stop RaffaelloServer` / `sc start RaffaelloServer`) and set the
client's server address to `https://raffaello.mobco.local:5443` in Settings > Data source. The certificate must be trusted by
the client PCs (domain CA certificates are). Keep the password out of source control: on the server it can also be given as
the environment variable `Kestrel__Certificates__Default__Password`.

## Module stores with server support (phase 6)

`MaterialsServerModule` (phase-3 tables + `DnLineLockGuard`: one invoice per DN line, 409 `locked`), `AconexServerModule` and
`VariationsServerModule` are listed in `ServerModules.All`. The client uses `RemoteMaterialsStore`, `RemoteAconexStore` and
`RemoteVariationStore` (`src/Raffaello.Core/Remote/RemoteModuleStores.cs`) when `DataSourceFactory.Current` is set; the app picks
the implementation per call (`MaterialsStoreSelector`). `ModuleEntities.RegisterAll()` makes the migration and the offline
cache include these tables; a data reset clears them on both sides.

### Data reset (Settings > START EMPTY / RESET DEMO, `POST /api/v1/admin/clear-all`)

Clears every project table and every module table, locally (`Db.ClearAll`) and on the server (`PgStore.ClearAll`):
Insight*, Asm* (the default templates are seeded again on next use), Dwg*, Cable*, Doc* (+ the SQLite FTS index `DocSearch`),
Assistant* (conversations, reminders, rules, briefs), Mat*, Aconex*, Variation*, and the portal's submissions and messages.
**Kept** (`ModuleEntities.ResetKept`): signing keys and record signatures (`UserSigningKeys`, `RecordSignatures` - evidence must
outlive the data), the portal company settings, and the server's own tables (users, portal accounts / sessions, audit log - the
audit hash chain continues from its head). Record ids are **not** restarted (SQLite keeps `sqlite_sequence`, PostgreSQL
`TRUNCATE ... CONTINUE IDENTITY`), so a kept signature can never point at a new record with an old id. `ModuleEntities.All`
(migration, offline cache) now also lists the Insights, Assemblies and Trust tables; signing keys already on the server never
block a migration.

<!-- [cables] begin -->
## Cables module

`CablesServerModule` (`CablePanels`, `CablePanelAliases`, `CableRuns`, `CableClaims`, `CableFlagDecisions`, `CableImportProfiles`) is in
`ServerModules.All`; no write guard (cable flags are warnings, computed client side by `CableFlagEngine`). Client: `RemoteCableStore`,
picked per call by `CableStoreSelector`; the types are in `ModuleEntities.All` (migration, offline cache, reset). Run references (C-0001) are assigned
before insert, so batches with temporary ids work.
<!-- [cables] end -->

<!-- [trust] begin -->
## Trust module and subcontractor portal

`TrustServerModule` (tables `UserSigningKeys`, `RecordSignatures`; `TrustGuard`; audit-chain sealer + `AuditChainHead`, anchor file
`<backup folder>/audit-anchors.log`) and `PortalServerModule` (tables `PortalCompanySettings`, `PortalSubmissions`, `PortalMessages`;
`PortalGuard`; own `PortalAccounts` / `PortalSessions`) are in `ServerModules.All`. Their own schema is created at start-up
(idempotent, advisory lock). `PermissionGuard` lets every signed-in role write keys / signatures (TrustGuard decides).

| Route | |
|---|---|
| `GET/POST /api/v1/trust/audit/verify` · `GET /audit/head` · `GET /audit/chain?afterSeq=&take=` · `POST /audit/seal` · `POST /audit/anchor` (ADMIN) | hash chain |
| `GET /api/v1/trust/signatures/verify?table=&id=` | server-side signature check |
| `GET/POST/PUT /api/v1/portal-admin/accounts` | portal accounts (list: EDIT_DATA, create / update: MANAGE_USERS) |
| `/portal` | the portal page (anonymous, strict CSP) |
| `POST /api/v1/portal/login` · `logout` · `GET me` · `GET template` · `GET/POST submissions` · `GET submissions/{id}/files` · `GET files/{id}` · `GET claims` · `GET invoices` · `GET remaining` · `GET/POST messages` · `POST messages/{id}/read` | portal API (portal token only) |

Settings: `Raffaello:Portal` (`Enabled`, `MaxFileBytes`, `MaxSubmissionBytes`, `MaxFilesPerSubmission`, `MaxSubmissionsPerHour`,
`MaxSubmissionsPerDay`, `RequestsPerMinute`, `MessagesPerHour`, `MaxMessageChars`, `TokenHours`). See `docs/TRUST.md`.
<!-- [trust] end -->
