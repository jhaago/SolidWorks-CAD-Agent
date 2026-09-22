# SolidWorks CAD Agent V1 Command Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the initial V1 command families from the approved design after the acceptance-part workflow is proven, without broadening into assemblies, drawings, complex surfacing, or remote access.

**Architecture:** Extend the existing versioned `CadCommandRegistry` and SolidWorks Bridge only. New commands keep millimetres at the product boundary, convert at the SolidWorks API boundary, run on the existing STA dispatcher, return structured results, and remain callable through the same Agent Host interfaces.

**Tech Stack:** C#, .NET Framework 4.8, SolidWorks 2018 COM/.NET interop, MSTest V2.

**Spec:** `docs/superpowers/specs/2026-09-22-solidworks-cad-agent-design.md`

**Execution order:** Run this companion plan after Task 11 of `docs/superpowers/plans/2026-09-22-solidworks-cad-agent-v1-implementation.md` and before that plan’s final V1 hardening/release-gate task.

## Global Constraints

- All existing V1 safety, workspace, approval, fail-stop, credential, and STA-thread rules remain unchanged.
- Do not introduce arbitrary macro/script execution.
- Do not expose the Agent Host to LAN/WAN.
- Use the installed SolidWorks 2018 interop library as the source of truth for exact API signatures.
- Add one command at a time with a failing test before implementation.

## Review Focus

1. Sketch relations/dimensions must operate on explicitly identified entities, not whatever happens to be selected from a previous command.
2. Fillet/chamfer selection must fail clearly when the requested edge reference is stale or ambiguous.
3. `CaptureView` must save only inside the workspace and must not overwrite without permission.
4. `CloseDocument` must never close an unrelated user document; it must target the job-owned document identity.
5. New commands must remain unavailable to the AI until registered with a strict schema.

---

### Task 1: Remaining Sketch Commands and Sketch Inspection

**Files:**
- Modify: `src/SolidWorksCadAgent.Contracts/Cad/CadCommandNames.cs`
- Modify: `src/SolidWorksCadAgent.Contracts/Cad/GeometryDtos.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/SketchCommandHandlers.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Inspection/SolidWorksInspector.cs`
- Modify: `tests/SolidWorksCadAgent.IntegrationTests/AcceptancePlateTests.cs`
- Create: `tests/SolidWorksCadAgent.IntegrationTests/SketchCommandTests.cs`

**Interfaces:**
- Produces commands: `AddLine`, `AddArc`, `AddDimension`, `AddRelation`, `GetSketchStatus`.

- [ ] **Step 1: Add strict DTO/schema tests for line and arc commands**

Require explicit millimetre coordinates. Reject NaN/infinity and zero-length lines. For arcs, require centre/start/end coordinates and reject degenerate radius.

- [ ] **Step 2: Implement `AddLine` and `AddArc`**

Use the active document’s `SketchManager` methods from the installed 2018 interop library. Convert coordinates to metres exactly once immediately before the API call. Return stable entity references from each created sketch segment so later relation/dimension commands can refer to a known entity rather than selection state.

- [ ] **Step 3: Write relation tests**

Create two lines and apply supported V1 relations by entity reference. Initial allowed relation names: `Horizontal`, `Vertical`, `Coincident`, `Parallel`, `Perpendicular`, `Tangent`, and `Equal`. Unknown relation names must return `UNSUPPORTED_PARAMETER_VALUE`.

- [ ] **Step 4: Implement `AddRelation`**

Resolve the requested job-owned sketch entity references, select only those entities, apply the SolidWorks relation, then clear selection. A failed relation must return a structured error and leave no later command executing.

- [ ] **Step 5: Write and implement `AddDimension` for initial linear/radial cases**

Support explicit `Horizontal`, `Vertical`, `Linear`, `Diameter`, and `Radius` dimension types needed for ordinary sketches. The request contains entity references, value in mm, and annotation placement coordinates. After creation, set the dimension system value using metres/radians as appropriate and return the resulting dimension reference/name.

- [ ] **Step 6: Implement `GetSketchStatus`**

Return whether a sketch is active and, for the target sketch, whether SolidWorks reports it under-defined, fully defined, over-defined, or invalid where the 2018 API exposes that state. Do not infer this from screen colour.

- [ ] **Step 7: Run tests and commit**

```bash
git add src tests/SolidWorksCadAgent.IntegrationTests
git commit -m "feat: add sketch relations dimensions and status"
```

---

### Task 2: Fillet, Chamfer, Document Close, and Dimension Inspection

**Files:**
- Modify: `src/SolidWorksCadAgent.Contracts/Cad/CadCommandNames.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/FeatureCommandHandlers.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Commands/DocumentCommandHandlers.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Inspection/SolidWorksInspector.cs`
- Create: `tests/SolidWorksCadAgent.IntegrationTests/FeatureFinishingCommandTests.cs`

**Interfaces:**
- Produces commands: `Fillet`, `Chamfer`, `CloseDocument`, `GetDimensions`.

- [ ] **Step 1: Define stable selection references for finishing features**

The command contract must identify edges/faces through bridge-issued references captured from inspection, not raw screen coordinates. A reference that no longer resolves returns `STALE_ENTITY_REFERENCE`.

- [ ] **Step 2: Write failing fillet/chamfer integration tests**

Create a known block, obtain explicit edge references, apply a 2 mm fillet in one test and a 1 mm × 45° chamfer in another, rebuild, and assert each feature exists with no rebuild error.

- [ ] **Step 3: Implement `Fillet`**

Use the SolidWorks 2018 FeatureManager fillet API corresponding to the locally installed interop signature. Only constant-radius edge fillets are supported in this V1 command. Convert radius mm to metres at the call boundary.

- [ ] **Step 4: Implement `Chamfer`**

Support distance-angle chamfer with distance in mm and angle in degrees in the command contract; convert to metres/radians for the API. Reject unsupported chamfer modes rather than guessing.

- [ ] **Step 5: Implement `GetDimensions`**

Walk native feature/sketch dimensions and return structured entries containing SolidWorks name/reference, owning feature/sketch, system value converted into user-facing mm where it is a length, and raw type metadata. Do not label angular values as mm.

- [ ] **Step 6: Implement job-owned `CloseDocument`**

Track the document title/path opened or created for the job. Refuse to close any active document whose identity does not match the job-owned document. Add a test with an unrelated second document open and assert it remains open.

- [ ] **Step 7: Run tests and commit**

```bash
git add src tests/SolidWorksCadAgent.IntegrationTests
git commit -m "feat: add finishing and document inspection commands"
```

---

### Task 3: CaptureView and Command-Family Regression Gate

**Files:**
- Modify: `src/SolidWorksCadAgent.Contracts/Cad/CadCommandNames.cs`
- Modify: `src/SolidWorksCadAgent.SolidWorksBridge/Inspection/SolidWorksInspector.cs`
- Create: `tests/SolidWorksCadAgent.IntegrationTests/CaptureViewTests.cs`
- Modify: `tests/fixtures/ai/acceptance-plate.json`
- Modify: `README.md`

**Interfaces:**
- Produces command: `CaptureView`.

- [ ] **Step 1: Write capture path-safety tests**

Requests for `..\capture.bmp`, an absolute path outside the workspace, or an existing file without overwrite approval must fail before calling SolidWorks.

- [ ] **Step 2: Implement deterministic view selection**

Support initial named views `Isometric`, `Front`, `Top`, `Right`. Use SolidWorks `ShowNamedView2`/zoom-to-fit operations on the STA dispatcher before capture.

- [ ] **Step 3: Implement `CaptureView` using SolidWorks 2018 `ModelDoc2.SaveBMP`**

Save the current view to a workspace-resolved `.bmp` path. `SaveBMP(path, 0, 0)` uses the current window size; return the resolved path and file size. Require the generated file to exist and be non-empty before reporting success.

- [ ] **Step 4: Add a full command-registry inventory test**

Assert that the registry now contains every initial command family named in the approved spec:

```text
LaunchSolidWorks, AttachSolidWorks, NewPart, OpenPart, SavePart, Rebuild,
CloseDocument, CreateSketch, AddLine, AddRectangle, AddCircle, AddArc,
AddDimension, AddRelation, ExitSketch, Extrude, CutExtrude, Fillet, Chamfer,
GetFeatureTree, GetSketchStatus, GetBoundingBox, GetDimensions, GetBodyCount,
GetRebuildErrors, CaptureView
```

The test fails if any is absent or duplicate.

- [ ] **Step 5: Run all unit and SolidWorks integration tests**

Ensure the original acceptance plate still passes after the registry expansion.

- [ ] **Step 6: Commit and return to the main plan release gate**

```bash
git add src tests README.md
git commit -m "feat: complete initial V1 CAD command set"
```
