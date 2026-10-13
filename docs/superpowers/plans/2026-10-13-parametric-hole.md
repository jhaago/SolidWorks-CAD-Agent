# Dimensioned Hole Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking. Inline execution and one final independent review were selected for this sequence.

**Goal:** Create and independently verify one native dimensioned through-hole on a new parametric plate.

**Architecture:** AddCircle v2 creates native diameter/X/Y driving dimensions on a second owned sketch. Extend exact plan scope, transactional captures and parameter verification using the existing managed model, journal and saved-artifact gates. Preserve plate and legacy operation semantics.

**Tech Stack:** C# .NET48, SOLIDWORKS2020 COM on existing STA, Newtonsoft JSON, SQLite schema7, MSTest, Windows MSBuild.

**Spec:** docs/superpowers/specs/2026-10-13-parametric-hole-design.md

## Global Constraints

- One new origin-centred Top Plane plate and one through-all circular hole.
- Positive X/Y greater than0.001mm; finite positive diameter and at least0.001mm edge clearance.
- Exact ten-step version sequence1,1,2,1,3,1,2,1,2,1; new relative.sldprt with overwrite false.
- Schema7 and plate serialized fields unchanged; plate role IDs/set retain their meaning.
- Native driving dimensions and full solve; no Fix, arbitrary edits, replay or name-based rebinding.
- User explicitly authorized planning and implementation without another approval stage; phone deferred.
- Worktree parametric-hole on feature/parametric-hole starts at b5b050d. No merge/release/deployment.

## Review Focus

- Nonfinite, zero/negative offset, tangent/outside circle or mixed versions: reject before mutation (Task1).
- Equal diameter/X/Y values and swapped names: native types and attachments distinguish roles (Tasks2/4).
- Missing/foreign/duplicate captures and generic-commit bypass: atomic failure/no replay (Task3).
- Second sketch/cut must retain plate constraints, revision and complete saved hash evidence (Tasks3/4).
- Post-reopen missing parameter with valid geometry must fail Host completion and download (Tasks3/4).

### Task1: Contract, roles and candidate construction

**Files:** Contracts/Cad/CadParametricHoleContracts.cs, CadParametricPlateContracts.cs;
Core/Commands/CadParametricHoleContract.cs, CadParametricPlateContract.cs,
CadPlanV2Preflight.cs; Core/References/CadParametricIdentity.cs;
AgentHost/Planning/CadParametricHolePrompt.cs; UnitTests/CadParametricHoleContractTests.cs.

**Interfaces:** CadParametricHoleIntent {DiameterMm,Xmm,Ymm};
CadParametricHoleContract.IsHole(CadPlanV2Document), Validate(plan), ReadIntent(plan),
BasePlatePlan(plan). CadParametricHolePrompt.IsRequest(prompt), BuildCandidate(prompt).
CadParametricIdentity.HoleRoles preserves For(model,sketch,role) encoding.
CadParametricSketchObservation supplies shared existing serialized capture fields;
CadParametricPlateObservation and CadParametricHoleObservation retain typed boundaries.

- [ ] Write contract tests: valid ten-step prompt and geometry/volume, unchanged six-step plate, role-ID separation, equal values, nonfinite/malformed/mixed versions, negative/zero/tangent/outside positions and path traversal.
- [ ] Run dotnet test UnitTests filter CadParametricHoleContractTests (Release); watch missing types/expected admission failure.
- [ ] Implement exact bounded hole scope. Project base plate validation from first five plus final save without recursion; full preflight still validates all references. AddCircle2 triggers parametric scope validation. Keep older operation/catalog defaults unchanged. Parser builds reviewed explicit summary and complete candidate, no provider call.
- [ ] Run focused and full unit suites green; commit feat: define versioned dimensioned-hole contract.

### Task2: Native circle recipe and independent semantic readback

**Files:** SolidWorksBridge/Parametric/ParametricHoleBuilder.cs,
ParametricHoleInspector.cs, ParametricRectangleBuilder.cs;
Commands/ParametricCircleCommandHandler.cs; IntegrationTests/ParametricHoleNativeRecipeTests.cs.

**Interfaces:** ParametricHoleBuilder.Build(object app,object doc,Guid model,Guid sketch,
double diameter,double x,double y): CadParametricHoleObservation. Inspector.Inspect
(object doc,CadModelIdentityRecord identity,Guid sketch,IReadOnlyList<CadEntityReferenceBinding>): observation.
Handler is AddCircle version2 and uses WithActiveSketchAsync's trusted context.

- [ ] Write native recipe tests for circle fully defined, three driving dimensions, diameter/placement regeneration and solver-disabled preference/manager/selection restoration. Inspect installed signatures/types before adding API calls; verify missing builder RED.
- [ ] Implement direct captured-circle creation, diameter and horizontal/vertical dimensions; native solver failure remains an uncertainty gate. Restore transient state in finally. Capture real owner references and exact names. Inspector resolves actual circle, native geometry, types and relation entities independently; no name lookup substitute for native ownership.
- [ ] Run recipe success/failure green on retained session; keep production intake unpublished until Host gates pass. Commit feat: create native dimensioned hole sketch.

### Task3: Transactional ownership and full Host verification

**Files:** AgentHost/Persistence/SqliteJobRepository.Parametric.cs,
SqliteJobRepository.V2Sequential.cs; Planning/CadPlanV2ExecutionReadiness.cs,
CadPlanV2ExecutionBinder.cs; Jobs/CadV2SequentialDispatchBoundary.cs,
CadParametricHoleVerifier.cs, CadV2RectangleBossVerifier.cs,
CadV2SavedVerificationRecorder.cs, CadV2ArtifactAuthorization.cs;
SolidWorksBridge/SolidWorksBridgeFacade.cs; UnitTests/ParametricHolePersistenceTests.cs,
CadParametricHoleVerifierTests.cs; IntegrationTests/ParametricHoleAcceptanceTests.cs.

**Interfaces:** Existing CommitV2ParametricOutcomeAsync now handles AddCircle2 with
four roles on plan.Steps[5].OutputEntityId, preserving rectangle/boss behavior.
Read-only InspectParametricHole uses managed model and sketch parameters. HoleVerifier
Check(intent,observation), VerifyAsync(job,revision,model,token) returns existing verification type.
Saved recorder adds V2SavedParametricHole; artifact authorization requires all complete evidence.

- [ ] Write real SQLite tests for correct second-sketch capture, foreign/missing/duplicate/native-name/axis ownership, forced insertion rollback and generic bypass/no replay. Write pure verifier tests for wrong/native-missing/driven/nonfinite geometry and dimensions. Watch RED before implementation.
- [ ] Adapt versioned active-sketch binding and dispatch, using existing atomic transaction and prospective model revision. Commit four captures with strict intent/name/owner checks; exclude circle2 from generic commit. Register version2 handler and read-only inspector. Preserve plate role fetch count.
- [ ] Add independent hole verifier before save and after reopen, alongside plate and full physical/cut verification. Record matching complete expected identities and actual hole observation; require latest successful saved hole evidence for download. Write native Host fixture using a deterministic fake planner before ordinary intake is wired, including a real post-reopen named-hole fault.
- [ ] Run focused/persistence/full units and native Host positive/failure green; commit feat: gate dimensioned-hole completion on native saved evidence.

### Task4: Reviewed intake, native acceptance, review and GitHub

**Files:** AgentHost/Jobs/JobCoordinator.cs, Host/AgentRoutes.cs;
UnitTests/ParametricHoleCoordinatorTests.cs; IntegrationTests/ParametricHoleAcceptanceTests.cs;
README.md, docs/CAD_CAPABILITY_MATRIX.md, CAD_IMPLEMENTATION_STATUS.md,
CAD_IMPLEMENTATION_ROADMAP.md, parametric-hole-verification-2026-10-13.md.

**Interfaces:** SupportsParametricHoleDimensions is true only on the registered real
native managed path. Explicit syntax enters existing current-revision approval without
provider calls. Health/restart projection and saved download response remain compatible.

- [ ] Write intake tests for no provider use, mandatory current revision and simulated/unsupported rejection; watch RED. Wire explicit prefix, native capability and summary through existing workflow, then run tests green.
- [ ] Native acceptance: asymmetric120/60/10 with Ø8(20,10), expected72000-pi*16*10; saved copies diameter12 and X25/Y12 regenerate with unchanged plate/source hash. Equal120/120/10 and diameter/X/Y20 rejects name swaps. Real post-reopen name fault retains valid geometry but leaves Failed/409. Restore exact immutable source after unmanaged copy probes. Run affected plate3 acceptance plus hole recipe/Host cases serialized on retained session.
- [ ] Run complete units in both interop modes (no skips), native Release solution/bundle and validator/capability/file-lock tests. Collect TRX, identities and hashes under ignored artifacts/parametric-hole.
- [ ] One fresh whole-branch reviewer; reproduce/fix Critical/Important in one pass, ledger Minor/declined judgments. Update truthful report/limits. Commit, push github/feature/parametric-hole and create/attach stacked draft PR against feature/parametric-plate; wait applicable CI. Preserve worktree. No automatic merge/release.

Self-review: task interfaces align: scoped ten-step plan supplies separate sketch ID;
native capture supplies four roles/three dimensions; transaction supplies version3
owner bindings; inspector supplies semantic/physical sketch readback; saved recorder
and artifact gate bind the full plan/revision/hash. First capability remains compatible.
