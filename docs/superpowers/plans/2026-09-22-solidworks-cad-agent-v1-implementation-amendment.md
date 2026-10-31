# V1 Implementation Plan Amendment — SOLIDWORKS 2020 Baseline

This amendment applies to `docs/superpowers/plans/2026-09-22-solidworks-cad-agent-v1-implementation.md` and its command-expansion companion.

The verified development environment is **SOLIDWORKS Premium 2020 SP0.0**. Replace implementation assumptions that name SOLIDWORKS 2018 with SOLIDWORKS 2020 for the first certified build.

Additional implementation requirements:

- Task 3 must capture runtime version information from the connected SOLIDWORKS instance and return it in session status.
- Add a bridge-level `SolidWorksRuntimeInfo`/capability abstraction so Agent Host and Desktop do not depend on 2020-specific COM signatures.
- The installed SOLIDWORKS 2020 interop library is the first compile-time source of truth.
- Newer releases are certified by rerunning the same integration/acceptance suite; version-specific fallbacks stay inside the bridge.
- No AI tool schema may contain a SOLIDWORKS year/version-specific method name or parameter list.

Execution environment ruling: the ChatGPT sandbox cannot run SOLIDWORKS and currently has no .NET/Mono toolchain. Unit-test RED/GREEN cycles may therefore run on GitHub Actions Windows CI, while COM integration tests remain mandatory on the user's SOLIDWORKS 2020 PC before those integration tasks can be declared fully verified.
