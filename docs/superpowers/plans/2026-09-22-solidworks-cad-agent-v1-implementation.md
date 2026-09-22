# SolidWorks CAD Agent V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first end-to-end Windows V1 that accepts a text CAD request, interprets it through OpenAI, requires approval by default, creates a native editable SolidWorks 2018 part through a whitelisted command layer, verifies the result, persists the job, and saves the `.SLDPRT` inside the controlled workspace.

**Architecture:** A WinForms desktop client talks to a separate localhost Agent Host over HTTP/JSON. The Agent Host owns job state, approval policy, SQLite persistence, OpenAI orchestration, and workspace enforcement; it invokes a SolidWorks Bridge that runs COM work on a dedicated STA thread and exposes only registered CAD commands. SolidWorks remains the authoritative CAD engine.

**Tech Stack:** C#, .NET Framework 4.8, WinForms, SolidWorks 2018 COM/.NET interop, `HttpListener`, Newtonsoft.Json, System.Data.SQLite.Core, MSTest V2, OpenAI Responses API, Windows Credential Manager.

**Spec:** `docs/superpowers/specs/2026-09-22-solidworks-cad-agent-design.md`

## Global Constraints

- All V1 Windows projects target **.NET Framework 4.8**.
- Development and SolidWorks integration testing occur on the same Windows PC as **SolidWorks 2018**.
- The desktop UI and Agent Host are separate processes from day one.
- Desktop-to-host communication is HTTP/JSON bound only to `127.0.0.1` in V1.
- Default workspace is `C:\SolidWorks-CAD-Agent\Workspace\`.
- All user-facing CAD dimensions are millimetres; convert to metres only at the SolidWorks API boundary.
- SolidWorks CAD changes are made through registered, whitelisted commands; no cloud-supplied VBA, C#, PowerShell, shell commands, or arbitrary filesystem execution.
- Approval is required by default. Auto mode is optional and cannot bypass hard safety checks.
- Existing CAD files are never overwritten without explicit overwrite approval.
- The OpenAI API key is stored in Windows Credential Manager and is never written to config, SQLite, logs, or Git.
- SolidWorks remains visible while V1 executes.
- A failed COM call, failed feature, failed rebuild, invalid tool call, unsafe path, unresolved material ambiguity, or verification mismatch stops execution.
- Remote phone/tablet control is not implemented in V1, but all V1 interfaces must preserve the V2 path by keeping client, Agent Host, and SolidWorks Bridge boundaries separate.

## Review Focus

1. **Millimetres versus SolidWorks metres:** every geometry command must convert exactly once at the API boundary; tests must catch 100 mm becoming 100 m or 0.1 mm.
2. **Workspace escape:** `..`, rooted paths, sibling-prefix paths such as `C:\SolidWorks-CAD-Agent\Workspace-Evil`, and overwrite attempts must be rejected before SolidWorks sees a path.
3. **COM threading/lifecycle:** all SolidWorks COM calls must occur on one STA worker thread, including attach, launch, document access, save, and cleanup.
4. **Partial execution/cancellation:** after a command fails or a job is cancelled, no later geometry command may run and the last recoverable SolidWorks document remains open.
5. **Ambiguous AI output:** a prompt with a materially unspecified feature such as “M8 hole” without tapped/clearance intent must enter `AwaitingClarification`, not guess or enter execution even in Auto mode.

---

## Planned Repository Structure

```text
SolidWorks-CAD-Agent/
├── SolidWorksCadAgent.sln
├── .gitignore
├── README.md
├── scripts/
│   └── register-agent-host-url.ps1
├── src/
│   ├── SolidWorksCadAgent.Contracts/
│   │   ├── SolidWorksCadAgent.Contracts.csproj
│   │   ├── Cad/
│   │   │   ├── CadCommandEnvelope.cs
│   │   │   ├── CadCommandResult.cs
│   │   │   ├── CadError.cs
│   │   │   ├── CadCommandNames.cs
│   │   │   └── GeometryDtos.cs
│   │   ├── Jobs/
│   │   │   ├── JobState.cs
│   │   │   ├── CadJob.cs
│   │   │   ├── JobRevision.cs
│   │   │   └── JobDtos.cs
│   │   └── Api/
│   │       └── AgentApiDtos.cs
│   ├── SolidWorksCadAgent.Core/
│   │   ├── SolidWorksCadAgent.Core.csproj
│   │   ├── Units/UnitConverter.cs
│   │   ├── Workspace/WorkspacePolicy.cs
│   │   ├── Commands/ICadCommandHandler.cs
│   │   ├── Commands/CadCommandRegistry.cs
│   │   ├── Jobs/JobStateMachine.cs
│   │   ├── Jobs/ApprovalPolicy.cs
│   │   └── Ai/IAgentModel.cs
│   ├── SolidWorksCadAgent.SolidWorksBridge/
│   │   ├── SolidWorksCadAgent.SolidWorksBridge.csproj
│   │   ├── Threading/SolidWorksStaDispatcher.cs
│   │   ├── Session/ISolidWorksSession.cs
│   │   ├── Session/SolidWorksSession.cs
│   │   ├── Commands/DocumentCommandHandlers.cs
│   │   ├── Commands/SketchCommandHandlers.cs
│   │   ├── Commands/FeatureCommandHandlers.cs
│   │   ├── Inspection/SolidWorksInspector.cs
│   │   └── SolidWorksBridgeFacade.cs
│   ├── SolidWorksCadAgent.AgentHost/
│   │   ├── SolidWorksCadAgent.AgentHost.csproj
│   │   ├── Program.cs
│   │   ├── Host/LocalHttpServer.cs
│   │   ├── Host/AgentRoutes.cs
│   │   ├── Jobs/JobCoordinator.cs
│   │   ├── Persistence/SqliteJobRepository.cs
│   │   ├── Persistence/Schema.sql
│   │   ├── Security/WindowsCredentialStore.cs
│   │   ├── Ai/OpenAiResponsesClient.cs
│   │   └── Ai/OpenAiAgentModel.cs
│   └── SolidWorksCadAgent.Desktop/
│       ├── SolidWorksCadAgent.Desktop.csproj
│       ├── Program.cs
│       ├── Api/AgentHostClient.cs
│       ├── MainForm.cs
│       ├── MainForm.Designer.cs
│       ├── SettingsForm.cs
│       └── SettingsForm.Designer.cs
└── tests/
    ├── SolidWorksCadAgent.UnitTests/
    │   ├── SolidWorksCadAgent.UnitTests.csproj
    │   ├── UnitConverterTests.cs
    │   ├── WorkspacePolicyTests.cs
    │   ├── CadCommandRegistryTests.cs
    │   ├── JobStateMachineTests.cs
    │   ├── ApprovalPolicyTests.cs
    │   ├── SqliteJobRepositoryTests.cs
    │   ├── LocalHttpServerTests.cs
    │   └── OpenAiAgentModelTests.cs
    └── SolidWorksCadAgent.IntegrationTests/
        ├── SolidWorksCadAgent.IntegrationTests.csproj
        ├── SolidWorksSessionTests.cs
        └── AcceptancePlateTests.cs
```

---

### Task 1: Solution Skeleton, Shared Contracts, and Unit Conversion

**Files:**
- Create: `SolidWorksCadAgent.sln`
- Create: `.gitignore`
- Create: `README.md`
- Create: `src/SolidWorksCadAgent.Contracts/SolidWorksCadAgent.Contracts.csproj`
- Create: `src/SolidWorksCadAgent.Contracts/Cad/CadCommandEnvelope.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Cad/CadCommandResult.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Cad/CadError.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Cad/CadCommandNames.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Cad/GeometryDtos.cs`
- Create: `src/SolidWorksCadAgent.Core/SolidWorksCadAgent.Core.csproj`
- Create: `src/SolidWorksCadAgent.Core/Units/UnitConverter.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj`
- Create: `tests/SolidWorksCadAgent.UnitTests/UnitConverterTests.cs`

**Interfaces:**
- Produces: `UnitConverter.MillimetresToMetres(double)`, `UnitConverter.MetresToMillimetres(double)`.
- Produces: `CadCommandEnvelope`, `CadCommandResult`, `CadError`, command-name constants, and initial geometry DTOs used by all later layers.

- [ ] **Step 1: Create the solution and net48 projects**

Use Visual Studio 2022 Community or MSBuild tooling with the .NET Framework 4.8 Developer Pack installed. The project files should be SDK-style where possible:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

The unit-test project adds:

```xml
<ItemGroup>
  <PackageReference Include="MSTest.TestAdapter" Version="3.6.4" />
  <PackageReference Include="MSTest.TestFramework" Version="3.6.4" />
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
</ItemGroup>
```

Add project references from UnitTests to Contracts and Core.

- [ ] **Step 2: Write the failing unit conversion tests**

```csharp
[TestClass]
public class UnitConverterTests
{
    [TestMethod]
    public void MillimetresToMetres_100mm_ReturnsPointOneMetre()
    {
        Assert.AreEqual(0.1, UnitConverter.MillimetresToMetres(100.0), 1e-12);
    }

    [TestMethod]
    public void MetresToMillimetres_PointZeroOne_Returns10mm()
    {
        Assert.AreEqual(10.0, UnitConverter.MetresToMillimetres(0.01), 1e-12);
    }
}
```

- [ ] **Step 3: Run the tests and verify failure**

Run from a Developer Command Prompt:

```powershell
msbuild SolidWorksCadAgent.sln /t:Restore,Build /p:Configuration=Debug
vstest.console.exe tests\SolidWorksCadAgent.UnitTests\bin\Debug\SolidWorksCadAgent.UnitTests.dll
```

Expected: build/test failure because `UnitConverter` is not implemented.

- [ ] **Step 4: Implement the unit converter**

```csharp
public static class UnitConverter
{
    public static double MillimetresToMetres(double millimetres)
    {
        if (double.IsNaN(millimetres) || double.IsInfinity(millimetres))
            throw new ArgumentOutOfRangeException(nameof(millimetres));
        return millimetres / 1000.0;
    }

    public static double MetresToMillimetres(double metres)
    {
        if (double.IsNaN(metres) || double.IsInfinity(metres))
            throw new ArgumentOutOfRangeException(nameof(metres));
        return metres * 1000.0;
    }
}
```

- [ ] **Step 5: Add the command/result contracts**

Use a generic envelope so the registry can remain extensible without allowing arbitrary code:

```csharp
public sealed class CadCommandEnvelope
{
    public string Command { get; set; }
    public Newtonsoft.Json.Linq.JObject Parameters { get; set; }
}

public sealed class CadError
{
    public string Code { get; set; }
    public string Stage { get; set; }
    public string Message { get; set; }
    public string Detail { get; set; }
}

public sealed class CadCommandResult
{
    public bool Success { get; set; }
    public Newtonsoft.Json.Linq.JObject Data { get; set; }
    public CadError Error { get; set; }

    public static CadCommandResult Ok(object data) => new CadCommandResult
    {
        Success = true,
        Data = data == null ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.FromObject(data)
    };
}
```

`CadCommandNames` initially defines exact strings for `AttachSolidWorks`, `LaunchSolidWorks`, `NewPart`, `CreateSketch`, `AddRectangle`, `AddCircle`, `ExitSketch`, `Extrude`, `CutExtrude`, `Rebuild`, `GetBoundingBox`, `GetBodyCount`, `GetRebuildErrors`, `SavePart`, and `OpenPart`.

- [ ] **Step 6: Run all unit tests**

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "build: scaffold SolidWorks CAD Agent solution"
```

---

### Task 2: Workspace Policy, Settings, and Command Registry

**Files:**
- Create: `src/SolidWorksCadAgent.Core/Workspace/WorkspacePolicy.cs`
- Create: `src/SolidWorksCadAgent.Core/Commands/ICadCommandHandler.cs`
- Create: `src/SolidWorksCadAgent.Core/Commands/CadCommandRegistry.cs`
- Create: `src/SolidWorksCadAgent.Core/AgentSettings.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/WorkspacePolicyTests.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/CadCommandRegistryTests.cs`

**Interfaces:**
- Produces: `WorkspacePolicy.ResolveForRead(string)`, `ResolveForWrite(string, bool allowOverwrite)`.
- Produces: `ICadCommandHandler.Name`, `Validate(JObject)`, `ExecuteAsync(JObject, CancellationToken)`.
- Produces: `CadCommandRegistry.ExecuteAsync(CadCommandEnvelope, CancellationToken)`.

- [ ] **Step 1: Write workspace escape and overwrite tests**

Include at least these cases:

```csharp
[TestMethod]
public void ResolveForWrite_TraversalOutsideWorkspace_Throws()
{
    var policy = new WorkspacePolicy(@"C:\SolidWorks-CAD-Agent\Workspace");
    Assert.ThrowsException<WorkspacePolicyException>(() =>
        policy.ResolveForWrite(@"..\secret.sldprt", false));
}

[TestMethod]
public void ResolveForWrite_SiblingPrefix_Throws()
{
    var policy = new WorkspacePolicy(@"C:\SolidWorks-CAD-Agent\Workspace");
    Assert.ThrowsException<WorkspacePolicyException>(() =>
        policy.ResolveForWrite(@"C:\SolidWorks-CAD-Agent\Workspace-Evil\x.sldprt", false));
}
```

Also test that an existing target rejects `allowOverwrite=false` and permits it only when explicitly true.

- [ ] **Step 2: Implement canonical workspace resolution**

`WorkspacePolicy` must canonicalize the configured root with `Path.GetFullPath`, append a directory separator when doing prefix checks, reject paths outside that root using `StringComparison.OrdinalIgnoreCase`, reject empty filenames, and ensure the final extension is permitted for the operation.

Do not use a raw `StartsWith(root)` check without the separator-boundary rule.

- [ ] **Step 3: Write unsupported-command and malformed-parameter tests**

```csharp
[TestMethod]
public async Task ExecuteAsync_UnregisteredCommand_ReturnsUnsupportedCommand()
{
    var registry = new CadCommandRegistry(Array.Empty<ICadCommandHandler>());
    var result = await registry.ExecuteAsync(
        new CadCommandEnvelope { Command = "RunPowerShell", Parameters = new JObject() },
        CancellationToken.None);

    Assert.IsFalse(result.Success);
    Assert.AreEqual("UNSUPPORTED_COMMAND", result.Error.Code);
}
```

- [ ] **Step 4: Implement registry validation and execution**

```csharp
public interface ICadCommandHandler
{
    string Name { get; }
    CadError Validate(JObject parameters);
    Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken);
}
```

`CadCommandRegistry` indexes handlers by exact command name, rejects duplicate names during construction, validates before execute, catches handler exceptions into structured `CadError`, and honours cancellation.

- [ ] **Step 5: Add `AgentSettings` defaults**

Set defaults exactly:

```csharp
public string WorkspaceRoot { get; set; } = @"C:\SolidWorks-CAD-Agent\Workspace";
public bool AutoMode { get; set; } = false;
public string HostPrefix { get; set; } = "http://127.0.0.1:53741/";
public string OpenAiModel { get; set; } = "gpt-5.6-sol";
```

- [ ] **Step 6: Run unit tests and commit**

```bash
git add src/SolidWorksCadAgent.Core tests/SolidWorksCadAgent.UnitTests
git commit -m "feat: add safe workspace and CAD command registry"
```

---

### Task 3: SolidWorks STA Dispatcher and Attach/Launch Session

**Files:**
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/SolidWorksCadAgent.SolidWorksBridge.csproj`
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Threading/SolidWorksStaDispatcher.cs`
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Session/ISolidWorksSession.cs`
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Session/SolidWorksSession.cs`
- Create: `tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj`
- Create: `tests/SolidWorksCadAgent.IntegrationTests/SolidWorksSessionTests.cs`

**Interfaces:**
- Produces: `SolidWorksStaDispatcher.InvokeAsync<T>(Func<T>, CancellationToken)`.
- Produces: `ISolidWorksSession.GetStatusAsync`, `AttachAsync`, `LaunchAsync`, and `GetApplicationAsync` for bridge-internal use.

- [ ] **Step 1: Add SolidWorks 2018 references on the SolidWorks PC**

In Visual Studio, add COM/.NET references for the installed SolidWorks 2018 API libraries so these namespaces compile:

```csharp
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
```

Set `Embed Interop Types = False`. Do not copy guessed interop DLLs from the internet into the repository.

- [ ] **Step 2: Write the dispatcher thread-affinity test**

Create a test that calls `InvokeAsync` twice and records `Thread.CurrentThread.ManagedThreadId` plus `Thread.CurrentThread.GetApartmentState()`. Assert both calls use the same thread and `ApartmentState.STA`.

- [ ] **Step 3: Implement the dedicated STA dispatcher**

Create one background thread, call `SetApartmentState(ApartmentState.STA)` before start, process a `BlockingCollection<Action>`, and marshal results/errors into `TaskCompletionSource<T>`. Disposal completes the queue and joins the thread.

All SolidWorks COM access in later tasks must run through this dispatcher.

- [ ] **Step 4: Implement attach and launch**

Attach path:

```csharp
var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
```

Launch path:

```csharp
var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: true);
var app = (SldWorks)Activator.CreateInstance(type);
app.Visible = true;
```

Return structured status containing connected/running state and `RevisionNumber()`/version information when available. Treat “not running” as a normal attach result, not an unhandled exception.

- [ ] **Step 5: Add integration tests**

Use `[TestCategory("SolidWorksIntegration")]` so these are opt-in. Test:

1. Attach succeeds when SolidWorks is manually running.
2. Attach reports disconnected when it is not running.
3. Launch makes SolidWorks visible and then reports connected.
4. All session methods execute on the dispatcher STA thread.

- [ ] **Step 6: Run the integration tests manually on the SolidWorks PC**

```powershell
vstest.console.exe tests\SolidWorksCadAgent.IntegrationTests\bin\Debug\SolidWorksCadAgent.IntegrationTests.dll /TestCaseFilter:"TestCategory=SolidWorksIntegration"
```

- [ ] **Step 7: Commit**

```bash
git add src/SolidWorksCadAgent.SolidWorksBridge tests/SolidWorksCadAgent.IntegrationTests
git commit -m "feat: connect to SolidWorks 2018 on dedicated STA thread"
```

---

### Task 4: Minimal Native CAD Commands for the Acceptance Part

**Files:**
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/DocumentCommandHandlers.cs`
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/SketchCommandHandlers.cs`
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/FeatureCommandHandlers.cs`
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/SolidWorksBridgeFacade.cs`
- Modify: `tests/SolidWorksCadAgent.IntegrationTests/AcceptancePlateTests.cs`

**Interfaces:**
- Produces registered handlers for `NewPart`, `CreateSketch`, `AddRectangle`, `AddCircle`, `ExitSketch`, `Extrude`, `CutExtrude`, and `Rebuild`.
- Every input geometry value remains in mm until the handler calls `UnitConverter.MillimetresToMetres`.

- [ ] **Step 1: Write the failing native-geometry integration test**

The test sequence must use the command registry rather than calling SolidWorks directly:

```csharp
await Execute("NewPart", new { });
await Execute("CreateSketch", new { plane = "Top Plane" });
await Execute("AddRectangle", new { centreXmm = 0.0, centreYmm = 0.0, widthMm = 100.0, heightMm = 60.0 });
await Execute("ExitSketch", new { });
await Execute("Extrude", new { depthMm = 10.0 });
await Execute("CreateSketch", new { plane = "Top Plane" });
await Execute("AddCircle", new { centreXmm = 0.0, centreYmm = 0.0, diameterMm = 20.0 });
await Execute("ExitSketch", new { });
await Execute("CutExtrude", new { endCondition = "ThroughAll" });
await Execute("Rebuild", new { });
```

Assert each result succeeds and one native part document remains active.

- [ ] **Step 2: Implement `NewPart` without hard-coded template paths**

Read the configured SolidWorks default part template:

```csharp
string template = _swApp.GetUserPreferenceStringValue(
    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
```

If it is missing or the file does not exist, return `PART_TEMPLATE_NOT_CONFIGURED`. Otherwise call `NewDocument(template, 0, 0, 0)` and require a non-null `ModelDoc2`.

- [ ] **Step 3: Implement sketch plane selection and geometry**

`CreateSketch` selects only allowed named base planes for V1 (`Top Plane`, `Front Plane`, `Right Plane`) using `ModelDocExtension.SelectByID2`, then calls `SketchManager.InsertSketch(true)`.

For the centred rectangle, convert half-width and half-height to metres, then use `SketchManager.CreateCenterRectangle(centreX, centreY, 0, centreX + halfWidth, centreY + halfHeight, 0)`.

For the circle, use `SketchManager.CreateCircleByRadius(centreX, centreY, 0, radiusMetres)`.

- [ ] **Step 4: Implement blind boss extrusion**

Use `IFeatureManager.FeatureExtrusion2` with a blind end condition and the converted depth. Return an error if the returned `Feature` is null. Keep all draft/thin-feature options off for V1.

- [ ] **Step 5: Implement through-all cut extrusion**

Use the SolidWorks 2018 extrusion-cut API exposed by the installed interop library, with Direction 1 end condition `swEndCondThroughAll`, no draft, no thin feature, and auto-select bodies. The handler accepts only `endCondition="ThroughAll"` in this milestone; reject any other value as `UNSUPPORTED_PARAMETER_VALUE`.

Before committing, record the equivalent operation once in SolidWorks 2018’s macro recorder and compare the generated argument ordering against the installed 2018 interop signature. The committed implementation must match the local 2018 signature, not a newer online example.

- [ ] **Step 6: Make command failure stop the acceptance sequence**

The integration helper must throw/abort immediately when any `CadCommandResult.Success` is false. Add a test with an invalid plane name and assert the next geometry command is not invoked.

- [ ] **Step 7: Run the acceptance geometry test and visually inspect SolidWorks**

Expected visible model: 100 × 60 × 10 plate with a centred 20 mm through-hole and native sketch/extrude/cut features in the FeatureManager tree.

- [ ] **Step 8: Commit**

```bash
git add src/SolidWorksCadAgent.SolidWorksBridge tests/SolidWorksCadAgent.IntegrationTests
git commit -m "feat: create native acceptance geometry in SolidWorks"
```

---

### Task 5: Inspection, Verification, Save, and Reopen

**Files:**
- Create: `src/SolidWorksCadAgent.SolidWorksBridge/Inspection/SolidWorksInspector.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/DocumentCommandHandlers.cs`
- Modify: `tests/SolidWorksCadAgent.IntegrationTests/AcceptancePlateTests.cs`

**Interfaces:**
- Produces: `GetBoundingBox`, `GetBodyCount`, `GetRebuildErrors`, `GetFeatureTree`, `SavePart`, `OpenPart` commands.

- [ ] **Step 1: Extend the acceptance test with numerical verification**

After rebuild, require:

```csharp
Assert.AreEqual(1, bodyCount);
Assert.AreEqual(100.0, box.SizeXmm, 0.02);
Assert.AreEqual(60.0, box.SizeYmm, 0.02);
Assert.AreEqual(10.0, box.SizeZmm, 0.02);
Assert.IsFalse(rebuild.HasErrors);
```

Also require the feature list to contain a boss/extrude and a cut feature; do not depend on user-localized display names for exact feature names if API type names are available.

- [ ] **Step 2: Implement body count and bounding-box inspection**

Use `PartDoc.GetBodies2((int)swBodyType_e.swSolidBody, true)` for solid bodies. For each body, obtain its box in metres, union extents, then convert the final extents to mm exactly once.

- [ ] **Step 3: Implement rebuild status**

Call `ModelDoc2.ForceRebuild3(false)` and inspect the return plus feature error codes where the 2018 API exposes them. Map any rebuild failure into structured `REBUILD_FAILED` data and stop later commands.

- [ ] **Step 4: Implement safe native save**

Resolve the requested output through `WorkspacePolicy`. Ensure `.sldprt`. Save with the SolidWorks document extension `SaveAs`/`SaveAs3` API available in 2018 and capture error/warning codes. Reject existing targets unless `allowOverwrite=true` was supplied by an already-authorized path.

- [ ] **Step 5: Reopen and re-verify**

Close the generated document, open the saved `.SLDPRT` through the SolidWorks API, rebuild it, and repeat body/bounding-box checks. This catches files that appeared valid only in the unsaved session.

- [ ] **Step 6: Add a millimetre-scale regression**

Build a 10 × 10 × 1 mm block in an integration test and verify 10/10/1 mm. This specifically catches accidental double/no conversion.

- [ ] **Step 7: Commit**

```bash
git add src/SolidWorksCadAgent.SolidWorksBridge tests/SolidWorksCadAgent.IntegrationTests
git commit -m "feat: verify and persist native SolidWorks parts"
```

---

### Task 6: Persistent Job Domain, State Machine, and SQLite Repository

**Files:**
- Create: `src/SolidWorksCadAgent.Contracts/Jobs/JobState.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Jobs/CadJob.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Jobs/JobRevision.cs`
- Create: `src/SolidWorksCadAgent.Contracts/Jobs/JobDtos.cs`
- Create: `src/SolidWorksCadAgent.Core/Jobs/JobStateMachine.cs`
- Create: `src/SolidWorksCadAgent.Core/Jobs/ApprovalPolicy.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/SolidWorksCadAgent.AgentHost.csproj`
- Create: `src/SolidWorksCadAgent.AgentHost/Persistence/Schema.sql`
- Create: `src/SolidWorksCadAgent.AgentHost/Persistence/SqliteJobRepository.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/JobStateMachineTests.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/ApprovalPolicyTests.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/SqliteJobRepositoryTests.cs`

**Interfaces:**
- Produces: `JobStateMachine.Transition(CadJob, JobState)`.
- Produces: `ApprovalPolicy.CanExecute(CadJob, AgentSettings)`.
- Produces: `SqliteJobRepository.CreateAsync`, `GetAsync`, `UpdateAsync`, `AppendRevisionAsync`, `AppendCommandAsync`.

- [ ] **Step 1: Define the state enum exactly**

```csharp
public enum JobState
{
    New,
    Interpreting,
    AwaitingClarification,
    AwaitingApproval,
    Approved,
    Executing,
    Verifying,
    ReadyForReview,
    Completed,
    Failed,
    Cancelled
}
```

- [ ] **Step 2: Write state-transition tests**

Test allowed happy path, clarification round-trip, cancellation from pre-terminal states, and rejection of invalid transitions such as `New -> Executing`, `Failed -> Executing`, and `Cancelled -> Verifying`.

- [ ] **Step 3: Write approval-policy tests including Auto mode ambiguity**

Required assertions:

- approval mode + `AwaitingApproval` => cannot execute;
- approved job => can execute;
- Auto mode + unambiguous valid plan => can execute;
- Auto mode + unresolved ambiguity => cannot execute;
- any overwrite request without explicit overwrite authorization => cannot execute.

- [ ] **Step 4: Implement domain/state logic**

Keep transition rules in a dictionary/set rather than scattered UI conditions. `Transition` must throw a domain exception on an invalid transition and update `UpdatedUtc` only after validation.

- [ ] **Step 5: Create SQLite schema**

Use tables `Jobs`, `Revisions`, `CommandExecutions`, `VerificationResults`, and `Attachments`. Store large images/CAD files as filesystem paths, not blobs. Store all timestamps in UTC ISO-8601 text or integer Unix milliseconds consistently.

- [ ] **Step 6: Write repository round-trip tests using a temporary DB**

Create a job, append a revision, append command success/failure data, close the repository, reopen it, and assert the complete job still reconstructs correctly.

- [ ] **Step 7: Implement the SQLite repository**

Use `System.Data.SQLite.Core`, parameterized SQL only, transactions when updating job state plus related execution records, and schema initialization at startup.

- [ ] **Step 8: Commit**

```bash
git add src/SolidWorksCadAgent.Contracts src/SolidWorksCadAgent.Core src/SolidWorksCadAgent.AgentHost tests/SolidWorksCadAgent.UnitTests
git commit -m "feat: persist CAD jobs and enforce lifecycle"
```

---

### Task 7: Localhost Agent Host HTTP/JSON API

**Files:**
- Create: `src/SolidWorksCadAgent.Contracts/Api/AgentApiDtos.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Program.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Host/LocalHttpServer.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Host/AgentRoutes.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Jobs/JobCoordinator.cs`
- Create: `scripts/register-agent-host-url.ps1`
- Create: `tests/SolidWorksCadAgent.UnitTests/LocalHttpServerTests.cs`

**Interfaces:**
- HTTP endpoints: `GET /health`, `GET /solidworks/status`, `POST /solidworks/attach`, `POST /solidworks/launch`, `POST /jobs`, `GET /jobs/{id}`, `POST /jobs/{id}/approve`, `POST /jobs/{id}/cancel`.

- [ ] **Step 1: Register the localhost HTTP prefix for development**

Create an elevated PowerShell script:

```powershell
param([string]$User = "$env:USERDOMAIN\$env:USERNAME")
netsh http add urlacl url=http://127.0.0.1:53741/ user="$User"
```

Document the corresponding delete command in the script comments.

- [ ] **Step 2: Write an HTTP binding safety test**

Configuration must reject prefixes whose host is not exactly `127.0.0.1` for V1. `http://0.0.0.0:53741/`, LAN IPs, and `http://+:53741/` are invalid.

- [ ] **Step 3: Implement `HttpListener` server and routing**

Use a single listener. Deserialize request bodies with Newtonsoft.Json, return JSON with UTF-8 content type, map exceptions to structured HTTP errors, and never echo secret values.

- [ ] **Step 4: Implement health and SolidWorks control routes**

`/health` returns process status and schema version. SolidWorks routes call the session through the bridge and return connection/version status.

- [ ] **Step 5: Implement basic job routes against SQLite**

`POST /jobs` accepts `{ "prompt": "..." }`, creates a persistent `New` job, and returns its ID. At this task, no cloud call is made yet; this route proves process separation and persistence.

- [ ] **Step 6: Test cancel semantics**

Create a fake command executor that records calls. Start a job, cancel it, then attempt to continue. Assert no subsequent command executes and state remains `Cancelled`.

- [ ] **Step 7: Run host manually**

```powershell
src\SolidWorksCadAgent.AgentHost\bin\Debug\SolidWorksCadAgent.AgentHost.exe
Invoke-RestMethod http://127.0.0.1:53741/health
```

Expected HTTP 200 JSON.

- [ ] **Step 8: Commit**

```bash
git add src/SolidWorksCadAgent.Contracts src/SolidWorksCadAgent.AgentHost scripts tests/SolidWorksCadAgent.UnitTests
git commit -m "feat: expose localhost CAD agent host API"
```

---

### Task 8: WinForms Desktop Control Panel

**Files:**
- Create: `src/SolidWorksCadAgent.Desktop/SolidWorksCadAgent.Desktop.csproj`
- Create: `src/SolidWorksCadAgent.Desktop/Program.cs`
- Create: `src/SolidWorksCadAgent.Desktop/Api/AgentHostClient.cs`
- Create: `src/SolidWorksCadAgent.Desktop/MainForm.cs`
- Create: `src/SolidWorksCadAgent.Desktop/MainForm.Designer.cs`
- Create: `src/SolidWorksCadAgent.Desktop/SettingsForm.cs`
- Create: `src/SolidWorksCadAgent.Desktop/SettingsForm.Designer.cs`

**Interfaces:**
- Consumes the Agent Host HTTP API only; no reference from Desktop to SolidWorks interop assemblies.

- [ ] **Step 1: Create the WinForms project**

Target net48 and reference Contracts. Add Newtonsoft.Json. Do not reference `SolidWorksCadAgent.SolidWorksBridge`.

- [ ] **Step 2: Build the main layout**

Create: status header, prompt box, Send button, attach/launch buttons, current-job panel, explicit ambiguities box, proposed-plan box, Approve Build/Request Changes/Cancel buttons, progress/verification log, job-history list, and Settings button.

- [ ] **Step 3: Implement `AgentHostClient`**

Use `HttpClient` with base URI `http://127.0.0.1:53741/`. Implement strongly typed methods for the routes from Task 7. Network errors should become user-readable “Agent Host unavailable” status instead of crashing the UI thread.

- [ ] **Step 4: Wire attach and launch controls**

On form load poll `/health` and `/solidworks/status`. Connect and Launch buttons invoke their respective routes and update visible SolidWorks version/status.

- [ ] **Step 5: Wire prompt submission and job history**

Send creates a job, shows its persisted ID/state, and refreshes job history. Keep attachment buttons visible but disabled or labelled “V1 image interpretation not enabled” until that capability is implemented; do not fake attachment processing.

- [ ] **Step 6: Add Settings UI shell**

Expose workspace root, Approval/Auto mode, OpenAI model, API credential status, SolidWorks executable override, logging level, and a read-only V2 Remote Access section stating “Not enabled in V1”.

- [ ] **Step 7: Manually verify process separation**

Start Agent Host, then Desktop. Close/reopen Desktop and confirm jobs remain in history because state lives in Host/SQLite, not the UI.

- [ ] **Step 8: Commit**

```bash
git add src/SolidWorksCadAgent.Desktop
git commit -m "feat: add desktop CAD agent control panel"
```

---

### Task 9: Windows Credential Manager and OpenAI Provider Abstraction

**Files:**
- Create: `src/SolidWorksCadAgent.Core/Ai/IAgentModel.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Security/WindowsCredentialStore.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Ai/OpenAiResponsesClient.cs`
- Create: `src/SolidWorksCadAgent.AgentHost/Ai/OpenAiAgentModel.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/OpenAiAgentModelTests.cs`

**Interfaces:**
- Produces: `IAgentModel.InterpretJobAsync`, `PlanJobAsync`, `ContinueJobAsync`, `ReviewResultAsync`.
- Produces: `ISecretStore.Set`, `Get`, `Exists`, `Delete` implemented by Windows Credential Manager.

- [ ] **Step 1: Define structured AI result contracts**

The interpretation result must include explicit fields:

```csharp
public sealed class JobInterpretation
{
    public string Summary { get; set; }
    public List<string> Assumptions { get; set; }
    public List<string> Ambiguities { get; set; }
    public List<PlannedFeature> Features { get; set; }
    public List<CadCommandEnvelope> ProposedCommands { get; set; }
}
```

No numeric “confidence score”.

- [ ] **Step 2: Implement Windows Credential Manager storage**

Use P/Invoke to `CredWriteW`, `CredReadW`, `CredDeleteW`, and `CredFree` with `CRED_TYPE_GENERIC`. Store under target name `SolidWorksCadAgent/OpenAI`. Ensure secret byte buffers are not logged and unmanaged buffers are freed in `finally`.

- [ ] **Step 3: Add credential-store tests through an in-memory fake**

Unit-test consumers against `ISecretStore`; do not require Windows Credential Manager in ordinary unit tests. Add one opt-in local integration smoke test for the real store if desired, using a disposable test target name that is deleted in `finally`.

- [ ] **Step 4: Implement a thin Responses API HTTP client**

Use `POST https://api.openai.com/v1/responses`, `Authorization: Bearer <key>`, and `store: false`. Default model comes from settings (`gpt-5.6-sol`). Do not persist raw request headers or API keys in logs.

Use current Responses API function-tool shape, for example:

```json
{
  "type": "function",
  "name": "cad_add_rectangle",
  "description": "Add a centred rectangle to the active sketch. Dimensions are millimetres.",
  "strict": true,
  "parameters": {
    "type": "object",
    "properties": {
      "centreXmm": { "type": "number" },
      "centreYmm": { "type": "number" },
      "widthMm": { "type": "number", "exclusiveMinimum": 0 },
      "heightMm": { "type": "number", "exclusiveMinimum": 0 }
    },
    "required": ["centreXmm", "centreYmm", "widthMm", "heightMm"],
    "additionalProperties": false
  }
}
```

When the model returns `function_call`, execute only by passing the name/arguments through `CadCommandRegistry`; send the resulting JSON string back as `function_call_output` using the returned `call_id`.

- [ ] **Step 5: Test malformed/unknown tool calls with a fake HTTP transport**

Return an AI response requesting `RunPowerShell`. Assert the model adapter/registry rejects it and the job becomes Failed with `UNSUPPORTED_COMMAND`; no shell process is started.

- [ ] **Step 6: Test ambiguity output**

Fake the model interpretation for “Add an M8 hole” with `Ambiguities = ["Tapped or clearance hole is not specified"]`. Assert the coordinator moves to `AwaitingClarification`, with zero CAD commands executed even when `AutoMode=true`.

- [ ] **Step 7: Commit**

```bash
git add src/SolidWorksCadAgent.Core src/SolidWorksCadAgent.AgentHost tests/SolidWorksCadAgent.UnitTests
git commit -m "feat: add secure OpenAI agent provider"
```

---

### Task 10: Approval Workflow, Auto Mode, and End-to-End Job Coordinator

**Files:**
- Modify: `src/SolidWorksCadAgent.AgentHost/Jobs/JobCoordinator.cs`
- Modify: `src/SolidWorksCadAgent.AgentHost/Host/AgentRoutes.cs`
- Modify: `src/SolidWorksCadAgent.Desktop/MainForm.cs`
- Modify: `src/SolidWorksCadAgent.Desktop/SettingsForm.cs`
- Create: `tests/SolidWorksCadAgent.UnitTests/JobCoordinatorTests.cs`

**Interfaces:**
- `POST /jobs/{id}/approve` authorizes only the current revision/plan hash.
- `POST /jobs/{id}/clarify` adds user clarification and re-enters interpretation.
- Settings expose `AutoMode` but hard gates remain in `ApprovalPolicy`.

- [ ] **Step 1: Write default-approval end-to-end unit test with fakes**

Fake AI returns a valid plate plan. Fake CAD executor records commands. After `CreateJob/Interpret`, assert state is `AwaitingApproval` and zero CAD commands ran. After `Approve`, assert the commands execute in order.

- [ ] **Step 2: Bind approval to the plan revision**

Generate a stable revision ID/hash when interpretation is stored. Approval request must include that ID. If the plan changed after the UI displayed it, reject with HTTP 409 instead of executing stale approval.

- [ ] **Step 3: Implement execution loop with fail-stop semantics**

For each approved command:

1. check cancellation;
2. validate registry command;
3. persist “started” execution;
4. execute;
5. persist result;
6. stop immediately on failure;
7. only continue when success is true.

After geometry commands, transition to `Verifying`, run required inspections, and only then transition to `ReadyForReview`.

- [ ] **Step 4: Implement Auto mode through `ApprovalPolicy`**

Auto mode may skip the `AwaitingApproval` pause only if the interpretation has zero unresolved ambiguities, all commands are registered, no overwrite is requested, and no privileged action is involved.

- [ ] **Step 5: Implement UI approval/clarification/cancel controls**

Display interpretation, assumptions, ambiguities, and plan before enabling Approve. “Request Changes” sends a clarification/revision prompt rather than directly editing database state. Cancel updates host state and disables further build actions.

- [ ] **Step 6: Add failure/cancellation tests from Review Focus**

Inject a failure on command 3 of 5 and assert commands 4/5 never execute. Cancel during a fake long-running command and assert no new command begins afterward.

- [ ] **Step 7: Commit**

```bash
git add src tests/SolidWorksCadAgent.UnitTests
git commit -m "feat: enforce approval and fail-stop CAD execution"
```

---

### Task 11: AI-Driven Acceptance Part, Verification Contract, and Regression Fixture

**Files:**
- Create: `tests/fixtures/ai/acceptance-plate.json`
- Create: `tests/fixtures/ai/ambiguous-m8-hole.json`
- Modify: `tests/SolidWorksCadAgent.IntegrationTests/AcceptancePlateTests.cs`
- Modify: `src/SolidWorksCadAgent.AgentHost/Ai/OpenAiAgentModel.cs`
- Modify: `src/SolidWorksCadAgent.AgentHost/Jobs/JobCoordinator.cs`
- Modify: `README.md`

**Interfaces:**
- End-to-end user prompt: `Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.`
- Output: verified native `.SLDPRT`, one solid body, 100/60/10 mm bounding box, centred Ø20 hole, successful rebuild, persistent job record.

- [ ] **Step 1: Add deterministic AI regression fixtures**

The acceptance fixture records expected interpreted intent and required command families; the ambiguous fixture requires clarification. The fixtures are provider-regression expectations, not hard-coded production answers.

- [ ] **Step 2: Add an explicit verification requirement to the AI system prompt**

The model must not declare success itself. It requests/uses bridge inspection results; the Agent Host decides job success from programmatic verification. Include the rule: “Never infer a measurement from a screenshot when a structured CAD measurement tool is available.”

- [ ] **Step 3: Run the real cloud interpretation in approval mode**

Submit the acceptance prompt through the WinForms UI. Confirm the AI proposes the correct dimensions/features and pauses at `AwaitingApproval`.

- [ ] **Step 4: Approve and execute against real SolidWorks 2018**

Observe SolidWorks visibly creating the native model. Require all commands to be present in SQLite execution history.

- [ ] **Step 5: Verify and save**

Require the programmatic checks from Task 5 and save to a unique workspace path such as `Jobs\<job-id>\AcceptancePlate.SLDPRT`. Close/reopen and verify again.

- [ ] **Step 6: Repeat the complete acceptance run three times**

Use new job IDs each time. All three must complete without manual SolidWorks intervention after approval. If one fails, diagnose/fix before considering V1 acceptance achieved.

- [ ] **Step 7: Run the ambiguity regression through the real provider**

Prompt “Make a plate with an M8 hole” without specifying tapped/clearance. The system must request clarification rather than create geometry. If provider variability makes this insufficiently deterministic, strengthen the engineering-intent prompt/schema until the behaviour is consistent.

- [ ] **Step 8: Update README with V1 setup/run procedure**

Document prerequisites, cloning/building, SolidWorks 2018 interop reference setup, URL ACL script, workspace, Agent Host launch, Desktop launch, API credential configuration through the Settings UI, and the acceptance prompt. Do not put an API key example value in the repository.

- [ ] **Step 9: Commit**

```bash
git add tests src README.md
git commit -m "test: prove AI to native SolidWorks acceptance workflow"
```

---

### Task 12: V1 Hardening and Release Gate

**Files:**
- Modify: `.gitignore`
- Modify: `README.md`
- Create: `docs/v1-release-checklist.md`
- Modify tests as required by failures found during the gate.

**Interfaces:**
- Produces the first reviewable V1 baseline; does not add deferred CAD feature families.

- [ ] **Step 1: Ensure generated/sensitive files are ignored**

Ignore at minimum:

```gitignore
.vs/
bin/
obj/
*.user
*.suo
*.db
*.db-shm
*.db-wal
Workspace/
*.sldprt
*.sldasm
*.slddrw
```

Do not ignore source fixtures/documents that are intentionally committed.

- [ ] **Step 2: Run every non-SolidWorks unit test from a clean checkout/build**

Expected: all pass.

- [ ] **Step 3: Run every `SolidWorksIntegration` test on the actual SolidWorks 2018 PC**

Expected: all pass with SolidWorks visible.

- [ ] **Step 4: Run the five Review Focus scenarios explicitly**

Record pass/fail in `docs/v1-release-checklist.md`:

1. mm/metre scale regression;
2. workspace traversal/sibling-prefix and overwrite rejection;
3. STA-thread enforcement;
4. command failure/cancellation prevents later commands;
5. ambiguous M8 prompt stops for clarification even in Auto mode.

- [ ] **Step 5: Validate process boundaries**

Confirm Desktop references no SolidWorks interop assemblies, SolidWorks Bridge contains no OpenAI/HTTP credential logic, and Agent Host remains bound only to `127.0.0.1`.

- [ ] **Step 6: Validate credential/log hygiene**

Search repository and runtime logs for the API key prefix used during testing. Confirm no key appears in Git history, SQLite, log files, exception text, or screenshots.

- [ ] **Step 7: Run the acceptance workflow in both modes**

Approval mode: must pause before build. Auto mode: may build without pause only for the unambiguous acceptance prompt; overwrite and ambiguity hard gates remain enforced.

- [ ] **Step 8: Confirm V2 extension seam**

Document in the release checklist that a future remote client can consume the Agent Host job API without referencing SolidWorks. Do not expose the V1 listener to LAN/WAN as part of this task.

- [ ] **Step 9: Commit the V1 release gate evidence**

```bash
git add .gitignore README.md docs/v1-release-checklist.md tests src
git commit -m "chore: complete SolidWorks CAD Agent V1 release gate"
```

---

## Reference Notes for Implementers

- SolidWorks 2018 API coordinates and feature depths are expressed in metres; the product/UI contract for this project is millimetres.
- Use the installed SolidWorks 2018 interop library as the compile-time source of truth for exact method signatures. Official SolidWorks API Help examples for `ISketchManager.CreateCenterRectangle`, `IFeatureManager.FeatureExtrusion2`, user default templates, and cut extrusion are useful references, but the installed 2018 type library wins if a later online page differs.
- OpenAI V1 should use the Responses API with function tools and `function_call_output`; `store=false` is required by this plan. Keep the model ID configurable even though the initial default is `gpt-5.6-sol`.
- Do not add image/sketch interpretation, assemblies, drawings, loft/sweep/shell, unrestricted macros, or remote network exposure merely because the architecture can support them. Those are subsequent increments after this acceptance baseline is reliable.
