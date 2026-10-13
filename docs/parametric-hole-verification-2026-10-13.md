# Dimensioned hole verification — 13 October 2026

Branch `feature/parametric-hole` builds on verified plate commit `b5b050d`.
The bounded ten-step plan adds AddCircle v2 on a separate Top Plane sketch and
the existing through-all CutExtrude v2. Schema7, plate roles and older operation
meanings are preserved. The deterministic explicit prompt requires current-plan
approval, including in Auto Mode, and does not call a planning provider.

## Scope and independent evidence

One new plate and one hole with positive X/Y offsets. Six native driving
dimensions: AgentWidth, AgentLength, AgentThickness, AgentHoleDiameter,
AgentHoleX and AgentHoleY. Hole bindings capture the circle and three native
dimension owners in the second sketch namespace. Inspection verifies native
diameter/horizontal/vertical types and actual circle/centre/origin relation
attachments. Equal numeric values cannot substitute for native role evidence.

The existing complete bounds/body/volume/cut checks remain mandatory. Plate and
hole observations must pass before save and after exact managed reopening.
V2SavedParametricHole is bound to the complete plan hash, model/revision,
canonical path and saved file hash alongside geometry and plate evidence.
Missing, failed or mismatched latest evidence blocks artifact delivery.

## Verification record

- Contract4 and intake3 tests cover valid scope, malformed/nonfinite/tangent/outside
  geometry, version combinations, role separation, no provider call, mandatory
  current approval and simulation/unsupported rejection.
- SQLite4 tests exercise four-role atomic commit, foreign/missing/duplicate/wrong
  names/owners/values, forced journal failure rollback, generic bypass and no replay.
- Pure verifier3 tests require actual circle geometry and all three driving dimensions.
- Complete unit suite:741/741 in both native-enabled and disabled-interop modes,
  no failures/skips (`task4-units-native.trx`, `task4-units-disabled.trx`).
- Native recipe3/3 on installed SOLIDWORKS2020 SP0: driving diameter/X/Y regenerate
  actual circle geometry, equal-valued X/Y name swaps fail, and solver-disabled
  failure restores temporary preference/manager/selection state.
- Initial native Host3/3: asymmetric and equal-valued saved parts retain all six
  parameters; a post-managed-reopen missing HoleX name preserves71497.345175mm3
  geometry but produces Failed, failed saved-hole evidence and artifact409.
- Final native9/9, no failures/skips,9m28 (`task4-native.trx`): hole Host3, hole
  recipe3 and affected plate Host3. Saved copies regenerate diameter8→12 and
  position(20,10)→(25,12), retaining all plate parameters and source hashes.
  Equal diameter/X/Y20 rejects both X/Y and diameter/X name swaps. Missing hole
  names and duplicate managed IDs fail closed. After raw copy probes, the exact
  immutable managed source is restored before collision/inspection checks.
- Retained asymmetric/equal source identities and matching registered file hashes
  are in `artifacts/parametric-hole/hole-native-artifacts.json`.
- Complete native Release solution/bundle build:0 warnings/errors. Bundle
  validation, rejection of false native claims and file-lock checks pass.
- Independent review and GitHub CI are being finalized; update before publication.

Logs/TRX and retained model identities/hashes belong under ignored
`artifacts/parametric-hole`. Native fixtures create only unique owned documents,
preserve immutable source hashes, and restore the saved managed fixture after raw
copy probes before testing production model-ID collision rejection.

## Boundaries

This certifies a bounded creation/readback path on the installed native runtime.
Existing-model editing, signed/zero offsets, multiple holes, patterns and general
constraints remain future work. No new release, installed service or Android
workflow is included. Physical phone, live cloud/network and installed upgrade
acceptance remain deferred. The branch will be a stacked draft PR; no automatic merge.
