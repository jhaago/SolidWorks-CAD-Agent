# Current state review — 11 October 2026

## Submission recovery followup — 12 October 2026

Product commit `7a52698` adds optional `clientSubmissionId` support across Host
and the authenticated RemoteAgent. A phone UUID becomes the job ID. SQLite
schema 7 atomically binds it to an immutable fingerprint of the initial trimmed
prompt and validated image input. Matching repeats return the current persisted
receipt; changed input or a legacy ID returns `409 SUBMISSION_CONFLICT`.
Only the atomic creator plans. Full queues, terminal jobs, Host restart-required
settings and changed planner capabilities do not prevent receipt recovery.
Health/status advertise `clientSubmissionIds`; omission preserves older clients.
Approval, STA execution, owned-document checks and native fail-stop remain intact.

Validation: full Release build has zero warnings/errors, and 710 unit tests pass
in both interop modes. SOLIDWORKS 2020 startup passes 1/1 and native V2 acceptance
passes 9/9 with no skips. Native results and nine retained model hashes are in
`artifacts/submission-recovery/`; see the native verification followup.
The native-capable bundle at `artifacts/submission-bundle/` passes positive and
negative capability checks and file-lock regressions. Its SHA-256 is
`9FA24594F17279DEE422442D18E6652B0C5CF2C0AECF58D7D77E710653E1F82C`.
Hosted Windows checks passed on the product commit:
https://github.com/jhaago/SolidWorks-CAD-Agent/actions/runs/38189437795

Independent review found no material Host/proxy defect and three Android lifecycle
issues; their regressions were reproduced and fixed in the companion repository.
An extra route regression using two different valid image fixtures remains a
minor coverage followup; input-hash byte distinction and image route validation
are already tested. Current hosted companion results are recorded in draft PR 2.
Physical-phone/cloud/network acceptance and merge/release remain deferred.
The original review below is retained as historical evidence.

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
- Combined source: 698 unit tests passed with native interop enabled. Full solution
  Release build passed with zero warnings/errors; integration fixtures compiled.
- Final compile-only run: all 698 unit tests passed, including the clarification
  and constrained-screen regressions, recorded in
  `artifacts/review-results/final-screen-disabled.trx`.
- Windows bundle built and validated with `NativeSolidWorksInterop`. Negative
  checks rejected a missing RemoteAgent and a native claim missing an interop DLL.
- Regression tests first reproduced missing V2 steps, lost intake/completion,
  completed-job cancellation and the blocked V2 clarification action.
- Desktop render fixtures passed and the minimum-size combined layout was visually
  inspected. This does not replace physical DPI/screen-reader acceptance.
- GitHub's smaller Windows screen reproduced an interpretation field only one
  pixel tall. The workspace now retains its scaled minimum content size inside
  a scrollable viewport. A local regression failed first, then passed; it also
  verifies scrolling reaches the submit action. The constrained render was
  visually inspected and the final bundle rebuilt and validated.
- Independent read-only backend and final desktop integration reviews completed.

Evidence: `artifacts/review-results/`, `artifacts/review-captures/`,
`final-screen-native-build.log`, `final-screen-native-tests.log`, and
`bundle-screen-review.log`.
Local logs/artifacts are excluded from source commits. NuGet vulnerability lookup
was disabled for offline local builds; ordinary build/CI commands retain auditing.

## Boundaries and local-only work

Subsequent authorized native verification passed launch/attach, both exact
semicircle/four-cut cases and all nine current V2 prismatic acceptance cases on
SOLIDWORKS 2020. The earlier startup failure did not reproduce outside the
restricted execution sandbox; its exact cause is not established. See
[the followup native report](native-verification-2026-10-11.md). No user document
or installation was changed. Cloud planning and
paired physical-phone execution, photos, artifact saves and network transitions
were not exercised. The user deferred physical-phone testing; no Android
device/emulator was connected or available locally.

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
