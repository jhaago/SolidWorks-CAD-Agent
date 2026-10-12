# Parametric plate: proposed first CAD upgrade

Status: approved by the user on 12 October 2026; implementation is not started.

## Intent and starting point

The user requested the next CAD capability upgrades after merging the Windows
and Android review branches. Physical-phone testing remains deferred. The
recommended outcome is reliable native parametric modelling, beginning with a
dimensioned rectangular plate and building toward conversational model edits.
The user approved this parametric-plate priority and design.

Baseline: Windows bb54d750f3a97d36028b068c00eaef433f586117. Existing V2 creates
one boss with supported subsequent cuts and verifies geometry/save/reopen.
Sketch references, revision approval, owned-document guards and durable execution
attempts already exist. Individual sketch entities, explicit native constraint
definitions and semantic driving dimensions are not managed today.

## Approaches considered

1. Parametric plate (recommended): introduce explicit constraints and named
   dimensions on a single owned, newly created plate. Establishes the foundation
   for width/thickness edits with independent native evidence.
2. Multiple bosses: expands shape complexity but requires new intersection/body
   verification and does not provide semantic parameters for reliable edits.
3. Face pockets and finishing: adds useful geometry, but needs verified face
   frames, direction and topology selection. Existing draft handlers are not
   evidence of supported capabilities.

## First increment

Create a new, agent-owned rectangular plate on Top Plane, centered at the sketch
origin, with positive finite width, length and thickness specified in millimetres.
Expose these as named, driving native parameters associated with the approved
model revision. Use explicit rectangle constraints and placement to fully define
the sketch, rather than relying on incidental inference or fixing every edge.
The sketch must remain a four-edge solid contour; any construction entities must
remain construction geometry and must not change the feature's contour.

The initial feature is one positive blind boss, one solid body and a final new
managed .sldprt path. Unsupported additional features or non-Top frames receive a
clear preflight rejection for this capability. Existing supported V1/V2 workflows
retain their current behavior and remain usable independently.

This increment creates and inspects parameters. It does not add a production
workflow to mutate previously saved models. Editing existing models requires a
subsequent design for checkpoints, stale-model detection and failure restoration.

## Integration and compatibility

Add an explicitly versioned capability to the existing Core operation catalog
and plan validation. Define typed parameter intent and stable semantic identities
for the required rectangle entities and dimensions. Preserve existing persisted
operation meanings; introduce explicit new operation versions/commands wherever
the contract differs. Do not reinterpret historical rectangle steps.

Host validates the complete proposed plate, binds its identities to the approved
revision, persists execution attempts and commits parameter/reference evidence
only after native readback succeeds. Bridge performs all native calls on the
existing dedicated STA and uses the shared managed-model/reference boundary.
Entity selection must resolve actual captured objects, not guessed names or
indices. Native parameter evidence must identify driving versus driven values,
units, semantic ownership and the corresponding feature/sketch.

Persist evidence in the existing versioned model/reference records where their
contracts fit. If new storage fields are needed, the implementation plan must
define their migration, compatibility and failure behavior before product edits.
No live COM objects are persisted. Publication stays capability-gated until
native acceptance passes; simulation must not advertise native solver evidence.

The existing desktop/Android review-and-approval path remains the entry point.
Android requires no new interaction in this first increment. Report the reviewed
parameter values and verified outcome through the existing plan/job evidence.

## Verification and failure behavior

Before save, independently inspect the native sketch solve state, relations,
driving dimensions and boss definition. Verify width, length and thickness from
native readback against approved intent, then verify physical bounds, expected
volume, one solid body and clean rebuild. Never derive expected values from the
same native objects being assessed.

Save to a unique managed path, reopen under the current identity guard, and repeat
parameter, reference, feature and geometry checks before authorizing the artifact.
An absent, duplicated, driven-only, conflicting or unresolved required parameter
blocks completion. Do not silently omit a constraint or substitute fixed geometry.
Uncertain native mutation follows the existing fail-stop/no-blind-replay policy.
Preserve unrelated documents and the retained healthy SOLIDWORKS session.

Native tests use deterministic planners and unique owned fixtures, without cloud
provider calls or production models. They verify a representative plate, asymmetric
width/length, constraint/dimension readback, save/reopen and incorrect/missing
parameter rejection. A controlled test-only parameter change on an owned fixture
must demonstrate regeneration and independent geometry change; this establishes
parameter behaviour without exposing a production existing-model edit workflow.

Run pure contract/preflight and persistence/reference regressions, complete unit
suites in both interop modes, native new-capability acceptance and the current
nine V2 prismatic regressions. Tests must prove that missing constraint/dimension
evidence cannot claim success. Build and validate the Windows bundle after the
capability is enabled. Physical-phone testing stays deferred.

## Completion and following increment

Done means the approved native plate has stable named driving parameters, a fully
defined intended sketch, correct physical geometry, and equivalent verified
readback after managed reopening. Unit-only or simulated results are insufficient.
Record exact test counts, source commits, retained native artifacts and limits;
update GitHub with a separate capability branch and reviewable PR.

Next after this increment: dimensioned hole placement, then checkpointed approved
parameter edits on a copy of an existing agent-authored model. Multiple bosses,
finishing, patterns and general imported-model editing remain separate increments.
