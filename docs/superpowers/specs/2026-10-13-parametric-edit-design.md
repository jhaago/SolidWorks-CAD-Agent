# Checkpointed parametric editing

The first edit changes exactly one approved driving dimension on an agent-created,
verified, saved parametric plate or plate-with-hole. Roles are width, length,
thickness, holeDiameter, holeX and holeY. Hole roles require a hole source.
Imported files, multiple changes and chained edit results are outside this slice.

## Architecture and alternatives

Use a copy of the last verified saved source, registered under a fresh child model
identity. Capture a saved checkpoint of that child before changing a dimension.
Resolve the dimension through captured owner references and independently verified
role semantics, rebuild, then verify all parameters and physical geometry before
and after saving to a new path. Preserve the original source bytes throughout.

Editing the original with undo risks losing the original if COM or undo fails.
Replaying a creation recipe produces another model rather than editing existing
features. Copying and checkpointing existing features gives bounded ownership and
recovery while preserving the source.

## Approved plan

Retain planVersion 2. A new optional parametricEdit object identifies the source
job/revision/model/model-revision, configuration, source path, file SHA256 and
source plan SHA256. It also contains the normalized source creation plan and the
single role/value change. The top-level steps are ForkParametricPart v1,
SetDrivingParameter v1, SavePart v1. The expected result is derived from the
source recipe by changing exactly the selected parameter and save path; source
sketch IDs remain valid within the new model namespace. Creation plans retain
their existing meaning. Edit candidates are constructed by the Host from local
verified evidence, never by a provider. Explicit syntax is:

`V2 parametric edit: job <GUID>; parameter width = 150 mm; save as edits/width.sldprt`

Source jobs must be real, validated, ReadyForReview or Completed, with current
successful managed-save and complete native/saved parameter and geometry evidence.
Approval binds the whole plan and immutable source identifiers/hashes. Recheck
the source and current approval immediately before copying and before mutation.
All edits require fresh explicit approval even in AutoMode.

## Recovery and publication

SQLite schema 8 adds an edit-attempt journal with checkpoint identity/path/hash
and durable states Prepared, Checkpointed, MutationStarted, Applied, Restoring,
Restored, Published and Uncertain. Persist MutationStarted before native mutation.
Never replay an interrupted or ambiguous mutation. On failure, close only the
owned copy, reopen its exact checkpoint and verify the complete baseline before
recording Restored. If this cannot be proved, record Uncertain and retain the
checkpoint. Startup recovery records interrupted attempts as Uncertain and fails
their jobs; it does not invoke COM or automatically retry. Cancelled work uses an
uncancelled recovery token. Failed/uncertain/restored edits publish no artifact.

Successful output uses a fresh relative .sldprt path with overwrite false. Saved
readback must prove identity, configuration, all captured parameter roles, fully
defined sketches, rebuild health, one body, bounds, volume and file hash. Download
requires the current Published journal plus exact saved evidence. Hash or revision
changes invalidate access. Keep the source untouched and report child lineage.

## Validation and delivery

Contract tests cover all six roles, unsafe geometry, malformed JSON, aliases,
unknown fields, recursive edits and source/expected-plan tampering. Persistence
tests cover transactional journal transitions, schema7 upgrades and interrupted
recovery. Native tests prove width/thickness/hole edits, source hash preservation,
rollback after a deliberate verification failure, and withheld artifacts.
Build and unit suites run with installed interop and interop disabled. One fresh
whole-branch reviewer checks the final change; GitHub receives a stacked draft PR
against feature/parametric-hole. No merge, release or installed service upgrade.
Physical-phone testing remains deferred. The user already authorized planning
and implementation of this and the following capability without another gate.
