# Per diem sample rates

Sample files for **Setup → Per diem rates** (`/setup/per-diem`). The values are illustrative, shaped like the GSA tables;
import the published tables (GSA CSV/Excel, the GSA Per Diem API, or DoD/State OCONUS CSVs) before real use.

- `gsa-conus-sample.csv`: Washington DC, Huntsville AL and San Diego CA (seasonal), plus the standard CONUS rate.
  Also embedded in `Workbench.Application` for "Load sample rates" and the development seed.
- `oconus-sample.csv`: a few OCONUS locations in the same shape (import with the OCONUS table selected).

Columns (case-insensitive; GSA's own headers such as `STATE`, `DESTINATION`, `COUNTY/LOCATION DEFINED`,
`SEASON BEGIN`, `SEASON END`, `FY27 Lodging Rate` and `FY27 M&IE` are recognized too):
`Country, State, Destination, County, Season Begin, Season End, Lodging, M&IE`.
Seasons are month and day (`October 1`, `10/1`) inside the federal fiscal year (October to September); blank means the
whole year. A row whose destination is `Standard CONUS` (or `Standard Rate`) is the standard rate.
