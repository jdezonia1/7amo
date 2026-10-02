// Raffaello components are CSS classes (bundle.css) mirrored by the WPF styles in RaffUi.cs.
// AppHeader .rf-header | WorkspaceTabs .rf-tabs .rf-tab(.is-active) | Kpi .rf-card.rf-kpi(.is-hero)
// Panel .rf-card .rf-panel-title | DataTable table.rf-table tr.is-total tr.stripe-over|warn|ok | Tag .rf-tag-over|warn|due|ok|fixed | Button .rf-btn(-primary|-yellow)
declare global { interface Window { Raffaello: { classes: Record<string,string> } } }
export {};
