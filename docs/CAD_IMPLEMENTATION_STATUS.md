# CAD implementation handoff

Updated 2026-10-06. Source revision: `a1aa0aee961abb8c5d8110b04384db45448b0b31`. The source checkout retains its existing uncommitted CAD blueprint, typed-adapter, lifecycle-preflight and face/finishing work. This slice is uncommitted; no pre-existing changes were overwritten.

## Completed slice

- Roadmap increment 1, lifecycle substeps 1e, 1f and 1g-a; AG-003/017. `CadPlanLifecycleValidator` is called by Host `JobCoordinator.ValidatePlan` after existing per-command checks. It tracks document/sketch lifecycle, single-use closed-profile availability, and post-save ordering. After a valid `SavePart`, it rejects later model changes, rebuild, another save, or a document-target switch before execution; `CloseDocument` remains allowed.
- Initial document state is `Unknown` because Host does not inspect the current SOLIDWORKS document during planning. Existing workflows against an active document remain possible; `NewPart`/`OpenPart` mark the planned target open and `CloseDocument` marks it closed. Sketch state begins closed, so profile geometry requires a `CreateSketch` earlier in the plan. This is a deliberate compatibility boundary, not a durable document identity.
- Profile validation is deliberately evidence-limited: raw line/arc commands alone, and empty sketches, do not satisfy the feature gate. The validator does not analyze arbitrary line/arc connectivity, self-intersection, profile selection, feature-reference relationships, dependency cycles or execution-mode support. Save ordering is a preflight boundary only; it does not prove the save succeeded or provide rollback. `CadCommandEnvelope` and persisted plan JSON remain unchanged.
- Face-sketch, fillet and chamfer commands remain draft constants/validators absent from planner exposure and bridge registration. The unfinished `EdgeFinishingCommandHandlers.cs` remains excluded from both interop build paths because its persistent topology resolver is absent.
- Files changed in the current slice: `Core/Commands/CadPlanLifecycleValidator.cs`, `CadPlanLifecycleValidatorTests.cs`, `JobCoordinatorTests.cs`, `CAD_AGENT_ARCHITECTURE.md`, `CAD_CAPABILITY_MATRIX.md`, `CAD_IMPLEMENTATION_ROADMAP.md` and this handoff. Existing command dispatch, approval, persistence and native handlers are unchanged.

## Verification

- Baseline focused lifecycle/coordinator tests: 10 passed. Before implementation, the two new rejection tests failed because direct lifecycle validation accepted post-save edits and the coordinator returned a reviewable plan. After implementation, focused lifecycle/coordinator tests: 12 passed, 0 failed; this includes save-last and close-after-save acceptance plus zero executor calls for post-save mutation.
- Full unit suite: 409 passed, 1 existing platform failure, 0 skipped, 410 total. `RemoteHttpTests.LoopbackServerCanRestartAndRejectUnauthenticatedFrames` still throws `PlatformNotSupportedException` in `HttpListener..ctor`, matching the prior slice's failure.
- Solution builds, interop disabled and enabled: both succeeded with 0 errors. Each emitted 8 `NU1900` warnings because vulnerability metadata could not reach NuGet; builds used restored local packages with `--no-restore`.
- No native SolidWorks scenario was run. The change is Core sequence preflight only and does not alter COM handlers or geometry creation.

Consolidation follow-up (2026-10-07): both build modes still pass in an isolated copy. The `HttpListener` case passed when run outside the restricted sandbox, and the full unit suite then passed **410/410**. The earlier 409/410 line remains the restricted-sandbox result, not a code regression. No native SolidWorks acceptance run was added.

## Remaining and next slice

Roadmap increment 1 remains open. The next dependency-ready slice is **1g-b: simulation-mode preflight for `SavePart`** (AG-003/017): pass execution mode into plan validation, reject simulation save plans before approval/execution, and preserve accepted native-mode plans plus the existing path/overwrite protections. Acceptance requires a native-mode accepted plan, a simulation-mode clarification result, zero executor calls for rejection, and unchanged persisted plan JSON.

No persistent schema, reference identity or recovery guarantee decision was introduced here. Native SolidWorks face/finishing verification remains outstanding and is unrelated to this typed-lowering slice.

Face sketch, cut-direction, fillet and chamfer work remains incomplete. Before re-enabling it, implement deterministic face selection and frame/context lifecycle, then prove native selection and geometry with the retained isolated acceptance scenarios.
