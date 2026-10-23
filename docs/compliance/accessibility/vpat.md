# Workbench Accessibility Conformance Report

**WCAG Edition** (based on VPAT® Version 2.5, ITI)

| | |
|---|---|
| **Name of product/version** | Workbench (web application, Interactive Server; phone layout at `/m`). Main branch, October 2026. |
| **Report date** | 2026-10-18 |
| **Product description** | Project accounting, timekeeping, expenses, purchasing, billing and project planning (sheets, Gantt, board, dashboards) for U.S. government contractors. Used in a desktop or mobile browser; the desktop app hosts the same pages in a WebView. |
| **Contact information** | The Workbench product team (see the accessibility statement, `statement.md`) |
| **Notes** | Section 508 (36 CFR 1194, the Revised 508 Standards) incorporates WCAG 2.1 Level A and AA by reference for web content; this report covers those criteria. Level AAA is not claimed and was not evaluated. The Windows/macOS desktop shell (`Workbench.Desktop`) was not evaluated separately; it renders the same pages. Printed/PDF outputs (invoice, purchase order, reconciliation and incurred-cost print views) were checked as web pages only. |
| **Evaluation methods used** | 1. Automated: axe-core 4.10 (WCAG 2.0/2.1 A and AA rules) through Playwright on every routed page (108 pages including one example of each detail page found in the demo data) as each seeded role (Admin, Finance/PM, Employee, Viewer), in the light and dark themes, at 1280 px and 375 px wide (`tests/a11y/scan.mjs`): 1,506 page scans on 2026-10-18 with no violations reported. 2. Component checks in the unit tests (`A11yAssert`: accessible names, nested controls, table names). 3. Browser checks of what axe can't see (`tests/a11y/keyboard.mjs`): the idle sign-out warning (opens in time, focus, Tab stays inside, Enter and Escape keep the session, axe on the dialog in both themes) and focus staying on a save button that disables itself. 4. Manual keyboard-only pass of the core tasks: enter and submit a timesheet (full site and phone), create an expense report, approve in the approvals inbox, work the sheet grid (including column widths), the Gantt and its dependencies (item details panel), the board, the Ctrl+K search, and the phone pages. 5. Code review of ARIA in the custom widgets (sheet grid, Gantt, board, command palette, heat map). Not yet done: testing with screen readers (JAWS, NVDA, VoiceOver, TalkBack) by users with disabilities, Windows High Contrast end to end, and 400% zoom on every page. |

## Applicable Standards/Guidelines

| Standard/Guideline | Included in report |
|---|---|
| [Web Content Accessibility Guidelines 2.0](https://www.w3.org/TR/2008/REC-WCAG20-20081211/) | Level A (Yes), Level AA (Yes), Level AAA (No) |
| [Web Content Accessibility Guidelines 2.1](https://www.w3.org/TR/WCAG21/) | Level A (Yes), Level AA (Yes), Level AAA (No) |
| [Web Content Accessibility Guidelines 2.2](https://www.w3.org/TR/WCAG22/) | Not evaluated |

## Terms

- **Supports**: the functionality of the product has at least one method that meets the criterion without known defects or meets with equivalent facilitation.
- **Partially Supports**: some functionality of the product does not meet the criterion.
- **Does Not Support**: the majority of product functionality does not meet the criterion.
- **Not Applicable**: the criterion is not relevant to the product.

## WCAG 2.1 Report

### Table 1: Success Criteria, Level A

| Criteria | Conformance Level | Remarks and Explanations |
|---|---|---|
| 1.1.1 Non-text Content (Level A) | Supports | Icon buttons have accessible names; decorative icons are hidden from assistive technology; progress and loading bars are named. Every chart is exposed as one named image with its data available in a table ("Show data table"), and the Gantt timeline has a "Show the schedule as a table" alternative listing dates, links, slack and critical path. |
| 1.2.1 Audio-only and Video-only (Prerecorded) (Level A) | Not Applicable | The product has no audio or video content. |
| 1.2.2 Captions (Prerecorded) (Level A) | Not Applicable | No video content. |
| 1.2.3 Audio Description or Media Alternative (Prerecorded) (Level A) | Not Applicable | No video content. |
| 1.3.1 Info and Relationships (Level A) | Partially Supports | Pages use a single `h1` and ordered headings, landmarks (skip link, banner, labelled main menu, breadcrumb navigation, main), labelled form fields, and data tables with column headers and a name. The sheet grid is an ARIA grid with row and column headers; the heat map uses row headers. Exceptions: some older data tables have column headers but no table name or row headers, and a few groups of related fields are grouped visually only. |
| 1.3.2 Meaningful Sequence (Level A) | Supports | DOM order matches the visual order; the phone layout reflows in reading order. |
| 1.3.3 Sensory Characteristics (Level A) | Supports | Instructions name controls by their label, not by shape or position alone. |
| 1.4.1 Use of Color (Level A) | Partially Supports | Status chips carry text and an icon; project health, the resourcing heat map and the Gantt critical path add a symbol (▲ ● ○) or text; links in tables and text are underlined; negative amounts carry a minus sign. Exception: chart lines and bars are told apart by color in the chart itself (the data table alternative names every series). |
| 1.4.2 Audio Control (Level A) | Not Applicable | No audio. |
| 2.1.1 Keyboard (Level A) | Supports | All core tasks work with the keyboard alone, including the sheet grid (arrow keys, Enter/F2 to edit, Escape, Space for checkboxes), the board ("Move to" menu on each card), the Ctrl+K search and every dialog. Everything done by dragging has a keyboard way: Gantt dependency links are added and removed under "Depends on" in the item details panel (on the chart they are drawn between bars); grid column widths are set with "Column width" in the column menu (or by dragging the header edge); Gantt dates can be changed in the Grid view and the schedule table; in Admin › Customization › Layouts fields, sections, list columns and home page widgets move with arrow buttons and a "Move to" menu as well as by dragging, and list columns sort from a header button. |
| 2.1.2 No Keyboard Trap (Level A) | Supports | Dialogs keep focus inside while open and close with Escape, returning focus to the control that opened them. |
| 2.1.4 Character Key Shortcuts (Level A) | Supports | Ctrl+K / ⌘K uses a modifier. The one single-key shortcut, N for the **+ New** menu, does nothing while focus is in a text field, grid or dialog, and can be turned off from the account menu ("Turn off the N shortcut for New"). Single-key commands in the sheet grid work only while the grid has focus. |
| 2.2.1 Timing Adjustable (Level A) | Partially Supports | Two minutes before the security idle sign-out (30 minutes by default, configurable by the administrator) a dialog warns how long is left and offers "Stay signed in"; Enter, Escape or the button keeps the session, and activity in another Workbench tab counts. The session's absolute lifetime (12 hours after sign-in by default) is announced two minutes before it ends so work can be saved, but it can't be extended without signing in again; an administrator who sets `Authentication:Session:AbsoluteLifetimeHours` to 20 or more meets the twenty-hour exception. No other time limits. |
| 2.2.2 Pause, Stop, Hide (Level A) | Supports | No moving, blinking or auto-updating content that lasts more than five seconds; notification messages close on their own or with a button. |
| 2.3.1 Three Flashes or Below Threshold (Level A) | Supports | Nothing flashes. Animations are turned off when the user prefers reduced motion. |
| 2.4.1 Bypass Blocks (Level A) | Supports | "Skip to main content" link (first in the tab order on every full-site page), landmarks and headings. |
| 2.4.2 Page Titled (Level A) | Supports | Every page has a unique title of the form "Page · Section · Workbench". |
| 2.4.3 Focus Order (Level A) | Supports | Focus order follows the visual order; focus moves to the page heading on navigation, into dialogs on open and back to the opener on close; after an approval decision focus returns to the inbox heading. A save or submit button that disables itself while it works keeps focus: it gets focus back when it is enabled again, or focus moves to the next control when it stays disabled (for example Save on the timesheet once nothing is left to save). Removing a dependency in the item details panel moves focus to the "Depends on" heading. |
| 2.4.4 Link Purpose (In Context) (Level A) | Supports | Link text (with its row or heading) describes the destination; repeated row actions name their row. |
| 2.5.1 Pointer Gestures (Level A) | Supports | No multipoint or path-based gestures are required. |
| 2.5.2 Pointer Cancellation (Level A) | Supports | Actions run on the up-event (standard buttons and links); drags can be abandoned by dropping back in place. |
| 2.5.3 Label in Name (Level A) | Supports | Accessible names start with the visible label (for example "Approve Alex Rivera: timesheet…" for the Approve button). |
| 2.5.4 Motion Actuation (Level A) | Not Applicable | No motion-operated functions. |
| 3.1.1 Language of Page (Level A) | Supports | `lang="en"` on every page. |
| 3.2.1 On Focus (Level A) | Supports | Focus never triggers a change of context. |
| 3.2.2 On Input (Level A) | Supports | Changing a field or filter updates the content on the page; it doesn't navigate or submit without a button. |
| 3.3.1 Error Identification (Level A) | Supports | Field errors are shown in text next to the field (`aria-invalid` set) and failed actions in an announced message. |
| 3.3.2 Labels or Instructions (Level A) | Supports | Fields have visible labels; required fields are marked; formats are shown (for example dates as YYYY-MM-DD). |
| 4.1.1 Parsing (Level A) | Supports | Pages are generated by the Blazor renderer (well-formed, unique IDs). (Obsolete in WCAG 2.2 and treated as always satisfied for 2.0/2.1 per the W3C errata.) |
| 4.1.2 Name, Role, Value (Level A) | Partially Supports | Custom widgets expose ARIA roles and states: the sheet grid (grid, row, gridcell, columnheader, `aria-activedescendant`, `aria-selected`, `aria-readonly`, checkbox and expand states), the command palette (combobox and listbox with `aria-activedescendant`), the board (named card links and move menus), menus (`aria-expanded`), dialogs (`role="dialog"`, `aria-modal`, labelled by title). Exception: the sheet-grid's cell editors are announced as plain text fields rather than as part of the grid. |

### Table 2: Success Criteria, Level AA

| Criteria | Conformance Level | Remarks and Explanations |
|---|---|---|
| 1.2.4 Captions (Live) (Level AA) | Not Applicable | No live audio. |
| 1.2.5 Audio Description (Prerecorded) (Level AA) | Not Applicable | No video. |
| 1.3.4 Orientation (Level AA) | Supports | Works in portrait and landscape. |
| 1.3.5 Identify Input Purpose (Level AA) | Supports | The user's own name and email come from the identity provider at sign-in, so few fields ask for them; the one that does (the certifying official's name when certifying an incurred-cost submission) declares `autocomplete="name"`. Fields about other people (employees, invitations, vendor contacts, delegates, a new organization's first administrator) turn autofill off so the browser doesn't offer the user's own details there. |
| 1.4.3 Contrast (Minimum) (Level AA) | Supports | The theme palette was adjusted so text and status colors meet 4.5:1 in both the light and dark themes (secondary text, status colors, filled chips and buttons, alerts, breadcrumbs); the axe scan reports no contrast failures on the pages listed in the evaluation. Disabled controls are exempt. |
| 1.4.4 Resize text (Level AA) | Supports | Text can be zoomed to 200% with browser zoom without loss of content or function (wide tables scroll horizontally). |
| 1.4.5 Images of Text (Level AA) | Supports | No images of text. |
| 1.4.10 Reflow (Level AA) | Partially Supports | The phone layout (`/m`) and most full-site pages reflow at 320 CSS px. Data grids that need two dimensions (the timesheet week grid, sheet grid, Gantt, wide financial tables) scroll horizontally, which the criterion allows; some full-site toolbars and forms also need horizontal scrolling at that width. |
| 1.4.11 Non-text Contrast (Level AA) | Partially Supports | Input borders, the focus ring (2 px, theme primary) and status icons meet 3:1. Exceptions: some chart series colors and outlined chips in the dark theme are below 3:1 against their background. |
| 1.4.12 Text Spacing (Level AA) | Partially Supports | Most content adapts to increased line, letter and word spacing. The virtualized sheet grid and Gantt rows have fixed heights and can clip text with large spacing overrides. |
| 1.4.13 Content on Hover or Focus (Level AA) | Partially Supports | Tooltips show on hover and keyboard focus and stay while hovered. Some tooltips can't be dismissed with Escape without moving focus. |
| 2.4.5 Multiple Ways (Level AA) | Supports | The main menu, the Ctrl+K search (pages, projects, sheets, dashboards, recent pages), breadcrumbs and the home page to-do list. |
| 2.4.6 Headings and Labels (Level AA) | Supports | Headings and labels describe their topic or purpose. |
| 2.4.7 Focus Visible (Level AA) | Supports | Every focusable control shows a 2 px focus ring in both themes (and in forced-colors mode). |
| 3.1.2 Language of Parts (Level AA) | Supports | All content is in English. |
| 3.2.3 Consistent Navigation (Level AA) | Supports | The app bar, menu and breadcrumbs are the same on every page; the menu varies only by role. |
| 3.2.4 Consistent Identification (Level AA) | Supports | The same functions use the same labels and icons throughout. |
| 3.3.3 Error Suggestion (Level AA) | Supports | Error messages say what went wrong and what to change (for example which charge code is closed or which ceiling would be exceeded). |
| 3.3.4 Error Prevention (Legal, Financial, Data) (Level AA) | Supports | Timesheets and expense reports are reviewed and certified before submission and can be returned; financial postings are reviewed before posting and corrected by reversal; deletions ask for confirmation. |
| 4.1.3 Status Messages (Level AA) | Supports | Save, submit and error messages are announced (`role="alert"`); the command palette announces its result count and the board announces moves (`role="status"`); the notification count is a live region. |

### Table 3: Success Criteria, Level AAA

Not evaluated.

## Revised Section 508 Report (summary)

| Chapter | Conformance | Remarks |
|---|---|---|
| Chapter 3: Functional Performance Criteria | Partially Supports | Use without vision relies on the WCAG items above, with the exceptions noted above. Use with limited vision: zoom to 200%, the dark theme and forced-colors mode work. Without perception of color: see 1.4.1. Without hearing or speech: no audio or speech input. Limited manipulation: all core tasks work with a keyboard. |
| Chapter 4: Hardware | Not Applicable | Software only. |
| Chapter 5: Software | See WCAG tables | Web content is evaluated against WCAG 2.1 as allowed by E205/E207. The platform's accessibility services are used through the browser. |
| Chapter 6: Support Documentation and Services | Partially Supports | This report, the accessibility statement and the in-app help describe accessibility features; documentation in alternate formats is available on request. |

## Known issues and plan

| Issue | Criteria | Plan |
|---|---|---|
| The 12-hour absolute session lifetime can't be extended | 2.2.1 | Users are warned two minutes before. Administrators who need full conformance set `AbsoluteLifetimeHours` to 20 or more (twenty-hour exception); revisit with the security policy owner. |
| Screen reader testing with users | all | Test the core tasks with JAWS, NVDA and VoiceOver before the first federal pilot. |

Re-run the checks before each release: `tests/a11y/scan.mjs` and `tests/a11y/keyboard.mjs` (see `docs/CONVENTIONS.md`, "Accessibility") and the
component tests (`A11yAssert`).
