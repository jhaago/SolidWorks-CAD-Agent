# SolidWorks CAD Agent

AI-assisted native CAD automation for SOLIDWORKS.

## V1 baseline

The first certified runtime is **SOLIDWORKS Premium 2020 SP0.0**. The bridge is designed so SOLIDWORKS-version-specific COM/API details stay behind a version-independent command layer, allowing later releases to be certified by rerunning the integration and acceptance suite.

The first acceptance model is a native editable 100 × 60 × 10 mm plate with a centred Ø20 through-hole.

See `docs/superpowers/specs/2026-09-22-solidworks-cad-agent-design.md` and the version-compatibility addendum for the approved architecture.
