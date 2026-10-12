# Submission Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover a phone submission by its durable UUID without creating a second job, replaying planning, or adopting an unrelated job.

**Architecture:** An optional `clientSubmissionId` becomes the existing job UUID. SQLite atomically records the initial input fingerprint and distinguishes creation from recovery; only the creator plans. Android persists identity and cancellation intent before sending, then resolves uncertainty through existing exact-job routes.

**Tech Stack:** .NET Framework 4.8, SQLite, Newtonsoft.Json; Android Kotlin, coroutines, app-private SharedPreferences, existing Compose UI.

**Spec:** [Approved design](../specs/2026-10-11-submission-recovery-design.md).

Status: ready for user review; implementation has not started. Paths below are relative to `solidworks/` unless prefixed `android/` (the sibling repository).

## Global Constraints

- Requests without `clientSubmissionId` retain server-generated job IDs.
- Reject a malformed or empty supplied UUID with HTTP 400.
- For a different hash or an existing legacy job without a submission hash, return HTTP 409 `SUBMISSION_CONFLICT`.
- Duplicate recovery must remain available when new-submission slots are full.
- Restart recovery keeps the existing fail-stop behavior for interrupted jobs.
- The new store retains no credentials, prompt or photo.
- Retain the tracked UUID until its terminal state is confirmed.
- Physical-phone acceptance is explicitly deferred. Merge/release remains a separate step.
- Preserve .NET 4.8/SOLIDWORKS 2020 support, the dedicated STA dispatcher, document ownership, approval, authentication and native execution gates.
- No OpenAI calls or provider configuration changes are needed for regression tests; use fixed fake planners.

## Review Focus

- Storage write failure before POST must prevent sending an untrackable request (Task 4).
- A stale terminal callback must not delete a newer persisted UUID (Tasks 4–5).
- Host restart-required or changed planner settings must still permit recovering an existing receipt (Task 2).
- Revoking one device or forgetting one endpoint must not clear another scope's record (Tasks 4–5).
- A valid image changing only byte content must conflict even when its prompt and media type are unchanged (Tasks 1–3).

## File map and boundaries

Host persistence owns immutable identity input, separately from mutable job revisions. Coordinator owns admission and planning. Routes validate wire values; RemoteAgent keeps its existing authenticated whitelist. Android's store owns durable metadata, a small submission state object owns original in-memory input and retry budget, and the driver retains session/generation ownership.

New Host files: `src/SolidWorksCadAgent.AgentHost/Jobs/SubmissionInputHash.cs`, `Persistence/SubmissionCreateResult.cs`, and `Persistence/SqliteJobRepository.Submissions.cs` (same project root). New Android files in `app/src/main/java/com/jhaago/cadagent/remote/live/`: `PendingSubmissionStore.kt`, `SharedPreferencesPendingSubmissionStore.kt`, `SubmissionRecovery.kt`. Corresponding focused tests are listed below. Avoid unrelated extraction or UI redesign.

The Host and phone deliverables share one protocol, so use one ordered plan. Tasks 1–3 ship a backwards-compatible server feature independently; Tasks 4–5 activate the phone capability.

### Task 1: Atomic persisted submission identity

**Files:**
- Create: the three Host files named above.
- Modify: `src/SolidWorksCadAgent.AgentHost/Persistence/Schema.sql`, `Persistence/SqliteJobRepository.cs`.
- Test: `tests/SolidWorksCadAgent.UnitTests/SubmissionInputHashTests.cs`, `SqliteJobRepositoryTests.cs`.

**Interfaces:**
- `SubmissionInputHash.Compute(string prompt, CadPlanningImage image) : string`.
- `SubmissionCreateResult` exposes `CadJob Job` and `bool Created`.
- `SqliteJobRepository.FindSubmissionAsync(Guid id, string inputHash, CancellationToken cancellationToken) : Task<CadJob>` returns null when absent; mismatches throw `JobCoordinatorException("SUBMISSION_CONFLICT", ...)`.
- `CreateSubmissionAsync(CadJob job, CadPlanningImage image, string inputHash, CancellationToken cancellationToken) : Task<SubmissionCreateResult>` atomically creates or returns matching existing input. Existing `CreateAsync` remains unchanged for legacy callers.

- [ ] Add failing tests `Hash_TrimsPromptButDistinguishesImagePresenceTypeAndBytes`: assert equal hashes for `" plate "` and `"plate"`; unequal for absent/present image, changed media type, changed bytes or changed prompt. Add `ConcurrentCreate_ReturnsOneCreator`, `RepeatAfterRepositoryReopen_ReturnsExisting`, `LegacyIdCannotBeClaimed`, `RevisionPromptDoesNotChangeSubmissionIdentity`, and `ImageAndJobRollbackTogether`. Assert one stored image/job, exactly one `Created=true`, and conflict error code for mismatches.
- [ ] Run `dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj -c Release --filter "FullyQualifiedName~SubmissionInputHashTests|FullyQualifiedName~SqliteJobRepositoryTests" -p:NuGetAudit=false`; confirm the new tests fail because the behavior is missing.
- [ ] Implement the hash with version 1, length-prefixed UTF-8 trimmed prompt, an explicit image-presence byte, length-prefixed media type, and SHA-256 of image bytes; hash the whole encoding with SHA-256 and return lowercase hex. Record these exact encoding choices in tests. Do not log inputs.
- [ ] Add nullable `Jobs.SubmissionInputHash TEXT`; migrate schema version 6 to 7 in the existing transactional initializer and reject future versions above 7. Do not include this column in mutable job updates. Reuse existing job/image insert bindings; atomically test identity and insert under a SQLite write transaction. Avoid `INSERT OR REPLACE` and process-only locks. Account for contention using the existing SQLite connection timeout policy.
- [ ] Run the focused tests; include old-schema initialization, repeated migration and reopen tests, all passing. Explicitly test a separately opened repository on the same database in the concurrent-create case.
- [ ] Commit only the named source/tests with `feat: persist atomic submission identities`.

### Task 2: Host receipt recovery and bounded planning

**Files:**
- Modify: `src/SolidWorksCadAgent.Contracts/Jobs/JobDtos.cs`, `src/SolidWorksCadAgent.AgentHost/Jobs/JobCoordinator.cs`, `Host/AgentRoutes.cs`.
- Test: `tests/SolidWorksCadAgent.UnitTests/JobCoordinatorTests.cs`, `AgentRoutesTests.cs`.

**Interfaces:**
- `CreateJobRequest.ClientSubmissionId : string` preserves explicit invalid/empty values for validation.
- Append optional `Guid? clientSubmissionId = null` to `SubmitAsync` and `CreateAndPlanAsync`; retain current parameter order and callers.
- Task 1 repository methods provide recovery and creator ownership. Host health exposes `clientSubmissionIds: true` only when a coordinator is available.

- [ ] Add failing tests `DuplicateSubmit_PlansOnce`, `ConcurrentDuplicateSubmit_PlansOnce`, `FullQueue_AllowsDuplicateButRejectsNew`, `TerminalOrRestartInterruptedRepeat_DoesNotPlan`, `SamePromptDifferentIds_CreateSeparateJobs`, and `RecoverExisting_WhenRestartRequiredOrImagePlannerUnavailable`. Use blocked fake planners to fill exactly four slots and assert duplicate receipts remain available, new requests fail `SUBMISSION_BUSY`, no executor calls occur, and all reservations eventually release.
- [ ] Add route tests for both POST creation routes: supplied nonempty GUID returns that ID; omitted ID retains random server IDs; empty, malformed and wrong JSON type return 400; changed valid input returns 409 `SUBMISSION_CONFLICT`. A malformed image retains existing 400/413 validation. Explicitly test that accepted jobs remain recoverable after planner/settings changes.
- [ ] Run the coordinator/route filters with the Task 1 test command pattern; confirm each new regression fails for its intended missing behavior.
- [ ] Implement supplied-ID structural validation, hash and existing-receipt lookup before new-job admission/settings restrictions. New jobs still pass all current semantic and planner checks. For asynchronous submissions: lookup before reserving a slot, and recheck after a failed reservation so a just-created duplicate resolves. After reserving, use atomic creation; a noncreator releases the reservation and returns a snapshot without scheduling. Only a creator passes the slot to `TrackBackground`. For synchronous creation, only its atomic creator calls `PlanCreatedJobAsync`. All exceptional paths release owned slots exactly once.
- [ ] Map conflict to HTTP 409; preserve existing route success envelopes and auth. A route instantiated without a coordinator must reject supplied IDs as unavailable rather than advertising support or silently ignoring them. Keep no-ID behavior and explicit approval semantics intact.
- [ ] Run focused tests plus existing cancellation, planning, approval and shutdown-drain regressions; all pass. Commit `feat: recover existing job submission receipts`.

### Task 3: Authenticated proxy and capability propagation

**Files:**
- Modify: `src/SolidWorksCadAgent.RemoteAgent/Host/RemoteRoutes.cs`, `Host/AgentHostRemoteClient.cs`.
- Test: `tests/SolidWorksCadAgent.UnitTests/Remote/RemoteHttpTests.cs`, `AgentHostRemoteClientTests.cs`.

**Interfaces:** Existing `agent/jobs` forwards optional canonical lowercase UUID `clientSubmissionId`; `agent/status` carries boolean `clientSubmissionIds` from Host health, default false.

- [ ] Add failing tests `JobPost_ForwardsCanonicalSubmissionId`, `JobPost_RejectsMalformedEmptyOrNonStringId`, `JobPost_StillRequiresAuthorizedSession`, `Status_PropagatesSubmissionCapability`, `Status_MissingCapabilityIsFalse`. Assert unknown properties remain stripped, prompt/image remain intact, Host 409 survives the proxy, and omitted identity remains supported.
- [ ] Run the remote HTTP/client test filters and confirm expected failures.
- [ ] Extend `ReadJobBody` whitelist with strict optional ID validation/canonicalization; propagate the feature flag. Do not introduce new lookup routes or weaken session validation.
- [ ] Run these tests plus existing proxy image/auth tests; commit `feat: proxy durable submission identity capability`.

### Task 4: Durable Android tracking metadata

**Files:**
- Create: the three Android main files in the file map.
- Modify: `android/app/src/main/java/com/jhaago/cadagent/remote/model/RemoteWorkstationStatus.kt`, `remote/live/WorkstationSettingsController.kt`, `di/AppContainer.kt`, `ui/CadAgentAppViewModel.kt`.
- Test: `android/app/src/test/java/com/jhaago/cadagent/remote/live/PendingSubmissionStoreTest.kt`, `SubmissionRecoveryTest.kt`.

**Interfaces:**
- `SubmissionScope(origin: String, deviceId: String)` uses the already parsed endpoint origin and paired device ID.
- `PendingSubmission(id: String, cancelRequested: Boolean)` stores a canonical nonempty UUID.
- `PendingSubmissionStore.read(scope): PendingSubmission?`, `write(scope, submission): Unit`, `clear(scope, expectedId): Unit`, `clearScope(scope): Unit`; storage failures throw. `clear` compares current ID before removal.
- `SharedPreferencesPendingSubmissionStore(preferences: SharedPreferences)` stores one versioned JSON record per hashed scope key; blocking `commit()` success is required before POST. No secret/input persistence. Invalid records surface a tracking error; they cannot silently enable a new submission.
- `SubmissionRecovery` owns `pending: PendingSubmission`, optional original `instruction: String`/`photo: CadPhoto`, and `retryCount: Int`; `canRetry(authorizedForeground: Boolean): Boolean` permits at most one automatic retry (two total POSTs), only with original input and no cancellation. Recreated records have no retry payload.
- Add `supportsClientSubmissionIds: Boolean = false` to `RemoteWorkstationStatus`.
- Thread `PendingSubmissionStore` through settings/container configuration into driver creation using named parameters; production ViewModel supplies the SharedPreferences implementation. Tests use an in-memory implementation of the same interface.

- [ ] Add failing store/recovery tests: recreated store reads the same UUID/cancel flag; endpoint/device isolation; expected-ID clearing cannot delete a newer UUID; unknown schema/malformed UUID surfaces error; failed commit throws; raw stored values contain no prompt/image/credential. Test retry budget exactly one, cancellation forbids retries and recreated state cannot resend an image.
- [ ] Run `testDebugUnitTest` using the established local JDK 17/SDK/Gradle setup documented in `REVIEW-HANDOVER.md`; confirm expected failures.
- [ ] Implement those interfaces and dependency wiring. Use storage dispatcher work outside the driver's synchronization gate. Capture generation/scope, persist, then recheck ownership before network work. Preserve existing test constructors with a default in-memory test-compatible store; production must explicitly supply persistent storage.
- [ ] Test forget/revoke clears only the affected scope and fails visibly if durable removal fails. Run focused and full JVM tests; commit in Android `feat: persist scoped pending submission metadata`.

### Task 5: Exact identity recovery through the phone lifecycle

**Files:**
- Modify: `android/app/src/main/java/com/jhaago/cadagent/remote/live/LiveConnectionDriver.kt`.
- Test: `android/app/src/test/java/com/jhaago/cadagent/remote/live/PhoneJobLifecycleTest.kt`, `LiveConnectionDriverTest.kt`.
- Test: `android/app/src/androidTest/java/com/jhaago/cadagent/remote/ui/LiveRemoteLifecycleTest.kt`.

**Interfaces:** Add constructor parameter `submissionStore: PendingSubmissionStore` after existing optional parameters. Task 4 supplies durable state and retry policy. Extend private `AgentRuntime` capability parsing; exact `agent/jobs/{uuid}` routes remain unchanged.

- [ ] Add transport-fake regressions: lost POST response then exact-ID GET accepts the job while status advertises another job with identical prompt; two new UUIDs with identical prompts remain distinct; unexpected receipt ID stays uncertain; transient GET 404 retains tracking; the one retry sends identical ID/prompt/image; exhaustion prevents a new UUID. Assert requested paths and bodies, not only displayed text.
- [ ] Add recreated-driver tests: persisted UUID resumes exact GET without POST; retained stop intent sends cancel only to that UUID after discovery; terminal confirmation clears its record; stale response cannot overwrite state or clear a newer record; renewal/reconnect retains identity; unsupported status uses the existing legacy flow. Add failure-before-POST and cancel-during-persistence tests proving no untracked request is sent.
- [ ] Run `testDebugUnitTest`; confirm new tests fail for absent behavior.
- [ ] For supported workstations generate/store UUID before POST and expose pending state while retaining real UUID separately from UI phase. Validate all returned IDs. Restore scoped tracking on activation, keep it through disconnect/renewal, and recover by exact GET after auth. Exclude tracked submissions from prompt-equality/status adoption. On transient/mismatched outcomes keep uncertainty; permit one retry only under Task 4 policy. GET 404 and HTTP 409 conflicts never clear uncertain tracking or synthesize a new request; known preacceptance validation rejection may clear with expected-ID comparison.
- [ ] Persist cancellation intent before attempting cancel, including before a receipt exists. Preserve generation/session checks for every asynchronous store/network completion. Persist tracking until confirmed terminal, and clear on explicit forget or credential revocation. Storage errors surface in existing task/message UI and block dependent POST/new submission. Do not claim restored prompt/photo or add a background outbox.
- [ ] Add an emulator regression using the existing fake transport for task identity after lifecycle recreation and pending cancellation. Run full JVM tests and hosted API 35 instrumentation tests; commit `feat: recover phone tasks by durable job identity`.

### Task 6: Integrated verification and GitHub update

**Files:**
- Modify: both repositories' `docs/current-state-review-2026-10-11.md`, Windows native verification report if rerun, README compatibility notes, and root `REVIEW-HANDOVER.md`.
- Update: existing draft PR 3 (Windows) and PR 2 (Android), preserving base branches.

- [ ] Use the isolated review checkouts after the using-git-worktrees skill verifies isolation. Explicitly stage only feature files; preserve the existing missing historical bundle-validator file and unrelated original checkouts. Commit approved spec/plan separately from product changes.
- [ ] Run full Windows Release unit tests with `SolidWorksInteropAvailable=true`, then `false`, rebuilding each mode; commands use `-m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false`. Record actual counts and TRX results. Run full solution build, positive/negative bundle capability and file-lock validators; verify packaged source matches feature commits.
- [ ] Run Android JVM tests, `assembleDebug`, `assembleDebugAndroidTest`, `lintDebug` with established environment; inspect reports. Push feature branches to their `github` remotes without force; verify hosted build and API 35 emulator CI. Do not claim physical-device acceptance.
- [ ] Run `V2PrismaticAcceptanceTests` with `SOLIDWORKS_RUN_REFERENCE_TESTS=1`, `SOLIDWORKS_EXPECTED_YEAR=2020`, native-capable Release integration binary and unsandboxed execution. Inspect currently running SOLIDWORKS before attaching; use owned fixture paths and never terminate user processes. Expect nine passing scenarios, no skipped tests. Keep approval/native ownership checks intact; no cloud requests needed.
- [ ] Apply requesting-code-review and verification-before-completion skills. Resolve material feedback with focused regression checks, then update both reports and draft PR descriptions with final behavior, CI evidence, compatibility limitations and deferred phone acceptance. Merge/release remains separate.

## Self-review

All approved spec sections map to Tasks 1–5; combined verification and GitHub publication map to Task 6. Legacy clients, migration, queue saturation, restart fail-stop, initial-image identity, generation ownership, cancellation, process recreation and scoped forgetting each have a named regression. The five Review Focus conditions are assigned above. The plan retains one public ID and no new public lookup route. Interface names/types are consistent across tasks; dependency order is Host persistence → coordinator → proxy → phone store → driver.

## Execution handoff

Recommended: **Native execution** (the current agent implements the ordered tasks), because the tasks depend closely on the same protocol and lifecycle interfaces. User review of this written plan and choice of execution approach precede product implementation under the writing-plans skill.
