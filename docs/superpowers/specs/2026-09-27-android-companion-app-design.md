# SolidWorks CAD Agent Android Companion App Design

Date: 2026-09-27
Status: Approved design, pending implementation plan
Companion repository: `jhaago/SolidWorks-CAD-Agent-Android`
Authoritative system repository: `jhaago/SolidWorks-CAD-Agent`

## 1. Purpose

Create the initial structure of a native Android companion app for SolidWorks CAD Agent so the mobile workflow can be designed, built, and tested before remote access to the Windows Agent Host is enabled.

The first Android slice is intentionally independent of a live SOLIDWORKS installation. It must provide a convincing end-to-end mobile workflow using fake data while establishing interfaces that can later be backed by the real remote service without rewriting the UI.

The long-term user workflow is:

1. Submit a natural-language CAD request from Android.
2. Allow the CAD planning system to produce a structured plan.
3. Review the proposed plan and job state on Android.
4. Approve the current plan revision or request changes.
5. Observe execution and verification status.
6. Review the completed result and, later, preview or retrieve generated artifacts.

The Android app is a client of the SolidWorks CAD Agent system. It does not contain the OpenAI API key, perform CAD planning itself, or communicate directly with SOLIDWORKS.

## 2. Repository and ownership boundaries

The Android app lives in its own repository:

`jhaago/SolidWorks-CAD-Agent-Android`

The existing repository remains authoritative for the overall system architecture and Agent Host behavior:

`jhaago/SolidWorks-CAD-Agent`

This design specification lives in the system repository because the Android application depends on the Agent Host contract and future remote-access architecture.

The repositories have independent build systems and release lifecycles. Android/Gradle tooling must not be added to the .NET/SOLIDWORKS solution merely to share source files.

## 3. Technology choice

The Android application will be native Kotlin using Jetpack Compose.

Use a conventional layered Android architecture:

- Compose screens and reusable UI components
- screen-level ViewModels
- domain-facing repository interface
- data-source/API interface behind the repository
- a fake implementation for the first milestone
- a real remote implementation added later

The first implementation should prefer Android platform and Jetpack components over additional frameworks unless a dependency provides clear value.

## 4. First milestone scope

The first milestone is an installable Android application that works entirely against a fake CAD Agent data source.

It includes these screens and flows:

### 4.1 Home

Show:

- app identity
- simulated PC/Agent status
- simulated SOLIDWORKS status
- prominent new-job action
- recent jobs with state indicators

### 4.2 New Job

Allow the user to:

- enter a natural-language CAD request
- submit it to the repository
- receive a newly created mock job
- navigate to that job's detail screen

A submitted fake job should exercise realistic lifecycle states rather than immediately jumping to completed.

Attachments are not implemented in this milestone. The architecture must not prevent adding images, sketches, or files later.

### 4.3 Job History

Show a list of jobs ordered with most recently updated first.

Each item should expose enough information to identify the request and understand its current state, including:

- prompt summary
- state
- simulated/remote indicator where relevant
- update time

The screen should be designed so cursor-based pagination can be added without redesigning it.

### 4.4 Job Detail / Approval

Show:

- full prompt
- current job state
- current plan revision
- human-readable proposed CAD actions
- validation/ambiguity state
- execution or completion information when applicable

When the job is awaiting approval, support:

- Approve
- Request Changes
- Cancel where allowed

Approval must conceptually apply to the displayed revision, matching the Agent Host rule that approval is bound to the current plan revision.

Request Changes must collect instructions and create a newer fake revision rather than silently mutating the approved revision.

### 4.5 Settings

For the first milestone, settings are local UI configuration only. Include a place for future remote connection configuration, but do not implement Internet exposure, authentication, OpenAI credentials, or direct Agent Host addresses.

The Android application must never ask the user for the OpenAI API key. The key belongs on the trusted Agent Host side.

## 5. Navigation

Initial top-level destinations:

- Home
- Jobs
- Settings

New Job and Job Detail are nested destinations reached from Home or Jobs.

Navigation should use stable route identifiers and typed navigation arguments where practical. A job detail route uses the job ID as its identity.

## 6. Domain model

The Android domain model should mirror the meaning of the Agent Host contract, not copy C# implementation details.

Core concepts:

### CadJob

At minimum:

- `id`
- `prompt`
- `state`
- `planValidated`
- `hasUnresolvedAmbiguity`
- `ambiguityMessage`
- `isSimulated`
- `outputPath` or future result reference
- `createdUtc`
- `updatedUtc`
- current revision summary

### JobRevision

At minimum:

- revision ID
- revision number or ordering metadata
- proposed actions / plan summary
- creation time
- optional change-request instructions that produced the revision

### JobState

The Android state representation must be able to represent every state returned by the Agent Host. UI presentation may group states visually, but the data layer must not collapse distinct server states into one value.

Unknown future server states must be handled safely rather than crashing deserialization or the UI.

### AgentStatus

Represent the future remote endpoint/PC status separately from job state. It should be possible to distinguish:

- available
- unavailable
- unknown / not configured

SOLIDWORKS status is also represented independently when supplied by the service.

## 7. Repository boundary

UI and ViewModels depend on a domain-facing interface such as:

`CadAgentRepository`

Its responsibilities include operations equivalent to:

- observe/read agent status
- create job
- list jobs
- get job
- approve current revision
- request changes to current revision
- cancel job

The first implementation is `FakeCadAgentRepository`.

A later `RemoteCadAgentRepository` or remote data source will implement the same behavior through the secure gateway.

No screen may call HTTP directly.

## 8. Fake behavior

The fake repository is not merely static sample text. It should make the application useful for exercising real UI behavior.

Seed several representative jobs, including:

- Awaiting Approval
- Building/Executing
- Completed
- Needs Clarification or ambiguity state
- Failed or Cancelled

Creating a job produces a deterministic fake plan and moves the job into an approval-oriented state suitable for testing.

Approving the displayed revision updates the fake job through execution/verification states to completion in a deterministic test-friendly manner. Production UI code must not rely on wall-clock delays for correctness; tests should be able to drive state transitions explicitly.

Request Changes creates a new revision and returns the job to a review state.

## 9. Future remote architecture

The Android app must not connect directly to the current localhost Agent Host.

Current V1 remains restricted to its localhost listener on the Windows machine.

The future path is:

Android app
-> authenticated HTTPS remote gateway/service
-> trusted Windows-side Agent Host / relay
-> SolidWorks bridge
-> SOLIDWORKS

The exact gateway hosting and authentication design are outside this milestone and require a separate security design before implementation.

The future remote layer must provide at minimum:

- encrypted transport
- authenticated user/device access
- authorization for job actions
- replay-resistant approval semantics
- revision-bound approvals
- no exposure of the OpenAI API credential to Android
- no arbitrary command/macro execution path from the phone

## 10. API compatibility

The current Agent Host already provides concepts equivalent to:

- health/status
- SOLIDWORKS status
- create job
- list jobs
- get job
- approve revision
- request changes
- cancel job

The Android models and repository API should be shaped around these semantics now so that later remote integration is an adapter replacement rather than a UI rewrite.

The Android client must not assume the eventual public remote API is byte-for-byte identical to today's localhost routes. A gateway may add authentication, versioning, result references, or other transport concerns.

## 11. State and error handling

Every screen that loads data must support:

- loading
- content
- empty state where applicable
- recoverable error

User actions such as Submit, Approve, Request Changes, and Cancel must prevent accidental duplicate submission while in progress.

Repository errors should be translated to user-facing domain errors rather than displaying raw stack traces or transport exceptions.

A stale revision response during approval or change request should instruct the user to refresh/reload the current revision rather than retrying the stale approval automatically.

## 12. Persistence

The first milestone does not require a local database.

Fake repository state may live in memory and be recreated on application restart. Architecture should permit later addition of a local cache without changing screen contracts.

User-entered secrets are not part of this milestone and must not be invented for the fake settings flow.

## 13. UI direction

The application should feel like an engineering tool rather than a generic chatbot.

Priorities:

- clear job state and system status
- prominent approval boundaries
- readable CAD plan/revision information
- restrained, modern Material/Compose presentation
- phone-first layout that also behaves sensibly on larger Android screens

The primary interaction is a CAD job workflow, not a free-form chat transcript.

## 14. Testing strategy

The initial project should establish tests around behavior that will remain valuable after remote integration.

At minimum:

- repository tests for create/approve/request-changes/cancel behavior
- ViewModel tests for key screen states and action gating
- Compose/UI tests for primary navigation and approval flow where practical
- model/state tests ensuring unknown future job states are handled safely

The fake repository should be injectable so tests do not depend on networking.

## 15. Explicitly out of scope for the first milestone

Do not implement yet:

- live connection to the Windows Agent Host
- Internet exposure of the localhost Agent Host
- remote gateway/server
- authentication/device pairing
- OpenAI credential entry or storage on Android
- image/file/sketch upload
- CAD preview rendering
- downloading `.SLDPRT`, drawing, STEP, PDF, or other artifacts
- push notifications
- background job monitoring
- Android-side AI planning
- direct SOLIDWORKS control

These remain future capabilities and should not be simulated in ways that imply they are secure or production-ready.

## 16. Success criteria for the first milestone

The milestone is successful when:

1. `SolidWorks-CAD-Agent-Android` builds as a normal native Android application.
2. The user can navigate Home, Jobs, Job Detail, New Job, and Settings.
3. A new prompt can create a fake CAD job.
4. The user can inspect a proposed plan/revision.
5. The user can approve, request changes, or cancel when state permits.
6. Job history reflects fake state/revision changes consistently.
7. The UI is separated from the data source by `CadAgentRepository` or an equivalent boundary.
8. Automated tests cover the core fake job lifecycle and ViewModel behavior.
9. No code exposes the V1 localhost Agent Host to the network.
10. No OpenAI API key or other production secret is present in the Android repository.

## 17. Next design boundary

After the fake-client milestone is stable, remote connectivity is a separate architectural step. That work must define the secure gateway, authentication/pairing, API versioning, PC availability/reconnect behavior, artifact transport, and threat model before the Android app is connected to a live Agent Host over the Internet.
