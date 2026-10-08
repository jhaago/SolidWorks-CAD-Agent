# SolidWorks CAD Agent V1 Release Checklist

Baseline: SOLIDWORKS Premium 2020 SP0.0  
Branch: `feature/v1-solidworks-2020`

## Automated gate

- [x] Millimetre/metre scale regression is covered by unit and integration-contract tests.
- [x] Workspace traversal, sibling-prefix, extension, and overwrite rejection are covered.
- [x] STA dispatcher and session boundaries are covered without leaking COM objects.
- [x] Command failure and persisted cancellation prevent later commands from starting.
- [x] An ambiguous M8 request stops for clarification even in Auto mode.
- [x] Agent Host binding rejects wildcard, LAN, and non-`127.0.0.1` prefixes.
- [x] OpenAI requests use strict structured output, `store=false`, and no parallel tool calls.
- [x] OpenAI credentials are abstracted through `ISecretStore` and the production store uses Windows Credential Manager.
- [x] Desktop references the localhost HTTP API only and has no SOLIDWORKS interop reference.

## Physical SOLIDWORKS 2020 gate

- [x] Register the localhost URL ACL and start Agent Host and Desktop as separate processes.
- [x] Confirm Attach and Launch report the installed SOLIDWORKS version.
- [x] Run every test marked `SolidWorksIntegration` with SOLIDWORKS visible.
- [x] Submit the acceptance prompt in approval mode and confirm zero CAD commands execute before approval.
- [x] Approve and confirm native sketch, boss-extrude, hole cut, rebuild, and structured verification.
- [x] Save, close, reopen, rebuild, and re-verify the native `.SLDPRT` inside the workspace.
- [x] Repeat the complete acceptance workflow three times with new job IDs.
- [x] Submit “Make a plate with an M8 hole” and confirm clarification is required.
- [x] Run the acceptance prompt in Auto mode and re-confirm ambiguity and overwrite hard gates.
- [ ] Search logs, SQLite, screenshots, and Git history for the test API-key prefix; confirm no match.

## V2 seam

Evidence for completed PC checks is recorded in `windows-validation-2026-10-05.md`. All four visible integration tests passed in lifecycle-first order. Screenshot visual review remains open. The credential scan found no matches in project/evidence files, SQLite storage or Git patch history. Remote control has its own pending physical checklist in `live-remote-windows-testing.md`.

The desktop consumes only the versioned localhost HTTP/JSON job API. A future authenticated remote gateway can reuse that API and persistent job model without referencing SOLIDWORKS interop assemblies. V1 must remain bound to loopback only.
