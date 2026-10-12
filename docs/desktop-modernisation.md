# Desktop modernisation — 11 October 2026

The following records the original UI work on an older source snapshot.
That UI is now integrated with the latest backend on `feature/current-state-reviewed`.
Image/design intake, Complete, history recovery and V2 ordered plan review are
retained. Non-admitted V2 candidates can be clarified; admitted V2 parts still
require a new request for changes. The [current verification report](current-state-review-2026-10-11.md)
supersedes the original test counts and source-isolation notes below.

## Assessment and decisions

The original .NET Framework 4.8 WinForms desktop had fixed coordinates, default
light controls, three raw JSON areas, session-only history and read-only settings.
The host already implements persisted history, revisions, settings, simulation
and native execution behind localhost HTTP/JSON.

Retain WinForms and the process boundaries. No dependencies were added. CAD,
planner, validation, database, credential store and COM bridge source are unchanged.
Source was discovered at `C:\SolidWorks-CAD-Agent-Source` and copied into this
task's empty workspace. Its upstream Git history was unavailable. A local baseline
commit records the copy; the original source and installation remain untouched.

## Design system and screens

`Desktop/Ui/Theme.cs` centralises charcoal/slate surfaces, cyan actions, green
success, amber clarification and red failure. Segoe UI supplies body/headline
text; Consolas is limited to diagnostics. Controls use subtle borders,
8/16/24px spacing, labelled line icons and keyboard focus. Windows title bars
and scrollbars remain native.

- Main workspace: persisted recent jobs with pagination, new request, connection,
  active execution mode and credential-presence status, attach/launch/refresh.
- Request area: submitted brief, actual planner summary/assumptions,
  clarification text, instruction composer and revision requests.
- Review area: proposed operations with revision-scoped completion, recorded
  activity/verification, separate redacted plan/revision/result JSON.
- Settings: actual host values, explicit save, approval policy and execution
  mode, restart notices and credential-presence status. Keys remain in Windows
  Credential Manager; no credential values are fetched by the desktop.

The layout docks and resizes; minimum is 1180 × 800 at 96 DPI. Ctrl+Enter sends
revision details when a revisable job is selected, otherwise creates a new plan.
F5 refreshes. Alt mnemonics and Tab navigate actions. Ctrl+Tab switches review
views when focus is in that area. Read-only text can be selected/copied.

## State rules

Approval includes the displayed revision ID and requires a validated,
unambiguous plan. Pending requests block conflicting actions. Cancellation
uses the existing endpoint; an operation already in progress may finish.
Cancel responses are reloaded for the full snapshot. Closing the desktop
does not cancel a host job.

GET polling reads persisted activity. Indeterminate progress means a request
is pending; no percentage or unrecorded success is invented. Initial planning
cannot show live progress until the synchronous POST returns a job ID. Activity
includes all revisions and labels each row; plan completion uses only records
from the displayed revision.

Cancelled jobs continue GET reconciliation while displayed, because the host
does not expose a separate signal that an in-flight operation has finished.
Navigating away ends that polling. Version guards prevent a delayed cancel or
approval response from replacing a more recent snapshot.

Settings never save on open/close. The host validates settings and owns mode
change safeguards. Active mode comes from health independently of configured
mode. Credential presence does not prove API availability. Diagnostics mask
common secret fields/key-shaped strings, as defensive redaction rather than
a general-purpose data-loss prevention system.

## Build and launch on Windows

```powershell
dotnet build SolidWorksCadAgent.sln -c Release -m:1 -p:UseSharedCompilation=false
dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj --no-build -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-desktop.ps1 -Configuration Release
```

After building, double-click `Launch CAD Agent.cmd`. The launcher reuses an
existing localhost host or starts one hidden with logs in `artifacts/host-logs`,
then opens the desktop. It does not change settings, URL ACL or credentials.
If an older host is running, close it before launching to use this host build.
Existing `%LOCALAPPDATA%\SolidWorksCadAgent` settings/history are shared.

Alternatively, extract `artifacts/SolidWorksCadAgent-Windows-Test-Bundle.zip`
to a folder on the target PC and double-click its `Launch CAD Agent.cmd`.
The bundle includes the compiled application; no SDK is needed to launch it.
Follow `START-HERE.txt` for first-time URL registration and prerequisites.

First-time setup still uses `scripts/register-agent-host-url.ps1` in an elevated
PowerShell. Real planning uses the existing generic credential target
`SolidWorksCadAgent/OpenAI`. Never put keys in source/logs.

For testing without native CAD, explicitly select Simulation in Settings,
save and restart Agent Host. Submit:

> Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.

Review ten proposed operations and approve. Expect thirteen recorded operations,
three passed checks and a clearly labelled simulated result. Reopen the job
from history. For clarification, submit a 100 × 60 × 10 mm plate with an M8 hole
and request changes specifying a 9 mm clearance through-hole.

Automated UI tests use one STA message loop, real AgentRoutes/SQLite and
deterministic simulation via an in-process HTTP handler. Set
`CAD_AGENT_CAPTURE_DIR` to capture actual forms with DrawToBitmap using fixtures.
No cloud billing, real credentials or native CAD operations occur in these tests.

Final local validation: Release solution build passed with zero warnings/errors;
all 142 unit tests passed, including 15 desktop/presentation/service checks.
The real SOLIDWORKS integration-test contract compiled. The bundle reports
`NativeSolidWorksInterop`, meaning installed interop assemblies were included;
native execution itself was not exercised. NuGet vulnerability lookup was
disabled for the sandbox build because the network endpoint was unavailable.
Normal user build commands retain the default audit behaviour.
The Windows bundle dependency/capability validator passed, and its negative
test correctly rejected a deliberately incorrect capability marker.

An independent read-only review identified cancellation reconciliation and
native history-text redaction gaps. Both were reproduced by failing tests and
fixed. Lowercase logging levels and accessible tab selection were also covered
and fixed. Real display scaling/screen-reader acceptance remains a target-PC check.

Physical acceptance remains required: native attach/launch, cloud planning,
approval, cancel during a long native operation, feature creation, save/reopen
and repeated runs against SOLIDWORKS 2020 SP0.0. Integration-test compilation
is not a physical pass. Additional display scaling and screen-reader checks
remain recommended.

## Remaining scope

Image interpretation/upload, embedded 3D preview, assemblies, drawings, remote
access and continuous conversational editing of completed geometry remain outside
V1. Inspect completed models in SolidWorks. Revisions work for clarification/
approval states; after completion, submit a new request. No fake feature controls
were added.
