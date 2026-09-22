# SolidWorks CAD Agent — Design Specification

Date: 2026-09-22
Repository: `jhaago/SolidWorks-CAD-Agent`
Status: Approved design for implementation planning

## 1. Purpose

Build a Windows-based AI CAD agent that can interpret natural-language CAD requests, later including hand sketches and reference images, and create or modify native, editable SolidWorks parts on the user's existing SolidWorks 2018 PC.

The first implementation goal is intentionally narrow: prove that the system can reliably automate SolidWorks 2018 to create, inspect, verify, and save a native `.SLDPRT` using a controlled CAD command layer. Cloud AI integration follows that foundation; remote phone/tablet CAD access is a first-class V2 goal and must be preserved by the V1 architecture.

## 2. Success Criteria

V1 is successful when the system can:

1. Run on the same Windows PC as SolidWorks 2018 and Visual Studio.
2. Attach to an already-running SolidWorks instance or launch SolidWorks if required.
3. Create native editable SolidWorks geometry through the SolidWorks API rather than importing neutral geometry.
4. Accept a text CAD request through the desktop UI.
5. Send the request to a cloud AI model through the Agent Host.
6. Convert the AI's output into validated, whitelisted CAD commands.
7. Show the interpreted dimensions and proposed build plan for user approval by default.
8. Execute the approved plan in SolidWorks.
9. Verify key resulting dimensions and rebuild status programmatically.
10. Save the resulting `.SLDPRT` inside the controlled workspace.
11. Persist the complete job history, plan, approval, command log, verification results, and output path locally.
12. Recover cleanly from failures without silently continuing past failed CAD operations.

The initial acceptance part is a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.

## 3. Scope

### 3.1 V1 in scope

- C# Windows application suite.
- WinForms desktop control panel.
- .NET Framework 4.8 for the V1 Windows projects, including the initial Agent Host process.
- Development and testing on the same PC as SolidWorks 2018.
- Separate local Agent Host process.
- Localhost HTTP/JSON communication between WinForms and Agent Host.
- SolidWorks Bridge using the SolidWorks 2018 COM/.NET API.
- Attach-to-running and launch-SolidWorks workflows.
- Controlled workspace rooted at `C:\SolidWorks-CAD-Agent\Workspace\` by default.
- Whitelisted and extensible CAD command registry.
- Cloud AI orchestration behind a provider abstraction.
- OpenAI as the first supported cloud provider.
- OpenAI API key stored in Windows Credential Manager.
- Approval-required mode as the default.
- Optional Auto mode in Settings.
- Persistent job history in SQLite.
- Text prompts in V1.
- Image/sketch attachment infrastructure may be represented in the UI/data model, but sketch interpretation itself is not required for the first acceptance milestone.
- Visible SolidWorks operation during V1; no headless requirement.
- Verification using rebuild state, body count, feature/sketch state where available, model dimensions, and bounding-box measurements.
- Safe cancellation and recoverable failure states.

### 3.2 Explicitly deferred to later V1 increments

- Broad feature coverage such as loft, sweep, shell, Hole Wizard, assemblies, drawings, mates, and complex surface workflows.
- Editing arbitrary existing models beyond the initial tested feature subset.
- Fully autonomous interpretation of complex engineering sketches.
- Multi-provider selection in the UI.
- Privileged arbitrary VBA/C# execution.

### 3.3 V2 goal: remote CAD

V2 must allow secure remote interaction from phone/tablet without requiring the user to sit at the SolidWorks PC. The intended experience is:

1. Submit a text prompt and/or sketch/photo remotely.
2. Have the Agent Host create a persistent CAD job.
3. Review the AI's interpreted dimensions, assumptions, and proposed feature plan remotely.
4. Approve, reject, or request changes.
5. Allow SolidWorks on the home Windows PC to build the model.
6. Review progress and generated screenshots/renders remotely.
7. Receive a Ready for Review state.
8. Submit revision requests remotely.

V1 must therefore keep the UI, Agent Host, CAD command layer, and SolidWorks integration separated so a remote gateway/client can later reuse the same Agent Host and job model.

## 4. Architecture

### 4.1 Recommended structure

```text
WinForms Desktop UI
        |
        | localhost HTTP/JSON
        v
Agent Host
  - job lifecycle
  - approvals / Auto mode
  - cloud AI orchestration
  - SQLite persistence
  - workspace policy
        |
        | validated internal CAD commands
        v
SolidWorks Bridge
  - attach / launch SolidWorks 2018
  - command registry
  - model interrogation
  - screenshots
  - rebuild / save
        |
        | SolidWorks COM/.NET API
        v
SolidWorks 2018
```

The cloud model is accessed only by the Agent Host. The desktop UI and SolidWorks Bridge do not call the cloud provider directly.

### 4.2 Component boundaries

#### WinForms Desktop UI

Responsibilities:

- Show SolidWorks connection state and detected version.
- Submit text prompts.
- Show current job state.
- Show AI interpretation, assumptions, ambiguities, and proposed build plan.
- Provide Approve Build, Request Changes, Cancel, and later revision controls.
- Show execution progress, verification results, and logs.
- Show persistent job history.
- Expose Settings including workspace, approval/Auto mode, API credential status, OpenAI model selection, SolidWorks path override, and logging level.

The UI must not contain SolidWorks API logic or cloud-provider implementation logic.

#### Agent Host

Responsibilities:

- Own the job state machine.
- Persist jobs and revisions to SQLite.
- Enforce approval policy and Auto-mode policy.
- Call the configured AI provider through an abstraction.
- Convert AI intent/tool requests into command-registry invocations.
- Validate commands before execution.
- Enforce workspace boundaries.
- Coordinate SolidWorks execution and verification.
- Record results, failures, screenshots, and output files.
- Expose a localhost HTTP/JSON API for the desktop UI.

The Agent Host is a separate process from the WinForms UI from day one.

#### SolidWorks Bridge

Responsibilities:

- Discover/attach to a running SolidWorks instance.
- Launch SolidWorks when requested and then attach.
- Execute known CAD operations through the SolidWorks 2018 API.
- Return structured results rather than opaque success/failure strings.
- Query the active document, feature tree, sketch status, dimensions, body count, bounding box, and rebuild errors where supported.
- Capture selected SolidWorks views/screenshots for visual inspection checkpoints.
- Save native `.SLDPRT` files only inside the controlled workspace.

The bridge must not accept arbitrary source code from the cloud model.

#### CAD Command Registry

The registry defines the only ordinary operations available to the AI. It is versioned and extensible.

Initial command families:

Document:

- LaunchSolidWorks
- AttachToSolidWorks
- NewPart
- OpenPart
- SavePart
- Rebuild
- CloseDocument

Sketch:

- CreateSketch
- AddLine
- AddRectangle
- AddCircle
- AddArc
- AddDimension
- AddRelation
- ExitSketch

Features:

- Extrude
- CutExtrude
- Fillet
- Chamfer

Inspection:

- GetFeatureTree
- GetSketchStatus
- GetBoundingBox
- GetDimensions
- GetBodyCount
- GetRebuildErrors
- CaptureView

Each command has a strict schema with explicit units and validation rules. Additional commands can be introduced later without changing the Agent Host/bridge boundary.

Higher-level composite commands such as CreateCentredPlate, CreateBoltCircle, CreateTappedHole, CreateBearingSeat, and domain-specific features may be added later. Composite commands expand internally into validated lower-level operations.

A separate privileged macro/script execution path may be considered in the future for unsupported SolidWorks features, but it is disabled in V1 and would require explicit local user approval.

## 5. Job Model and Lifecycle

Every CAD request is a persistent Job.

Primary states:

```text
New
 -> Interpreting
 -> AwaitingClarification (only when required)
 -> Interpreting
 -> AwaitingApproval
 -> Approved
 -> Executing
 -> Verifying
 -> ReadyForReview
 -> Completed
```

Terminal/interrupt states available from applicable stages:

- Failed
- Cancelled

A job with no ambiguity proceeds directly from Interpreting to AwaitingApproval. Auto mode may transition from Interpreting directly into execution only after all validation and ambiguity checks pass. Hard safety gates are not bypassed by Auto mode.

A Job stores:

- unique identifier
- creation/update timestamps
- original prompt
- attachment references
- AI interpretation
- explicit assumptions
- unresolved ambiguities
- proposed feature/build plan
- approval status
- selected provider/model metadata
- ordered CAD command log
- per-command result/error data
- verification results
- screenshot/checkpoint references
- output `.SLDPRT` path
- revision history

### 5.1 Revisions

A user revision request remains part of the same Job. Each revision records the requested change, revised plan, approval where required, commands executed, verification, and resulting model state.

Where feasible, revisions should modify native SolidWorks features rather than recreate an opaque imported body.

### 5.2 Ambiguity policy

The agent must not silently invent important manufacturing/design dimensions or feature intent. If a material ambiguity can affect geometry or function, the Job enters AwaitingClarification before execution.

Examples:

- tapped versus clearance hole
- unspecified feature location
- conflicting dimensions
- ambiguous through/blind depth
- unclear reference face

## 6. AI Orchestration

The cloud model has three logical responsibilities:

1. Interpret the user request into explicit engineering intent.
2. Produce a proposed SolidWorks feature/build plan.
3. Invoke only registered CAD tools and react to structured execution/verification results.

The Agent Host supplies the model only the information required for the current task:

- user prompt
- relevant attachment(s) when supported
- current job/revision context
- available CAD tools and schemas
- relevant model/feature state
- structured measurements and error data
- screenshots only at useful visual checkpoints

The provider is hidden behind an interface such as:

```text
IAgentModel
  InterpretJob(...)
  PlanJob(...)
  ContinueJob(...)
  ReviewResult(...)
```

V1 implements OpenAI first. The architecture must permit later replacement with another cloud provider or an on-prem/local model without changing the SolidWorks Bridge or desktop UI.

## 7. Approval and Auto Mode

### 7.1 Default approval mode

Before geometry-changing execution, the user sees:

- interpreted part/features
- important dimensions
- assumptions
- unresolved items
- proposed SolidWorks feature sequence

The user must explicitly press Build/Approve before execution.

### 7.2 Auto mode

Auto mode is optional and disabled by default. It allows validated ordinary CAD commands to proceed without the normal approval pause.

Auto mode never bypasses hard safety rules, including:

- workspace containment
- overwrite protection
- unsupported command rejection
- unresolved material ambiguity
- rebuild-error stop
- verification contradiction stop
- privileged code execution prohibition

## 8. Workspace and File Safety

Default workspace:

`C:\SolidWorks-CAD-Agent\Workspace\`

V1 CAD file operations are restricted to the configured workspace.

Rules:

- No arbitrary browsing/writing outside the workspace through the Agent Host.
- Existing files are not overwritten without explicit user approval.
- New jobs operate on a temporary/working copy where practical.
- A final output is promoted only after a successful rebuild and required verification pass.
- Cancellation must stop further commands and preserve the last recoverable SolidWorks state.
- The system must not expose arbitrary PowerShell, command shell, or general filesystem execution as AI tools.

## 9. Credentials and Secrets

The OpenAI API key is stored locally using Windows Credential Manager.

Requirements:

- Never commit API keys to GitHub.
- Never store raw API keys in SQLite or ordinary config files.
- Never include raw API keys in logs.
- UI may show credential presence/health but not reveal the full secret.

## 10. Persistence

SQLite is used for local job/history persistence.

The data model must support:

- jobs
- revisions
- approvals
- command executions
- verification results
- attachment references
- screenshots/checkpoints
- output file references
- failures/cancellations
- timestamps

Binary CAD files and large images remain in the filesystem workspace; SQLite stores references and metadata rather than duplicating large binary payloads.

## 11. Desktop UI

V1 uses C# WinForms on .NET Framework 4.8.

Primary areas:

1. Connection/status header
   - SolidWorks status
   - detected version
   - current approval/Auto mode

2. Prompt/input area
   - text prompt
   - attachment controls reserved for sketch/photo/CAD file workflows
   - Send

3. Current Job area
   - interpreted dimensions/features
   - assumptions and ambiguities
   - proposed build plan
   - Approve Build / Request Changes / Cancel

4. Progress and verification area
   - command progress
   - rebuild state
   - measurements
   - error messages

5. Job History
   - previous jobs
   - revisions
   - outputs and status

6. Settings
   - workspace path
   - approval/Auto mode
   - OpenAI credential status
   - OpenAI model configuration
   - SolidWorks executable override
   - logging verbosity
   - V2 remote access placeholder/status only

The UI must make unresolved ambiguity explicit rather than presenting a fabricated numerical confidence score.

SolidWorks remains visible while the agent works in V1.

## 12. Error Handling

The system fails conservatively.

Execution stops when any of the following occurs unless a command-specific recovery is explicitly implemented:

- SolidWorks COM/API exception
- feature creation failure
- failed rebuild
- invalid or malformed AI tool request
- unsupported command
- workspace policy violation
- verification mismatch
- missing required dimension/intent
- unsafe overwrite condition

Failures are returned as structured error objects including operation, stage, message, and relevant SolidWorks/API data where available.

The Agent Host records the error and moves the Job to Failed or AwaitingClarification as appropriate while preserving recoverable state. It must not silently skip a failed operation and continue building later features.

## 13. Testing Strategy

### 13.1 Unit tests without SolidWorks

Cover:

- CAD command schema validation
- workspace/path enforcement
- job state transitions
- approval/Auto-mode rules
- overwrite policy
- provider abstraction behavior using fakes
- persistence/revision logic
- error mapping

### 13.2 SolidWorks integration tests

Run on the actual Windows/SolidWorks 2018 development PC.

Initial integration coverage:

- detect/attach to running SolidWorks
- launch and attach
- create new part
- create sketch
- create rectangle
- create circle
- create dimensions/relations required by the acceptance geometry
- extrude
- cut-extrude through all
- rebuild
- query body count
- query bounding box/dimensions
- save native `.SLDPRT`
- reopen generated part and re-verify

### 13.3 AI regression tests

Maintain fixed natural-language prompts with expected interpreted geometry and required clarification behavior. Provider/model updates are evaluated against this set so regressions are measurable.

## 14. First Acceptance Test

Prompt intent:

"Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole."

Required result:

- native SolidWorks part
- editable feature tree
- one solid body
- overall model dimensions 100 x 60 x 10 mm within API measurement tolerance
- centred Ø20 through-hole
- successful rebuild with no unresolved rebuild error
- saved inside the controlled workspace
- job record contains command log and verification results

The acceptance test must pass repeatedly before expanding the command set or depending on AI-generated complex geometry.

## 15. Implementation Order

The implementation plan should preserve this sequence:

1. Solution/project skeleton and shared contracts.
2. SolidWorks Bridge connection/launch support.
3. Minimal CAD commands required for the acceptance part.
4. Programmatic verification and save/reopen test.
5. Agent Host and localhost HTTP/JSON API.
6. SQLite job persistence and lifecycle.
7. WinForms control panel.
8. Cloud provider abstraction and OpenAI integration.
9. Approval workflow and optional Auto mode.
10. AI-driven acceptance-part workflow.
11. Incremental command expansion.
12. Sketch/image interpretation in a later increment.
13. V2 secure remote gateway and phone/tablet experience.

## 16. Non-Goals and Engineering Constraints

- V1 is not an autonomous mechanical engineer and must not invent critical missing engineering requirements.
- V1 does not need to reproduce the entire SolidWorks feature set.
- V1 does not expose unrestricted desktop or arbitrary-code execution to the cloud model.
- V1 does not require a local GPU or local language model.
- V1 does not require phone/tablet remote access, but it must not prevent it architecturally.
- SolidWorks remains the authoritative CAD engine and creator of native `.SLDPRT` output.

## 17. Future Direction

Once the V1 acceptance workflow is reliable, the project can expand toward:

- hand-sketch and photo interpretation
- editing existing parametric models
- higher-level engineering feature commands
- assemblies and mates
- manufacturing drawings
- complex mechanism workflows
- visual result review and iterative correction
- secure remote CAD job submission and review from phone/tablet
- alternate cloud providers
- optional local/on-prem AI models

The central architectural principle remains unchanged: AI plans and orchestrates; the Agent Host validates and controls; the SolidWorks Bridge executes only permitted CAD operations; SolidWorks produces the native editable engineering model.
