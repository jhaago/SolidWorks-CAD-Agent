# Checkpointed parameter edit verification — 13 October 2026

Branch `feature/parametric-edit` builds on hole commit `d74662d`.
The explicit edit prompt changes one driving parameter of a verified agent-created
plate or plate with one hole. It creates a child model, preserves the source,
and always requires current-plan approval, including in Auto Mode.

Example: `V2 parametric edit: job <GUID>; parameter width = 150 mm; save as edits/width.sldprt`.
Supported roles are width, length, thickness, holeDiameter, holeX and holeY;
hole roles require a source with a hole. Imports, chained edits and multiple
simultaneous changes are outside this increment.

## Behavior and recovery

The three approved steps fork the verified source, set one driving parameter,
and save to a new filename. Source identity, complete creation-plan hash,
file hash and latest verification are rechecked before execution.
A saved child checkpoint and complete native role captures are committed before
mutation. Durable MutationStarted and Saving states prevent replay across COM
ambiguity or restart. Schema8 migration is transactional and future versions fail closed.

Native dimensions are resolved through managed owner references and independently
inspected semantic roles. All original constraints, driving values, hole geometry,
body count, bounds and volume must match the derived expected part. Saved output
is reopened and independently inspected before Published enables downloading.
Failed latest evidence revokes artifact authorization. Failure restores and
verifies the exact checkpoint; inability to prove restoration marks the child
Uncertain and retains its checkpoint. The source is preserved in either case.

## Verification

- Post-review full unit suite: 762/762 in each native-enabled and disabled-interop build,
  zero skips (`review-units-native.trx`, `review-units-disabled.trx`).
- Separate native recipes: 3/3 real SOLIDWORKS2020 cases for width, thickness and
  hole diameter; source hashes, fresh child identities, replay rejection and
  exact checkpoint restoration verified (`task2-native.trx`, 1m51s).
- Final native acceptance: 10/10, zero skips, 5m32s
  (`final-native-acceptance.trx`): three edit cases, three plate regressions and
  four hole regressions. Edits cover width and holeX, mandatory approval,
  verified child downloads, stale-source denial before reservation, and a
  deliberately corrupted saved observation causing verified restoration and
  artifact denial. Failed latest child evidence also revokes downloads.
- Native Release solution/package build: zero warnings and errors. Windows zip
  validation, false-native capability rejection and file-lock regression pass.
- The legacy bundle regression fixture initially failed because its native zip
  still contained both interop DLLs. It now removes swconst deliberately and
  confirms the expected rejection, using the existing capability regression's
  guarded cleanup. Watched RED then GREEN; product binaries unchanged.

Package: `artifacts/parametric-edit/review-bundle/SolidWorksCadAgent-Windows-Test-Bundle.zip`.
SHA256: `1EECC056F7D844B163DBAC7176D7E23A7D555176EE64E1BF242FA2A6CA20DA6F`.
Post-review native evidence: `review-native-edits.trx`; fourteen retained fixture roots and28 model records have zero registered hash mismatches (`artifacts/parametric-edit/native-artifacts-post-review.json`). A preliminary boundary run passed4/5 before SOLIDWORKS exited with RPC0x800706BE during the last fixture, before its intended fault injection; the journal marked that incomplete attempt Uncertain. The crash cause is not established. After restarting and adding cleanup of test-owned source documents, the full14-case native run passed. Final native Release rebuild and all bundle validation/capability/file-lock regressions pass. Native fixtures and test evidence are retained locally; SOLIDWORKS PID14828 is left responding.
Physical phone testing, live cloud/network acceptance and installed-service
upgrade acceptance remain deferred. No Android changes were required for the
existing generic V2 three-step approval display.

## Independent review

One fresh whole-branch reviewer found zero Critical, two Important and zero Minor findings. Both entered one material fix pass. Authorization is now checked through a trusted, non-persisted Host callback immediately before native source copying and immediately before SetSystemValue3, after baseline inspection and role resolution. Recovery now accepts an empty document context for the exact stored execution and child; it rejects private filename/child identity conflicts and foreign bound contexts, and preserves unrelated open documents. Five controlled native cases reproduced both findings before the fixes: source revocation before copy/mutation, edit cancellation before mutation, cancellation after close, and failed reopening. A sixth case additionally invalidates the latest source evidence at the immediate native mutation guard. Both findings are fixed. The final affected run passes14/14 native cases with zero skips in7m24s: six authorization/recovery boundaries, five edit-and-restore recipes, and three approval/download/saved-readback cases. All six supported roles have native edit evidence. The full unit suite passes762/762 in both interop modes. No second review was dispatched.

## Rulings

1. Proceed through design and inline implementation under standing explicit user authorization — user authorized this and following capability without repeated permission — cost if wrong: design assumptions may need revision, isolated branch remains reviewable.

2. Native worktree tool cannot operate from the non-repository conversation root; manual sibling worktree follows established workspace convention — native tool returned Not a git repository — cost if wrong: app cannot manage this checkout lifecycle automatically.

3. Add Saving and Saved journal states — save is a separate COM side effect and must be fenced against replay before post-reopen publication — cost if wrong: two additional persisted states and migration compatibility.

4. Use dedicated edit observation/artifact classes instead of weakening creation mutation provenance — derived creation geometry is reused, complete edit-plan hash remains the authorization identity — cost if wrong: verification drift across two paths, pinned by native/legacy regression tests.

5. Restore an already closed child using stored execution/child identity and an empty matching document context — no conflicting child/private filename may be open; unrelated documents made active by SOLIDWORKS after the owned close are preserved while the restored child opens — cost if wrong: a conflicting context is marked Uncertain rather than risking a foreign close.

6. Imported/chained/multiple edits remain excluded — the first increment deliberately changes one parameter on verified creation jobs — cost if wrong: users need separate supported jobs or a later capability.

7. Zero/negative hole offsets and broader geometry remain excluded — inherited validated creation restrictions stand — cost if wrong: otherwise valid CAD designs require future geometry support.

8. Physical phone operation remains deferred by the user — generic V2 presentation remains unchanged — cost if wrong: device-specific approval issues await phone testing.

9. Installed-service upgrade, release and actual-installation migration remain deferred — deliver a reviewable draft and native test bundle under existing authorization — cost if wrong: deployment-specific migration faults await installation acceptance.

10. Other SOLIDWORKS versions/configurations remain unverified — acceptance evidence is installed SOLIDWORKS2020 SP0 — cost if wrong: compatibility failures on other installations.

11. Every COM boundary and ambiguous native outcome cannot be proven by static review — add targeted copy/mutation revocation, cancellation-after-close and failed-reopen tests; durable no-replay and Uncertain recovery remain the fallback — cost if wrong: untested boundary faults can require manual checkpoint recovery.

12. New-edit equal-value/swap/foreign-native adversarial execution was not rerun independently — existing semantic owner/attachment inspectors and earlier hole adversarial evidence remain applicable — cost if wrong: an edit-specific native reference regression may remain undetected.

13. Length and holeY edit evidence was absent from review — add native edit/restore recipe cases for both; all six parameter roles then have native edit evidence across recipe and Host runs — cost if wrong: behavior on other geometries/configurations remains bounded by scope.

14. Hostile local interference, mid-operation tampering and machine/power failure remain without live fault injection — hash checks, journal no-replay and startup Uncertain recovery stand — cost if wrong: manual recovery may be required under external interference.

15. Reviewer did not rerun tests or packaging — implementer recorded real executions and retained TRX/logs/fixture hashes — cost if wrong: independent runtime reproduction remains weaker than a second operator's acceptance.

16. Review covered d761213 plus 9b4b4ba test delta; subsequent fixes are verified by targeted RED-to-GREEN and full suites without a second reviewer — executing-plans requires one review and one material fix pass — cost if wrong: fix-specific blind spots rely on tests rather than another reviewer.

## Deferred minors

None.


