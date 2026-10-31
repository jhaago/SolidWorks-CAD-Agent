# SOLIDWORKS 2020 verification increment

The user's investigation/integration brief authorizes independent implementation. Baseline:
`origin/feature/parametric-edit`, `e3a909df3478ae45abe5977c28708e1f4cb8607d`.
This includes the image-intake, dimensioned-plate, dimensioned-hole and checkpointed-edit work.

## Decision

Retain the .NET Framework 4.8 Host/Core/Contracts/Bridge/Desktop architecture and the
SOLIDWORKS 2020 execution boundary. Neither external server is a drop-in replacement:
Project A targets .NET 10 and tests SOLIDWORKS 2025; Project B uses Python/pywin32 and
contains SOLIDWORKS 2026-specific workarounds. Directly importing their hosts adds a
second owner of the same COM application and bypasses existing approval/reference rules.

Independently implement the useful verification pattern from A: measure material
before and after each shared boss/cut operation. Reject a no-op, wrong-sign material
change, unavailable measurements or rebuild errors. Evidence is returned in the existing
command result. Read every solid, including hidden bodies. No material density assumptions.
Retain the existing checkpointed edit/recovery path; never claim an unperformed rollback.
On a post-mutation failure, stop the job, return explicit possibly-partial state and
retain the document for inspection. Do not automatically delete features by name.

Inspect absorbed sketches and other subfeatures as well as top-level features, with
depth/node limits and cycle detection. Report parent/depth as additive fields. Duplicate
references in the tree are emitted once; actual cycles and exceeded bounds fail inspection
instead of returning truncated evidence as complete. Keep all COM calls inside the existing STA.

## Boundaries

No new production framework, API version, cloud call, MCP listener or modelling permission.
No arbitrary VBA, executing downloaded scripts, uploading CAD, assembly/drawing capability
advertisement, generic constraints or unsafe automatic retry. Existing named driving dimensions
and precise bounds remain authoritative; material change is an additional coarse check,
not proof of requested shape, hole placement or engineering fitness.

## Verification

Test material arithmetic, invalid/overflow measurements, no-op/wrong-sign changes, empty
parts, multiple solids, and bounded traversal of duplicate/cyclic/deep graphs. Link the
same production sources and unit tests into a portable .NET test project. Keep production
targets at net48. Build and run the Windows suite separately; native SOLIDWORKS checks
must remain explicitly opt-in. Supply a synthetic mounting-plate/hole/edit acceptance
procedure that reuses existing parametric workflows and checks saved/reopened models.
