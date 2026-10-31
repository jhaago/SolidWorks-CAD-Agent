# PC Agent V2: expanded native CAD

Date: 2026-10-06
Authorization: user explicitly approved PC desktop completion followed by V2 design and development without further design checkpoints. Android is out of scope.

## Outcome
Make the Windows agent useful for a wider range of native editable mechanical parts, building on the validated SOLIDWORKS Premium 2020 SP0.0 baseline.

## Delivery sequence
1. Desktop completion: persistently browse jobs, load full snapshots on selection, submit clarification/change instructions against the displayed revision, keep cancellation available and reject stale approvals.
2. V2 sketch foundation: lines, arcs, entity identities scoped to the owned document/sketch, dimensions and relations, sketch definition inspection. First acceptance: a dimensioned bracket profile extruded to a solid.
3. Finishing and inspection: bridge-issued edge/face references, constant-radius fillets, distance-angle chamfers, dimension inspection and named view captures. First acceptance: finished mounting block with explicit selected edges.
4. Repeated and rotational features: linear and circular patterns, revolve, multiple holes and bolt-circle composites. First acceptance: flange with repeated holes and a turned spacer.
5. Controlled native revisions: inspect a workspace part, select named dimensions/features, edit a working copy, rebuild and verify before saving a separate result. First acceptance: modify plate thickness and hole diameter while preserving an unrelated open part.
6. Advanced single-part features: shell, Hole Wizard, sweep and loft delivered individually with their own schemas and acceptance fixtures. Ambiguous thread standards, paths or section correspondence require clarification.

## Architecture and constraints
Keep Desktop, Agent Host, validated command registry and STA SolidWorks Bridge separated. Keep the Host on loopback. Use explicit millimetres/degrees at interfaces and metres/radians at COM calls. No Android changes, arbitrary macros, arbitrary desktop actions or unvalidated AI code. Assemblies and drawings are later milestones after single-part coverage.

New commands require strict schemas, runtime availability, simulation behavior that never claims real CAD creation, planner exposure and native acceptance evidence. An installed API signature must be inspected before using it. Stable references expire when the owned document/sketch/topology changes; stale or ambiguous references fail before mutation.

Approval is bound to a persisted revision; clarification never executes geometry. A plan containing unsupported operations fails validation. Cancellation prevents subsequent commands. Workspace containment, overwrite protection and document ownership remain enforced.

## Testing and release evidence
For every capability run contract/validation tests, meaningful execution failure tests, native rebuild/geometry checks and save/reopen checks. Keep the original plate acceptance as a regression gate. Separate compile-only, simulated and real SOLIDWORKS evidence. V2 is incremental: implemented commands and pending milestones are reported separately.