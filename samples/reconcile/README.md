# Reconciliation samples

Costpoint reports for the parallel-run reconciliation (**Setup → Go-live migration → Reconcile**,
`/setup/migration/reconcile`). Each file is read through a saved column mapping, so the customer's own report layouts
work without code changes: upload, check the suggested mapping, adjust, save it, compare.

The trial balance, AR aging, AP aging and revenue samples match Workbench exactly after the go-live import samples
(`samples/import/costpoint`, cut-over 2026-11-01) for fiscal period 2026-10 (FY2027 period 1). The project cost,
labor distribution and indirect rate samples show the layouts of those Costpoint reports; their amounts are
illustrative (Workbench computes them from approved time, ODCs and indirect rates entered after cut-over).

| File | Report | Keys | Amounts | Notes |
|---|---|---|---|---|
| [`trial-balance.csv`](trial-balance.csv) | Trial balance | Account | Ending = Debit Balance − Credit Balance | A title row above the headers and the Total row are skipped |
| [`project-cost.csv`](project-cost.csv) | Project cost by element | Project (or WBS) | Direct labor, fringe, overhead, G&A, ODCs, total | WBS-level ids add up to the project unless "Compare WBS elements" is on |
| [`labor-hours.csv`](labor-hours.csv) | Labor distribution | Employee × project | Hours, labor cost | |
| [`ar-aging.csv`](ar-aging.csv) | AR aging (as of 10/31/2026) | Customer | Current, 1–30, 31–60, 61–90, over 90, total | Days past due from the due date |
| [`ap-aging.csv`](ap-aging.csv) | AP aging (as of 10/31/2026) | Vendor | Buckets, total (excluding retainage), retainage | |
| [`revenue-by-project.csv`](revenue-by-project.csv) | Revenue by project | Project | Revenue | Credits less debits on revenue accounts |
| [`indirect-rates.csv`](indirect-rates.csv) | Indirect rates (year to date) | Pool | Pool cost, base, rate | Rates are percentages: the suggested scale is 0.01; FRINGE BENEFITS is rewritten to Fringe |

Mapping options: debit/credit pairs (column − minus column), negate, scale, absolute and percentage tolerances per
amount (default 0.00, rates 0.0001), skipped key prefixes (Total, Grand Total, Report Total, Subtotal), key rewrites,
ignoring punctuation or leading zeros in keys, and the range of amounts (period, year to date, inception to date).
