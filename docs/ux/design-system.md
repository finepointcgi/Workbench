# Workbench design system (Material 3)

Workbench follows the Material 3 ("Material You") design language on top of MudBlazor, which implements Material 2. The
look is a skin: the colors, type, shapes, elevation and motion come from tokens defined once, and the shared styles apply
them to every MudBlazor component, so a new page that uses `MudButton`, `MudTextField`, `MudTable`, `MudDialog` and the
rest looks right without page-level styling. Ease of use comes first: the skin never changes a layout or hides anything.

## Where things live

| What | File |
| --- | --- |
| Color roles, light and dark (the single definition) | `src/Workbench.Ui/Theme/M3Tokens.cs` |
| The roles as CSS variables for the current mode | `Layout/ThemeTokens.razor` (in each layout, beside `MudThemeProvider`) |
| MudBlazor palette and type scale, filled from the roles | `Theme/WorkbenchTheme.cs` |
| Shape, elevation, motion and state-layer tokens, and the component styles | `src/Workbench.Ui/wwwroot/theme.css` |
| Focus rings, underlined links, reduced motion, forced colors | `src/Workbench.Ui/wwwroot/a11y.css` (loaded last, wins) |
| Roles for pages that follow the operating system's light/dark setting | `src/Workbench.Web/wwwroot/css/m3-tokens.css` (generated) |
| Public pages: customer link, public forms, supplier portal | `wwwroot/css/customer-view.css`, `public-form.css`, `supplier-portal.css` |

## Color

The roles are the Material Theme Builder output for the brand seed **#B53C0A** (the Workbench blue); the seed is
`M3Tokens.Seed`. Light and dark are two sets of the same roles. Use the CSS variables, never a hex value:

| Role | Use |
| --- | --- |
| `--md-sys-color-primary` / `on-primary` | The one main action on a screen (filled button), links, the active tab, focus |
| `primary-container` / `on-primary-container` | The brand mark, floating action buttons, the "important" tonal fill |
| `secondary-container` / `on-secondary-container` | The selected navigation item, tonal buttons, selected chips, the current group tab |
| `tertiary` and its container | An accent that is neither the main action nor a status; use sparingly |
| `error`, `error-container` | Errors, destructive actions, overdue |
| `success`, `warning`, `info` (+ `on-…`) | Status (extended colors, "custom colors" in M3), harmonised with the blue |
| `surface` | The page |
| `surface-container-lowest … highest` | Tonal layers: drawer and cards (low), menus (container), dialogs (high), filled text fields and filled cards (highest). A higher layer is a stronger tint of the primary, so depth shows without a shadow |
| `on-surface` / `on-surface-variant` | Text, and secondary text and icons |
| `outline` / `outline-variant` | Input borders and outlined buttons / dividers and card edges |
| `inverse-surface` / `inverse-on-surface` | Snackbars and tooltips |

In MudBlazor terms the same roles are in the palette, so `Color.Primary`, `var(--mud-palette-primary)` and
`var(--md-sys-color-primary)` are the same color. Prefer `Color.*` in Razor and `--md-sys-color-*` in CSS.

### Contrast (WCAG 2.1 AA, Section 508)
Every `on-x` role is at least 4.5:1 on its `x` role; text roles are at least 4.5:1 on every surface container; input
outlines and status icons are at least 3:1. `tests/Workbench.Tests/Theme/M3TokenTests.cs` checks these for both
schemes, so a changed role that fails a pair fails the build. Do not put text on a role other than its `on-` partner.

### Changing a color
Edit `M3Tokens.cs` (both schemes), run the tests with `WORKBENCH_UPDATE_TOKENS=1` once to regenerate
`m3-tokens.css`, and rerun. Nothing else needs to change.

## Type

The scale is the M3 roles, mapped onto MudBlazor's `Typo` in `WorkbenchTheme`; the font is Roboto where installed, then
the platform UI font (no web font, so the page works offline and under the Content-Security-Policy).

| M3 role | MudBlazor | Size / weight | Use |
| --- | --- | --- | --- |
| Display small | `Typo.h2` | 36 / 400 | Rare; big numbers |
| Headline large / medium / small | `Typo.h3` / `h4` / `h5` | 32 / 28 / 24, 400 | `h4` is the page title |
| Title large | (brand, drawer title) | 22 / 400 | App bar |
| Title medium | `Typo.subtitle1`, `Typo.h6` (18) | 16 / 500 | Section and card titles |
| Title small | `Typo.subtitle2` | 14 / 500 | Table headers, small headings |
| Body large / medium / small | `Typo.body1` / `body2` / `caption` | 15 / 14 / 12, 400 | Text; `body2` for dense lists |
| Label large / medium | `Typo.button` / `Typo.overline` | 14 / 12, 500 | Buttons, tabs, chips |

For markup that isn't `MudText`, the classes `md-headline-small`, `md-title-medium`, `md-body-medium`, `md-label-large`
and the rest of the scale (see `theme.css`) apply the same role.

## Shape

| Token | Value | Used for |
| --- | --- | --- |
| `--md-sys-shape-corner-extra-small` | 4px | Text fields, menus, tooltips, snackbars |
| `…-small` | 8px | Chips, group tabs, the brand mark |
| `…-medium` | 12px | Cards, tables, alerts |
| `…-large` | 16px | Floating action buttons, dialogs on a phone |
| `…-extra-large` | 28px | Dialogs |
| `…-full` | 999px | Buttons, the navigation indicator, toggle groups |

## Elevation

Five levels (`--md-sys-elevation-level1…5`, key plus ambient shadow). Most things rest flat and use a surface
container for depth. Menus are level 2, dialogs and snackbars level 3, FABs level 3 (4 on hover). Elevated cards use a
very light level-1 shadow on `surface-container-low`.

## Components

* **Buttons.** Pills, 40px (32px small). `Variant.Filled` is the main action (flat, level 1 on hover);
  filled with the default color is the tonal button (secondary container); `Variant.Outlined` uses the outline role;
  `Variant.Text` for the quietest. One filled primary per screen.
* **Icon buttons and FABs.** 40px circle with a state layer; FABs are 16px-radius primary-container squares.
* **Text fields.** `Variant.Outlined` (4px corners, 2px primary border when focused) is the default for forms;
  `Variant.Filled` sits on `surface-container-highest`.
* **Cards and papers.** The default is the elevated card (`surface-container-low`). `Outlined="true"` gives the outlined
  card (surface plus `outline-variant`). Add `Class="wb-card-filled"` for a filled card (`surface-container-highest`,
  no shadow) or `wb-card-tonal` for one highlighted card (secondary container). Add `wb-card-clickable` to a card that
  is a link or button so it gets the state layer.
* **Navigation drawer.** Surface container low; 48px items; the current page is a full pill on the secondary container.
* **Top app bar.** Surface, flat, 64px, with the brand mark.
* **Tabs.** Sentence case, 3px primary indicator with rounded top corners, primary-colored active label. The group tabs
  across the top of each menu group are pill-cornered chips with the current one on the secondary container.
* **Chips.** 8px corners; selected is the secondary container; clickable ones get the state layer.
* **Dialogs.** Surface container high, 28px corners, headline-small title, 32% scrim, actions right-aligned.
* **Snackbars and tooltips.** Inverse surface; severity snackbars keep their status color.
* **Tables.** Title-small header in `on-surface-variant`, outline-variant rules, row hover state layer.

## State layers and motion

Hover, focus and pressed are the control's own text color laid over it at 8%, 10% and 10%
(`--md-sys-state-*-opacity`; `--wb-state-hover`, `-focus`, `-pressed` are ready-made `color-mix` values to use as a
`background-color` or `background-image: linear-gradient(...)`). Because it is the control's own color it works for
every color and in both modes. Transitions use `--wb-transition` (200ms, M3 standard easing). Keyboard focus keeps the
2px ring from `a11y.css`; `prefers-reduced-motion` collapses all transitions and animations there.

## Public pages

The customer link, public forms and supplier portal are plain pages that load `m3-tokens.css` and map the roles onto
their own short names (`--bg`, `--surface`, `--text`, `--link`, …) at the top of each stylesheet. They follow the
operating system's light/dark setting. Inputs use the `outline` role for their border and buttons are pills.

## Adding a page: checklist

1. Use `MudText` with a `Typo` from the table, `MudButton`/`MudTextField` etc. with `Color`/`Variant`; no hex colors or
   pixel radii in Razor.
2. Need a custom surface? Use a `surface-container-*` role and a `--md-sys-shape-corner-*` radius in CSS.
3. Text goes on its `on-` role; check any new pair with the ratios above.
4. Keep `data-testid`s and the nav structure; tests rely on them.
5. Try it in dark mode and at 375px wide.
