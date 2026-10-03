# Real data findings (03-Oct-2026, work PC, read-only analysis)

Files live in `Desktop\RAFFLES MASTER FOLDER` (not in the repo). Trackers are not final - everything must stay re-importable.

## Trackers (run through the app's TrackerImporter)
- HOTEL_MEP_TRACKER.xlsm: 2,226 rooms, 21,959 PROJECT QTY cells (63 stage|item keys), 6,109 ledger lines, 19 subcontractors,
  38 invoices, 5 plans, 923 shapes. Plan shapes matched only 276/923 (names cut at first space) -> fixed (ResolveRoom): 923/923.
  Only 406 of 1,914 rooms with PROJECT QTY have a traced shape (tracing incomplete). EILAF has ledger lines with invoice no 0.
- BRANDED_MEP_TRACKER.xlsm: 119 rooms, 3,684 cells, 4,045 ledger lines (ROOTS 1-4, ABRAG 1-2, MOST2BL 1, ELAF 1, AWRAD 1,
  CONCRETE PLUS 4/6/7/8/9 = 2,748 movement lines). 102 warnings: non-room locations (STAIR, CORRIDOR, EMCC/SMDB panels,
  REWORK, HOTEL, ALL), 9 lines with empty ITEM.

## Owner BOQ - Modified BOQ rev 0.xlsx  (current BoqImporter CANNOT read it)
- Print-style bills 01-08, 10, 11: 4-line title block per page, header row "Ref | Description | Quantity | Unit | Rate | Total"
  repeated, section banners "SECTION R - ELECTRICAL INSTALLATIONS", page footers "B6.R / Page 1", "To Collection" subtotals.
- Two column blocks: A-F original (description wrapped over many rows, Ref on the last line) and H-M merged (full description
  in column I, header blank). Bill 03 has only the merged block (B-G) with NO "Description" header -> importer skips it.
- No BOQ codes in the workbook: key = bill + section + page + Ref. ERP (E-Promise) codes are B6-01-01-00-6-26-AM-3 =
  {bill}-{CSI division}-{Ref}-{page}; section R = division 26. Trial match vs E-Promise: B6 R 417/419, B3 R 597/603;
  B2 / B5 page numbers off by one. Section R: B2 389 items 31.5M, B3 649 / 55.0M, B5 207 / 11.7M, B6 419 / 41.5M.
- Gas meters are MECHANICAL (B6-..-6-22-R-18, B5 Q p10 P, B3 Q p43 E); kWh metering is electrical (B6-..-6-26-AM-3).
- Importer fixes needed: prefer merged block, blank description header, footer/section tracking, code key, "Rate Only" text
  in Total = rate-only (amount 0), unit normalisation, ignore repeated headers.

## Owner IPC + MOS - DD-2022-370 MOBCO - IPC 24 - Tax Invoice.xlsx
- 48 sheets. START HERE (per-IPC fields), 01.IPC (cumulative / previous / interim; advance recovery -10% of works,
  retention -10% of works + VOs, MOS not retained, VAT 15% once), 4. Summary (contractor vs PMC blocks), 7. App B BOQ bills,
  11. App F - MOS On (rows 13-2183, Mechanical then Electrical ~880 rows).
- App F columns: Item, BOQ item, description, material sub-item, contract qty, unit, BOQ rate, delivered prev / month / to date,
  used at site, balance, MARKET rate, 75% of market rate, amount (capped at 0.75 x BOQ rate x contract qty), MIR ref, PO ref.
- App differs: values MOS at owner BOQ rate x 75%, own 13-column layout with VAT (would double-count). No owner IPC model.
- BOQ code forms in App F are mixed (2-23-Z-2, 03-23-547, B5-01-01-00-6-17-A-12, New-...): need normalisation.

## Variations / EIs - VARIATIONS folder
- EI-04 GRMS (+295,291.83), EI-05 generator (876,941.89), EI-06 kitchen (REV-03 summary has #REF!), EI-07 mock-up
  (11,652,921.74), EI-09 restaurant, EI-13 security (-1,045,440), PUMPS (+192,606.62), ACTIVE COMPONENT (V5.1/V5.2),
  DECORATIVE LIGHT (not priced yet). GRMS final proposal subject row says "EI NO-05-GENERATOR" (copy-paste error).
- MOBCO "Commercial Proposal" format: header (project, contract DD-2022-370, client, consultant Mirage, EI + Aconex letter
  ref L0xxx), OMITTED / ADDITIONAL blocks grouped by Bill / section, columns Ref | Item | Qty | Unit | Rate | Total,
  variance, markups 5% / 8% / 9%, VAT 15%; supporting BOQ extract, vendor comparison, discipline working sheets.
- App gaps: header fields, bill/section/page per line, markups, quotations + comparison, revisions, non-PDF documents,
  export layout, time impact (EOT).

## CONCRETE PLUS
- Per-invoice split ALREADY done in BRANDED\TRACKING SHEET BRANDED\CONCRETE_PLUS_CHECK_ALL_INVOICES.xlsx and loaded into the
  Branded LEDGER (INV4 73, INV6 807, INV7 384, INV8 316, INV9 1,168 lines). INV 1, 2, 5 missing. Executed cum:
  INV3 1,024,331 (certified) / INV4 744,802 / INV6 1,271,413 / INV7 1,752,225 / INV8 1,935,188 / INV9 1,907,091 SAR.
- Problems: cumulative decreases (272 negative ledger lines), carry-forward breaks, #VALUE! in INV9 B5 row 158, filled-down
  BOQ codes, INV8 PC-007 approved file ~229k above progress file, over-cap claims (emergency 881 vs 104, terrace light),
  ledger lines carry RATE 0.
- Code evidence: AV -> S-9 (AV/BGM points), EVAC -> C-9 (VES to equipment); GRMS W-7 vs AB-7 still ambiguous.

## Decisions (Mohamed, 03-Oct) and what was built
- IPC: the app produces App F (MOS) only. MOS = 75% of the supplier / PO rate x balance, capped at 75% x BOQ rate x
  contract qty. Built: MosLine App F fields, App F Excel export (no VAT), OWNER MOS screen columns.
- Codes: the E-Promise-style code is the master key and is called "project code" in the app. "Modified BOQ rev 0" is the
  contract BOQ. Built: ContractBoqReader (print layout) + ProjectCodeMatcher (page map learned per division).
- Variations: fixed markups (5 / 8 / 9 %, compounding, editable per variation), omissions negative, no EOT (planning does it).
  Built: proposal header fields + markups, PROPOSAL sheet (OMITTED / ADDITIONAL by bill + section, variance, markups, VAT).
- CONCRETE PLUS: only the existing invoices (1, 2, 5 had no electrical).
- Still open: GRMS W-7 vs AB-7; INV8 official figure (PC-007 approved vs progress file); over-cap CONCRETE PLUS claims;
  -400,000 / -650,000 manual adjustments in IPC 4. Summary; gas meters electrical or mechanical subcontractor.
