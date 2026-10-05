# Windows validation — 5 October 2026

Tested on the user's Windows PC with SOLIDWORKS 2020, starting from feature-branch commit `fa37711`.

## Changes from live testing

- Resolve Instant3D `ICE` features through `IFeature.GetTypeName()` so native cuts retain their underlying `Cut` classification. Keep other `GetTypeName2()` classifications unchanged.
- Include the returned feature tree in the plate test's cut assertion failure.
- Explicitly package both installed SOLIDWORKS interop DLLs in native Agent Host bundles. The original builder omitted `SolidWorks.Interop.swconst.dll`; the existing validator rejected that bundle.

## Verified

The user ran the tests in normal Windows PowerShell and supplied their output:

- Full Windows unit suite: 193 passed, 0 failed, 0 skipped.
- Plate acceptance: 1 passed, verifying one body, 100 × 60 × 10 mm bounds, centred Ø20 through-hole topology/volume, native boss/cut, rebuild, save/close/reopen and repeat inspection.
- Unit-scale block acceptance: 1 passed, verifying 10 × 10 × 1 mm dimensions.
- Desktop Real-mode AI plate workflow created a native part; user confirmed Accept / Complete.
- Desktop opened before Agent Host reconnected automatically after Host startup.

The updated builder was then run from a fresh output directory on this PC. Build and native bundle validation passed without manual dependency copying. NuGet vulnerability-data retrieval produced NU1900 warnings.

## Environment observations and remaining scope

- The Codex restricted execution environment returned `HttpListener.IsSupported = false` and failed one loopback HTTP test; all 193 tests passed in the user's normal PowerShell.
- Initial native attempts crashed; a SOLIDWORKS crash report named CAMWorks. Testing subsequently succeeded after the CAM startup checkbox was cleared and SOLIDWORKS restarted. This isolates an environment interaction but does not establish the add-in's root cause.
- Separate lifecycle integration and live remote-control tests were not verified in this session.
- No merge to main.

## Desktop Settings follow-up

- Settings now loads actual Host values instead of fixed placeholders. Model and execution mode are editable; Save uses the existing settings API and preserves unedited fields.
- Save displays Host errors and the required Host restart notice. Controls are disabled during requests; requests have a ten-second deadline and closing the form cancels pending requests. Writes are not retried automatically.
- The user verified Real → Simulation → Real through Desktop Settings, including saving, restart notices and persistence after Host restarts.
- The unavailable-model live check displayed HTTP 404, `invalid_request_error` and `model_not_found`; model changes require Host restart. Host stop/restart recovery and mode switching via API also passed.
- Updated full unit suite outside the restricted environment: **195 passed, 0 failed, 0 skipped**. New client tests cover settings preservation, JSON PUT and restart information, plus readable Host errors.
- Updated native bundle build and validation passed. Live native geometry was verified earlier; Settings does not change geometry commands.
- The separate automation-launch lifecycle test failed second-session attachment even after 30 seconds and a UserControl experiment. Host attached to a manually launched instance but not that test-launched instance. Diagnostic test changes remain separate and are not part of the Settings change; the cause is unresolved.

## Normal executable launch follow-up

- Read-only probes showed that the COM-activated instance published no matching running-object-table entries. A manually launched instance published its class moniker and `SolidWorks_PID` moniker, and both 32-bit and 64-bit compiled bridge clients attached successfully.
- Launch now starts the registered executable without COM activation arguments, then waits for the active COM object and `StartupProcessCompleted`. An already discoverable instance is reused. COM calls remain on the session STA; waits run asynchronously. A session launch gate prevents overlapping launches from that session.
- Automatic executable discovery uses the 64-bit `SldWorks.Application` class registration. A configured absolute executable override is passed from Host settings. Missing paths and registration failures produce launch errors. Cancellation stops waiting without terminating SOLIDWORKS. Registration/startup probes have a sixty-second elapsed deadline; a blocked probe is left on its STA and any later fault is observed. The final visibility/status call and session disposal still depend on COM responsiveness.
- Full Windows unit suite outside the sandbox: **203 passed, 0 failed, 0 skipped**. Eight added cases cover existing-instance reuse, delayed registration/startup, timeout, cancellation, blocked probes, and registry executable parsing. Independent review caught the original polling-only timeout; deadline/cancellation regressions passed after repair and re-review reported no remaining substantive findings.
- Native lifecycle test passed: startup complete, user control true, second-session attachment succeeded on its first attempt. The temporary UserControl mutation was absent. Diagnostic retries were then removed, restoring the original immediate second-session assertion.
- Both native geometry tests reran successfully against the new normally launched instance: **2 passed**, including plate save/reopen verification and the millimetre-scale block.
- Updated native bundle build and validation passed. The user verified the Desktop Launch button opened SOLIDWORKS and connected, then restarted Host while leaving SOLIDWORKS open and successfully reattached. The user was unsure about brief startup behavior; no crash dialog was confirmed.
