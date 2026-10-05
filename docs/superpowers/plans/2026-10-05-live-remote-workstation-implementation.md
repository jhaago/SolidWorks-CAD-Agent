# Live Remote Workstation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for native execution, or superpowers:subagent-driven-development if Jordan selects that method. Steps use checkbox syntax for tracking.

**Goal:** Deliver paired Android viewing and manual control of the home Windows desktop, including away access through separately installed Tailscale.

**Architecture:** A separate loopback Windows RemoteAgent owns pairing, capture, session authority and input. HTTPS via Tailscale Serve reaches its versioned protocol. Android Live adapters implement the existing Remote boundaries alongside the preserved demo; CAD Host and COM bridge remain independent.

**Tech Stack:** C#/.NET Framework 4.8, Windows Forms, HttpListener, Newtonsoft.Json, DPAPI, SendInput, System.Drawing; existing Kotlin/Compose/coroutines stack, platform HTTPS APIs and Android Keystore. Existing MSTest and JUnit/Compose testing.

**Spec:** `docs/superpowers/specs/2026-10-05-live-remote-workstation-design.md` (approved).

## Global constraints

- Windows `feature/v1-solidworks-2020`, Android `feature/android-v0`; no main merge.
- No SOLIDWORKS dependency in RemoteAgent; existing native plate acceptance stays pending.
- Loopback listener only, external HTTPS with normal certificate validation; no Funnel/public exposure.
- Ordinary HTTPS transport contracts; no Tailscale SDK, account or identity assumptions in app logic.
- Pairing secret: random 256 bits, single-use, two minutes, local approval.
- Session token: random, five minutes; heartbeat every second, control revoked after three seconds.
- JPEG: maximum five frames/second, 1600-pixel longest edge, 2 MB encoded; one frame request in flight.
- Reconnect delays: 1, 2, 4, 8, then 15 seconds; foreground only, explicit Resume Control.
- Live Manual only; demo Assist/Agent preserved and labelled separately.
- No recording, clipboard, file transfer, shell, printer commands or new CAD capabilities.

## Review focus

- An uncertain HTTP result must never replay an input event (Task 7).
- Pairing polling by someone knowing only a request ID must not reveal credentials (Tasks 2, 4).
- Monitor scaling/layout changes must reject stale coordinates (Tasks 3, 5).
- Background, rotation and cancelled gestures must not leave a button held (Task 8).
- Failed persistence must not report successful pairing or lose revocation (Tasks 2, 6).

## File map

Windows paths below are relative to `SolidWorks-CAD-Agent`; Android paths to `SolidWorks-CAD-Agent-Android`.

- `src/SolidWorksCadAgent.Contracts/Remote/RemoteDtos.cs`: versioned JSON DTOs and bounded protocol values.
- `src/SolidWorksCadAgent.Core/Remote/`: clock, credential, capture/input interfaces; pairing/session coordinators.
- `src/SolidWorksCadAgent.RemoteAgent/`: new executable; `Security/`, `Host/`, `Platform/`, `MainForm.cs`, `Program.cs`.
- `tests/SolidWorksCadAgent.UnitTests/Remote/`: deterministic protocol, pairing, session, HTTP and platform-mapping tests.
- Android `remote/live/`: transport, credential store, live session/display/input adapters and connection driver.
- Android `remote/ui/`: workstation setup UI and existing screen/ViewModel changes; `di/AppContainer.kt` wiring.
- Existing solution, bundle scripts, validators and CI workflows: compilation, tests and packaging.

Each task uses a failing focused test before implementation, then runs its relevant complete suite and commits only its own files. Windows command convention is `dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj -c Release --filter FullyQualifiedName~Remote`; execute .NET Framework tests on Windows CI when unavailable locally. Android command convention is `gradle testDebugUnitTest --tests '<class>'` from the Android repository, with installed Gradle 9.6.0/JDK 17 as existing CI uses.

### Task 1: Versioned protocol and boundaries

**Files:** create Contracts `Remote/RemoteDtos.cs`; Core `Remote/RemoteInterfaces.cs`; test `Remote/RemoteContractTests.cs`.

**Interfaces:** `IRemoteClock.UtcNow: DateTimeOffset`; `IRemoteInputSink.Apply(RemoteInputEvent): bool` and `ReleaseAll(): bool`; `IRemoteCapture.Capture(): RemoteCapturedFrame`. DTOs: `RemoteSessionSnapshot` (sessionId, authorityEpoch, controlling, expiresAt), `RemoteInputEvent` (sessionId, authorityEpoch, sequence, displayGeneration, kind, x, y, button, key, scroll), `RemoteCapturedFrame` (frameId, displayGeneration, width, height, capturedAt, cursorX, cursorY, jpegBytes). JSON frame response has a bounded base64 image representation.

- [ ] Write `DtoSerializationUsesCamelCase`: serialized session contains `sessionId`/`authorityEpoch`, frame contains `displayGeneration`/`capturedAt`, no PascalCase aliases. `InputValidationRejectsInvalidValues`: reject NaN/infinity, coordinates outside [0,1], unknown enum/key, negative sequence and oversized strings; allow defined pointer and keyboard events.
- [ ] Run Remote contract filter; confirm failure from missing contracts.
- [ ] Implement DTOs, bounded validator and interfaces; use the same validation before server injection and Android enqueue. Keys are named protocol identifiers, never arbitrary Windows key codes. Define explicit arrows, navigation, modifiers, letters, digits and function keys; exclude Ctrl+Alt+Delete.
- [ ] Run focused tests green; commit `feat: define remote workstation protocol contracts`.

### Task 2: Pairing, approval, storage and revocation

**Files:** Core `Remote/RemotePairingCoordinator.cs`, `Remote/RemoteCredentialRecord.cs`; new agent `Security/DpapiDeviceCredentialStore.cs`; tests `Remote/RemotePairingTests.cs`, `Remote/RemoteCredentialStoreTests.cs`.

**Interfaces:** `IRemoteCredentialStore.Load(): IReadOnlyList<RemoteCredentialRecord>` and `Save(IReadOnlyList<RemoteCredentialRecord>): void`; injected `IRemoteSecretGenerator.Create(): byte[]` returns 32 bytes. `OpenPairing(): PairingWindow`; `RequestPairing(secret, deviceName): PairingReceipt`; `Approve(requestId): void`; `Poll(requestId, receiptSecret): PairingResult`; `Revoke(deviceId): void`; `Authenticate(deviceId, credential): bool`. Receipt secret is independently random and required to poll; request ID alone is insufficient. Coordinator exposes a revocation event consumed by Task 3.

- [ ] Write `PairingRequiresLocalApprovalAndReceiptSecret` asserting no credential before approval or with a wrong receipt; after approval the valid poll returns one credential once. Write `PairingExpiresAfterTwoMinutesAndIsSingleUse`, `PairingRateLimitClosesWindowAfterFiveFailedAttempts`, `RevokePersistsBeforeReturningSuccess`, `StoreFailureCannotReportEnrollmentSuccess`; clock and failing stores are fakes.
- [ ] Run tests red; implement constant-time verifier checking, credential hashing, single-use receipts and bounded pending requests (one active request, device label max 80 printable characters). Persist before issuing credentials; fail closed on corrupted storage. Device count is bounded to 10 with actionable full-list response.
- [ ] Write and run DPAPI round-trip/corrupt-file Windows tests red then implement user-scoped atomic file replacement. Never include token values in ToString, errors or logging. Revocation failure disables remote control until storage is repaired rather than pretending revocation persisted.
- [ ] Run pairing/store tests green; commit `feat: add locally approved remote device pairing`.

### Task 3: Session authority and input release

**Files:** Core `Remote/RemoteSessionCoordinator.cs`; tests `Remote/RemoteSessionTests.cs`.

**Interfaces:** `CreateSession(deviceId, credential): SessionGrant`; `RenewSession(sessionToken): SessionGrant`; `Heartbeat(sessionToken): RemoteSessionSnapshot`; `ResumeControl(sessionToken): RemoteSessionSnapshot`; `TakeControl(sessionToken): RemoteSessionSnapshot`; `SubmitInput(sessionToken, event): RemoteInputResult`; `StopRemote(): void`; `Close(sessionToken): void`; `Tick(): void`; `UpdateDisplayGeneration(long): void`. Session creation/reconnection starts view-only until Resume Control; the Windows approved design's Manual/User mode refers to human authority, not automatic input enablement. Renew requires the device credential as specified, rather than only the old token: final signature `RenewSession(sessionToken, deviceId, credential): SessionGrant`.

- [ ] Write `ExpiredOrRevokedTokenCannotReadOrControl`, `SecondDeviceCannotDisplaceController`, `DuplicateAndWrongEpochInputNeverCallsSink`, `HeartbeatLossAtThreeSecondsReleasesAndRevokes`, `DisplayGenerationChangeReleasesAndRejectsOldInput`, `StopRacingInputCannotInjectAfterRevocation`, `FailedReleaseDisablesControl`, `ReconnectRequiresResumeAndClearsHeldInput`. Assert sink call count and final authority, with fake clock and synchronized race barriers.
- [ ] Run tests red; implement five-minute token grants, lease watchdog state, strict sequence consumption and synchronized sink/release operations. All errors revoke authority when delivery is uncertain; no automatic event retry. Use one release path for close, stop, revocation, expiry and adapter failure.
- [ ] Run session tests green, including renew using valid device credential and cancellation idempotence; commit `feat: enforce remote session authority and input leases`.

### Task 4: Loopback host and sanitized HTTP protocol

**Files:** new agent csproj, `Host/RemoteHttpServer.cs`, `Host/RemoteRoutes.cs`, `Host/RemoteRequestPolicy.cs`; solution/unit-test project wiring; tests `Remote/RemoteHttpTests.cs`.

**Interfaces:** server consumes Tasks 1–3; `RemoteHttpServer.Start(Uri loopbackPrefix): void`, `Dispose(): void`; `RemoteRoutes.Handle(RemoteRequest): RemoteResponse`. Versioned `/remote/v1/` routes for pair/request, pair/status, session/create, session/renew, session/status, session/heartbeat, session/resume, session/take-control, session/close, display/frame, input. Local Stop Remote is not anonymously exposed as an HTTP route.

- [ ] Write loopback integration tests `UnauthenticatedFrameAndInputAreRejected`, `PairingPollRequiresReceiptAuthorization`, `NonLoopbackConfigurationIsRejected`, `BodiesAndFrameResponsesAreBounded`, `ErrorsNeverEchoSecrets`, `UnsupportedVersionAndMethodAreRejected` against fake platform adapters.
- [ ] Run red; implement 64 KB request cap, bounded concurrency, JSON content-type checks, no CORS grants, no cookie auth and no query-token auth. Authorization headers select pairing receipt/device/session credential explicitly. Frame response max 3 MB after base64 encoding; raw image cap stays 2 MB. Read/write deadlines avoid blocking stop/watchdog.
- [ ] Run green on Windows, start/close host repeatedly without leaked listeners; commit `feat: host authenticated remote protocol on loopback`.

### Task 5: Real Windows capture, input and local stop window

**Files:** agent `Platform/WindowsDesktopCapture.cs`, `Platform/WindowsInputSink.cs`, `Platform/DesktopCoordinateMapper.cs`, `Platform/RemoteStopHotkey.cs`, `MainForm.cs`, `Program.cs`; tests `Remote/DesktopCoordinateMapperTests.cs`, `Remote/FrameBudgetTests.cs`.

**Interfaces:** implementations of Task 1 adapters; `DesktopCoordinateMapper.Map(x, y, monitorBounds, virtualDesktopBounds): ScreenPoint`; `MainForm` consumes coordinators and owns hotkey lifecycle. Frame cache latest-only, independent watchdog timer calls Tick.

- [ ] Write `MappingHandlesNegativeMonitorOriginsAndEdges`, `GenerationChangesOnMonitorLayoutOrScale`, `ResizePreservesAspectRatioAnd1600Limit`, `FrameCacheDropsObsoleteFrames`, `OversizedJpegIsRejected` asserting exact edge mapping and bounded dimensions/bytes.
- [ ] Run red; implement DPI-aware primary-monitor capture to in-memory JPEG, explicit normalized cursor data and stable generation metadata. Capture failure yields unavailable, never stale success. Track only injected held keys/buttons; release on authority loss. Verify SendInput return count; partial sends end control and attempt release. Do not release unrelated locally held keys.
- [ ] Add pairing/device approval/revocation, Stop Remote, Ctrl+Alt+F12 and actionable access/UAC/locked-desktop errors to MainForm. Hotkey failure is displayed. Set DPI awareness before creating windows; stop on display generation change.
- [ ] Compile executable and run mapping/cache tests green. Mark actual capture/input/hotkey behaviour pending Windows device tests; commit `feat: add Windows desktop adapters and remote stop controls`.

### Task 6: Android HTTPS endpoint and encrypted pairing store

**Files:** `remote/live/RemoteEndpoint.kt`, `RemoteTransport.kt`, `HttpsRemoteTransport.kt`, `RemoteCredentialStore.kt`, `KeystoreRemoteCredentialStore.kt`; Android manifest/network backup configuration; tests under `remote/live/`.

**Interfaces:** suspending `RemoteTransport.call(operation: RemoteOperation): RemoteResponse`; `RemoteCredentialStore.read(origin): PairedWorkstation?`, `write(workstation): Unit`, `delete(origin): Unit`; `RemoteEndpoint.parse(value): RemoteEndpoint` provides canonical HTTPS origin/base path. Network implementation uses bounded platform HTTPS connections on Dispatchers.IO, no new VPN SDK.

- [ ] Write endpoint tests rejecting HTTP, userinfo, fragments, queries, changed credential origins and malformed URLs. Write transport tests asserting secrets only in headers, no automatic input retries, response byte cap/deadline and sanitized errors. Write store tests `FailedWriteDoesNotMarkPaired` and `ForgetInvalidatesConnectionBeforeDeleting`.
- [ ] Run tests red; implement protocol encoding matching Task 1 fixtures, TLS verification and bounded body decoding. Disable redirects for authenticated requests to avoid cross-origin credential forwarding. Bound control response to 64 KB and frame response to 3 MB, verify raw JPEG/dimensions after decode.
- [ ] Add Android Keystore AES-GCM storage with no plaintext fallback; exclude pairing data from cloud/device-transfer backup. Add emulator round-trip/deletion/corrupt-record tests and fail closed when the key is unavailable.
- [ ] Run green; commit `feat: add Android remote transport and encrypted pairing storage`.

### Task 7: Live session, display, ordered input and retries

**Files:** `remote/live/LiveRemoteSessionRepository.kt`, `LiveRemoteDisplaySource.kt`, `LiveRemoteInputController.kt`, `LiveConnectionDriver.kt`; extend model/display interfaces narrowly; tests under `remote/live/`.

**Interfaces:** implement existing RemoteSessionRepository, RemoteDisplaySource, RemoteInputController. Add explicit live capability/status values (view-only, resume, stale and unsupported AI), keeping demo defaults compatible. Connection driver accepts foreground state and injectable scheduler/clock. `resumeControl()` requests fresh human control; display holds immutable bytes+metadata and newest generation.

- [ ] Write `ReconnectBackoffIsOneTwoFourEightFifteen`, `DisconnectCancelsLateCompletion`, `BackgroundStopsRetryAndReleasesInput`, `ReconnectNeverResendsInputAndRequiresResume`, `FramePollingHasOneInflightAndFiveFpsLimit`, `StaleFrameDisablesInput`, `WrongGenerationCannotEnqueue`, `QueueOverflowStopsControl`, `ReleaseCannotBeDropped`, `InputTimeoutDoesNotRetry`.
- [ ] Run red with coroutine test time and fake transport; implement separate heartbeat/frame/input work, five-frame cap, latest-image replacement and bounded 100-event input queue. Coalesce unsent move events, never Down/Up. Stop uses independent request path and local cancellation; three-second server watchdog remains fallback when transport is broken.
- [ ] Add token renewal before expiry, busy/revoked/unsupported errors and new session IDs on reconnect. Preserve display-only reconnect; invalidate origin changes and cancellations using attempt identity.
- [ ] Run green and full Android unit suite; commit `feat: connect live remote adapters with safe reconnect and ordered input`.

### Task 8: Main app live UI and lifecycle

**Files:** `di/AppContainer.kt`; `remote/ui/RemoteViewModel.kt`, `RemoteScreen.kt`, new `WorkstationSettingsScreen.kt`; `RemoteDisplaySurface.kt`, `RemoteControlBar.kt`; app navigation wiring; unit/Compose tests.

**Interfaces:** shared container selects Demo/Live only while disconnected; ViewModel consumes common adapters and live capability flags. Endpoint/pairing editor accepts copy/paste secret; credential content never appears in status/error semantics. Render JPEG image and cursor using existing aspect-fit viewport.

- [ ] Write Compose flows for pairing pending/local rejection/success, Live showing real frame fixture, Resume Control, stop/disconnect, stale image and unavailable AI. Assert existing CAD jobs survive switching. Write device tests for rotation, background during connect, ViewModel disposal and drag cancellation; assert release called and authority not restored automatically.
- [ ] Run tests red; implement labelled Demo/Live selection, endpoint/pairing/forget settings and clear connection errors. On live sessions show Manual, Resume/Take Control, Stop Remote and Disconnect; disable unavailable AI task UI. Do not show simulated task advancement or success on live sessions.
- [ ] Wire lifecycle handling to stop foreground network work/release input on background, preserve configuration state and start disconnected after process restart. Await authority acknowledgement before enabling controls.
- [ ] Run full unit/Compose emulator suites and `gradle assembleDebug lintDebug`; commit `feat: add paired live workstation controls to Android Remote`.

### Task 9: Packaging, regression gates and physical test guide

**Files:** Windows `.github/workflows/unit-tests.yml`, bundle scripts/validators, new `scripts/register-remote-agent-url.ps1`, `docs/live-remote-windows-testing.md`; Android CI lint/report steps and `docs/remote-workstation.md`; READMEs.

**Interfaces:** bundle includes separate RemoteAgent executable and required dependencies, without changing CAD Host exposure or native capability claims. URL registration script reserves only the fixed loopback RemoteAgent prefix for current user and is an explicitly documented elevated setup action; errors identify missing URL ACL, stale process or unsupported configuration without killing processes.

- [ ] Add failing validator tests requiring new executable, isolated listener/setup instructions and no bundled credentials. Run red, extend builder/validator and actionable file-lock process names, then green. Existing bundle checks still pass.
- [ ] Document Tailscale Windows/Android installation, private HTTPS Serve setup, no Funnel, pairing/local approval, normal start/stop, revocation and cleanup. Provide exact implemented port/commands, ordinary-PC interactive-session prerequisites and troubleshooting. Explain five-FPS limitation and independently pending SOLIDWORKS acceptance.
- [ ] Run complete Windows unit suite, all non-COM integrations, solution build, script tests and bundle validation on Windows CI. Run Android complete unit/build/lint and emulator tests; inspect reports, not only compilation. Perform one whole-branch independent review using the review skill, address material findings and rerun affected gates.
- [ ] Deliver CI-built APK and Windows bundle with actual verification counts, source commits and separate pending physical checklist. Persist user artifacts; publish source only to existing feature branches. Mark real capture/input/mobile-data acceptance pending until Jordan tests it. Commit `build: package and document first live remote workstation version`.

## Plan self-review

All approved spec sections map to Tasks 1–9. Protocol consistency uses shared JSON fixtures; transport remains HTTPS-only with no provider SDK. Pairing poll requires a separate receipt secret, credentials are issued only once after successful persistence, and revocation storage errors fail closed. Stop/input races, viewport generation, input replay, background cancellation and device backup are assigned explicit tests. Windows physical behaviour and native SOLIDWORKS remain separate unclaimed gates.

Execution recommendation: Native implementation in this session, followed by one independent whole-branch review. Tasks share protocol/security interfaces; keeping implementation context together reduces handoff cost. Jordan may instead select subagent-driven task execution at plan review.
