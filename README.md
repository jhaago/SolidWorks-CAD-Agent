# SolidWorks CAD Agent

AI-assisted native CAD automation for SOLIDWORKS.

## PC V2 development

The latest CAD capability increment is on `feature/v2-cad-profiles-and-pockets`, based on the image-design intake V2 line. It adds native rotated obround slots, regular polygon profiles and depth-controlled blind cuts through the existing approved planner. See [capabilities, examples and precise placement limits](docs/prismatic-cad-capabilities.md).

The image-design intake milestone is on `feature/v2-image-design-intake`, based on the newer local V2 line. Open **Image / Design Intake** in the desktop app to attach PNG/JPEG references, hold an engineering clarification conversation, inspect revisioned Design Briefs and approve the current brief. **Create CAD Plan** then enters the existing job workflow; **Approve Build** remains a separate mandatory action even in Auto Mode. Discussion has no CAD execution capability. See [usage, architecture, security and roadmap](docs/image-design-intake.md).

The first V2 increment adds Desktop clarification revisions and navigable persisted job history, plus native `AddLine` and `AddArc` sketch commands for custom profiles. On the SOLIDWORKS 2020 PC, the new line/arc profile passed geometry and save/reopen acceptance alongside the existing plate and document-safety regressions. See [V2 design](docs/superpowers/specs/2026-10-06-pc-agent-v2-design.md) and [validation and remaining boundaries](docs/windows-validation-v2-2026-10-06.md).

Further stages cover dimensions/relations, fillets/chamfers, patterns/revolve and safe native model edits. Android development is outside this PC milestone. Simulation does not verify solids made from custom line/arc profiles; use native execution for those profiles.
## V1 baseline

The V1 target runtime is **SOLIDWORKS Premium 2020 SP0.0**. The bridge is designed so SOLIDWORKS-version-specific COM/API details stay behind a version-independent command layer, allowing later releases to be certified by rerunning the integration and acceptance suite.

The first acceptance model is a native editable 100 × 60 × 10 mm plate with a centred Ø20 through-hole.

See `docs/superpowers/specs/2026-09-22-solidworks-cad-agent-design.md` and the version-compatibility addendum for the approved architecture.

## Current V1 scope

- Separate localhost Agent Host and WinForms desktop processes.
- OpenAI Responses API planning with strict structured output and `store=false`.
- Approval required by default; approval is bound to the current persisted plan revision.
- Whitelisted native CAD commands, fail-stop execution, programmatic verification, and SQLite history.
- API credentials stored in Windows Credential Manager, never in repository configuration or SQLite.
- The CAD listener accepts only `http://127.0.0.1:53741/`.

Image interpretation, assemblies, drawings and arbitrary macros are not enabled in V1.

## Manual remote workstation test build

The separate `SolidWorksCadAgent.RemoteAgent.exe` serves primary-monitor JPEG viewing and allowlisted manual input on `127.0.0.1:5079`, via private Tailscale Serve HTTPS. Pairing needs local Windows approval; every connection starts view-only. V2 also provides authenticated paired job submission, status, approval, revisions, completion and bounded native artifact download. Android UI availability depends on the companion build. Image-design intake remains desktop/loopback-only in this milestone. See [setup and physical tests](docs/live-remote-windows-testing.md). The CAD Host remains unexposed, and native plate and millimetre-scale acceptance passed on the SOLIDWORKS 2020 PC; see [Windows validation](docs/windows-validation-2026-10-05.md).

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

The PC V1 native acceptance checks passed on SOLIDWORKS 2020. V2 development now provides navigable persisted history and Desktop clarification revisions. Settings persist across Host restarts. See the dated validation notes for exact automated/native evidence; manual Desktop visual review and remote physical acceptance remain separate pending checks.
