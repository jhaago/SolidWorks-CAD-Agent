# Desktop modernisation implementation plan

## Source and scope

This task started with an empty workspace. Source was copied from
`C:\SolidWorks-CAD-Agent-Source`, excluding build outputs and runtime data,
and committed as a local baseline. It is an exported source tree, not the
upstream Git history. The original remains untouched.

Keep .NET Framework 4.8 / WinForms and the localhost HTTP client boundary.
Do not change host, planner, bridge, database, credentials or CAD safeguards.
The user's brief authorizes autonomous design decisions and direct execution.

## Design

Charcoal background (#10151C), raised surfaces (#171F2A), quiet borders
(#2C3949), pale readable text, muted slate labels and restrained cyan-blue
accent. Segoe UI 10pt body, 18pt workspace title, Consolas for diagnostics.
Use 8/16/24px spacing, docked table layouts, a 240px job sidebar and a
resizable request/review workspace. Retain the normal Windows title bar.
Use drawn line icons and text labels, visible keyboard focus and explicit
state copy. Indeterminate activity means a request is pending; never infer
CAD percentage or command success from elapsed time.

## Task 1 — Presentation contracts and service integration

- Extend Desktop API DTOs/client for existing paged history, revision change,
  settings and credential-presence endpoints only.
- Add pure `JobPresentation` logic for approval/cancellation eligibility,
  readable lifecycle copy, command/verification rows and redacted diagnostics.
- Tests first: stale/ambiguous approval, terminal cancellation, revision-scoped
  operation results, JSON redaction, paginated history, settings request bodies
  and revision-bound change instructions. Run the full unit suite.

## Task 2 — Main workspace and shared controls

- Add `Ui/Theme.cs` and reusable surface, button/icon and list factories.
- Replace MainForm fixed coordinates with sidebar, connection header,
  request/revision composer, design brief, plan/activity tabs and diagnostics.
- Wire persisted job selection, history pagination, explicit refresh,
  revision requests, busy gating and safe form lifetime handling.
- Poll the displayed job during execution using GET only; cancellation uses
  the existing endpoint and remains available while approval is executing.
- Keep raw plan/results accessible under diagnostics with redaction.
- Test form construction/layout and asynchronous workflows using fake HTTP
  on an STA thread; no API billing or SolidWorks commands.

## Task 3 — Settings and validation

- Modernise SettingsForm; load actual host settings and show credential
  presence only. Save explicitly through PUT /settings, surface restart
  requirements, preserve hidden defaults and do not provision credentials.
- Verify solution build and unit tests; compile real integration contracts.
- Render the real forms to PNG with a local test harness and inspect minimum,
  normal, large and settings layouts. Add a Windows launch script.
- Document scope, launch steps, test evidence and physical acceptance gaps.

## Review focus

Busy actions must not enable stale approval. History navigation must not
replace an in-flight job. Cancel responses are partial snapshots, so refresh
the job before presenting results. Distinguish active host mode from saved
mode. Closing forms during pending HTTP must not touch disposed controls.
Diagnostics must mask common credential fields and key-shaped strings.
