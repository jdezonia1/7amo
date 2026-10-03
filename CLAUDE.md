# RAFFAELLO — Raffles / DSQ MEP work (Mohamed, MOBCO)

Working context for the "Raffaello" claude.ai Project, consolidated from 7 chats (as of 30-Sep-2026).
Most deliverables live on Mohamed's Windows PCs (work PC `C:\Users\DELL\...`, laptop `C:\Users\m6000\...`,
`D:\RAFFLES ELEC\...`). Source code/scripts should be pushed into this repo so cloud sessions can work on them.

User preferences: brief uppercase instructions; iterate by screenshots; palette dark red #8B0000 / grey #A6A6A6 /
black / white / yellow #FFFF00; dislikes grey UI themes; "auto replace" files; timezone Asia/Riyadh.

## Workstreams and open items

### 1. BRANDED residences electrical/ELV QS
Deliverables: `C:\Users\DELL\Desktop\NEW BRANDED QS SURVEY\` (APARTMENTS / PUBLIC AREAS, each PDF + AUTOCAD).
Tracker: `...\NEW BRANDED QS SURVEY\TRACKING SHEET BRANDED\BRANDED_MEP_TRACKER.xlsm`.
Rules: twin socket = 1; twin data = 2 at 2nd fix; TV+soundbar = 1; switches under LIGHT; H>=3000 -> ceiling;
thermostat = CP-4 counted in GRMS only; no C/ms in GRMS; ceiling WAP = flexible; exclude terrace power/light;
public thermostats = BMS; CONTROL->GRMS, EM->EMERGENCY LIGHT, EV->EVACUATION; EMT = rework (no PQ);
cable pulling = site statement; 1 DB + 1 GRMS panel per unit; 5 gas points per unit; Revit only for cable tray;
1BR-A = 1BR-B, 3BR-A = 3BR-C.
Open: DB panel size for non-1BR/2BR (PANEL 48 assumed); B1 isolators (32) in/out; P3/P4 basement boundary
(X=-560,000 assumed); GF public DATA ~1.7x user survey; 2BR switches 19 vs 12; 3BR-A DATA +3; delete old-named files;
confirm ROOMS plan colours refresh in Excel.

### 2. HOTEL enlarged rooms QS (28 types)
Output: OneDrive `0-Enlarged Rooms\QS SURVEY\HOTEL_ENLARGED_ROOMS_QS.xlsx` + 28 markups. 4,401 points
(LIGHT 2,697 / POWER 914 / GRMS 350 / DATA 282 / AV 144 / DISABLED 14; 56 CP-4; 324 switches).
Open: LED strips per tag or per feed; confirm judgement calls (DOUBTS tab); rooms per type; match to PROJECT QTY.

### 3. HOTEL plan subcontractors (REV-03)
`D:\RAFFLES ELEC\WORKS\00-SUBCONTRACTORS PROGRESS PLANS\HOTEL\REV-03\HOTEL_SUBCONTRACTOR_QTY.xlsx`;
17,222 tagged blocks; LISPs QSEXPORT (v6), QSARCH3/4; EXPORT_ROOM_BOUNDARIES.dyn.
Goal: REMAINING = TOTAL QS - GIVEN per room/system/stage.
Open: run the .dyn in Revit 2024 on work PC (QSARCH4ALL fallback); point-in-polygon rooms; ~7,900 off-plan blocks
(only L2 lighting shift known: -566500, +499000); unknown owners PR-/QS-/FOH-CORRIDOR-INVOICE; MARUF INV-02/04;
BANDER SEIF INV-03 tabs are copies of INV-02; L2 room numbers; B1 orientation.

### 4. BRANDED_MEP_TRACKER.xlsm + statement_reader.py (v19)
LEDGER-based tracker; compare after SITE %; WIR % for invoices; never sum stages; post exceeded lines at full qty.
Open: Excel/COM posting never tested on Windows; Awrad invoice 1 must be redone; PROJECT QTY ~19% filled;
RATE row empty; villas P1-1/P1-2/P1-1V/P1-2V have no plan shape.

### 5. aconex_downloader.py (v3)
Upload Excel of doc numbers -> type filter -> downloads from Document Register (ksa1.aconex.com).
Open: no real run yet (test 3-4 of the 389 WIRs first); shop-drawing code guessed (SDW/SD/SHD);
Zip fallback untested; EL-Electrical pinned filter was removed from the user's register view.

### 6. Project Manager WPF app (.NET 8), latest v1.5.0 = ProjectManager_UPGRADED_27.zip
Open: build on Windows via RUN_ONE_CLICK.bat (send build_log.txt); confirm 1.4.8 accent-button fix;
test BOQ Analyzer on Raffles BOQ R4 PDF (stated-total diff ~0, no "Page 1" bill); logo verdict (column-P);
switch Excel exports / BOQ workbook headers to #A6A6A6 with bold black text; verify AI model names.

### 7. material_tracker.py (v5)
PO (PDF/Excel) + MIR/DN PDFs -> PROGRESS sheet with one column per DN, SUMPRODUCT total row, exceeds-PO alarms,
PCS<->m pipe conversion (6 m default).
Open: test GUI on work PC (1-Oct); install Tesseract (UB Mannheim) or use API key; tune PO reader on one real
signed PO PDF; pipe conversion untested on a real DN.

## Working rules (from Mohamed, 03-Oct-2026) - follow in every session
- ALWAYS commit AND push to GitHub (repo jdezonia1/7amo, branch claude/dazzling-turing-20kq62) automatically after
  every finished task, fix or update - do not wait to be asked. Before each commit: build, run
  `dotnet run --project tools/Raffaello.XamlCheck -- src/Raffaello.App/bin/Debug/net8.0-windows10.0.19041.0 src/Raffaello.App`
  (0 problems) and the Core tests. Never force-push (the patch updater needs the history).
- Updates reach Mohamed as PATCHES (tools/update.ps1, SETTINGS > UPDATES > UPDATE NOW), never as a zip to download.
  Line 1 of every commit message is what he sees in the update list - write it in plain words.
- His single folder on the work PC: C:\Users\DELL\RAFFAELLO (app installed by the updater in code\, chat in CHAT\,
  README.txt). Real data findings and his decisions: docs/DATA_FINDINGS.md.
