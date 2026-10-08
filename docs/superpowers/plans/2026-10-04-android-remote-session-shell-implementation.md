# Android Remote Session Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Remote Workstation feature into the main `CAD Agent` Android app now, using deterministic fake session/control backends so the UI, control modes, human-override behavior, and interfaces are established before live Windows streaming and input are connected.

**Architecture:** The existing Android companion keeps its CAD-job repository intact and gains separate remote-session and AI-control boundaries. Compose screens/ViewModels depend on `RemoteSessionRepository` and `AiControlRepository`; fake implementations drive the first slice. This plan deliberately establishes the product/UI contract before transport-specific WebRTC/capture/input work, so the later Windows Remote Workstation Agent can replace the fake data source without rewriting the Android experience.

**Tech Stack:** Existing Android companion stack: Kotlin; Jetpack Compose + Material 3; Navigation Compose; AndroidX Lifecycle/ViewModel; Kotlin coroutines/StateFlow; JUnit; kotlinx-coroutines-test; Compose UI tests.

**Spec:** `docs/superpowers/specs/2026-10-04-android-remote-workstation-design.md`

## Global Constraints

- Android source repository: `jhaago/SolidWorks-CAD-Agent-Android`; overall remote/CAD system architecture authority remains `jhaago/SolidWorks-CAD-Agent`.
- Remote Workstation is part of the existing `CAD Agent` Android app, not a second Android application.
- Preserve the existing CAD-job workflow and `CadAgentRepository`; remote-session code must use separate abstractions.
- The first slice uses fake remote-session/AI-control implementations only. It must not expose the Windows localhost Agent Host to the network.
- Control modes are `Manual`, `Assist`, and `Agent`.
- The current controller must always be visible to the user.
- `Take Control` / `Stop AI` must be available whenever AI control is active and must transition immediately to Manual mode.
- Human manual input while Agent mode is active is treated as an interruption; the AI must not continue fighting for pointer/keyboard control.
- Starting a physical 3D print is a protected action and cannot be auto-approved.
- No OpenAI API key, Windows credential, Bambu credential, or other production secret may be added to Android.
- SolidWorks engineering operations will continue to prefer the existing CAD Agent API once the live workstation backend is added.

## Review Focus

- An AI task interrupted by `Take Control` must never silently resume; pinned in Task 2 and Task 4 tests.
- A protected action request must remain pending until the user explicitly approves or rejects it; pinned in Task 2 and Task 4 tests.
- Losing the fake/real remote connection while AI control is active must leave the UI in a disconnected/non-controlling state; pinned in Task 2 and Task 3 tests.
- Repeated taps on Connect, Run AI Task, Take Control, Approve, or Reject must not duplicate state transitions; pinned in Task 2 and Task 4 tests.
- The CAD Home/Jobs workflow must remain usable when Remote is unavailable or disconnected; pinned in Task 3 navigation tests.

---

### Task 1: Add remote-session and AI-control domain contracts

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/remote/model/RemoteConnectionState.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/model/RemoteControlMode.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/model/RemoteController.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/model/RemoteWorkstationStatus.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/model/AiTaskState.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/model/ProtectedActionRequest.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/data/RemoteSessionRepository.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/data/AiControlRepository.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/remote/model/RemoteModelTest.kt`

**Interfaces:**
- Consumes: no transport-specific implementation.
- Produces: `RemoteControlMode { Manual, Assist, Agent }`; `RemoteController { User, Ai, None }`; `RemoteConnectionState`; `RemoteWorkstationStatus`; `AiTaskState`; `ProtectedActionRequest`.
- Produces: `RemoteSessionRepository` with observable session status plus `connect()`, `disconnect()`, `setMode(mode)`, and `takeControl()` operations.
- Produces: `AiControlRepository` with observable AI task/protected-action state plus `submitTask(instruction)`, `stopTask()`, `approveProtectedAction(id)`, and `rejectProtectedAction(id)` operations.

- [ ] **Step 1: Write `RemoteModelTest`** asserting all control modes/controllers are representable, disconnected state cannot claim an active controller, and protected-action requests have stable IDs plus explicit pending/approved/rejected disposition.

- [ ] **Step 2: Run the focused test and verify it fails** before the new model/contracts exist.

Run: `./gradlew testDebugUnitTest --tests '*RemoteModelTest'`

Expected: FAIL before implementation.

- [ ] **Step 3: Implement the minimal model and repository interfaces** without any network, screen-capture, input, or AI-provider dependency.

- [ ] **Step 4: Run unit tests.**

Run: `./gradlew testDebugUnitTest`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 5: Commit.**

```bash
git add app/src/main/java/com/jhaago/cadagent/remote app/src/test/java/com/jhaago/cadagent/remote
git commit -m "feat: add remote session domain contracts"
```

### Task 2: Implement deterministic fake remote and AI-control repositories

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/remote/data/FakeRemoteSessionRepository.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/data/FakeAiControlRepository.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/remote/data/FakeRemoteSessionRepositoryTest.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/remote/data/FakeAiControlRepositoryTest.kt`

**Interfaces:**
- Consumes: Task 1 contracts.
- Produces: deterministic fake connection transitions `Disconnected -> Connecting -> Connected` driven explicitly by tests.
- Produces: deterministic AI transitions `Idle -> Running -> AwaitingProtectedAction/Completed/Stopped`.
- Produces: `takeControl()` semantics that set mode/controller to `Manual/User`, stop active AI input authority, and leave the stopped task stopped.

- [ ] **Step 1: Write failing repository tests** covering connect/disconnect, duplicate connect gating, mode changes, AI task start, explicit stop, manual takeover, connection loss during Agent mode, and no automatic AI resume after takeover.

- [ ] **Step 2: Add protected-action tests** using a fake `Start3dPrint` request: the task must pause with the request pending, approval/rejection must be explicit, and duplicate approval/rejection must not create duplicate transitions.

- [ ] **Step 3: Run the focused tests and verify failure.**

Run: `./gradlew testDebugUnitTest --tests '*FakeRemoteSessionRepositoryTest' --tests '*FakeAiControlRepositoryTest'`

Expected: FAIL before fake implementations exist.

- [ ] **Step 4: Implement the deterministic fake repositories** using `StateFlow`; do not use wall-clock sleeps as a correctness requirement.

- [ ] **Step 5: Run all unit tests.**

Run: `./gradlew testDebugUnitTest`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 6: Commit.**

```bash
git add app/src/main/java/com/jhaago/cadagent/remote/data app/src/test/java/com/jhaago/cadagent/remote/data
git commit -m "feat: add fake remote workstation state"
```

### Task 3: Put Remote into the main Android navigation and app container

**Files:**
- Modify: `app/src/main/java/com/jhaago/cadagent/app/CadAgentContainer.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/ui/CadAgentApp.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/ui/navigation/Destination.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/ui/RemoteViewModel.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/ui/RemoteScreen.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/remote/ui/RemoteViewModelTest.kt`
- Modify/Test: `app/src/androidTest/java/com/jhaago/cadagent/ui/AppNavigationTest.kt`

**Interfaces:**
- Consumes: Task 1 repository contracts and Task 2 fakes.
- Produces: stable top-level `remote` destination in the existing app.
- Produces: `RemoteViewModel` state containing connection state, workstation identity/status, mode, current controller, AI task state, and pending protected action.

- [ ] **Step 1: Write failing `RemoteViewModelTest`** for disconnected, connecting, connected-manual, connected-agent, AI-running, protected-action, connection-loss, and take-control states.

- [ ] **Step 2: Run the test and verify it fails.**

Run: `./gradlew testDebugUnitTest --tests '*RemoteViewModelTest'`

Expected: FAIL before ViewModel implementation.

- [ ] **Step 3: Wire fake remote repositories into `CadAgentContainer` and add Remote to top-level navigation.** Preserve Home, Jobs, New Job, Job Detail, and Settings behavior.

- [ ] **Step 4: Implement the first `RemoteScreen` shell** with connection status, workstation card, a placeholder live-view surface, Manual/Assist/Agent mode selector, current-controller indicator, AI instruction entry area, and Take Control/Stop AI affordance.

- [ ] **Step 5: Extend `AppNavigationTest`** to prove Home/Jobs remain usable when Remote is disconnected and navigation to/from Remote does not reset CAD repository state.

Run: `./gradlew connectedDebugAndroidTest`

Expected: navigation tests PASS.

- [ ] **Step 6: Run unit tests and assemble.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 7: Commit.**

```bash
git add app/src
git commit -m "feat: add Remote workstation destination"
```

### Task 4: Implement human/AI handoff and protected-action UX

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/remote/ui/components/RemoteControlBar.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/ui/components/AiTaskPanel.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/ui/components/ProtectedActionDialog.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/remote/ui/RemoteScreen.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/remote/ui/RemoteViewModel.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/remote/ui/RemoteViewModelTest.kt`
- Test: `app/src/androidTest/java/com/jhaago/cadagent/remote/ui/RemoteControlFlowTest.kt`

**Interfaces:**
- Consumes: Task 2 fake transitions and Task 3 `RemoteViewModel`.
- Produces: visible controller state; `Take Control`; `Stop AI`; protected-action confirmation/rejection; AI instruction submit with duplicate-action gating.

- [ ] **Step 1: Extend ViewModel tests** so `Take Control` while AI is running immediately produces `Manual/User`, stops the active AI task, and never resumes it when the fake backend later emits stale task progress.

- [ ] **Step 2: Add tests** that a pending `Start3dPrint` protected action blocks AI completion until the user approves/rejects and that the dialog cannot auto-confirm.

- [ ] **Step 3: Implement control bar, AI task panel, and protected-action dialog.** The controller indicator must be visually prominent enough that the user can tell whether `You` or `AI` has control without opening another screen.

- [ ] **Step 4: Add `RemoteControlFlowTest`** covering connect -> Agent -> run fake task -> protected action -> reject; and connect -> Agent -> run task -> Take Control.

Run: `./gradlew connectedDebugAndroidTest`

Expected: `RemoteControlFlowTest` PASS.

- [ ] **Step 5: Run all tests and assemble.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 6: Commit.**

```bash
git add app/src
git commit -m "feat: add human AI remote-control handoff"
```

### Task 5: Add remote-display and input interfaces for the live backend

**Files:**
- Create: `app/src/main/java/com/jhaago/cadagent/remote/display/RemoteDisplaySource.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/input/RemoteInputController.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/input/RemotePointerEvent.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/input/RemoteKeyboardEvent.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/data/FakeRemoteDisplaySource.kt`
- Create: `app/src/main/java/com/jhaago/cadagent/remote/input/FakeRemoteInputController.kt`
- Modify: `app/src/main/java/com/jhaago/cadagent/remote/ui/RemoteScreen.kt`
- Test: `app/src/test/java/com/jhaago/cadagent/remote/input/RemoteInputControllerTest.kt`

**Interfaces:**
- Consumes: session control state from Tasks 1-4.
- Produces: a display-source boundary suitable for a later streamed video/render surface without binding Compose to a particular transport library.
- Produces: input methods for normalized pointer coordinates, click/down/up, drag/move, scroll, and keyboard events.

- [ ] **Step 1: Write failing input tests** asserting input is accepted only when connected and controller is `User`, rejected while AI owns control, and immediately accepted after `takeControl()`.

- [ ] **Step 2: Run the focused tests and verify failure.**

Run: `./gradlew testDebugUnitTest --tests '*RemoteInputControllerTest'`

Expected: FAIL before implementation.

- [ ] **Step 3: Implement transport-agnostic display/input interfaces plus fakes.** Do not add a production WebRTC/socket dependency in this task.

- [ ] **Step 4: Replace the static placeholder with a fake display surface** capable of rendering deterministic sample frames/labels and accepting basic tap/drag/scroll gestures through the fake input controller.

- [ ] **Step 5: Run unit tests and assemble.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

- [ ] **Step 6: Commit.**

```bash
git add app/src
git commit -m "feat: define remote display and input seams"
```

### Task 6: Document the next live-Windows implementation boundary

**Files:**
- Modify: `README.md`
- Create: `docs/remote-workstation.md`

**Interfaces:**
- Consumes: Tasks 1-5.
- Produces: Android-repository documentation that points to the authoritative system design and records the exact next backend milestone.

- [ ] **Step 1: Document the Remote feature** as part of the same app and describe Manual/Assist/Agent modes plus protected actions.

- [ ] **Step 2: Record the next implementation milestone** as a separate Windows-side plan: Remote Workstation Agent -> real desktop capture -> authenticated remote transport -> manual input -> AI observation/UI Automation -> SolidWorks routing/Bambu Studio validation.

- [ ] **Step 3: Explicitly state** that the current Android slice uses fake session/display/input backends and must not be represented as a production remote-control connection.

- [ ] **Step 4: Run the full Android verification.**

Run: `./gradlew testDebugUnitTest assembleDebug`

Expected: BUILD SUCCESSFUL.

Run when an emulator/device is available: `./gradlew connectedDebugAndroidTest`

Expected: all navigation/workflow tests PASS.

- [ ] **Step 5: Commit.**

```bash
git add README.md docs/remote-workstation.md
git commit -m "docs: define remote workstation next milestone"
```

## Completion Gate

Do not claim this slice provides real remote desktop access until a later Windows/backend plan has implemented and physically tested capture, transport, authentication/pairing, and input injection.

This slice is complete when the Android app visibly contains Remote, its Manual/Assist/Agent workflow can be exercised end-to-end with deterministic fakes, human takeover is enforced by tests, protected actions are explicit, and the display/input seams are ready for the real Windows Remote Workstation Agent.
