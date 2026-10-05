# Android Remote Workstation + AI Co-Control Design

Date: 2026-10-04
Status: Approved direction; implementation planned for the Android companion roadmap
Android repository: `jhaago/SolidWorks-CAD-Agent-Android`
Authoritative system repository: `jhaago/SolidWorks-CAD-Agent`
Related design: `docs/superpowers/specs/2026-09-27-android-companion-app-design.md`

## 1. Purpose

Extend the existing SolidWorks CAD Agent Android companion app with a secure Remote Workstation capability so the same app can:

1. show a live view of the trusted Windows workstation;
2. allow manual TeamViewer-style mouse/keyboard control from Android;
3. allow the user to hand individual tasks to an AI workstation-control agent while watching its actions live;
4. allow the user to immediately take control back;
5. use the existing SolidWorks CAD API bridge for deterministic CAD operations, while retaining UI/vision control for operations outside that bridge;
6. support workflows involving ordinary Windows applications such as Bambu Studio without making those applications part of the SolidWorks bridge.

The Remote Workstation feature is a subsystem of the main Android companion app, not a separate Android application.

## 2. Product model

The Android app should ultimately present three closely related capabilities:

- **CAD** — submit/review/approve SolidWorks CAD jobs and revisions.
- **Remote** — view and manually control the workstation desktop.
- **AI Control** — give bounded workstation tasks to the AI agent and observe/interrupt execution.

Remote and AI Control may share one Remote session screen rather than being separate top-level destinations. The important requirement is that manual control, AI control, and CAD-job control share the same authenticated device/workstation relationship.

## 3. Control modes

A remote session has three conceptual modes.

### 3.1 Manual

The user has direct control of pointer, keyboard, scrolling, and supported touch gestures. AI may observe only when explicitly enabled for the session.

### 3.2 Assist

The user remains primary controller. The AI may inspect the current screen and available UI-automation metadata, explain what it sees, suggest the next action, highlight a target, or perform one explicitly approved bounded action.

### 3.3 Agent

The user gives the workstation agent a bounded goal such as:

- "Open the latest exported part in Bambu Studio and stop once it is loaded."
- "Orient the part on its largest flat face and slice it, but do not start the print."
- "Open the current SolidWorks model, hide sketches, and show an isometric view."

The user watches the same live session. The AI must expose its current action/state, and the user can interrupt and return to Manual mode immediately.

## 4. Windows-side architecture

Do not put remote desktop responsibilities inside the SolidWorks COM bridge.

Add a separate **Remote Workstation Agent** on the trusted Windows PC. It owns:

- desktop/window capture;
- remote session transport;
- remote input injection;
- Windows UI Automation inspection/action support;
- AI workstation task orchestration;
- application/window discovery;
- session permissions, audit events, and interruption;
- adapters to trusted local capabilities, including the existing SolidWorks Agent Host/API.

The existing SolidWorks Agent Host remains the engineering-grade CAD execution path.

Conceptual architecture:

Android app
-> authenticated remote session/gateway
-> Remote Workstation Agent
-> Windows desktop / ordinary apps
-> SolidWorks Agent Host for CAD-domain commands
-> SolidWorks bridge
-> SOLIDWORKS

The Remote Workstation Agent may interact with applications such as Bambu Studio through desktop/UI automation. It must not require application-specific reverse-engineered cloud APIs merely to support remote use.

## 5. Hybrid control priority

When the AI needs to perform an action, use the most deterministic available control channel in this order:

1. **Domain API/tool** — for example the existing SolidWorks CAD Agent API for CAD operations.
2. **Windows UI Automation** — for accessible buttons, menus, text fields, lists, dialogs, and other semantic controls.
3. **Vision + pointer/keyboard control** — for rendered/custom UI such as 3D viewports or controls not exposed semantically.

The agent must not deliberately replace a reliable domain API operation with coordinate clicking merely because the UI is visible.

## 6. Remote viewing and input

The Android Remote screen must support:

- low-latency live desktop view;
- aspect-fit/zoom/pan on phone and tablet;
- tap-to-click and optional relative trackpad mode;
- drag, scroll, right-click, and keyboard input;
- clear connection quality/state;
- explicit session connect/disconnect;
- orientation changes without losing session state where practical.

The architecture should support a workstation with multiple displays, although first implementation may expose one selected display at a time.

## 7. AI observation context

For an AI-controlled workstation task, the Windows agent may construct an observation from:

- current screenshot/frame or cropped application frame;
- active application/window identity;
- Windows UI Automation tree or selected semantic elements;
- current pointer position;
- recent agent actions and results;
- trusted domain state from the SolidWorks Agent Host when relevant.

The Android client should not be responsible for interpreting screenshots or running the workstation-control reasoning loop. The AI execution authority belongs on the trusted workstation/service side.

## 8. Human control and interruption

Human override is a core requirement, not a recovery feature.

The UI must make it obvious whether the current controller is:

- User
- AI
- No controller / observing

When AI control is active:

- show an AI action/status indicator;
- visually distinguish AI pointer activity where feasible;
- provide an always-available **Take Control** / **Stop AI** action;
- stop issuing new AI input immediately when interrupted;
- do not resume automatically after human takeover.

Manual input during Agent mode may be treated as an interruption rather than allowing two controllers to fight over the pointer.

## 9. Protected actions

Remote AI control must use explicit safety boundaries for consequential actions.

At minimum, require separate user confirmation before actions such as:

- starting a physical 3D print;
- deleting user files outside an explicitly approved workflow;
- installing/uninstalling software;
- changing system security/network settings;
- entering or submitting credentials;
- running arbitrary shell/PowerShell commands outside a separately approved developer/admin mode;
- destructive SolidWorks file overwrite when the existing CAD Agent policy would reject it.

For the Bambu workflow, the intended initial boundary is:

AI may open/import/orient/configure/slice in Bambu Studio, but **starting the print requires explicit user approval**.

## 10. SolidWorks interaction

Remote control may interact with the SolidWorks UI, but it does not replace the SolidWorks bridge.

Use remote/UI control for operations such as:

- camera/view manipulation;
- window/dialog interaction;
- display settings;
- add-in or plugin UI that has no supported bridge command;
- recovering from unexpected modal dialogs;
- visual inspection tasks.

Use the existing CAD Agent API for supported engineering changes such as sketches, dimensions, features, rebuilds, verification, and save/open operations.

The workstation agent should be able to route a natural-language task between both mechanisms.

## 11. Bambu Studio workflow

Bambu Studio remains the slicer and print-control application.

The Remote Workstation feature should enable the user to:

1. complete or approve a CAD model;
2. export STEP/STL/3MF as appropriate;
3. launch/open the exported model in Bambu Studio on the home workstation;
4. manually control Bambu Studio remotely, or ask the AI to perform bounded preparation tasks;
5. inspect the sliced preview through the live desktop session;
6. explicitly approve any command that starts the physical print.

No Bambu cloud/printer protocol integration is required for the first remote-workstation implementation.

## 12. Security model

Do not expose the current localhost Agent Host directly to the Internet.

Remote connectivity must be designed around authenticated device/workstation sessions with encrypted transport. The eventual implementation must include:

- device/user authentication and pairing;
- encrypted transport;
- short-lived session authorization;
- revocation of paired devices;
- replay resistance for protected actions;
- audit trail for AI actions and protected confirmations;
- no OpenAI API key or Windows credential material sent to Android;
- no unauthenticated remote input endpoint;
- no generic Internet-accessible command shell as part of normal operation.

Internet/NAT traversal and relay design are part of the remote-connectivity implementation milestone and must not be solved by opening an unrestricted inbound port on the home router.

## 13. Android architecture additions

Keep the existing CAD repository boundary for CAD jobs.

Add separate remote-session abstractions, for example:

- `RemoteSessionRepository`
- `RemoteSessionState`
- `RemoteDisplayFrame` / video-render surface abstraction
- `RemoteInputController`
- `AiControlRepository`
- `AiTaskState`
- `ProtectedActionRequest`

Compose/ViewModels depend on these interfaces rather than directly on WebRTC/socket/input implementations.

The Android app should remain usable for CAD job review even when Remote Workstation is unavailable.

## 14. Initial implementation stages

### Stage A — Remote shell and fake session

Add Remote as an app destination using a fake session/data source. Establish session state, connection UI, Manual/Assist/Agent mode presentation, Take Control behavior, and protected-action UI without live desktop access.

### Stage B — Live view

Connect the Android app to the Windows Remote Workstation Agent and show a real desktop stream. No remote input is required to declare this stage complete.

### Stage C — Manual control

Add authenticated pointer/keyboard input and validate reliable user takeover/reconnect behavior.

### Stage D — AI observation + bounded actions

Allow the workstation AI to inspect the screen/UI Automation context and execute one bounded action at a time while the user watches.

### Stage E — Agent task mode

Allow multi-step bounded goals with live action status, interruption, audit history, and protected-action confirmation.

### Stage F — Workflow adapters

Integrate routing to the SolidWorks CAD Agent and validate Bambu Studio preparation workflows without bypassing Bambu Studio.

## 15. First remote milestone success criteria

The first real remote milestone is successful when:

1. Remote is part of the main Android CAD Agent application.
2. A paired Android device can establish an authenticated session to the trusted Windows workstation without exposing the existing localhost Agent Host directly.
3. The Android app can display a live workstation desktop stream.
4. The user can manually control mouse and keyboard with an immediate local/remote stop mechanism.
5. Session disconnect/reconnect does not leave stuck input or an uncontrolled AI task.
6. The design has working seams for AI observation/control even if full Agent mode is delivered in a following slice.
7. CAD job screens remain independent and usable when remote control is unavailable.

## 16. AI-control milestone success criteria

The AI-control milestone is successful when:

1. the user can issue a bounded workstation instruction from the Android Remote screen;
2. the AI can observe both visual and semantic UI context where available;
3. the user can watch the live session while AI control is active;
4. the current AI action is visible;
5. **Take Control** immediately halts AI input and returns the session to Manual mode;
6. SolidWorks engineering changes use the CAD Agent API when supported;
7. a Bambu Studio preparation task can be performed up to slicing/preview without starting a physical print;
8. protected actions stop and wait for explicit confirmation.

## 17. Out of scope for the first implementation slice

Do not attempt all of the following in the first remote slice:

- TeamViewer-class fleet/enterprise administration;
- unattended control of arbitrary third-party PCs;
- arbitrary shell access from Android;
- fully autonomous unbounded desktop operation;
- automatic physical print start;
- replacing Bambu Studio with a custom slicer;
- replacing the SolidWorks API bridge with screen clicking;
- remote control of multiple user accounts/sessions at once.

The first objective is a reliable single-user trusted-workstation remote session with clean AI-control seams.
