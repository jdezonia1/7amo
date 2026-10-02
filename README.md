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
```

Keep `--db` and `--out` outside the repository when running against real company files.

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
Ctrl+E export current view, Ctrl+Shift+L light/dark, F5 reload, Esc close panels.

## Building on Linux (CI / cloud sessions)

WPF compiles on Linux with `EnableWindowsTargeting`, but only with Microsoft's SDK build, which ships
`Sdks/Microsoft.NET.Sdk.WindowsDesktop`. Distro-built SDKs (e.g. Ubuntu's `dotnet-sdk-8.0`) do not; copy that
folder from Microsoft's `dotnet-sdk-8.0` package (packages.microsoft.com) into `<dotnet>/sdk/<version>/Sdks/`.
The app itself only runs on Windows.
