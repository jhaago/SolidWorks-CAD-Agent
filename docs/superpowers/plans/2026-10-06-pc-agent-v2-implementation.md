# PC desktop completion and V2 sketch foundation implementation plan

> For agentic workers: use superpowers:executing-plans; execute inline with test-first development.

Goal: finish desktop revisions and persisted history, then expand the native sketch toolset.
Architecture: reuse the existing Host revision and paginated jobs endpoints; add Desktop client contracts and UI flows. Extend registry, bridge, simulation and planning contracts together for each new CAD command.
Tech Stack: C#, WinForms, .NET Framework 4.8, MSTest, SOLIDWORKS 2020 COM.
Spec: docs/superpowers/specs/2026-10-06-pc-agent-v2-design.md

## Global constraints
PC only. Host stays loopback. Preserve ownership, workspace, approval, cancellation and millimetre conversion. User authorized design and development without additional checkpoints.

## Review focus
- Stale history summaries must never authorize a build: load full current snapshot.
- A reconnect must retry initial history load after failure.
- History selection during execution must not move Cancel to a different job.
- Change requests must contain the displayed revision and nonblank instructions.
- Refreshing a list must not trigger recursive selection/network requests.

## Task 1: Desktop client contracts
Files: src/SolidWorksCadAgent.Desktop/Api/AgentHostClient.cs, AgentHostDtos.cs; tests/SolidWorksCadAgent.UnitTests/AgentHostClientTests.cs.
Produces: Task<JobPageDto> ListJobsAsync(int limit, string cursor, CancellationToken token); Task<JobViewDto> RequestChangesAsync(Guid jobId, Guid revisionId, string instructions, CancellationToken token).
- [x] Add failing tests for cursor encoding, page parsing, displayed revision/instructions and blank rejection.
- [x] Run filtered MSTest suite and verify red.
- [x] Implement client methods and JobPageDto; preserve Host API error handling.
- [x] Run filtered tests and full unit suite.

## Task 2: Desktop workflows
Files: MainForm.cs, MainForm.Designer.cs, new RevisionInstructionsForm.cs; Desktop workflow tests.
Consumes Task 1 interfaces. Produces navigable persisted history and editable pre-execution revisions.
- [x] Test loading a persisted job after initial connection, page navigation, selection snapshot loading and revision request flow.
- [x] Implement explicit Refresh/Load older controls, first-connect load/retry, selection guard and full snapshot loading.
- [x] Add a multiline clarification dialog, submit against captured job/revision, display revised plan without auto-approval.
- [x] Prevent history navigation and change submission during active local operations; retain independent Cancel.
- [x] Build Desktop and run unit suite. Record manual Windows UI acceptance separately.

## Task 3: Native sketch primitives
Files: CadCommandNames.cs; new AdvancedSketchCommandHandlers.cs; existing bridge registration, planner schema and simulation executor; new validation and native integration tests.
Produces AddLine and AddArc with explicit millimetre coordinates, strict finite validation and active owned sketch checks.
- [x] Inspect installed COM signatures and command registry/schema interfaces.
- [x] Write failing tests for missing/nonnumeric/nonfinite values, zero-length line, degenerate/inconsistent arc radius and explicit direction.
- [x] Implement handlers and registrations, convert units once, return structured failures.
- [x] Add planner schemas and honest simulation support; run validation/contract suite.
- [x] Native integration: build a closed line/arc profile, extrude, verify geometry, save/reopen and run plate regression.

## Next separate plans
Entity reference lifecycle plus dimensions/relations; finishing plus capture; patterns plus revolve; safe existing-part revisions; advanced single-part features. Each gets exact contracts and native acceptance before implementation.
## Execution record
Task 1: complete; client tests RED to GREEN.
Task 2: complete; history tests RED to GREEN; independent review findings fixed with RED to GREEN regressions; manual visual acceptance pending.
Task 3: complete; 13 sketch tests RED to GREEN; 235 total unit tests passed; 4 native integration tests passed.
No design checkpoints requested, per explicit user authorization. Work remained on the clean existing repository checkout on a dedicated V2 branch.
