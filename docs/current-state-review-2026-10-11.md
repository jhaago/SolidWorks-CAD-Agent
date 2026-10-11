# Current state review — 11 October 2026

## Source consolidation

The review branch preserves the latest Windows history through `63100d4` and
integrates the desktop UI from `6d40074`. The UI checkout used an older standalone
baseline, so publishing it alone would have omitted the RemoteAgent and newer CAD
work. No original checkout, installed service or user model was replaced.

The combined desktop retains persisted history, automatic read-only connection
recovery, cancellation during approval, current-revision status, explicit Complete,
and image/design intake. V2 ordered steps are visible before approval. Admitted V2
plans cannot be revised in place; non-admitted candidates remain clarifiable.
Settings retain unedited JSON fields and the restart-required notice across saves.

## Verification

- Latest backend baseline: 678 unit tests passed outside the sandbox.
- Combined source: 697 unit tests passed with native interop enabled. Full solution
  Release build passed with zero warnings/errors; integration fixtures compiled.
- Final compile-only run: all 697 unit tests passed, including the clarification
  regression, recorded in `artifacts/review-results/final-disabled.trx`.
- Windows bundle built and validated with `NativeSolidWorksInterop`. Negative
  checks rejected a missing RemoteAgent and a native claim missing an interop DLL.
- Regression tests first reproduced missing V2 steps, lost intake/completion,
  completed-job cancellation and the blocked V2 clarification action.
- Desktop render fixtures passed and the minimum-size combined layout was visually
  inspected. This does not replace physical DPI/screen-reader acceptance.
- Independent read-only backend and final desktop integration reviews completed.

Evidence: `artifacts/review-results/`, `artifacts/review-captures/`,
`final-native-build.log`, `final-native-tests.log`, and `bundle-review.log`.
Local logs/artifacts are excluded from source commits. NuGet vulnerability lookup
was disabled for offline local builds; ordinary build/CI commands retain auditing.

## Boundaries and local-only work

Current exact line/arc cut readback has not passed native acceptance: earlier runs
were blocked before model creation by SOLIDWORKS 2020 startup/COM failure. This
review did not repeat the crashing launch or operate on user documents. Recovery
and controlled semicircle/four-cut acceptance remain required. Cloud planning and
paired physical-phone execution, photos, artifact saves and network transitions
were not exercised. No Android device/emulator was connected or available locally.

Original uncommitted remote diagnostic logging, a whitespace edit and assistant
setup/handover files remain in their source checkout, outside the verified branch.
The historical bundle-regression script has an unresolved Windows filename access
restriction, also recorded in the earlier implementation status. Its committed
copy is preserved; the deletion is not published. CI now calls the separately
verified `scripts/test-bundle-capability.ps1`, which works with a native ZIP and
checks cleanup remains inside its generated temporary fixture.

The companion Android source is reviewed separately. Its full JVM suite passes
124 tests, debug/instrumentation APKs build, and lint has zero errors plus the
same 17 baseline warnings. Its draft-restoration owner check was reproduced
failing and fixed. Physical-device and live-workstation acceptance remain pending.
