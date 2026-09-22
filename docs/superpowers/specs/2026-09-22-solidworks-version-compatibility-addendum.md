# SOLIDWORKS Version Compatibility Addendum

Date: 2026-09-22
Applies to: `docs/superpowers/specs/2026-09-22-solidworks-cad-agent-design.md`

## Verified development baseline

The user's installed CAD environment is **SOLIDWORKS Premium 2020 SP0.0**. Any earlier references to SOLIDWORKS 2018 in the design are superseded by this addendum.

## Compatibility requirement

V1 is certified first against SOLIDWORKS 2020, but the architecture must not hard-code the AI, Agent Host, job model, or CAD command contracts to the 2020 API surface.

The SolidWorks Bridge must:

- detect the connected SOLIDWORKS version at runtime;
- expose a version-independent CAD command contract to the Agent Host;
- keep COM/API details isolated inside the bridge;
- prefer long-lived supported API calls where they satisfy the command;
- isolate version-specific fallbacks behind bridge adapter/capability code;
- report runtime version and capability information to the Agent Host/UI;
- fail explicitly when a command is unavailable rather than silently substituting different geometry;
- use the installed 2020 interop/type library as the compile-time source of truth for the first certified build;
- retain regression tests so newer SOLIDWORKS releases can be certified by running the same command and acceptance suite.

## Certification model

`SOLIDWORKS 2020 SP0.0` is the initial certified runtime. Later releases are considered architecturally supported targets but are not labelled certified until the automated unit suite and the SolidWorks integration/acceptance suite pass against that release.

The native CAD command protocol remains stable across certified releases. For example, the Agent Host requests an extrusion using millimetres and semantic feature intent; only the bridge decides which installed SOLIDWORKS API call/signature implements that operation.
