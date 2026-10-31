# Parametric plate verification — 13 October 2026

Product branch: `feature/parametric-plate`, baseline `3cf2898`, product snapshot
`39a1f0c` followed by the recorded review fixes. Installed SOLIDWORKS2020 SP0; .NET48 Release with
native interop. Physical-phone testing is explicitly deferred. No cloud planning,
installation, licensing/security configuration change, or production-model edit
was performed.

## Capability and compatibility

Explicit intake: `V2 parametric plate: 120 x 60 x 10 mm; save as parametric/plate.sldprt`.
It creates an origin-centred Top Plane rectangle with four contour edges and one
construction diagonal, intended native relations and named driving AgentWidth,
AgentLength and AgentThickness dimensions. Host-owned references identify the
actual segments, sketch and base extrusion. Dimension-owner references plus
captured local dimension names resolve after document rename/save/reopen.

The new operation versions are AddRectangle2 and Extrude3; historical versions
remain unchanged. Role bindings reuse schema7 fields with fingerprint version1.
Atomic outcome commits reject missing/foreign/duplicate captures; failed writes
roll back all new bindings and cannot authorize replay. Existing generic outcome
commit cannot bypass parametric validation. Native readback checks fully-defined
solve state, relations, driving values, contour, bodies and rebuild; independent
physical bounds/volume checks remain required. Saved parameter evidence is bound
to job/model/revision, plan hash and artifact hash in addition to geometry evidence.

## Automated evidence

- Complete unit suites:727/727 each interop mode, no failures/skips. Tests cover
  finite/representable sizes, malformed/mixed versions, identity separation,
  actual SQLite atomicity/rollback and no replay, native parameter ownership and
  values, missing/driven/duplicate evidence, explicit intake and simulation rejection.
- Native startup1/1 in32s; test-launched SLDWORKS PID22260 retained.
- Native recipe3/3: width120→150 preserves centre and length60; thickness10→12
  gives expected physical volume/bounds; a solver-disabled failure restores the
  previous dimension-input preference, manager state and clears selection.
- Initial Host saved-copy acceptance1/1 in56s: reviewed revision, explicit approval, all
  six journalled steps, save and managed reopen, three native driving parameters,
  artifact200; width/thickness edits on separate native copies give90000/86400mm3,
  missing named dimension is rejected, original artifact hash unchanged; a failed
  saved-parameter verification record causes artifact409.
- Final review-fix acceptance3/3 in3m56s (`review-verified.trx`): asymmetric and
  square plates retain native driving parameters after reopen; aliased/swapped edges,
  equal-valued swapped width/length names, missing dimensions, deleted captured boss
  and copied model identity are rejected. The actual Host negative case corrupts a
  named width only after managed reopen, preserves physical geometry and leaves the
  job Failed with failed saved parameter evidence and artifact409. Final unit suites
  again pass727/727 in29s native and28s disabled, no failures/skips.
- Existing V2 prismatic native regressions9/9, no skips: standard planes, supported
  profiles and cuts, physical geometry, reference and save/reopen verification.
- Native full Release bundle builds0warnings/errors and passes validator, negative
  capability and file-lock diagnostics.

Recipe3 and legacy9 passed in the initial combined13-case run; its Host case failed
a new base-feature classification check. The corrected Host case passed separately
with the extended saved-copy assertions. These are13 distinct passing tests across
the recorded runs, not a claim that the initial13-case run was green.

## Diagnosed failures and corrections

Relation arrays required DispatchWrapper marshaling; plain object arrays produced
a native server fault at the point-relation boundary. Premature EditRebuild3 closed
sketch edit mode before journalled ExitSketch; a native sketch-active regression
reproduced the failure and the early rebuild was removed. GUID strings in stored
fingerprints require JSON Guid deserialization rather than Value<Guid> conversion.

The installed runtime labels the first feature Extrusion: its extrusion definition
reports IsBossFeature=false and IsBase2=true. Inspection accepts that base extrusion
or a boss, with explicit blind/non-thin/one-direction definition checks, actual
driving thickness and independent positive plate geometry. Existing Instant3D
wrappers use the established underlying-type lookup.

An intermediate rerun hit DOCUMENT_NAME_CONFLICT because a prior test-owned file
was still open with the same basename. The application correctly withheld save;
fixtures now use unique native filenames and canonical paths for owned cleanup.
No filename guard was weakened. Failed fixture journals/models are retained.

## Independent review and corrections

One fresh reviewer examined the complete product snapshot and identified two
Important findings, with no Critical or Minor findings. The single fix pass adds
distinct semantic edge coordinates and dimension-to-native-endpoint associations,
including square plates where numeric values cannot distinguish width and length.
Aliased edge references and swapped square-plate dimension names each reproduced
acceptance before their fixes. Relation attachment comparison uses COM identity or
matching nonempty native persistent tokens; it checks both actual definition and
internal relation entities. SOLIDWORKS documents that distinction in
[GetDefinitionEntities2](https://help.solidworks.com/2020/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISketchRelation~GetDefinitionEntities2.html).

The Host negative test now changes a named parameter only after real managed
reopen, preserving72000mm3 geometry. Production inspection, recording and coordinator
leave the job Failed with failed saved parameter evidence and artifact409. This
passed independently before the semantic attachment correction and in the final
three-case acceptance. Test copies bypass production duplicate-ID protection only
for native parameter probes; the original immutable saved fixture is reloaded and
its hash rechecked before testing the production collision guard. In-memory effects
of unmanaged duplicated-ID documents remain outside production certification.

The reviewer declined to certify live COM lifecycle, copy/deleted native cases and
packaging/CI from source alone. Parent native and bundle checks supply that evidence;
no second review was requested. General runtime/version compatibility remains limited
to the installed2020 SP0 runtime.

The previously retained PID22260 was absent at the final attach-only rerun, which
failed all three cases before mutation in729ms. Standard lifecycle restart passed
1/1 in29s and retained PID27136; no process was terminated by this work.

## Local evidence

Ignored directory `artifacts/parametric-plate` contains contract/bridge/persistence/
verifier/intake red and green logs; task1-final, task3-units, task5-units-native,
final-disabled and final-native-units TRX files; startup, recipe-thickness,
all-native and parametric-copy-acceptance TRX files; bundle/build logs. The all-native
TRX has12pass/1fail as described above. `review-verified` records the final3/3;
`review-units-final-native` and `review-units-disabled` record727 each.
`review-native-artifacts.json` records final source paths, job/model/revision IDs
and confirms both file hashes match their registered values; native bundle is under
`bundle/`. The execution ledger is archived as `execution-ledger.md` there.

Final asymmetric fixture SHA256:
`ebb85b7bb012dedaa7741eb13d2cf0f8b8e101effc02f4a0af9981142a6bf023`.
Final square fixture SHA256:
`24289aa6b45081ba8ffe3049b8f011100919e462dba906ca114a4b60c106d494`.

Successful source fixture retained:
`C:\SolidWorks-CAD-Agent\Workspace\capability-tests\parametric-efa08fa7e2174f33b1562939bfe03764\parametric-efa08fa7e2174f33b1562939bfe03764.sldprt`.
SHA256 `3fcea7fb757b601db458233d183bad12c01fda2ddd282bfda13f5cccd824d598`.
Its job database includes the deliberate final negative parameter-evidence record;
that artifact endpoint is intentionally blocked after the negative assertion.

## Limits

This is creation and inspection of one new Top Plane plate. Production saved-model
edits, general equation/constraint tools, hole parameters, multiple bosses and
finishing remain separate increments. Copy editing above is confined to native
test fixtures. Physical phone, live cloud/network and installed-service upgrade
acceptance remain unverified. This feature branch is for draft review, not automatic
merge, release or deployment.
