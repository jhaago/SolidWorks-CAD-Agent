# Batched native finishing ports

Port related operations together behind the existing bridge: share selection, units, document ownership, native definition readback, material checks and failure reporting. This removes repeated integration work without importing either upstream server or changing the net48/SOLIDWORKS 2020 host. This batch adapts three pinned MIT source files from `soheil-mp/solidworks-mcp`; provenance and hashes are in `third_party/reuse-manifest.json` (18 total recorded source files). Existing notices are retained.

## Implemented scope

| Bridge command | Parameters | Evidence |
|---|---|---|
| FilletEdges | `normalAxis`, `side`, `radiusMm` | Simple constant-radius definition, selected edge count, analytic round-face radii |
| ChamferEdges | `normalAxis`, `side`, `distanceMm` | Angle-distance definition at 45 degrees, selected edge count, planar orthogonal legs |
| GetEdgeFinishingFeatures | Empty object | Stored native definitions; explicitly not geometric verification or selection authority |

Axis is `X`, `Y` or `Z`; side is `Min` or `Max`. Selection uses the independently implemented exact face/outer-loop resolver, not an upstream first-face heuristic. Execution supports straight outer edges with orthogonal planar neighbors, a single solid and no active sketch/user selection. Inner hole loops are excluded. Geometry is native/editable; created features receive unique `AgentFilletN`/`AgentChamferN` names. Native dimensions remain editable through SOLIDWORKS; this batch does not add conversational finishing edits.

Execution is restricted to a newly created, unsaved part owned by the current bridge execution. Opening, saving, rebinding or starting another execution removes eligibility. Saved-part finishing needs integration with checkpointed editing before it can be enabled. This restriction protects existing documents while making the creation path testable.

Each native mutation is attempted once. Definition, clean rebuild, material removal, single-body preservation and exact one-feature inventory addition must all pass. No equal-distance retry, tangent propagation, freeform radius fallback or deletion rollback is imported. Cancellation, verification failure or selection-cleanup failure after a possible mutation reports partial state and quarantines further finishing on that context. Quarantine applies to these finishing commands, not every existing bridge operation. Discard the failed synthetic part and start a new creation execution.

Both tools are registered in the native bridge. The AI operation catalogue and existing V2 planner remain unchanged pending native acceptance and planner admission work. No new runtime, provider call or dependency is introduced.

## Validation

- Portable production-helper suite: **240 passed**, zero failures/skips, using the official MSTest executable.
- All eight net48 project source sets compiled with the official Roslyn compiler; Bridge and integration test projects compiled with actual version **28.1.0.74** references and `SOLIDWORKS_INTEROP` enabled.
- The local MSBuild host failed before compilation because process IDs exposed through procfs differ from the execution namespace. Direct compilation used the existing compiler arguments and regenerated current source lists; the SDK was not patched. Windows CI runs the standard full solution build, unit suite, package/licence checks and actual 2020 reference build.
- Public repository publication was explicitly approved on 2026-10-31, resolving the earlier approval block. The standard Windows CI build and full Windows unit suite are pending at this documentation commit; current results are recorded on PR #7. The prior published head passed 981 Windows unit tests.
- Portable linking of the production context produces platform-analysis warnings for its COM identity methods; the tests use managed objects and do not invoke COM.
- SOLIDWORKS geometric execution, stored selection counts without `AccessSelections`, feature naming, real COM cleanup and save/reopen acceptance are **unverified** here. Compilation is not native acceptance. Unsupported native evidence fails closed, including nonfinite definition measurements.

## Installed 2020 acceptance

Use a clean SOLIDWORKS 2020 session and the native-enabled Debug build described in [the Windows handoff](windows-acceptance-open-source-2026-10-29.md). Run only the finishing fixture; the separate sketch-on-face fixture remains pending:

```powershell
$env:SOLIDWORKS_RUN_FACE_FINISHING_TESTS = '1'
dotnet test tests\SolidWorksCadAgent.IntegrationTests\SolidWorksCadAgent.IntegrationTests.csproj --no-build --configuration Debug --filter 'TestCategory=EdgeFinishingCapability' --logger 'trx;LogFileName=edge-finishing-2020.trx'
```

The fixture creates an isolated plate with a through hole, fillets one outer face and chamfers the opposite face by 1 mm, checks names/material/definitions/geometry and preserved inner hole, saves and reopens, checks definitions again, then proves another finishing attempt on the saved document is rejected before mutation. Preserve revision, TRX, part hashes and readback evidence. The ports retain upstream selection mark 0; confirm this works with FeatureFillet3 in 2020 and record the accepted mark. If a different mark is required, change selection and exact mark verification together. If SOLIDWORKS requires `AccessSelections` for reliable edge counts, implement and test a correctly released selection scope before planner admission; never treat missing counts as success.

## Next batches

1. Run native acceptance; connect saved-part finishing to the existing checkpoint editor and admit only verified tools to the AI catalogue.
2. Reuse the same lifecycle for a bounded revolve and then linear patterns, each with native definition and saved geometry evidence.
3. Add native sketch relations/dimension solving and inspection; keep the imported constraint graph as validation rather than claiming it solves sketches.
4. Extend to sweeps/lofts, drawings and assemblies with dedicated selection/verification contracts. Image tracing still needs intent reconstruction and constraints, not a direct bitmap-to-editable-model copy.
