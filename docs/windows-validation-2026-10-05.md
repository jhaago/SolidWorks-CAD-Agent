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
