# BC client reference (from user screenshots, step 1 addendum)

Source: uploads/pasted-1786447530521-0.png (customer card), uploads/pasted-1786447579955-0.png (No. Series lookup dialog).

## Input fields
- Square corners (radius 0), 1px hairline border, white fill when editable.
- Read-only / calculated fields: flat light-grey fill, NO border. Right-aligned numerics ("0,00").
- Label column left-aligned with dotted leader to the control; control column fixed width.
- Assist-edit "..." button sits as an attached square button on the right edge of the field.
- Dropdowns: same box + right-side chevron, no separate button chrome.
- Two-column form layout inside a "Generelt" style section; collapsible sub-sections with a chevron
  and a right-aligned summary value (e.g. "Delvis", "1M(8D)").

## Buttons / actions
- Primary: teal-dark fill (#008089 family), white text, square corners, no shadow. e.g. "OK".
- Secondary: white fill, 1px grey border, dark ink. e.g. "Annuller".
- Command bar: flat text + teal icon, no border/background until hover.
- Dialog footer: right-aligned, primary then secondary.

## Lists
- Selected row: pale teal tint + leading arrow glyph; column headers small, plain, sortable arrow.
- Header row has a light background, hairline separators only.

## Open question for step 2
Our token layer currently keeps the toolbox radii (6/8/12/16). BC itself is square.
Decide: BC-square controls, or keep the softer toolbox radius. See --r-control proposal.
