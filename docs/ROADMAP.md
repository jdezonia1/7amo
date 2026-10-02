# Raffaello — upgrade roadmap (recommendations, 02-Oct-2026)

Benchmarked against what leading construction-cost and site tools do worldwide (CostX / Togal.AI takeoff,
Procore / Aconex document control, Kojo materials, Document Crunch contract AI, Buildots / OpenSpace site progress,
RIB iTWO / Primavera cost & schedule), filtered to what pays off for an electrical QS on Raffles.

## Tier 1 — biggest time savings for Mohamed
1. **AI drawing takeoff (symbol counting on PDF drawings).** Count electrical symbols (sockets, lights, data, GRMS,
   switches…) per room straight from the PDF drawings with vision + template matching — works even when AutoCAD
   blocks are exploded. Builds PROJECT QTY per room automatically and re-counts when a drawing revision arrives.
2. **Marked-up statement verification.** Read the subcontractor's highlighted drawings: detect the highlighted symbols,
   count them per room and compare with what he claimed ("claimed 39 lighting, highlighted 31"). Turns the review
   of a 78-page statement into a list of discrepancies.
3. **Site app for the site team (tablet / phone, works offline).** Tap a room on the plan, mark stage done per system,
   take photos (time + location stamped), WIR no. — replaces paper site statements; feeds the ledger directly.
4. **Contract intelligence.** Every contract clause becomes a rule the app enforces (payment % per stage, retention,
   15 m rule, 4.5 m bands, penalties, caps, tolerance) with a side-by-side comparison of subcontractors' contracts
   and an obligations calendar (warranty end, penalty triggers, handover dates).
5. **Claim anomaly detection.** Flags unusual claims automatically: sudden jumps vs previous invoice, a room claimed
   by two subcontractors, length claims far above typical for that room type, the same photo reused, quantities
   claimed before the WIR date, invoice copied from a previous one (already detects INV-03 = INV-02).

## Tier 2 — control and visibility
6. **Material reconciliation: delivered vs installed vs paid.** Theoretical consumption from installed points
   (e.g. metres of cable per point by room type) vs delivered → wastage %, stock on site, MOS release timing.
7. **Rate benchmarking.** The same item across subcontractors, POs and the owner BOQ: highest / lowest / average,
   outliers, margin per item (owner rate − subcontract rate) → negotiation and variation pricing.
8. **Cash-flow forecast.** From the programme + remaining quantities + payment terms (90/10, 70/20/10, LC before
   delivery) → monthly payables to subcontractors/suppliers vs receivables from the owner.
9. **Drawing revision compare.** Overlay two revisions of a PDF drawing, highlight changes, list affected rooms and
   items → feeds variations / EIs and updates PROJECT QTY.
10. **Earned value & productivity.** Planned vs actual per stage/system (S-curve already), productivity per
    subcontractor (points per week), forecast finish per area, early warnings.

## Tier 3 — platform
11. **Ask Raffaello over all data and documents** (Arabic + English): "which rooms on L2 still need 2nd fix data?",
    "where is DN 81064344?", "draft the rejection reply for INV-03" — answers cite the record/page.
12. **Morning brief & notifications** (email / Teams / WhatsApp): what changed overnight, Aconex steps overdue,
    invoices waiting on you, new over-cap claims.
13. **Bilingual Arabic/English interface (RTL)** for site staff and subcontractors.
14. **E-signature & approvals inside the app** with tamper-evident audit (hash chain) — the package proves what was
    approved.
15. **Integrations:** E-Promise (ERP) export in its import format; Aconex API when MOBCO gets access; AutoCAD/Revit
    add-in to push room boundaries and counts (replaces the LISP + Dynamo steps).
16. **Subcontractor portal:** subcontractors submit statements and see their own claims/status — fewer emails,
    standard format enforced.
