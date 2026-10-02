# Raffaello — requirements (from Mohamed, 02-Oct-2026)

Elec QS engineer, Raffles project (MOBCO), two buildings: HOTEL and BRANDED.
The app must be multi-user (several people editing at once).

## Workflows
1. **Subcontractor invoices** (biggest pain; many subcontractors across both buildings)
   - Site team sends a site statement: highlighted drawings per system/location/stage.
   - Must check no other subcontractor already claimed the same room/item/stage.
   - Old way (AutoCAD block selection) was slow (exploded blocks) and inaccurate.
   - Target: total qty per location (room names/types/numbers) → enter claimed qty → see total and remaining.
   - Needs: a unified site statement form; plan view with highlighted areas per level showing qty by system,
     stage, WIR %, site %; contract upload (Excel/PDF, OCR) to get rates and descriptions; link quantities to the
     right contract/BOQ item; invoice output into the existing Excel template; PDF printing only the pages that
     have quantities (invoices are 200+ pages); ZIP package to head office (contract, signed invoice, WIRs,
     Excel tracker with plan + highlights driven by VBA).
2. **Material (supplier) invoices**: scanned PO → invoice Excel (same format as subcontractor invoices);
   compare DN vs MIR vs PO for correct qty/item; output PDF + Excel + tracker (each MIR qty with its DNs and
   qty columns); DN lookup: filter by supplier + DN number → which PO / invoice (suppliers have many POs, each
   with its own invoice series).
3. **Owner invoice**: materials on site (MOS) — review MIR + DN against our BOQ item, enter into our BOQ,
   export PDF + Excel.
4. **Variations / EIs**: study consultant documents, find related BOQ items, prepare addition / omission /
   new-item variation for submission; track them.
5. **Aconex**: every invoice is uploaded with its own workflow; management asks daily where each one is.
   Need workflow lookup by number → status + screenshot. Download WIRs/MIRs by date, group, discipline or number.
6. **BOQ**: upload Excel → categorise by system and category.

7. **Auto-coding new PO lines** (added 02-Oct): when a new PO is loaded for invoicing and its lines have no
   BOQ no. / cost code / budget resource, fill them from similar items already invoiced: (1) exact history by
   supplier code or normalised description, (2) attribute fingerprint (cables: cores×size, CU/AL, MICA/fire-rated,
   voltage; conduit size/type; fittings type/wattage), (3) text similarity against past invoiced lines and the
   E-Promise budget list, unit must agree, top-3 suggestions with score. High confidence → auto-filled; medium →
   filled + flagged for review; low → user picks. Every confirmation is learned; each line records where its code
   came from. Reuse the same matcher for variations and new owner BOQ items.

## Findings from the sample files
- **Subcontractor contract (Excel)** `contract_excel.xlsx`: Arabic labour-only rate schedule, one sheet,
  ~323 items in 23 sections (Lighting & Power, Façade, LED strip, Cable tray, Terminations, Cable pulling,
  Earthing, Isolators, FA/PA, BMS, Data/CCTV/TV, GRMS, Disabled, Access, Lighting control, Intercom, Parking,
  Metering, EV, Floor box, MDB/SMDB, DP, Panels). Columns: No, Description, Unit (عدد / م.ط), Qty, Rate,
  Total (formula). Each rate depends on **stage (1st/2nd/3rd fix) + conduit type (PVC/EMT/RS/flexible) +
  wall vs ceiling + height < / > 4.5 m**. 2nd-fix points over 15 m count one extra point per 15 m.
- **Subcontractor contract (PDF)** `contract_1.PDF`: 4-page scan, no text layer. Page 1 = Aconex workflow
  screenshot (WF-008795 Electrical_PR_PO_Approval: step, assignee, date in/due/completed, status, outcome).
  Pages 2–4 = Arabic contract Mobco-Raffles-SUB-ELE-028-2026 with Roots Landscape (شركة روتس لاندسكيب),
  12-May-2026, labour only, prices per attached schedule, excl. 15 % VAT. Payment: 90 % per stage
  (1st/2nd/3rd fix) after consultant acceptance + 10 % at initial handover; cable tray / pulling / panels
  70 % install + 20 % test & termination + 10 % handover. Delay penalty SAR 500/week, cap 10 %. Warranty 1 year.
- **Delivery note** `dn.pdf`: digital (text layer), Riyadh Cables (Saudi Modern Co.). DN 81064344
  15-Aug-2026, ref PO RAF-P.O-E-045-2026, order 40222645, lines: item, material code, description, batch
  (drum) no., qty in **KM**. Customer = MOBCO's legal name.
- **MIR** `...MIR-EL-000169...pdf`: 46 pages. p1–2 MIR form (text), p3–4 MAR, p5 receiving checklist,
  p6–8 **phone photos of the DNs**, p9 required qty list (M), p10–25 test certificates (scans, drum no. + qty,
  some reference a different PO STC-PO-E-099-2026), p26–46 drum-label photos (batch numbers matching the DN).
  One MIR can bundle several DNs and POs.
- **BRANDED_MEP_TRACKER.xlsm (v19)**: the room ledger already exists in Excel — LEDGER (1,297 rows,
  5 subcontractors: ROOTS 879, ABRAG EL NEIR 272, AWRAD ALMUHEET 78, MOST2BL EL AT7AD 35, ELAF 33; 9 invoices;
  117 locations), PROJECT QTY (119 rooms × stage|item keys: CEILING / 1ST FIX / 2ND FIX / EMT / FLEXIBLE ×
  POWER, LIGHT, DALI, GRMS, DATA, AV, BMS, EMERGENCY LIGHT, FIRE, EVACUATION, CCTV, ACCESS, GAS, TERRACE …,
  plus DB panels, cable pulling, cable tray), SUMMARY/CAP (~10k keys), PLANS (5 level images + 138 named room
  shapes `RM_<room>-<level>`), ROOM detail, CONTROL checks, ENTRY form. Very formula-heavy (one sheet is 37 MB
  of XML) → slow; the app should own the data and generate this workbook as an export.

- **Subcontractor invoice** `PC-RIY-RAFFLES_HOTEL-ELEC-SUB-ELE-028-2026-(ROOTS-01)`: sheet `ROOTS INV 1` = 4,282 line
  rows = 314 contract items × owner BOQ codes (579 distinct, e.g. `B6-01-01-00-6-26-V-5` = Bill-Sec-Page-Rev-Item);
  each contract qty is split evenly across its BOQ codes (e.g. 4000 / 19 = 210.53). Columns: BOQ code, cost code,
  budget resource code, description, unit, qty, rate, stage % (0.9 / 0.7 / 0.1), billed prev/curr/cum, executed
  prev/curr/cum, remaining, forecast. Footer: subcontract value, retention, advance, discount, net, VAT 15 %,
  12 signature roles. Sheet `E promise - Resource` = MOBCO ERP budget (job P0876, 30k BOQ lines, bills B1–B11,
  WBS / activity / budget resource code) — effectively the owner BOQ. Sheet `INVOICE 1 QTY` = the manual entry:
  stage, floor, room, POWER / LIGHT / IT / GRMS counts × SITE % × WIR %.
  **Bugs found:** row 4732 `K = SUM(K15:K2612)` leaves out 1,966 lines that carry contract qty (rows 2613–4731),
  so Subcontract Value / Total Contract Amount are understated; executed sums `R/S/T = SUM(…15:…4162)` miss rows
  4163–4731; retention % (T6/U6) is blank and S4736 reads U6 → retention always 0.
- **Contract ↔ BOQ link tables** (`Contract_For_Residence_Subcontractors_-_English_24.6.2026`, `Raffles_Hotel_-_english_copy
  _UPDATED_24.6.2026`): English contract schedule where every contract item is followed by the owner BOQ codes it
  covers. Residence: 314 items → 1,805 code rows (B6 1,169, B5 576, plus 30 B2 and 30 B3 — check), Hotel: 323 items →
  2,540 code rows (B3 1,656, B2 884; 924 with BOQ description). Items 303–310 (+207 residence) have no codes.
  This is the mapping table the app needs; the remaining choice is which BOQ code a claimed qty goes to
  (driven by system + area type, e.g. small power / lighting BOH / FOH / guestrooms / switches).
- **Contract PDF #2** (`Mobco-Raffles-SUB-ELE-028-2026_contract_2.pdf`, 23 pages): scanner-OCR text layer is garbled
  (reversed Arabic, `O28-2O26`, `MPBCO`) → never trust scanner OCR; pages 5–23 are the signed rate schedule, so the app
  should cross-check the Excel schedule against the signed PDF.
- **PO** `po.pdf` RAF-P.O-E-045-2026 (Riyadh Cables, 26-Apr-26): text layer (p1 scan), 11 pages, items in MT,
  re-measurable, total SAR 9,367,585.11 + VAT = 10,772,722.88, payment 100 % before delivery by LC, delay penalty
  2 %/week max 10 %, header says tolerance 0 % but clause 6 allows ±5 % per item (conflict). DN quantities are in KM
  → ×1000. Cable matching key: cores × size + conductor (CU/AL) + MICA (fire-rated) flag; DN codes use HF = LSOH.
- **Site statement** `Roots_site_statment.PDF`: 78 scanned pages. p1 handwritten Arabic/English summary
  (stage, unit type, floors, rooms e.g. P2-106 / P3-103 / P4-04, WIR no., %), then marked-up drawings per unit type
  with counts written on them (2BR: 36 power + 9 IT + 39 lighting = 84 points, 18 GRMS, × 3 units = 252 / 54).
  Confirms the typical-unit × room-list approach.

## Still needed from Mohamed
Owner MOS invoice, owner BOQ (beyond the E-Promise sheet) and a variation/EI example are not on the laptop — later.
HOTEL tracker / room list still needed.
