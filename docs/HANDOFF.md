# STATUS 02-Oct-2026 night (branch claude/dazzling-turing-20kq62) - READ THIS FIRST

- Windows: RUN_ONE_CLICK.bat -> BUILD OK, all tests pass (Core 443, Server 9 + 42 skipped without PostgreSQL, Automation 9, OCR 9).
- Fixed on Windows: SQLite temp-file lock in tests, server SafeName drive-letter bug, duplicate x:Key "Num" (start-up crash),
  command palette stuck open (DataContext + Palette.IsOpen binding).
- The app now OPENS on Mohamed's laptop. Next job: open every page, fix what breaks (layout, crashes in commands, empty data).
- Diagnostics: %APPDATA%\Raffaello\startup.log, error.log, binding_errors.log; RUN_DIAG.bat collects them into diag_log.txt.
- Fast loop on Windows: UPDATE_AND_RUN.bat (downloads the branch zip, incremental Debug build, starts the app). A local session
  on the PC can instead build directly: dotnet build src\Raffaello.App -c Debug, run bin\Debug\net8.0-windows10.0.19041.0\Raffaello.exe.
- Before pushing: tools/check_app.sh (cloud) or `dotnet run --project tools/Raffaello.XamlCheck -- <app bin> src/Raffaello.App`
  - every {Binding} path on 47 screens checked, currently 0 problems.
- Paused (not pushed, redo from section 2b): B5/B6 bill rule + gas-meter vs BMS link-table check.

# Handoff — continue Raffaello on Mohamed's PC (02-Oct-2026)

Branch: `claude/raffaello-artifact-design-nm5ade` — everything merged; build 0 errors / 0 warnings;
tests: Core 443, Server 51 (PostgreSQL), Automation 9, OCR 9 — all passing on Linux.
Read first: `docs/REQUIREMENTS.md` (all of Mohamed's rules and answers), `docs/ROADMAP.md`, `README.md`,
`docs/SERVER.md`, `docs/TRUST.md`, `docs/CAD_EXCHANGE.md`.

## 1. First run on Windows (nothing has ever been opened on Windows)
- Install .NET 8 SDK (and PostgreSQL 16 only for server tests). Run `RUN_ONE_CLICK.bat` → send/inspect `build_log.txt`.
- Open every page; fix XAML / binding errors. Checklist in README ("What to verify on Windows").
- First OCR run (Paddle native DLLs next to Raffaello.exe), Windows OCR (Arabic language pack), PDFium, DPAPI,
  Windows toasts, review windows, Plan / Drawings canvas (zoom, pan, box drawing, calibration).

## 2. Cross-module wiring - DONE (02-Oct-2026, adapters in `src/Raffaello.Core/Wiring`)
- Assistant: `list_anomalies` -> Insights engine (`InsightsAnomalies`: severity, explanation, suggested action, evidence citations;
  built-in checks only when Insights fails); `search_documents` -> Documents FTS archive (`ArchiveDocumentSearch`, SQLite FTS5 or
  server full text, page + linked-record citations); `get_contract_terms` + morning brief "obligations due" -> contract-rules
  obligations calendar (`ContractObligations`; `NotificationHub.Obligations`, server `StoreBriefData` queue); pages publish
  `SelectionService.SelectedRecord` (Ledger line / room, Invoice, PO / DN, Variation, Plan room, Cable run, Drawing takeoff).
- Assemblies `route_len` <- Drawings (`DrawingRouteLengths`: measured route m / counted points per system and room type, or exact
  cable lengths when computed; template default otherwise; the source is shown in the breakdown, Excel and PDF).
  Insights reconciliation <- `BulkRequirements.Reconcile` (`AssemblyConsumption`; replaces the DEFAULT norms, user norms win).
- Cables: CABLES > MEASURED FROM DRAWINGS proposes `MeasuredLength` from traced routes whose ends sit next to panel names (or whose
  circuit text names FROM / TO); the user ticks + applies (audited). Scanned SLDs: the app registers `IPageRasterizer` (PDFium)
  and `ILayoutOcrEngine` (`FirstAvailableLayoutOcr`: PaddleOCR, then Windows OCR).
- Reset data clears every module table locally and on the server; signing keys / signatures, portal settings + accounts and the
  audit chain are kept; ids are not restarted (docs/SERVER.md "Data reset"). `ModuleEntities.All` covers Insights, Assemblies, Trust.
- Statement preview: WARNINGS panel with cable flags + contract-rule warnings, BYPASS WITH REASON / BYPASS ALL OPEN (same records
  and audit as the Cables page / invoice WARNINGS tab, applies to the claims once posted).
- Still to check on Windows: the new CABLES tab, the statement WARNINGS panel, the Assemblies "route length" line; labels of
  scanned drawings are not OCR-read for measured lengths (vector PDF words / CAD texts only - type the circuit text on scans).

## 2b. To build next (answers from Mohamed, 02-Oct)
- **Bills (Branded owner BOQ): B5 = BASEMENT, B6 = APARTMENTS.** BOQ-code mapping must pick the bill from the claim's
  location: basement levels (Basement 1 / B1 / LB1 / LB2, plot basements like P2-BS1) → B5 codes; apartments and other
  above-ground residence areas → B6. Unknown location → flag "bill not determined", never guess. Configurable rule table
  (building → level/area pattern → bill). Hotel bills B2/B3 unchanged until explained. Re-check CONCRETE PLUS B5 citations.
- **Gas meter items are SEPARATE from BMS.** The contract link table wrongly links Metering-section items to
  B6-01-01-00-6-22-X-18 ("BMS system"): exclude it for gas/metering, propose the right gas-meter codes from E-Promise for
  confirmation, and add a "link-table check" warning for contract items linked to a BOQ code of a different system.

## 3. Real-system checks
- Aconex browser automation: edit `%APPDATA%\Raffaello\aconex.config.json` selectors; test 1 workflow + 3–4 WIRs.
- Server: `SETUP_SERVER.bat` on the always-on PC from IT; Windows sign-in; migrate local data (dry run first).
- AutoCAD / Revit add-ins in `tools/cad` (compiled only against stubs).
- Claude: set the API key in Settings; test the assistant and cloud reading of handwritten statements.

## 4. Open questions for Mohamed
- CONCRETE PLUS cited codes (AV S-9 vs U-11, EVAC C-9 vs D-9, GRMS W-7 vs AB-7, lighting areas).
- 2nd-fix GRMS for ROOTS? Hotel DB panel BOQ code?
- Cable stages 20 % / 10 % conversion rule when the invoice row is at another %.
- BOQ breakdown defaults (route lengths, drops, waste, 18 SAR/man-hour, OH/profit 10 %) and material norms (Insights).
- Owner BOQ with rates, MOS %, owner invoice template; CONCRETE PLUS invoice files 1–9 (cumulative split).
- Clean drawing set (vector PDF / DWG) for takeoff — scanned statements alone are not enough for symbol counts.

Company sample files are NOT in the repo (by design). Keep them on the PC and give them to the local session when needed.
