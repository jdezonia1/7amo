# Raffaello

Raffaello is MOBCO's desktop dashboard for the Raffles Hotel and Branded Residences project: contracts, MIRs/WIRs, invoices and statements, BOQ quantities, material deliveries, Aconex downloads and reports in one C#/WPF app that sits on a shared drive for the whole team. It wears the same skin as Mizan: dark red, gold, grey, ink and white, condensed uppercase headings, and tables made of rounded card-rows instead of ruled grids.

*Measure twice. Build once.*

## Content fundamentals

- **Voice:** short, plain, site-office English. Page titles and labels are UPPERCASE (the team writes in capitals). No emojis, no jargon.
- **Labels name the thing, not the action:** `CERTIFIED TO DATE`, `REMAINING QTY`, `DELIVERED vs PO`.
- **Numbers:** always IBM Plex Mono, tabular, right-aligned; SAR with thousands separators, quantities with their unit (`M`, `NO`, `SET`).
- **House rules shown in the UI:** quantities are never summed across stages; rework never counts as progress; PROJECT QTY caps subcontractor claims — over-cap lines may be posted but carry an `OVER` tag.

## Visual foundations

- **Ground and cards.** `bg` behind everything; content sits on `surface` cards with `radius-card` (16px), a `line` edge and `shadow-card`. Page padding and panel gaps are `space-6`; card padding `space-4`.
- **Header bar.** Full-width `accent` fill, `on-accent` text: the diamond mark, the `RAFFAELLO` wordmark (display, 0.18em tracking), then project switcher, the Claude assistant button (Ctrl+Shift+A) and the user.
- **Workspace tabs.** Uppercase condensed (`tab` style), `radius-btn`. The active tab is filled `accent`; the rest are ghost on `bg`. Modules: DASHBOARD · CONTRACTS · QUANTITIES · STATEMENTS · MATERIALS · INVOICES · ACONEX · REPORTS · ASSISTANT, then a DOCUMENTS overflow for document control.
- **KPI cards.** 11px uppercase `muted` label over a 32px `kpi-value`. One per page may be the **hero**: `hero` fill, `yellow` label, `hero-ink` value, a 3px `accent` line across the top.
- **Tables.** No grid. Header in the `label` style, `muted`. Each row is its own card-row: `surface-2`, `radius-row`, `space-1` between rows; hover darkens one step; an optional 4px left stripe carries status colour. Total rows are `accent-soft` with `num-total`.
- **Tags.** `tag` style, `radius-tag`: OVER = `accent` / `on-accent`; WARN / DUE = `yellow` / `on-yellow`; OK = `good-soft` / `good`.
- **Buttons.** `radius-btn`. Primary `accent` + `on-accent`; ghost = `line` outline on transparent; yellow = `yellow` + `on-yellow` for secondary emphasis (Export, Import).
- **Charts.** Series in order: `accent`, `yellow`, `graphite`, `good`. Bars have `radius-tag` tops; no gridlines beyond a faint `line` baseline.
- **Themes.** Light and Dark ship; the user may swap the accent (red default, yellow, green, blue, purple, graphite) — components read `accent` so they follow.

## Iconography

Line icons at 18px, 1.75px stroke, `currentColor` (the WPF app draws them as path geometry). The assistant uses a sparkles icon. The brand mark is the diamond R (see Logos); never redraw it with a different letter or colour.

## Fonts

Barlow Condensed 600/700 (display), IBM Plex Sans 400/600 (body), IBM Plex Mono 500/700 (numbers), Cinzel 800 (the R in the mark only). All are Google Fonts; the app bundles them under `Assets/Fonts`.
