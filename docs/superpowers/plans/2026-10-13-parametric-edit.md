# Checkpointed Parametric Edit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Inline execution and one final independent review apply.

**Goal:** Edit one approved native driving dimension with a verified checkpoint and saved-result gate.

**Architecture:** Host constructs an exact edit plan from a verified source job. A durable journal brackets a native fork, one parameter change and verified restore/publication. Reuse creation-plan geometry and parameter inspectors through a derived expected-result plan.

**Tech Stack:** C# .NET48, SOLIDWORKS2020 STA COM, Newtonsoft JSON, SQLite schema8, MSTest.

**Spec:** docs/superpowers/specs/2026-10-13-parametric-edit-design.md

## Global Constraints

- One change on a verified agent-created plate or plate-with-hole; no chained edits.
- Fresh child identity, source bytes preserved, fresh relative .sldprt output and overwrite false.
- Roles width, length, thickness, holeDiameter, holeX, holeY; existing exact plate/hole limits apply.
- Durable MutationStarted before COM; no replay; verify checkpoint restore or record Uncertain.
- Current explicit approval and immutable source/plan/file hashes are required.
- Preserve legacy and creation-plan semantics; SQLite7->8 transactional migration.
- User's standing authorization covers design and implementation; phone testing deferred.
- Worktree parametric-edit on feature/parametric-edit starts at d74662d; draft PR only.

## Review Focus

- Source job/file/revision changes between approval and dispatch fail before native mutation (Tasks1/3).
- Equal dimension values, swapped roles and foreign owner references never select by name alone (Task2).
- Cancellation/COM ambiguity/startup interruption cannot replay or publish an edit (Tasks2/3).
- Expected result changes only one parameter and preserves all other constraints (Tasks1/4).
- Saved result corruption or failed latest evidence revokes downloads (Tasks3/4).

### Task1: Exact edit contract and result derivation

**Files:** Core/Commands/CadParametricEditContract.cs, CadPlanV2Document.cs,
CadPlanV2Preflight.cs, CadPlanDocumentReader.cs; AgentHost/Planning/CadParametricEditPrompt.cs;
UnitTests/CadParametricEditContractTests.cs.

**Interfaces:** CadParametricEditSource holds immutable source identifiers/hashes,
SourcePlan and Parameter/ValueMm. CadParametricEditContract.Validate(plan) returns
IReadOnlyList<string>; ExpectedPart(plan) returns a cloned normalized creation plan.
CadParametricEditPrompt.Parse(prompt) returns source JobId, Parameter, ValueMm, OutputPath.

- [ ] Write RED tests: six valid roles, plate-only role limits, exact three steps, one-field result changes, zero/nonfinite/clearance errors, unknown fields, aliases, recursive source and malformed paths.
- [ ] Run dotnet test UnitTests -c Release --filter CadParametricEditContractTests; expected missing type/contract failure.
- [ ] Add optional parametricEdit metadata and strict bounded validation; derive result without mutating source, preserve logical sketch IDs. Keep candidate admission Host-only.
- [ ] Run focused/full units green and commit feat: define checkpointed parametric edit contract.

### Task2: Owned native fork, parameter mutation and restore

**Files:** Core/References/ICheckpointedParametricEditExecutor.cs;
SolidWorksBridge/Parametric/CheckpointedParametricEditor.cs; SolidWorksBridgeFacade.cs;
IntegrationTests/CheckpointedParametricEditNativeTests.cs.

**Interfaces:** ICheckpointedParametricEditExecutor supports owned fork/checkpoint,
single captured-role mutation and exact checkpoint restoration. Requests carry
Host-owned source/child identities, immutable file hashes, source plan and bindings.
Results contain child captures and independent native observations; no arbitrary command exposure.

- [ ] Write RED native tests for width/thickness/hole changes, fresh identity/source SHA preservation, ambiguous foreign reference rejection and checkpoint restoration following invalid readback.
- [ ] Run native focused tests; expected unavailable editor failure.
- [ ] Implement under facade command gate/STA with actual captured owner resolution; independently inspect baseline before mutation, then SetSystemValue3 and ForceRebuild3. Restore only the owned document with no source save and no blind retry.
- [ ] Run native tests green; commit feat: checkpoint native parameter edits.

### Task3: Journal, approval, execution and artifact gate

**Files:** AgentHost/Persistence/Schema.sql, SqliteJobRepository.ParametricEdit.cs,
SqliteJobRepository.cs; Jobs/CadParametricEditRunner.cs, JobCoordinator.cs,
CadV2ArtifactAuthorization.cs; Planning/CadPlanV2ExecutionReadiness.cs;
UnitTests/ParametricEditPersistenceTests.cs, ParametricEditCoordinatorTests.cs.

**Interfaces:** Repository prepares one edit attempt per revision, commits guarded
transitions and atomically registers child/captures. Runner executes exact current
approved plan through ICheckpointedParametricEditExecutor, records native/saved
verification and Published or verified Restored/Uncertain. Coordinator constructs
source-bound plans deterministically and requires approval; downloads require Published.

- [ ] Write RED tests: SQLite7 migration, wrong transition/source hash, duplicate attempt, atomic captures, startup uncertainty/no replay, AutoMode awaiting approval, stale source denial and failed-result download denial.
- [ ] Run focused units; expected missing journal/runner/admission failures.
- [ ] Implement schema8 journal and recovery, source authorization, explicit prompt intake, guarded runner and artifact branch. Verification consumes ExpectedPart while evidence binds complete approved edit-plan hash.
- [ ] Run focused and full units green in both interop modes; commit feat: approve and verify checkpointed edit jobs.

### Task4: Acceptance, independent review and GitHub

**Files:** IntegrationTests/CheckpointedParametricEditAcceptanceTests.cs;
docs/parametric-edit-verification-2026-10-13.md; artifacts/parametric-edit/.

**Interfaces:** End-to-end real Host jobs through existing approval/status/download APIs.

- [ ] Write RED acceptance tests for source->edit->saved reopen/download, unchanged source bytes and rejected result with verified checkpoint restoration.
- [ ] Run acceptance tests; expected missing full-path behavior until preceding tasks complete.
- [ ] Resolve integration gaps with test-first changes; run native acceptance and existing plate/hole regression, both unit modes and Windows bundle validation green.
- [ ] Dispatch one fresh whole-branch reviewer, make one material fix pass with RED/GREEN evidence, archive ledger/review package and record limitations/rulings.
- [ ] Commit final report, push feature/parametric-edit, create/attach stacked draft PR against feature/parametric-hole and verify CI for exact final HEAD. No merge/release.

## Self-review

Every spec requirement maps to Tasks1-4. Task1 result derivation is consumed by
native baseline/result verification and Host evidence. Task2 ownership and captures
are committed transactionally in Task3; the journal brackets every native side
effect and startup uses no COM. Task4 proves publication/restore with real APIs.
Standing user authorization selects inline implementation without a new design gate.
