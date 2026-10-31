# Android Companion App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first installable native Android companion app for SolidWorks CAD Agent with a complete fake-data CAD job workflow and clean seams for future secure remote connectivity.

**Architecture:** A single-module Kotlin/Jetpack Compose app uses screen-level ViewModels over a `CadAgentRepository` domain boundary. `FakeCadAgentRepository` owns in-memory job state for this milestone; UI code never depends on HTTP or SOLIDWORKS and a future remote implementation can replace the fake repository without rewriting screens.

**Tech Stack:** Kotlin; Jetpack Compose + Material 3; Navigation Compose; AndroidX Lifecycle/ViewModel; Kotlin coroutines/StateFlow; JUnit; kotlinx-coroutines-test; Compose UI tests; AGP 9.4.0; Gradle 9.6.0; JDK 17; Compose BOM 2026.09.00; compileSdk 36; targetSdk 36; minSdk 26.

**Spec:** `docs/superpowers/specs/2026-09-27-android-companion-app-design.md`

## Global Constraints

- Android source repository: `jhaago/SolidWorks-CAD-Agent-Android`; overall system/API authority remains `jhaago/SolidWorks-CAD-Agent`.
- Package/namespace: `com.jhaago.cadagent`.
- App name: `CAD Agent`.
- Native Kotlin + Jetpack Compose only for this milestone; no cross-platform framework.
- No live network access, localhost exposure, remote gateway, authentication, file upload, CAD preview, push notifications, background monitoring, Android-side AI planning, or direct SOLIDWORKS control.
- No OpenAI API key or other production secret may exist in the Android repository or UI.
- Approval is revision-bound: actions must carry the revision ID currently displayed.
- Unknown future job-state values must degrade safely instead of crashing UI/model code.
- Fake repository state is in-memory only; no database is required.
- UI/ViewModels must depend on `CadAgentRepository`, never directly on a fake or future HTTP implementation.

## Review Focus

- Blank/whitespace job prompts must be rejected without creating duplicate or empty jobs; pinned in Task 2 repository and Task 4 ViewModel tests.
- Approving or requesting changes with a stale revision ID must return a recoverable stale-revision error and leave the current job untouched; pinned in Task 2 tests and surfaced in Task 4.
- Unknown future job-state strings must render as an `Unknown` state rather than fail parsing or navigation; pinned in Task 1 and Task 5 tests.
- Rapid repeated taps on Submit/Approve/Request Changes must not create duplicate actions while an operation is in progress; pinned in Task 4 ViewModel tests.
- Missing/invalid job IDs opened from navigation must produce a recoverable not-found state rather than crash the app; pinned in Task 4 tests.

---

### Task 1: Scaffold the Android project and stable domain model

**Files:**
- Create: `settings.gradle.kts`
- Create: `build.gradle.kts`
- Create: `gradle/libs.versions.toml`
- Create: `gradle.properties`
- Create: `app/build.gradle.kts`
- Create: `app/src/main/AndroidManifest.xml`
- Create: `app/src/main/java/com/jhaago/cadagent/MainActivity.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/model/JobState.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/model/JobRevision.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/model/CadJob.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/model/AgentStatus.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/model/JobStateTest.kt`

**Interfaces:**
- Consumes: current Agent Host job-state meanings from `SolidWorksCadAgent.Contracts.Jobs.JobState`.
- Produces: `JobState.fromWireValue(raw: String): JobState`; `CadJob`; `JobRevision`; `AgentStatus`; a buildable Compose Android application.

- [ ] **Step 1: Write the failing `JobStateTest`** asserting all current host values (`New`, `Interpreting`, `AwaitingClarification`, `AwaitingApproval`, `Approved`, `Executing`, `Verifying`, `ReadyForReview`, `Completed`, `Failed`, `Cancelled`) map to known states and `FutureServerState` maps to `JobState.Unknown("FutureServerState")`.

- [ ] **Step 2: Run the model test and verify it fails** with the model types absent.

Run: `./gradlew testDebugUnitTest --tests com.jhaago.cadagent.model.JobStateTest`

Expected: FAIL before implementation.

- [ ] **Step 3: Scaffold the single-module Compose project and implement the domain model** using `java.time.Instant` timestamps, string job/revision IDs, `JobState.Unknown(rawValue: String)`, and an `AgentAvailability` value that distinguishes `Available`, `Unavailable`, `NotConfigured`, and `Unknown`.

- [ ] **Step 4: Run unit tests and assemble a debug APK.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL and `JobStateTest` PASS.

- [ ] **Step 5: Commit.**

```bash
git add .
git commit -m "build: scaffold Android CAD Agent app"
```

### Task 2: Implement the repository contract and deterministic fake job lifecycle

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/data/CadAgentRepository.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/data/CadAgentError.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/data/FakeCadAgentRepository.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/data/FakeJobProgression.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/data/FakeCadAgentRepositoryTest.kt`

**Interfaces:**
- Consumes: `CadJob`, `JobRevision`, `JobState`, `AgentStatus` from Task 1.
- Produces: `CadAgentRepository` with `val agentStatus: StateFlow<AgentStatus>`, `val jobs: StateFlow<List<CadJob>>`, `suspend fun createJob(prompt: String): Result<CadJob>`, `suspend fun getJob(jobId: String): Result<CadJob>`, `suspend fun approveJob(jobId: String, revisionId: String): Result<CadJob>`, `suspend fun requestChanges(jobId: String, revisionId: String, instructions: String): Result<CadJob>`, and `suspend fun cancelJob(jobId: String): Result<CadJob>`.
- Produces: `FakeJobProgression` abstraction so tests can manually drive `Executing -> Verifying -> ReadyForReview -> Completed` without sleeping; the app fake may use a short coroutine-driven progression policy, but UI correctness must not depend on timing.

- [ ] **Step 1: Write failing repository tests** for seeded jobs, most-recently-updated ordering, create-job validation, revision-bound approval, stale revision rejection, request-changes creating a new revision, cancellation, and manual execution progression through all required states.

- [ ] **Step 2: Run repository tests and verify they fail.**

Run: `./gradlew testDebugUnitTest --tests com.jhaago.cadagent.data.FakeCadAgentRepositoryTest`

Expected: FAIL before repository implementation.

- [ ] **Step 3: Implement the minimal repository/error/progression types** with deterministic seeded jobs representing Awaiting Approval, Executing, Completed, Awaiting Clarification, Failed, and Cancelled states. A newly created fake job gets a deterministic human-readable CAD plan revision and enters `AwaitingApproval`.

- [ ] **Step 4: Run repository and full unit tests.**

Run: `./gradlew testDebugUnitTest`

Expected: BUILD SUCCESSFUL; repository tests PASS.

- [ ] **Step 5: Commit.**

```bash
git add app/src/main/java/com/jhaago/cadagent/data app/src/test/java/com/jhaago/cadagent/data
git commit -m "feat: add fake CAD Agent repository"
```

### Task 3: Add dependency wiring, navigation, Home, and Jobs screens

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/app/CadAgentContainer.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/CadAgentApp.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/navigation/Destination.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/components/JobStateBadge.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/home/HomeViewModel.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/home/HomeScreen.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/jobs/JobsViewModel.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/jobs/JobsScreen.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/MainActivity.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/ui/home/HomeViewModelTest.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/ui/jobs/JobsViewModelTest.kt`
- Test: `app/src/androidTest/java/com/jhaago/cadagent/ui/AppNavigationTest.kt`

**Interfaces:**
- Consumes: `CadAgentRepository` from Task 2.
- Produces: `CadAgentContainer(repository: CadAgentRepository)`; stable destinations `home`, `jobs`, `settings`, `newJob`, and `job/{jobId}`; Home and Jobs screen state flows.

- [ ] **Step 1: Write failing ViewModel tests** asserting Home exposes agent/SOLIDWORKS status plus recent jobs and Jobs exposes repository jobs newest-first with loading/content/empty/error state types.

- [ ] **Step 2: Run the new unit tests and verify they fail.**

Run: `./gradlew testDebugUnitTest --tests '*HomeViewModelTest' --tests '*JobsViewModelTest'`

Expected: FAIL before ViewModels exist.

- [ ] **Step 3: Implement manual dependency wiring, ViewModels, shared state badge, bottom-level Home/Jobs/Settings navigation shell, and the Home/Jobs Compose screens.** Home must show fake agent/PC status, SOLIDWORKS status, a prominent New Job action, and recent jobs; Jobs must render all current jobs and navigate by job ID.

- [ ] **Step 4: Write and run a Compose navigation test** asserting the app opens Home, can navigate to Jobs, and can return without losing repository-backed content.

Run: `./gradlew connectedDebugAndroidTest`

Expected: `AppNavigationTest` PASS on an emulator/device.

- [ ] **Step 5: Run unit tests and assemble.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 6: Commit.**

```bash
git add app/src
git commit -m "feat: add Android app shell and job lists"
```

### Task 4: Implement New Job and Job Detail approval workflow

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/ui/newjob/NewJobViewModel.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/newjob/NewJobScreen.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/jobdetail/JobDetailViewModel.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/jobdetail/JobDetailScreen.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/ui/CadAgentApp.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/ui/newjob/NewJobViewModelTest.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/ui/jobdetail/JobDetailViewModelTest.kt`
- Test: `app/src/androidTest/java/com/jhaago/cadagent/ui/JobWorkflowTest.kt`

**Interfaces:**
- Consumes: `CadAgentRepository.createJob/getJob/approveJob/requestChanges/cancelJob` and revision IDs from Tasks 1-2.
- Produces: New Job submit state; Job Detail state containing the displayed job/current revision; UI actions `submit`, `approve`, `requestChanges`, `cancel`, and `retry/reload` where valid.

- [ ] **Step 1: Write failing ViewModel tests** for blank prompt rejection, successful job creation, duplicate-submit gating, valid revision approval, stale revision recovery, request-changes creating/displaying the new revision, duplicate action gating, cancel-state rules, and invalid/missing job ID producing a recoverable not-found UI state.

- [ ] **Step 2: Run the focused tests and verify they fail.**

Run: `./gradlew testDebugUnitTest --tests '*NewJobViewModelTest' --tests '*JobDetailViewModelTest'`

Expected: FAIL before implementation.

- [ ] **Step 3: Implement New Job and Job Detail ViewModels/screens.** Job Detail must show the full prompt, current state, revision identity, plan actions, ambiguity/validation information, and only enable Approve/Request Changes/Cancel when allowed. Approve and Request Changes must always send the revision ID actually displayed by the screen.

- [ ] **Step 4: Add the Compose workflow test** covering Home -> New Job -> submit -> Job Detail -> approve and a separate request-changes path.

Run: `./gradlew connectedDebugAndroidTest`

Expected: `JobWorkflowTest` PASS on an emulator/device.

- [ ] **Step 5: Run all unit tests and assemble.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 6: Commit.**

```bash
git add app/src
git commit -m "feat: add mobile CAD job approval workflow"
```

### Task 5: Add Settings placeholder, resilient presentation, and engineering-tool polish

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/ui/settings/SettingsScreen.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/theme/Theme.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/ui/components/ScreenState.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/ui/CadAgentApp.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/ui/components/JobStateBadge.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/ui/JobPresentationTest.kt`
- Test: `app/src/androidTest/java/com/jhaago/cadagent/ui/SettingsAndUnknownStateTest.kt`

**Interfaces:**
- Consumes: Task 1 unknown states and Task 3 navigation.
- Produces: Settings placeholder explicitly stating remote connection is not configured in this milestone; consistent loading/content/empty/recoverable-error presentation; safe human-readable labels for every known and unknown `JobState`.

- [ ] **Step 1: Write failing presentation tests** asserting every known state has a stable user label and `JobState.Unknown("FutureServerState")` renders a neutral `FutureServerState`/unknown-state label rather than throwing.

- [ ] **Step 2: Run the focused test and verify failure.**

Run: `./gradlew testDebugUnitTest --tests '*JobPresentationTest'`

Expected: FAIL before presentation mapping exists.

- [ ] **Step 3: Implement Settings, screen-state components, restrained Material 3 engineering-tool theme, and resilient state presentation.** Settings must not ask for an Agent Host URL, API key, login, or any secret; it may only explain that secure remote pairing/connectivity is a future milestone.

- [ ] **Step 4: Add the UI test** that opens Settings and renders a synthetic unknown job state without crashing.

Run: `./gradlew connectedDebugAndroidTest`

Expected: `SettingsAndUnknownStateTest` PASS on an emulator/device.

- [ ] **Step 5: Run unit tests and assemble.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 6: Commit.**

```bash
git add app/src
git commit -m "feat: polish Android CAD Agent shell"
```

### Task 6: Add CI, project documentation, and milestone verification

**Files:**
- Create: `.github/workflows/android.yml`
- Create: `README.md`
- Create: `.gitignore`
- Test/verify: entire Android project

**Interfaces:**
- Consumes: complete app from Tasks 1-5.
- Produces: reproducible CI build/test workflow and concise setup/run documentation for Android Studio and command-line use.

- [ ] **Step 1: Add CI** that installs JDK 17 and Android SDK requirements, runs `./gradlew testDebugUnitTest assembleDebug`, and does not require secrets or a SOLIDWORKS machine.

- [ ] **Step 2: Add README documentation** covering architecture, fake-data limitation, local build/run, test commands, relationship to `jhaago/SolidWorks-CAD-Agent`, and an explicit statement that remote connectivity/OpenAI credentials are not implemented in this milestone.

- [ ] **Step 3: Run the full local verification suite.**

Run: `./gradlew clean testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL with all unit tests PASS and a debug APK produced.

- [ ] **Step 4: Run connected UI tests when an emulator/device is available.**

Run: `./gradlew connectedDebugAndroidTest`

Expected: all Compose/UI tests PASS. If the execution environment has no Android emulator/device, record this as an unexecuted device-only gate rather than claiming it passed.

- [ ] **Step 5: Inspect the repository for secrets and forbidden connectivity.**

Verify there is no OpenAI key, production credential, external Agent Host URL, `INTERNET` permission, or code that exposes/connects to the Windows localhost service.

- [ ] **Step 6: Commit.**

```bash
git add .github README.md .gitignore
git commit -m "ci: verify Android companion app"
```

## Milestone Acceptance

The implementation is complete when the Android repository builds an installable debug APK; Home, Jobs, New Job, Job Detail, and Settings are navigable; fake jobs can be created/reviewed/approved/revised/cancelled; job history stays consistent; tests cover the repository and ViewModel lifecycle; unknown job states and stale revisions fail safely; and the project contains no live network integration or secrets.