# SolidWorks CAD Agent

AI-assisted native CAD automation for SOLIDWORKS.

## V1 baseline

The first certified runtime is **SOLIDWORKS Premium 2020 SP0.0**. The bridge is designed so SOLIDWORKS-version-specific COM/API details stay behind a version-independent command layer, allowing later releases to be certified by rerunning the integration and acceptance suite.

The first acceptance model is a native editable 100 × 60 × 10 mm plate with a centred Ø20 through-hole.

See `docs/superpowers/specs/2026-09-22-solidworks-cad-agent-design.md` and the version-compatibility addendum for the approved architecture.

## Current V1 scope

- Separate localhost Agent Host and WinForms desktop processes.
- OpenAI Responses API planning with strict structured output and `store=false`.
- Approval required by default; approval is bound to the current persisted plan revision.
- Whitelisted native CAD commands, fail-stop execution, programmatic verification, and SQLite history.
- API credentials stored in Windows Credential Manager, never in repository configuration or SQLite.
- The listener accepts only `http://127.0.0.1:53741/`; V2 remote access is not enabled.

Image interpretation, assemblies, drawings, arbitrary macros, and remote phone/tablet access are not enabled in V1.

## Prerequisites

- Windows 10 or 11 with .NET Framework 4.8 developer tools and Visual Studio/MSBuild.
- SOLIDWORKS Premium 2020 SP0.0 for the first certified physical acceptance run.
- An OpenAI API key if using the cloud planner.
- Administrator access once to reserve the localhost HTTP URL.

## Build

From a Developer PowerShell prompt:

```powershell
msbuild SolidWorksCadAgent.sln /t:Restore /p:Configuration=Debug
msbuild SolidWorksCadAgent.sln /p:Configuration=Debug /p:Restore=false
```

The CI-compatible build path does not require SOLIDWORKS to be installed. Real COM integration tests remain opt-in and must run on the SOLIDWORKS PC.

## First-time setup

1. Open an elevated PowerShell prompt and run:

   ```powershell
   .\scripts\register-agent-host-url.ps1
   ```

2. Create `C:\SolidWorks-CAD-Agent\Workspace` or change `AgentSettings.WorkspaceRoot` before building.
3. Add a **generic credential** under Windows Credential Manager's **Windows Credentials** section for the target `SolidWorksCadAgent/OpenAI`. Store the OpenAI API key as the credential secret. A normal domain-style “Windows credential” is a different credential type and will not be read by the Agent Host. The key must not be placed in source files, JSON configuration, SQLite, screenshots, or logs.

## Run

Start the Agent Host first:

```powershell
.\src\SolidWorksCadAgent.AgentHost\bin\Debug\net48\SolidWorksCadAgent.AgentHost.exe
```

Then start the desktop application:

```powershell
.\src\SolidWorksCadAgent.Desktop\bin\Debug\net48\SolidWorksCadAgent.Desktop.exe
```

Use **Attach** to connect to an already-running SOLIDWORKS instance or **Launch** to start it. Submit this first acceptance prompt:

> Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.

The expected cloud-planning state is `AwaitingApproval`. Inspect the proposed plan, then select **Approve Build**. The Agent Host—not the model—determines success from structured body-count, precise bounding-box, and rebuild-error checks.

## Current testing boundary

GitHub Actions builds the Agent Host, desktop client, unit tests, and the integration-test contract. Remaining software work includes persisted/navigable job history, editable Settings, and clarification revisions. The separate physical release gate is a run on the user's SOLIDWORKS 2020 SP0.0 PC, including attach/launch, native feature creation, save/reopen, verification, and three repeated acceptance runs.
