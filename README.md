# Raffaello

MOBCO's desktop app for the Raffles Hotel + Branded Residences MEP (electrical / ELV) project, Riyadh.
**Every quantity. One chain.**

Each line item (room x system x stage x BOQ item) carries one chain of quantities:

```
QS / PROJECT QTY  ->  GIVEN (subcontractor)  ->  DONE (approved WIR)  ->  CLAIMED (statement)  ->  DELIVERED (DN)
```

Every problem is a mismatch somewhere in that chain. The rules engine turns mismatches into
verdicts (OVER / CHECK / DUE / OPEN / OK) and into the "Needs you today" queue.

## Run it

On Windows with the .NET 8 SDK: double-click `RUN_ONE_CLICK.bat`. It restores, builds, runs the tests,
publishes a self-contained single-file `publish\Raffaello.exe` and launches it. Everything is logged to
`build_log.txt`.

Manual:

```
dotnet build Raffaello.sln -c Release
dotnet test tests/Raffaello.Core.Tests -c Release
dotnet run --project src/Raffaello.App -c Release
```

On first run the app creates `%LOCALAPPDATA%\Raffaello\raffaello.db` and seeds a realistic demo project
(turn off in Settings, or use START EMPTY). Point Settings > Shared data file at a file on the shared drive
so the whole team works on the same data. Per-user settings live in `%APPDATA%\Raffaello\settings.json`;
errors are logged to `%APPDATA%\Raffaello\error.log`.

## Layout

```
Raffaello.sln
src/Raffaello.Core          net8.0, no WPF: domain, store, rules, chain, analytics, import/export, AI client, seeder
  Domain/                   entities (Entity: Id, UpdatedBy, UpdatedAt, RowVersion) and constants
  Data/                     IProjectStore + IStoreBatch (the persistence boundary), Db (SQLite implementation), ProjectSnapshot
  Rules/                    IChainRule rules (one class per house rule) + RulesEngine, house-rule helpers, invoice copy detector
  Chain/                    ChainRow, ChainBuilder, FilterSpec, ClaimBuilder (payment certificate)
  Analytics/                S-curve, regression forecast, heatmap, ageing, cash flow, materials, statements
  Queue/                    NeedsTodayQueue
  Import/                   TableReader (xlsx/csv) + importers with preview/validation
  Export/                   ExcelExporter (house format) + ReportBuilder (weekly report, PO sheets, chain sheets)
  Ai/                       AnthropicClient (HttpClient, SSE streaming) + AskContextBuilder
  Aconex/                   ExternalScriptRunner (runs aconex_downloader.py, streams the log)
  Seed/                     DemoSeeder
  Tracker/                  TrackerImporter (BRANDED_MEP_TRACKER v19: ROOMS, PROJECT QTY, LEDGER, PLANS + room shapes), AreaTypes
  Contracts/                ContractAttributeParser (EN + AR), ContractLinkImporter, EPromiseImporter, InvoiceTemplateImporter
  Ledger/                   LedgerRules (remaining per room x stage x item), HeightCheck (>4.5 m), LengthCheck (15 m), Invoiceable
  Mapping/                  MappingEngine + IItemResolver / IBoqResolver (pluggable), learned MappingRules, explanations
  Invoicing/                InvoiceBuilder (prev = last approved), InvoiceWorkflow, InvoiceExcelExporter, InvoicePdfExporter (QuestPDF)
  Statements/               SiteStatementService (generate / read back / duplicate detection)
  Workflow/                 WorkflowService - audited operations the screens call
  ProjectService.cs         the only entry point the UI uses
src/Raffaello.App           net8.0-windows WPF, MVVM (CommunityToolkit.Mvvm), DI (Microsoft.Extensions.Hosting)
  Themes/                   Tokens.xaml, Light.xaml, Dark.xaml (generated from design-system tokens), Icons.xaml, Controls.xaml
  Assets/                   raffaello-link.svg / .png / .ico ("The Link" logo), LogoDrawing.xaml, Fonts/
  Controls/                 LogoMark, IconView, ChainPanel, converters
  Services/                 ThemeService, FilterState, DataService, Toasts, Dialogs, Export, Presence, ChartKit
  ViewModels/ Views/        one per module + shell, Ask panel, command palette, import wizard
tests/Raffaello.Core.Tests  xUnit (synthetic fixtures generated in code - no company files in the repo)
tools/Raffaello.Cli         raffaello-cli: the same importers / mapping / invoice builder from the command line
```

## Workflow (Phase 1)

1. **Contracts & BOQ**: import the contract link workbook (enter contract no / subcontractor / building - the file
   does not carry them), the invoice template (INV sheet) and the E-Promise BOQ list. Item attributes (stage,
   conduit, wall/ceiling, height band, systems, size) are parsed from the English / Arabic text; correct them in
   the grid and SAVE + CONFIRM.
2. **Rooms & Ledger**: IMPORT TRACKER, then post claims per room x stage x item. Remaining = PROJECT QTY - all
   subcontractors; claims above remaining are blocked unless an OVER reason is given. Area type per room selects
   the lighting BOQ row. `>4.5 M QTY` and `15 M CLAIMED QTY` open a HEIGHT / LENGTH check.
3. **Checks**: decide pending HEIGHT (accept / partly / reject) and LENGTH (accept / revise from groups or total
   route length / reject) checks. Pending lines are never invoiced.
4. **Invoices**: BUILD FROM LEDGER (claims up to the invoice no, previous = last approved invoice), confirm or
   re-map the AMBIGUOUS / GUESS / UNMAPPED groups (each choice is learned as a rule), SAVE DRAFT, SUBMIT with the
   Aconex workflow no, REJECT / NEW REVISION, APPROVE (locks). Export Excel in the template layout (+ QTY BACKUP)
   or a filtered PDF.
5. **Site statements**: generate a protected statement workbook for a subcontractor, read it back (duplicates
   by statement no / file hash are refused, over-remaining lines need a reason).

```
raffaello-cli import-tracker  <tracker.xlsm>  [--db file]
raffaello-cli import-contract <links.xlsx> --contract NO --sub NAME [--building BRANDED|HOTEL]
raffaello-cli import-epromise <invoice.xlsx>
raffaello-cli import-template <invoice.xlsx> --contract NO
raffaello-cli map             --contract NO --sub NAME [--invoice N]
raffaello-cli build-invoice   --contract NO --sub NAME --invoice N [--save]
raffaello-cli approve         --contract NO --sub NAME --invoice N [--aconex WF]
raffaello-cli export          --contract NO --sub NAME --invoice N --out FOLDER
raffaello-cli statement       --sub NAME --no ST-01 --out FILE.xlsx
raffaello-cli report
raffaello-cli analyze-sub     --sub NAME --contract NO            (totals, shared / over-cap keys, BOQ codes cited in notes vs mapping)
raffaello-cli split           --contract NO --sub NAME FILE... [--commit]   (past invoice files -> split a cumulative block)
raffaello-cli tracker-export  --out FILE.xlsx [--contract NO --sub NAME --invoice N] [--all]
raffaello-cli package         --contract NO --sub NAME --invoice N --out FOLDER [--final] [--wir-folder DIR]
mapping options: --data-mount WALL|CEILING  --grms-mount WALL|CEILING   (default WALL, confirmed)
```

Keep `--db` and `--out` outside the repository when running against real company files.

### Rules confirmed by Mohamed (Phase 1 follow-up)

- Site % applies to DB panels like every other item.
- DATA / GRMS 1ST FIX = the WALL outlet items (183 / 200). One setting (`Data1stFixMount`, `Grms1stFixMount`) if that ever changes.
- Lighting in apartments = "to apartment".
- GAS in the ledger = gas meter points -> the contract's Metering items (1st fix PVC wall / EMT ...); an item whose text says "gas" wins.
- DATA RACK in the ledger = extra data points for long routes (15 m rule): read as a LENGTH claim on DATA 2ND FIX with plan qty 0,
  so it never counts against PROJECT QTY; tracker lines already invoiced come in ACCEPTED; same BOQ row as DATA 2nd fix.
- A CUMULATIVE invoice (notes "INV-9 (cumulative)") replaces that subcontractor's earlier lines for the same room / stage / item.
  Until his INV 1..N files are imported the block counts once as INV N ("INV 1-N CUMULATIVE", banner "awaiting invoice files to split").
  Invoices page > PAST INVOICE FILES reads the files (template layout, O prev / P curr / Q cum), takes current(n) = cum(n) - cum(n-1)
  per item x BOQ row and splits every ledger line by those shares (largest remainder, whole points stay whole; every line and every
  invoice total reconciles exactly); the files are stored as approved invoices so "previous" is right for the next one.

## Phase 2 - plan view, head-office tracker, invoice package

- **PLAN VIEW** (TRACK): level tabs, the plan image with every room shape from the tracker PLANS drawing; wheel = zoom, drag = pan,
  FIT; colour by STATUS (over cap / in progress / done / not started / no project qty), SUBCONTRACTOR (main sub per room),
  % USED (white -> dark red) or PENDING CHECKS; filters stage / item / subcontractor / up to invoice; hover tooltip; click a room
  for its stage x item balance, ledger lines and checks, with ADD CLAIM / OPEN IN LEDGER; labels on/off; rooms without a shape
  listed; EXPORT PNG.
- **HEAD-OFFICE TRACKER** (Invoices page, also inside the package): macro-free .xlsx, values only, every sheet protected
  (select / filter / sort allowed), workbook structure protected (password in Settings). Sheets DASHBOARD, PLANS (each level image
  + one DrawingML shape per room named `RM_<room>-<level>`, filled with the status colour, hyperlinked to the room block),
  ROOM DETAILS (per room: stage x item project / all subs / this invoice / remaining / % used / status, "back to plan" link),
  LEDGER (incl. height and length check columns), PROJECT QTY, CONTROL (over cap, unknown location, item not in stage,
  site % missing, pending checks), INVOICE SUMMARY. Cells via ClosedXML, drawing via DocumentFormat.OpenXml.
- **INVOICE PACKAGE** (Invoices page, DRAFT / FINAL): `<CONTRACT>_<SUB>_INV-<n>_Rev<r>.zip` with 00_INDEX.pdf (cover, totals,
  contents with SHA-256, missing WIRs), 01_Invoice.xlsx, 02_Invoice_filtered.pdf, 03_Signed_invoice (FINAL only - attach it first),
  04_Contract/ (Contracts page attachments), 05_WIRs/ (found by WIR no. in the WIR folder, Settings), 06_Site_statements/
  (attached scans), 07_Tracker_<SUB>_INV-<n>.xlsx, 08_Checks.pdf. Same inputs give the same bytes (fixed dates, entry order and
  timestamps); file and SHA-256 are recorded on the invoice revision. Needs-today flags a missing signed invoice and missing WIRs.

<!-- [phase6] begin -->
## User guide (every screen)

**Start.** The first start opens a wizard: your name; the data source (a data file - put it on the shared drive - or the
office server); the shared **documents folder** (WIR, MIR, packages, variation documents, Aconex screenshots get
sub-folders under it; Settings > Shared documents folder); optional first imports (tracker, contract link workbook);
the Aconex configuration; DEMO MODE (a sample project for training - Settings > RESET DEMO / START EMPTY switch later).
**BUILDING** at the top (BRANDED / HOTEL / ALL) drives the ledger, plans, checks, site statements, contracts, invoices
and reports. F1 lists every shortcut; Ctrl+K jumps anywhere.

| Screen | What it is for | Main buttons |
|---|---|---|
| DASHBOARD | "Needs you today" from every module: over-cap keys, pending height / length checks, cumulative invoice waiting for its files, invoices rejected / in Aconex / missing the signed copy or WIRs, Aconex overdue steps, DNs without MIR, PO lines over PO + tolerance, PO tolerance conflicts, VOs ageing | click an item to go there |
| ROOMS & LEDGER | rooms of the building, balance per room x stage x item (PROJECT QTY - all subcontractors), append-only claim lines, plan with coloured rooms | IMPORT TRACKER, IMPORT ROOM LIST (any building, columns by header), POST CLAIM LINE (WIR NO, >4.5 M QTY, 15 M CLAIMED), REVERSE, SAVE ROOM (area type) |
| PLAN VIEW | every level plan with the room shapes; colour by status / subcontractor / % used / pending checks; filters stage, item, subcontractor, invoice | wheel = zoom, drag = pan, FIT, labels, EXPORT PNG, ADD CLAIM / OPEN IN LEDGER |
| CHECKS | HEIGHT (>4.5 m: accept / partly / reject) and LENGTH (15 m: groups like 10x20;5x35, total route length, override; accept / revise / reject). Pending lines are never invoiced | per-line decision |
| QUANTITIES / STATEMENTS / MATERIALS OVERVIEW | the original chain views (QS -> GIVEN -> DONE -> CLAIMED -> DELIVERED) | filters |
| MATERIALS | PURCHASE ORDERS (read PDF / Excel, PO total and tolerance checks, auto-coding of BOQ / cost code / budget resource with confidence), DELIVERY NOTES, MIR (DNs, certificates, drum labels), 3-WAY MATCH (only exceptions need you), SUPPLIER INVOICES (from DN lines; a DN line can be invoiced once), DN LOOKUP, SETTINGS | REVIEW window: page image left, extracted lines right, failed checks highlighted |
| OWNER MOS / BOQ | materials on site to the owner (BOQ rate x MOS %, release when installed); owner BOQ categorised by system | build, export Excel / PDF |
| ACONEX | status board, workflow lookup (screenshots attached to the invoice revision), document downloads, script runner, setup. An APPROVED / REJECTED workflow asks whether to record it on the linked invoice revision (approve, or reject + prepare the next revision) - never silently | LOOK UP, REFRESH ALL, LINK TO INVOICE, DOWNLOAD |
| CONTRACTS & BOQ | import the contract link workbook (contract no. / subcontractor / building typed in), the invoice template (INV sheet), the E-Promise BOQ list; item attributes editable; contract documents (go into packages) | SAVE + CONFIRM ATTRIBUTES, ATTACH |
| SITE STATEMENTS | issue a protected statement workbook per subcontractor (WIR NO column included), read it back (duplicates refused, over-remaining needs a reason), attach the signed scan | GENERATE, OPEN FILLED STATEMENT, POST TO LEDGER, ATTACH SCAN |
| VARIATIONS / EI | register, documents, BOQ suggestions, omission / addition / new item lines, submission Excel / PDF | see phase 4 section |
| INVOICES | list by kind (SUBCONTRACTOR / SUPPLIER / OWNER_MOS); build a subcontractor invoice from the ledger (previous = last approved), confirm / learn mappings, revisions, submit (Aconex no.), reject, approve + lock, diff; past invoice files split a cumulative block | BUILD FROM LEDGER (subcontractor kind only), PAST INVOICE FILES, SAVE DRAFT, NEW REVISION, ATTACH SIGNED INVOICE, DRAFT / FINAL PACKAGE, EXPORT HEAD-OFFICE TRACKER, EXCEL / PDF |
| REPORTS | weekly progress, subcontractor scorecards (incl. >4.5 m and 15 m rejected, over-cap keys), cash flow (claimed vs certified), materials status (PO delivered %, DNs without MIR, MIR status, exceptions), VO register, invoice status board - Excel (#A6A6A6 bold headers) and PDF | EXCEL, PDF |
| SETTINGS | data source (local file / server, migrate local -> server), invoice header, package / WIR folders, documents root, tracker password, Aconex config, AI key | SAVE |

## Daily workflow (Mohamed)

1. **Statement** - SITE STATEMENTS > GENERATE for the subcontractor; he fills qty, SITE %, WIR %, WIR NO, >4.5 m and 15 m
   columns; OPEN FILLED STATEMENT > preview > POST TO LEDGER (lines above the remaining need a reason; duplicates refused).
2. **Ledger** - ROOMS & LEDGER: check balances and the plan; single corrections with POST CLAIM LINE / REVERSE.
3. **Checks** - CHECKS: decide the >4.5 m and 15 m claims (pending ones stay out of the invoice).
4. **Invoice** - INVOICES: BUILD FROM LEDGER; confirm the NEEDS CONFIRMATION groups (learned for next time); SAVE DRAFT.
5. **Package** - ATTACH SIGNED INVOICE, then FINAL PACKAGE (index with SHA-256, Excel, PDF, signed scan, contract, WIRs found
   by number in the WIR folder or the Aconex downloads, statements, head-office tracker, checks report).
6. **Aconex** - SUBMIT with the workflow no.; ACONEX > REFRESH ALL daily; when the workflow is approved / rejected Raffaello
   asks to approve (locks, becomes "previous") or to reject and prepare the next revision.

## What to verify on Windows (cannot be run in the Linux build)

- [ ] RUN_ONE_CLICK.bat completes; build_log.txt ends with BUILD OK; `publish\Raffaello.exe` starts; `publish\.playwright\node` exists.
- [ ] First-run wizard: local file on the shared drive; documents folder created; tracker + contract imports from the wizard.
- [ ] Every page opens without a XAML error (none of the WPF screens has been opened on Linux): Ledger, Plan view, Checks,
      Invoices, Site statements, Contracts, Reports, Materials, Owner MOS, BOQ, Aconex, Variations, Settings.
- [ ] Plan view: zoom / pan / fit, room click, PNG export; Rooms & Ledger plan polygons line up with the image.
- [ ] Building switcher BRANDED / HOTEL / ALL changes the room list, plans, checks, contracts and reports.
- [ ] Head-office tracker in Excel: sheets protected, room shapes clickable (link to ROOM DETAILS and back), images sharp
      enough after compression (~1.2 MB for 5 levels).
- [ ] Invoice package: open each file in the ZIP; build twice -> same SHA-256.
- [ ] Windows OCR / PDF rendering in the Materials REVIEW window (scanned DN / MIR).
- [ ] Aconex: real login (persistent profile), workflow lookup selectors in aconex.config.json, approval / rejection prompt.
- [ ] Server: SETUP_SERVER.bat on the office PC, Windows sign-in, Materials / Aconex / Variations in server mode,
      sign-in lockout after 5 wrong passwords, optional HTTPS (docs/SERVER.md), LOCAL -> SERVER migration incl. module tables.
- [ ] Printing (Reports > PRINTABLE SUMMARY) and the shell opening exported files.

## Phase 6 notes

- Server support for the phase-3 / phase-4 stores (`MaterialsServerModule` with the DN-line lock guard, `AconexServerModule`,
  `VariationsServerModule`; client `Remote*Store` chosen per call). Reset and migration include their tables.
- Invoices carry `Kind` (SUBCONTRACTOR / SUPPLIER / OWNER_MOS); packages and the tracker export are kind-aware.
- Plan images in the head-office tracker are recompressed with a small managed PNG codec (`Raffaello.Core/Imaging`) to
  ~1.2 MB for the five levels (was 6 MB); shapes stay aligned (normalised coordinates, same frame).
- Server sign-in rate limit and optional HTTPS: see docs/SERVER.md.
- CLI: `import-rooms FILE --building HOTEL`, `map-sample --contract NO`, `reports --out DIR [--building B]`, plus the earlier commands.
<!-- [phase6] end -->

## House rules (Core/Rules, all unit tested)

| Rule | Where |
|---|---|
| Stages (1ST FIX / 2ND FIX / FINAL FIX) are never summed together | `ChainMath.TotalsByStage`, `SingleStageTotal` throws `StageMixException` |
| Exceeded lines post at full qty with an OVER flag | `PostingRules.Post`, `GivenOverQsRule` |
| CLAIMED > DONE (WIR) = CHECK, hold before certifying | `ClaimOverDoneRule`, `ClaimRules.NeedsHold` |
| EMT is rework and never counts as progress | `ProgressRules.CountsAsProgress` |
| PROJECT QTY caps subcontractor claims (QS while empty) | `ClaimOverCapRule`, `ClaimRules.CertifiableQty` |
| Compare after SITE %, value invoices on WIR % | `SiteVsWirRule`, `ProgressRules.SiteQty`, `ClaimRules.InvoiceQtyFromWirPct` |
| Pipes PCS <-> M (6 m default, per PO line override, Settings) | `UnitConverter` |
| Delivered > PO qty = OVER alarm; PO lines must add up to the stated total | `MaterialRules` |
| Twin socket = 1 point, twin data = 2 points at 2ND FIX | `PointRules.PointsFor` |
| Statement that copies an earlier one line for line | `InvoiceCopyDetector` |

Rules implement `IChainRule` and are registered in `RulesEngine.DefaultRules()`; add, remove or rework them independently.

## Data and multi-user

All data access goes through `IProjectStore` (Core/Data). View models only call `ProjectService`; no SQL or
SQLite types are visible above Core. Today's implementation is `Db`: one SQLite file (WAL, busy timeout),
every table has UpdatedBy / UpdatedAt / RowVersion, updates are optimistic (stale RowVersion ->
`ConcurrencyException`, the user gets a toast and a reload), and every change writes an AuditLog row in the
same transaction. A Presence table gets a heartbeat every 60 s ("who else is editing" on the welcome screen).
A server implementation (API + PostgreSQL + live updates) can replace `Db` behind the same interface.

Excel is import / export only. Exports use a #A6A6A6 header fill with bold black text, number formats, frozen
header row and autofilter.

<!-- [phase5] begin -->
## Multi-user server (Phase 5)

`src/Raffaello.Server` is an ASP.NET Core service with PostgreSQL behind the same `IProjectStore` interface.
In Settings > **Data source** each user picks **Local (this PC / shared file)** or **Server (URL)**; nothing else
in the app changes. Over the server:

- the ledger is append-only and REMAINING is re-checked on the server at save time under a lock on the
  room | stage | item key, so two users can never both claim the last units (the second gets "exceeds remaining";
  with a reason it posts OVER, as on the desktop);
- a stale RowVersion gets 409 with the current row and who changed it; every change is in the AuditLog
  (who / when / PC / old / new); presence shows who is online and on which screen;
- changes are pushed live (SignalR): other people see "updated by X just now - review" and their screens refresh;
- roles: SITE (upload statements, view), QS (enter data, prepare invoices), REVIEWER (check / approve), ADMIN
  (users, templates, settings). Invoices go DRAFT -> CHECKED -> APPROVED -> SUBMITTED; each stamp records who and when
  (Settings > Data source > Invoice approvals). Sign-in is the Windows account on domain PCs, or an app account;
- documents are stored on the shared partition (UNC path); the database keeps their metadata and SHA-256;
- if the server is unreachable the app keeps working on its last copy; changes are queued and sent on reconnect.
  Anything that no longer fits (someone changed the same row, or the remaining quantity is gone) shows up in
  Settings > Data source > **Sync conflicts**: keep mine / keep theirs / merge field by field;
- **Copy local data to server** moves an existing data file to the server (dry run first; ids kept; safe to repeat).

Developer notes (API, plug-in pattern for new stores, tests): `docs/SERVER.md`.

## Server setup for IT

What is needed: **one always-on Windows PC or VM on the office LAN** (Windows 10/11 Pro or Windows Server),
about **4 GB RAM and 100 GB disk**, a fixed IP address or DNS name, reachable from the users' PCs on TCP port 5180.
Users' PCs only need the Raffaello app. Documents stay on the existing shared partition.

1. Install **PostgreSQL 16** for Windows (https://www.postgresql.org/download/windows/). Keep port 5432 and write
   down the password you give the `postgres` user. PostgreSQL only needs to accept connections from the same PC.
2. Copy the `server-publish` folder (made by `RUN_ONE_CLICK.bat`) and `SETUP_SERVER.bat` to the server PC, side by
   side. (Alternatively copy the whole repository and install the .NET 8 SDK; the script then builds the server.)
3. Right-click `SETUP_SERVER.bat` > **Run as administrator**. It asks for the postgres password, a new password
   for the `raffaello` database user (letters and digits), the shared folder for documents (e.g.
   `\\FILESERVER\RAFFLES\RaffaelloDocs`) and a backup folder (preferably on another disk or share), then:
   installs the program in `C:\RaffaelloServer`, creates the database, creates the first ADMIN account, installs
   the Windows service **Raffaello Server** (automatic start, restarts itself on failure), opens port 5180 in the
   Windows firewall (domain + private networks) and prints the address users must type, e.g. `http://RAFFAELLO-SRV:5180`.
4. If documents are on a network share: services.msc > Raffaello Server > Log On > **This account** = a domain
   service account that can read/write that share (the default LocalSystem account cannot open `\\server\share`).
   For Windows sign-in without passwords the server PC must be joined to the domain.
5. Backups run **every night at 02:00** (PostgreSQL dump, kept 30 days, at least the last 7). Manual:
   `C:\RaffaelloServer\Raffaello.Server.exe backup`; restore: stop the service, then
   `Raffaello.Server.exe restore <file.dump>`, start the service. Include the backup folder and the documents
   share in the normal IT backup. Settings live in `C:\RaffaelloServer\appsettings.Local.json` (not overwritten by updates).
6. Users: everyone signing in with Windows gets the SITE role on first use; the ADMIN raises roles
   (`Raffaello.Server.exe adduser NAME ROLE [PASSWORD]`, `passwd NAME PASSWORD`, `users`).
7. How users connect: Raffaello > Settings > Data source > Server, type the address, TEST CONNECTION,
   USE THIS DATA SOURCE. The first time, an ADMIN runs MIGRATION DRY RUN and COPY LOCAL DATA TO SERVER from the PC
   that has the current data file.

Updating the server: run `RUN_ONE_CLICK.bat` on a build PC, copy the new `server-publish` folder over, run
`SETUP_SERVER.bat` again (it keeps the settings, upgrades the database and restarts the service).
<!-- [phase5] end -->

## Ask Raffaello

Slide-in assistant (Ctrl+Shift+A). It calls the Claude Messages API over HttpClient with streaming, model
`claude-opus-5-5` by default (Settings), adaptive thinking, explicit effort (default `medium`) and server-side
refusal fallbacks (`fallbacks: "default"`, can be switched off in Settings). The system prompt carries the house
rules, the filter scope with per-stage totals, the selected line's full chain and today's queue. With no API key
(Settings or `ANTHROPIC_API_KEY`) it answers offline from the rules engine.

## Fonts

Barlow Condensed, IBM Plex Sans, IBM Plex Mono and Cinzel (SIL Open Font License, Google Fonts) are bundled in
`src/Raffaello.App/Assets/Fonts` with fallbacks to Arial Narrow / Segoe UI / Consolas.

## Keyboard

Ctrl+K / Ctrl+F command palette, Ctrl+Shift+A ask, Ctrl+1..9 / Ctrl+0 modules (1 Dashboard, 2 Ledger, 3 Checks, 4 Quantities,
5 Statements, 6 Materials, 7 Contracts, 8 Invoices, 9 Site statements, 0 Reports), Ctrl+H welcome, Ctrl+I import,
Ctrl+E export current view, Ctrl+Shift+L light/dark, F5 reload, Esc close panels, **F1 the full list**.

## Building on Linux (CI / cloud sessions)

WPF compiles on Linux with `EnableWindowsTargeting`, but only with Microsoft's SDK build, which ships
`Sdks/Microsoft.NET.Sdk.WindowsDesktop`. Distro-built SDKs (e.g. Ubuntu's `dotnet-sdk-8.0`) do not; copy that
folder from Microsoft's `dotnet-sdk-8.0` package (packages.microsoft.com) into `<dotnet>/sdk/<version>/Sdks/`.
The app itself only runs on Windows.

<!-- [phase4] begin -->
## Aconex automation and Variations (phase 4)

**ACONEX** is a hub with five tabs: STATUS BOARD (every open invoice revision with its workflow: current step, who
has it, due / overdue, outcome; REFRESH ALL, optional daily auto refresh while the app runs, step-change history,
Excel / PDF for management), WORKFLOWS (look a WF number up, steps table + full-page and table screenshots attached
to the invoice revision), DOWNLOADS (WIR / MIR from the Document Register by number list, date range, group,
discipline or type into the WIR / MIR folders, resumable queue, register with SHA-256, already-downloaded
revisions skipped, ZIP bundles extracted), SCRIPT RUNNER (the old `aconex_downloader.py` route) and SETUP.

- Code: `Raffaello.Core/AconexWeb` (config, models, parsers, stores, services), `Raffaello.Automation`
  (Playwright client, DPAPI vault). Tables live in the same data file (`AconexWorkflowLinks`, `AconexWorkflowChecks`,
  `AconexStepChanges`, `InvoiceAttachments`, `AconexDownloads`, `AconexDownloadJobs`, `AconexQueueItems`).
- Browser: Playwright with a persistent profile in `%LOCALAPPDATA%\Raffaello\aconex-profile`. Log in once in the
  visible window (SSO / 2FA by hand); later runs reuse the session. A password is only stored if you choose to, and
  then encrypted with Windows DPAPI. Default browser channel is `msedge` (no browser download needed on Windows).
- Everything site-specific is in `%APPDATA%\Raffaello\aconex.config.json` (written with defaults on first use):
  `BaseUrl`, `ProjectId`, `Login.*` selectors, `Workflows.SearchPath / WorkflowNoInput / SearchButton / ResultsTable /
  FrameSelector / GroupRowRegex / Columns.*`, `Documents.SearchPath / DocNoInput / DateFromInput / DateToInput /
  DateInputFormat / DisciplineSelect / DocTypeSelect / GroupInput / ResultsTable / DownloadMode / DownloadButton /
  DownloadConfirmButton / RowCheckbox / Columns.*`, `DateFormats`, `Folders.*`. Columns are matched by header text,
  never by position. Defaults are best guesses: check them on the first real run (SETUP > OPEN, edit, RELOAD).
- Tests: `tests/Raffaello.Automation.Tests` runs real Chromium against a local mock Aconex site (login, Search
  Workflows table, paged document register with ZIP bundles). Needs a Playwright browser
  (`PLAYWRIGHT_BROWSERS_PATH`, or set `RAFFAELLO_TEST_BROWSER` to a chromium/edge exe); skipped when none.

**VARIATIONS / EI** (DOCUMENTS): register of VO / EI / SI with consultant ref, status (Draft, Submitted, Under review,
Approved, Rejected, Withdrawn; approved / closed are locked), Aconex workflow no, attached documents (text read with
PdfPig; scans are flagged), SUGGEST related BOQ / contract items (TF-IDF keywords + attribute fingerprint: cable
cores x size, CU / AL, LSOH, fire rated, conduit size / type, amps, ways, watts, IP; optional Claude re-ranking in
Settings), lines OMISSION / ADDITION (contract rate) / NEW ITEM (material + labour + equipment, overhead %, profit %),
totals and ageing, submission Excel / PDF and register export in the house style.
<!-- [phase4] end -->

<!-- [cables] begin -->
## Cables - panel & cable register (TRACK > CABLES)

Mohamed's rule: read panel names and cable FROM -> TO from drawings / SLDs, so a site statement that claims a FROM-TO already claimed is FLAGGED.

- **Register** (`Raffaello.Core/Cables`): panels (normalised name, type, building, zone, level, fed-from parent, aliases) and cable runs
  (FROM -> TO, cores x size, CU/AL, XLPE / LSOH / MICA, companion earth e.g. 1x16 with 4x16, design / measured length, breaker, source doc + page,
  confidence). Name normaliser: spaces / hyphens / case / leading zeros / O-for-0 / token order ('SMDB HT-Z1-LB2- CM 01' = 'SMDB-HT-Z1-LB2-CM-01');
  names without a building token are scoped by building. Similar names (FAN / FANS) are only suggested: confirm (merge + learned alias) or reject.
- **Readers**: cable schedule Excel / CSV (header found anywhere, columns by synonyms, mapping remembered per header layout); SLD PDF (PdfPig vector text +
  lines: boxes, feeder lines joined at junctions, bus bars, size / breaker / length annotations, printed schedule rows); DWG / DXF (ACadSharp 3.8.0, MIT:
  attributes, TEXT / MTEXT, lines / polylines); scanned SLD through the document reader's layout OCR + rasterizer when registered. Review, edit, save.
- **Claims**: tracker 'CABLES BRANDED' / 'CABLES HOTEL' (the subcontractors' cable statements) + ledger CABLE PULLING lines (paired with the sheet
  rows that are the same claim, incl. per-size total lines - nothing counted twice); the site statement has a CABLES sheet (STAGE, SUBCONTRACTOR,
  INVOICE #, LOCATION, LEVEL, FROM, TO, CABLE SIZE, QTY, SITE %, WIR %, WIR NO, NOTES) + a CABLE RUNS list. Claims without a design run create
  PROVISIONAL runs ("from statements only - no design length"); a later SLD / schedule confirms them in place.
- **Flags (warnings, bypass with reason, audited)**: DUPLICATE FROM-TO (same route + size + stage, any sub / invoice, with the earlier claims),
  OVER LENGTH, CUMULATIVE > 100 %, EARTH COMPANION (info), STAGE BEFORE PULLING, UNKNOWN RUN, DIFFERENT SPELLING. Shown in the statement preview,
  ledger entry (cable lines), invoice build warnings, Needs-today, 09_Cable_checks.pdf in the invoice package and the Cables page.
- **Invoicing**: pulling claims are ledger lines (existing mapping: cable item by size -> BOQ code, 70 %); TERMINATION & TEST (20 %) and HANDOVER (10 %)
  are added by the invoice hook to the same item row (row at that stage % if the layout has one, else converted qty x stage % / row %).
- Server: `CablesServerModule`; client `RemoteCableStore` / `CableStoreSelector`. CLI: `cables-import-tracker FILE`, `cables-report [--out DIR]`,
  `cables-read FILE [--save]`, `cables-dump FILE`.
<!-- [cables] end -->
