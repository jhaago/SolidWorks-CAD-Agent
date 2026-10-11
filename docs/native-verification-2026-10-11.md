# Native verification after the current-state review

Verified on 11 October 2026 against source commit `42d61ab`, built in Release
with native interop enabled, and installed SOLIDWORKS 2020 SP0 (revision 28.0.0).
The user authorized native recovery/verification and deferred physical-phone
testing. No installed application, licensing service, registration, security
setting or user configuration was changed.

## Startup investigation

No SOLIDWORKS instance was running before the check. COM registration points to
the installed, validly signed executable. The main FlexNet services were running;
the demand-start licensing service was stopped. There was no matching recent
application crash event or archived crash report. The retained SOLIDWORKS crash
reports date from November 2025 and concern a different exception, so they do not
explain the earlier October 2026 breakpoint dialog.

A single lifecycle test outside the restricted execution sandbox passed in
29 seconds: launch, dedicated-STA attachment, visible session, SOLIDWORKS version
and second-session attachment. The previously reported startup failure did not
reproduce. This establishes a healthy current session; it does not prove the exact
cause of the earlier failure or certify repeated cold starts. Native tests must
run in an ordinary interactive user session outside the restricted sandbox.
The test-launched SOLIDWORKS process was retained for the acceptance tests.

## Native acceptance

The targeted exact-segment cases passed **2/2** in 62 seconds:

- `RectangularPlateWithSemicircleCut_ApprovedJobVerifiesContourAndReopens`
- `RectangularPlateWithFourDisjointCuts_ApprovedJobVerifiesThroughAndBlindDepthsAfterReopen`

The entire current `V2PrismaticAcceptanceTests` suite then passed **9/9**, with
zero failures/skips, in 3 minutes 16 seconds. The targeted cases are included in
those nine, rather than nine additional distinct cases.

Coverage includes offset circular, rotated slot and hexagon bosses; asymmetric
boss/through-cut/blind-pocket jobs on Top, Front and Right planes; a Front-plane
semicircle boss; the line/arc semicircle cut; and a plate with four disjoint cuts.
The real Host coordinator and native Bridge verify physical volume/bounds,
body count, rebuild status, cut definitions and plan-derived profile segment
geometry before saving and after managed reopening. Persisted sketch references
resolve after reopening. Approval occurs explicitly in the controlled fixtures.
Fixed test planners are used: no OpenAI/provider request was made.

Nine nonempty native `.sldprt` files and their job databases remain in unique
`C:\SolidWorks-CAD-Agent\Workspace\capability-tests\v2-*` fixture directories.
Each fixture closes only its owned model and preserves an unrelated original
document. No production CAD document was used or overwritten.

Local evidence (excluded from Git):

- `artifacts/native-recovery/startup-unrestricted.trx`
- `artifacts/native-recovery/exact-segment-native.trx`
- `artifacts/native-recovery/v2-prismatic-current-native.trx`
- `artifacts/native-recovery/native-artifacts.json` — test names, paths, sizes
  and SHA-256 hashes for all nine retained native models.
- `startup-unrestricted.log`, `exact-segment-native.log`,
  `v2-prismatic-current-native.log`

Reproduction after a native-enabled Release build:

```powershell
$env:SOLIDWORKS_EXPECTED_YEAR = '2020'
# Requires no existing SOLIDWORKS instance; it launches and retains one.
dotnet test tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj --no-build -c Release --filter 'FullyQualifiedName~SolidWorksSessionTests.Lifecycle_ClosedToLaunchToAttach_ReportsVersionAndVisibleSession'
$env:SOLIDWORKS_RUN_REFERENCE_TESTS = '1'
# Requires the healthy, attachable SOLIDWORKS instance.
dotnet test tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj --no-build -c Release --filter 'FullyQualifiedName~V2PrismaticAcceptanceTests'
```

## Remaining acceptance

Hosted CI on the documentation-only followup also exposed an inconsistent
WinForms test-host failure: one runner aborted with `Error creating window
handle` after 695 passing tests, while another runner on the identical commit
completed all 698 tests and package validation. A fresh local Debug compile-only
run with runner/sequence diagnostics passed all 698 tests in 27 seconds and did
not reproduce it. This is not evidence that a product defect was fixed. CI now
collects test-run sequence information (`--blame`) and always uploads unit TRX
and available sequence reports so a future abort retains the unfinished test
identity. It does not collect process memory dumps.

These results supersede the native-startup blocker in the current-state review
for the tested session and named V2 cases. They do not establish production
deployment, cloud planning, physical-phone pairing/photos/input, network
transitions, or a phone-to-native-artifact download. The user explicitly chose
to continue without a physical phone for now. Those device-dependent checks
remain deferred; the source review PRs remain drafts.

Submission recovery still reconciles uncertain receipts by prompt text. The next
development phase is an agreed durable submission identity across Android,
RemoteAgent and Host, with exact-ID recovery and no replay of native mutations.
