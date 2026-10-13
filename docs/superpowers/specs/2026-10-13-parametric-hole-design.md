# Dimensioned through-hole on a parametric plate

The user authorized this next CAD increment and its planning and implementation
after the parametric plate. This builds on tested plate commit `b5b050d`; physical
phone testing remains deferred. Execution stays inline with one final independent
review. The user explicitly waived repeated design/plan approval for this sequence.

## Outcome and scope

Create one new origin-centred Top Plane plate and one through-all circular hole.
Preserve native AgentWidth, AgentLength and AgentThickness. The hole has driving
AgentHoleDiameter, AgentHoleX and AgentHoleY, with X/Y measured in the Top Plane
sketch from its origin. First-increment offsets are positive and greater than
0.001mm; diameter is finite and greater than0.000002mm for native representation,
and the circle stays at least0.001mm
inside both positive rectangle edges. Negative/zero offsets, multiple holes,
patterns, production-model editing and arbitrary constraints remain future work.

Explicit intake:
`V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as parametric/hole.sldprt`.
Review and current-revision approval remain mandatory. Unsupported syntax or
scope fails before mutation; this deterministic intake never calls a provider.
Simulation and builds without native interop advertise the capability as false.
No Android flow change, deployment, automatic merge or release is included.

## Approach

An unconstrained legacy circle would create the shape but provide no placement
parameter guarantee. A general constraint/equation engine would expand the scope
and solver combinations substantially. Select an explicit dimensioned-circle
recipe on a second owned Top Plane sketch, followed by the existing through-all
CutExtrude v2. AddCircle v2 is new; v1 retains its existing semantics.

The exact normalized ten-step sequence is NewPart1, CreateSketch1,
AddRectangle2, ExitSketch1, Extrude3, CreateSketch1, AddCircle2, ExitSketch1,
CutExtrude2 and SavePart1. Both sketches are Top Plane. The existing circle
diameter parameter retains the reviewed diameter; native creation uses half of it
as radius. Save is a new relative
`.sldprt` path with overwrite disabled. Existing schema7, sequential reservation,
prospective revisions, uncertainty fence and no-replay behavior remain intact.

## Native creation and semantic evidence

On the owned empty second sketch, create one non-construction full circle with
inference suppressed. Restore exact dimension-input preference, AddToDB,
DisplayWhenAdded and clear selection in finally on success and failure. Create
native diameter, horizontal and vertical dimensions selecting the returned circle,
its centre and the actual origin; set/read native metre values and require driving
dimensions plus a fully defined solve state. Do not fix the circle or its centre.

Capture the actual circle token and dimension-owner feature tokens. Roles
`holeCircle`, `holeDiameter`, `holeX`, `holeY` use the existing versioned role-ID
encoding with the second sketch GUID. Keep the plate role set unchanged. Shared
capture/observation fields may be factored into CadParametricSketchObservation;
the public plate DTO and its serialized property shape remain compatible.

Independently resolve five bindings (sketch plus four roles) in the exact managed
model/configuration. Require the captured full circle to be the sole contour,
zero construction entities, no Fix, fully defined state and no native rebuild
errors. Read centre/radius from actual circle geometry. Resolve unique native names
on the captured sketch and verify their native display types: diameter, horizontal
linear and vertical linear. Their relation entities must attach to the actual
circle or its centre and origin, using actual definition/internal entity readback
and COM identity or equal nonempty persistent tokens within that model. Equal
numeric values cannot substitute for diameter/axis associations.

Atomic capture commit validates the exact approved hole intent, model, second
sketch, configuration, role IDs, owner kinds, native names, values and driving
state; insert all four role bindings and journal outcome in one transaction.
Generic commit cannot bypass parametric capture. No schema migration is required.

## Completion and artifacts

The existing independent bounds/body/volume and native through-all cut/profile
checks stay mandatory. Add hole parameter inspection alongside plate inspection
before save and after exact managed reopen. Record V2SavedParametricHole bound to
the same complete plan hash, model/revision, path and artifact hash as geometry and
plate evidence. Download requires all three latest saved verification records to
pass and match those identities. Missing, driven, wrong-axis, aliased, stale or
wrong-valued evidence withholds completion and download even when the solid is
physically correct. No command replay or name-based rebinding is permitted.

## Acceptance and limits

For120x60x10 with Ø8 at(20,10), expected volume is
`72000 - pi * 4^2 * 10`mm3, with the original plate bounds and one solid body.
Saved owned copies changing diameter8→12 or X20→25/Y10→12 must regenerate the
native hole while preserving plate dimensions, driving state and source hash.
A120x120x10 plate with diameter/X/Y all20 tests equal-valued dimension-name swaps;
native axis/type/attachment evidence must reject them. A real Host fault injected
only after managed reopen removes a required hole name while keeping geometry
correct; production inspection/recording must leave Failed and artifact409.

Verify actual SQLite rollback/ownership/no-replay and generic-commit bypass, malformed
and boundary inputs, normal current-revision approval, source identity and historical
plate/V2 contracts. Native recipe includes success and solver-disabled restoration.
Run all units in both interop modes, the affected plate native acceptance and new
hole native suite, a complete native Release bundle and its validators. Use one
fresh whole-branch review, one material fix pass, then a stacked draft PR against
feature/parametric-plate and applicable CI. Preserve both worktrees and native
fixtures. Certification applies to installed SOLIDWORKS2020 SP0; device, cloud and
installed upgrade acceptance remain unverified.

Self-review: intent, positive-offset boundary, native semantic proof, atomicity,
saved hash provenance, recovery and legacy compatibility are explicit. No general
editing engine or new credentials/dependencies are required.

