# Narrow V2 Rectangle Boss Implementation Plan

**Outcome (2026-10-09):** The bounded source/native milestone is accepted. The checkboxes below preserve the original execution plan rather than serving as the live status record. See `docs/CAD_IMPLEMENTATION_STATUS.md` and matrix evidence E52 for final 572/572 unit suites in each interop mode, both clean builds, controlled SOLIDWORKS 2020 `/jobs` create/approve/artifact acceptance 1/1, release limitations and next action. No install, deployment, merge or push occurred.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An approved v2 plan creates one new Top Plane centred rectangle, boss extrudes it, verifies geometry, saves and reopens a new part, and makes only that verified artifact available through the existing CAD job interface.

**Architecture:** Extend the existing `JobCoordinator`, v2 mutation-attempt boundaries, saved verification recorder, and artifact route. Keep v1 untouched. A cross-process remote-input lease excludes Android mouse/keyboard injection during native mutation and verification. The first UI path is explicit and narrow; arbitrary v2 plans remain review-only.

**Tech Stack:** .NET Framework 4.8, C#, SQLite, SOLIDWORKS 2020 COM on STA, MSTest, existing desktop/remote job APIs.

**Spec:** `docs/CAD_AGENT_ARCHITECTURE.md`, `docs/CAD_IMPLEMENTATION_ROADMAP.md`, and `docs/CAD_IMPLEMENTATION_STATUS.md`.

## Global Constraints

- Preserve all accumulated uncommitted v2 work and historical v1 behavior. This checkout holds required uncommitted boundaries, so do not create a clean worktree from HEAD or include unrelated changes in a commit.
- Only the exact normalized six-step `NewPart → CreateSketch(Top Plane) → AddRectangle(centred) → ExitSketch → Extrude(blind) → SavePart(nonoverwrite)` is in scope.
- Native calls remain on `SolidWorksBridge` STA; identities, workspace protections, no-replay uncertainty, revision-bound approval and explicit millimetre units remain mandatory.
- No push, merge, deployment, running-binary replacement or user-document mutation. Native tests use the controlled owned-document fixture.
- Do not advertise or mark native verification for a whole approved job until that exact scenario passes on SOLIDWORKS.

## Review Focus

- A stale or changed plan must never reach native dispatch after approval.
- A failed or uncertain native step must never be retried automatically or produce a downloadable artifact.
- Android manual input must not race CAD mutation; viewing and Stop AI remain available.
- A saved file with the right bounds but wrong volume, hash or ownership must fail verification/delivery.
- Unsupported v2 plans and simulation must remain in clarification without approval.

### Task 1: Exact six-step runner behind existing journal

**Files:** Add `AgentHost/Jobs/CadV2SixStepRunner.cs`; extend `JobCoordinator.cs` only for call sites; tests in `CadV2CoordinatorTests.cs`.

- [ ] Write a failing test for ordered preparation/dispatch through all six existing boundaries, with each result recorded against the current revision and no extra dispatch.
- [ ] Write negative tests for stale revision, failed/uncertain attempt, and cancellation before the next step.
- [ ] Implement runner from persisted normalized steps, not caller-built requests. Keep single-process no-replay fences and record successful SavePart in `CommandExecutionRecord` for artifact delivery.
- [ ] Run focused and full unit suites in both interop modes; review the diff.

### Task 2: Revision-bound v2 admission and completion

**Files:** `JobCoordinator.cs`, narrow admission helper under `AgentHost/Planning` if useful, coordinator tests.

- [ ] Write failing tests for exact supported real-mode plan becoming `AwaitingApproval`, explicit approval executing it, and unsupported/simulated/ambiguous/overwrite plans staying closed.
- [ ] Require current revision and whole-plan native capability readiness again at approval; never reinterpret historical v1 JSON.
- [ ] After runner save, invoke recorder-owned close/reopen verification; transition to `ReadyForReview` only after passing evidence, otherwise `Failed` or `Uncertain` as appropriate. Preserve cancellation semantics.
- [ ] Run focused and full unit suites; review approval and recovery paths.

### Task 3: Cross-process remote manual-input lease

**Files:** Add a small lease abstraction in Core/Remote; integrate Host execution and RemoteAgent session input/resume; tests for both sides.

- [ ] Write tests that manual control cannot start during a CAD execution lease, and CAD execution cannot start while remote manual control is held.
- [ ] Use a same-Windows-session named synchronization primitive with bounded acquisition/cancellation, acquired before the first native mutation and released after final verification/failure. Remote viewing and Stop AI do not need the lease.
- [ ] Fail closed if the lease is unavailable or broken; preserve existing session-token/epoch controls and v1 behavior.
- [ ] Run remote and coordinator tests, then both solution builds.

### Task 4: Controlled approved-job SOLIDWORKS acceptance

**Files:** Extend owned-document integration fixture; test evidence TRX and docs.

- [ ] Run a real approved v2 job through the public coordinator using a deterministic v2 test planner. Assert six Applied attempts, command history, passing saved verification, 20 × 10 × 5 mm bounds, 1000 mm³ volume, exact hash/path, close/reopen and prior-document restoration.
- [ ] Add wrong-path/active-document and unavailable save negative acceptance where safe; distinguish native checks from simulation.
- [ ] Review and fix any native failure without weakening tests.

### Task 5: Narrow normal-interface entrypoint

**Files:** Explicit versioned planning adapter/route and existing Desktop/RemoteAgent job UI as appropriate; interface tests.

- [ ] Provide a deliberate narrow v2 request with typed width, height, depth and new relative `.sldprt` path; require clarification for missing/ambiguous values. Existing v1 natural-language jobs remain v1.
- [ ] Surface plan version, supported scope, approval, progress, failure and artifact download in existing desktop/paired remote job views. Do not claim general v2 support.
- [ ] Test requests and UI projections for supported and unsupported shapes, then run the controlled native approved-job path again through the normal route.

### Task 6: Final review and evidence

**Files:** Matrix, architecture, roadmap, status, tests as needed.

- [ ] Review all accumulated uncommitted v2 changes for scope, compatibility, failure safety and accidental changes; fix important findings with regression tests.
- [ ] Run full unit tests in both interop modes, complete solution builds, controlled native acceptance and artifact-download check.
- [ ] Update capability IDs and evidence honestly; report exact available scope and remaining limitations. Do not push, merge or deploy.
