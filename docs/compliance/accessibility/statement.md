# Accessibility statement

Workbench is built for U.S. government contractors, whose staff and customers include people with disabilities. We aim
for the Web Content Accessibility Guidelines (WCAG) 2.1 at Level AA, the standard Section 508 of the Rehabilitation Act
incorporates.

## Where we are

Workbench **partially conforms** to WCAG 2.1 AA: most of it meets the standard, and the exceptions are listed in our
Accessibility Conformance Report (`vpat.md`, VPAT 2.5 WCAG edition, October 2026). The main ones today:

- Gantt dependency links and sheet column widths can only be changed with a mouse (dates can be changed from the keyboard
  in the Grid view).
- The 30-minute idle sign-out doesn't warn first.
- A few buttons lose keyboard focus while they save.

## What we've built in

- A skip link, landmarks and one heading structure on every page; the whole product works with a keyboard.
- Visible focus in light and dark themes and in Windows forced-colors mode; motion turns off when your system asks for
  reduced motion.
- Text and status colors that meet contrast in both themes, and status never shown by color alone.
- Every chart has its numbers in a table, and the Gantt has a table view of the schedule.
- A phone layout (`/m`) for timesheets, expenses, approvals and leave, with large touch targets.
- Automated accessibility checks (axe-core on every page and role, and component tests) on every change.

## Tell us

If something in Workbench gets in your way, tell your Workbench administrator or contact the Workbench team through the
in-app help. Say which page and what you were trying to do; we aim to reply within five business days and treat
accessibility defects as bugs, not feature requests. We can provide this statement and the conformance report in
another format on request.

Last reviewed: 2026-10-18.
