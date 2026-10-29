# Licensed open-source reuse pass

The user's standing brief authorizes independent implementation; the latest instruction
asks to copy everything useful from the two reviewed projects. Preserve SOLIDWORKS 2020,
net48, existing approval, document binding, references and checkpointed edits.

## Approach

Port useful self-contained components into our architecture, retaining full MIT notices
and pinned source provenance. Whole-server replacement would duplicate the COM owner and
lose existing persistence/approval contracts. A vendor source dump without consumers would
inflate the repository without accelerating the agent. Prefer compiled, tested ports with
real callers. Keep a manifest of included and deferred subsystems.

First reusable batch:

1. A's Vec3, SOLIDWORKS row-vector transforms, axis parsing and edge-chain ordering.
   Adapt modern .NET syntax/runtime dependencies to net48 and strengthen invalid-input checks.
2. A's conjunctive geometry filtering, signed-normal versus unsigned-parallel behavior,
   deterministic ordering and exact expected-match cardinality. Expose a read-only
   InspectTopology bridge command using existing bound-document STA execution. Gather
   planar/cylindrical faces and line/circle edges with 2020 API calls; include hidden solids.
   Query results are inspection evidence, not cross-request entity handles. Selection of
   native objects stays inside a future immediate STA operation, never by first-match index.
3. B's named constraint-graph validation and relation-code mapping, ported from Python to
   C#. Add arity/entity/endpoint validation, budgets, finite dimensions, units and explicit
   Fix opt-in. Register a COM-free ValidateSketchConstraintGraph tool. This is design
   preflight, not a solver or proof of full definition. Existing native recipes stay intact.
4. B's inventory-difference approach to detect unexpected feature additions/removals/type
   changes during checkpointed parameter edits; attach structured evidence and fail on changes.
5. A's OLE message-filter pattern: bounded retry of SERVERCALL_RETRYLATER refusals only,
   on the existing STA, with caller cancellation and prior-handler restoration. This does
   not retry accepted/failed mutations or add an in-flight COM timeout/cancellation claim.

No new planner mutation capabilities are advertised before native acceptance. No arbitrary
Python/VBA, GPU runtime, model weights, newer host runtime or assembly/drawing host is copied.
Those parts remain in the reuse manifest with concrete prerequisites, not silently abandoned.

## Verification

Port and expand pure mathematical/graph regression cases. Native read-only topology fixtures
must check expected six faces/twelve edges of a box, normals/lengths, hidden solids, bounds,
count mismatch and no sketch/selection/dirty-state mutation. Extend existing native edit
fixtures for inventory preservation. Build solution and full Windows unit project; run
portable tests locally and full Windows CI after publication. Native COM build/run remains
an explicit pending gate because this environment has no SOLIDWORKS installation.
