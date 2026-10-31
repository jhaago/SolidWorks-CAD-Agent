# Execution ledger — desktop modernisation

Plan: `docs/superpowers/plans/2026-10-11-desktop-modernisation.md`

- Baseline: `0e292da`, source export copied from C:\SolidWorks-CAD-Agent-Source.
  The task started empty, with no upstream Git repository/history available.
- Task 1: desktop contracts/presentation added. Host/engine source unchanged.
- Task 2: main workspace, shared theme, revision/history/activity integration.
  Approval busy/ambiguity regression tests observed RED then GREEN.
- Task 3: editable settings, launcher, runtime bundle and layout validation.
  Minimum-size test observed RED then GREEN after layout allocation fixes.
- Harness decision: UI tests use a single persistent STA message loop. Creating
  a separate STA per test left WinForms system-event context/thread problems.
  This is test-only; the application already runs on a single STA loop.
- Independent review: two Important findings, two Minor findings. Cancellation
  polling, delayed-snapshot ordering and native accessibility text redaction
  each reproduced RED then GREEN. Logging case compatibility and accessible tab
  selection also reproduced RED then GREEN and fixed.
- Reconciliation decision: continue polling displayed Cancelled jobs because
  no host quiescence signal exists. Cost: one read every 1.5 seconds until the
  user navigates away. This avoids inventing an operation completion timeout.
- Latest Release verification: 0 warnings/errors; 142/142 tests passed. Includes
  real routes, SQLite persistence and simulation without cloud/native execution.
- Native integration contracts compiled; physical SolidWorks/cloud acceptance,
  high-DPI and live screen-reader checks remain unverified.
- Packaging validation reproduced a missing swconst interop assembly. MSBuild
  omitted that unused reference from transitive Host output. The bundle step
  now explicitly copies both detected interop assemblies; engine source stays
  unchanged. The existing capability/dependency validator is the release gate.
- The negative bundle-validator test originally assumed a compile-only CI
  input. On a native build it rewrote Native to Native and falsely failed.
  It now flips the actual marker, so either build capability exercises rejection.
- Final bundle validation passed with NativeSolidWorksInterop; the negative
  capability-marker test correctly rejected the tampered package.
- The source's original V1 progress ledger describes earlier checkpoints and
  is preserved. Current desktop capability/launch instructions are in
  `docs/desktop-modernisation.md` and README.md.
