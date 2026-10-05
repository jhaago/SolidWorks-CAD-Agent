# V1 hardening and next Windows test session

Branch: `feature/v1-solidworks-2020`. Do not merge to main.
Baseline: `cc5a11a` (includes the previously changed single-ended boss, cut direction and camelCase acceptance assertions).

## Implemented

- OpenAI failures retain the existing structured `OPENAI_HTTP_ERROR` envelope and now include upstream `httpStatusCode`, `openAiErrorCode` and `openAiErrorType`. A bounded, sanitized message reaches Desktop. Malformed responses retain the HTTP fallback; credential/header assignments are suppressed, configured credentials and key-shaped strings are redacted. Raw upstream bodies, transport exceptions and JSON exception details are not exposed. Provider timeouts have a safe `OPENAI_REQUEST_TIMEOUT` diagnostic. Request Changes receives the same structured planning-error handling as initial creation.
- `POST /jobs/{id}/complete` uses the existing state machine and compare-and-swap persistence. Only ReadyForReview can become Completed. It returns the full job snapshot. Other states and repeated completion return 409; missing jobs return 404. Completed remains terminal and is excluded from the existing active-job query, so it stops blocking execution-mode changes. Host restart is still required to activate a changed execution mode.
- Desktop has an **Accept / Complete** button for ReadyForReview. Job actions are disabled during an action and for terminal jobs. Host connection checks retry at 2, 4, 8, 16, then 30 seconds; healthy checks occur every 30 seconds. Each probe has a five-second cancellation deadline. Only read-only health/status probes retry; CAD writes are never automatically replayed. Form closure cancels the monitor. A delayed SOLIDWORKS status probe does not mark a healthy Host offline. Malformed Host error bodies cannot crash reconnect handling.
- The bundle builder checks relevant existing output trees for locked/loaded `SQLite.Interop.dll`, gives process names and PIDs when available, and wraps replacement/removal/build failures with instructions to close Agent Host/Desktop and check permissions. It never kills processes. A PowerShell lock/release regression runs in CI.
- JSON regression tests pin exact camelCase bounding-box names and nested inspection feature properties.
- Native plate acceptance now verifies physical hole evidence before saving and after closing/reopening/rebuilding: a single solid body; 100 × 60 × 10 mm dimensions; successful rebuild and no feature errors; native boss and cut; two actual circular inner loops on opposite outer planar faces; Ø20 rim diameter and centred location; one full cylindrical wall; seven fixture faces; expected cylindrical area and removed volume. Dimensional assertions allow reference-plane orientation to permute global axes.

The hole collector and evaluator are test utilities, not new CAD commands. Native collection uses explicit SOLIDWORKS COM interfaces on the session STA. The integration project detects/references installed interop assemblies like the bridge; compile-only builds cannot run the native collector. No fillets, arbitrary face selection, assemblies, patterns or additional sketch capabilities were added. The existing native feature-command geometry changes were not modified in this pass.

## Verification performed here

This is Linux without SOLIDWORKS. None of the native acceptance tests was executed or passed here. The interop-enabled collector has not been compiled here either; that native build check remains for Windows.

- Compiled all five production projects and both test projects against .NET Framework 4.8 reference assemblies using Roslyn, in compile-only/no-interop mode: no warnings or errors in the final compile.
- Ran 43 portable cases against the real source in a temporary .NET 8 harness: 43 passed, 0 failed. This covers OpenAI diagnostics/redaction/fallback/timeout, state transitions, client errors, retry/recovery/no-write-retry, camelCase contracts and the fixture evidence evaluator.
- Ran every unit-test case with a temporary reflection runner under Mono, using the pinned managed SQLite library and the official 1.0.119 native SQLite source built for Linux: 149 passed, 7 failed. This is compatibility evidence, not a passing Windows MSTest result. The runner and Linux native library are scratch-only and are not shipped or added to the repository.
- The seven remaining failures are existing Windows-path tests, listed below. Their Windows drive/backslash expectations are not valid on this Linux runtime. New completion and mode-unblocking tests pass in the full run.
- `scripts/test-bundle-file-locks.ps1`: passed with PowerShell 7.4.6, including failure while the fixture DLL is locked and success after releasing it.
- `git diff --check`: passed. Independent review identified a malformed Host error-body crash; failing tests reproduced it and the fix passed. Final scope review found no new CAD capability.
- Test-first evidence: OpenAI detail tests failed against the old generic HTTP message; timeout and malformed-error tests failed with the expected unstructured exception types; fixture positive cases failed before its evaluator was implemented. Completion regressions also failed with 404 against an isolated copy of the pre-fix route. Reconnect tests first exposed the missing monitor/client API, then passed after implementation.

### Remaining Linux compatibility failures

1. `AgentHostCompositionTests.ExecutionModeChange_RequiresRestartBeforeNewJobsAndKeepsStartupMode` (Simulation → Real row): expected 200, actual 400 for Windows-path settings.
2. The same test (Real → Simulation row): expected 200, actual 400.
3. `WorkspacePolicyTests.ResolveForWrite_TraversalOutsideWorkspace_Throws`: Linux does not treat backslash as a path separator.
4. `AgentRoutesTests.Settings_RoundTripPersistsValidatedValuesAndRejectsModeChangeWithActiveJob`: expected 200, actual 400 for Windows-path settings.
5. `SolidWorksBridgeCommandValidationTests.SavePart_TraversalOutsideWorkspace_IsRejectedBeforeCom`: backslash traversal is not a Linux traversal path; expected workspace-policy error, actual compile-only interop-unavailable result.
6. `AgentSettingsTests.JsonStore_RoundTripsSettingsAndIgnoresInterruptedTemporaryWrite`: Windows drive paths fail settings validation.
7. `AgentSettingsTests.JsonStore_InvalidSettingsAreRejectedWithoutReplacingLastGoodFile`: Windows drive paths fail settings validation.

The integration project currently contains only three tests categorized `SolidWorksIntegration`, all requiring a live COM session. There are no non-COM tests in that project to run. HTTP/SQLite/simulation integration coverage currently lives in the unit-test project and was included in the full compatibility run.

## Publishing / Windows CI status

Published on the requested feature branch after explicit user approval: test-first commit `ef1aa8e` and implementation commit `6f537dd`. The connected GitHub app published the commits because command-line push credentials were unavailable; both published trees were verified against the local commits. No PR was created and nothing was merged.

[Windows CI run 37198403652](https://github.com/jhaago/SolidWorks-CAD-Agent/actions/runs/37198403652) passed for implementation commit `6f537dd`:

- Complete solution restore/build: passed.
- Full Windows MSTest unit suite: **156 passed, 0 failed, 0 skipped**. This includes the seven Windows-path cases that failed in the Linux compatibility run.
- Integration-test project compile-only contract: passed. No live COM tests were executed; the interop-enabled collector still needs compilation on the SOLIDWORKS machine.
- Bundle file-lock regression: passed.
- Windows test bundle build, validation and capability-validation regression: passed; bundle artifact uploaded.

The workflow does not have SOLIDWORKS installed. This CI success does not verify the previously changed native boss/cut geometry or the new physical hole collector.

## Next Windows / SOLIDWORKS 2020 session

1. Close old `SolidWorksCadAgent.AgentHost.exe` and `SolidWorksCadAgent.Desktop.exe` processes before rebuilding. If the builder reports a PID, close that instance normally. Do not auto-kill processes.
2. Fetch the published feature branch. In the Developer PowerShell environment used previously, restore/build the complete solution with the installed SOLIDWORKS interop DLLs:

```powershell
msbuild SolidWorksCadAgent.sln /t:Restore /p:Configuration=Debug
msbuild SolidWorksCadAgent.sln /p:Configuration=Debug /p:Restore=false
dotnet test tests\SolidWorksCadAgent.UnitTests\SolidWorksCadAgent.UnitTests.csproj --no-build --configuration Debug
.\scripts\test-bundle-file-locks.ps1
```

Confirm the native bridge capability; compile-only success is not native verification. If automatic discovery misses the interop directory, supply `/p:SolidWorksApiPath="C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"` to the MSBuild commands.

3. Open Desktop before Host: observe retry status, start Host, and confirm labels/actions recover within the bounded retry window. Stop/restart Host and check recovery again. Close Desktop while retrying to check shutdown.
4. In Simulation, create/approve a job, inspect ReadyForReview, click Accept / Complete, and confirm Completed. Confirm cancel/approve/complete are disabled for that terminal job. Switch execution mode after all nonterminal jobs are completed/cancelled; expect restart-required, restart Host, then confirm the requested mode is active.
5. Check a known invalid credential or unavailable-model error without displaying/copying the credential itself. Desktop should show safe status/code/type and an actionable message. Restore the valid credential afterwards.
6. Run the plate acceptance tests with SOLIDWORKS 2020 available:

```powershell
$env:SOLIDWORKS_EXPECTED_YEAR = "2020"
dotnet test tests\SolidWorksCadAgent.IntegrationTests\SolidWorksCadAgent.IntegrationTests.csproj --no-build --configuration Debug --filter "FullyQualifiedName~AcceptancePlateTests"
```

This verifies both the 100 × 60 × 10 plate with centred Ø20 through-hole and the 10 × 10 × 1 unit-scale block. The plate must pass before and after native save/close/reopen/rebuild. Investigate any COM binding, topology or geometry failure; do not weaken assertions simply because a Cut feature exists.

7. If testing the separate lifecycle integration test, first close SOLIDWORKS normally. It explicitly expects a closed starting session. Keep this separate from the plate tests, which can attach to a running session.
8. Rebuild/validate a native Windows bundle and repeat the Desktop real-part workflow. Record the result, SOLIDWORKS version/service pack and any failures on this feature branch. The boss single-ended direction, through-cut direction and the new physical hole collector all remain unverified until this session.
