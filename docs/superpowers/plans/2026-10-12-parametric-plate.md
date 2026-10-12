# Parametric Plate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create an approved, fully constrained native rectangular plate with named driving width, length and thickness, independently verified before save and after managed reopen.

**Architecture:** Extend the existing V2 pipeline using AddRectangle operation version 2 and Extrude operation version 3. Reuse managed sketch/model ownership, journalled sequential dispatch and staged-save provenance; add focused parameter capture, resolution and verification modules. Existing rectangle version 1 and Extrude versions 1/2 keep their meanings.

**Tech Stack:** C#, .NET Framework 4.8, MSTest, Newtonsoft.Json, System.Data.SQLite, dedicated-STA SOLIDWORKS 2020 interop; existing Windows bundle tooling.

**Spec:** `docs/superpowers/specs/2026-10-12-parametric-plate-design.md`

**Status:** Ready for user plan review; no product implementation started. Recommended execution: native inline, followed by one independent whole-branch review.

## Global Constraints

- Baseline Windows commit: `bb54d750f3a97d36028b068c00eaef433f586117`.
- Create a new, agent-owned rectangular plate on Top Plane, centered at the sketch origin, with positive finite width, length and thickness specified in millimetres.
- The initial feature is one positive blind boss, one solid body and a final new managed .sldprt path.
- Unsupported additional features or non-Top frames receive a clear preflight rejection for this capability.
- Existing supported V1/V2 workflows retain their current behavior and remain usable independently.
- No live COM objects are persisted. Simulation must not advertise native solver evidence.
- Uncertain native mutation follows the existing fail-stop/no-blind-replay policy.
- Preserve unrelated documents and the retained healthy SOLIDWORKS session.
- Native tests use deterministic planners and unique owned fixtures, without cloud provider calls or production models.
- Physical-phone testing stays deferred. No production existing-model edits, Android feature changes, release publication or installed-service changes belong to this increment.

## Review Focus

1. Small/nonrepresentable dimensions or overflow: fail before native mutation; Task 1 pins this.
2. Incidental inference, reversed endpoints or redundant relations: require a genuinely dimension-driven, fully defined contour; Task 2 pins asymmetric geometry and change/regenerate behaviour.
3. A captured dimension belonging to another sketch/model/configuration: fail closed; Tasks 3/4 pin ownership and wrong-role rejection.
4. Cancellation or database failure after native success: report uncertainty and never replay; Task 3 pins atomic outcome failure.
5. Missing/driven/duplicated parameters after reopen, despite correct volume: withhold completion and artifact delivery; Tasks 4/5 pin this.

## Working setup and verification commands

At execution, read the approved spec, shared project guide from the original checkout and `REVIEW-HANDOVER.md`. Verify live Git state and processes. Use the using-git-worktrees skill to create isolated branch `feature/parametric-plate` from the reviewed documentation commit based on the exact baseline, preferably a new worktree in this writable task workspace. Do not carry or stage the historical inaccessible `scripts/test-windows-bundle-validator.ps1` deletion. Preserve both originals and RC1.

Run commands from the implementation checkout, outside the restricted sandbox when necessary. Serialize Windows builds/tests to avoid locked test-owned binaries.

Unit command, replacing FILTER and NAME with the concrete values named below:

```powershell
dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj --no-restore -c Release -m:1 -p:SolidWorksInteropAvailable=true --filter FILTER --logger 'trx;LogFileName=NAME.trx' --results-directory artifacts/parametric-plate
```

Restore first if the isolated checkout has no assets, using installed Visual Studio MSBuild and the established package cache. Compile-only full suite uses `-p:SolidWorksInteropAvailable=false`; native unit mode uses `true`. Native integration command:

```powershell
$env:SOLIDWORKS_EXPECTED_YEAR = '2020'
$env:SOLIDWORKS_RUN_REFERENCE_TESTS = '1'
dotnet test tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj --no-restore -c Release -m:1 -p:SolidWorksInteropAvailable=true --filter 'FullyQualifiedName~ParametricPlateAcceptanceTests' --logger 'trx;LogFileName=parametric-native.trx' --results-directory artifacts/parametric-plate
```

A missing method/type compile failure is acceptable only as the first red step for a new interface. Runtime regressions must fail for their intended assertion. A native result with skips or inconclusive tests does not meet acceptance. If the native constraint recipe cannot pass, stop that increment and report the exact evidence; do not substitute fixed geometry or enable the capability.

## File boundaries and contracts

Create focused modules under the existing namespaces:

- Contracts `Cad/CadParametricPlateContracts.cs`: versioned intent, capture and observation DTOs; no interop types.
- Core `Commands/CadParametricPlateContract.cs`: operation-version pair and six-step scope validation, explicit expectations and representability checks.
- Core `References/CadParametricIdentity.cs`: trusted semantic role identity, shared by Host/Bridge.
- Bridge `Commands/ParametricRectangleCommandHandler.cs`, `Commands/ParametricExtrudeCommandHandler.cs`: respective registered versioned operations.
- Bridge `Parametric/ParametricRectangleBuilder.cs`: native geometry/relations/dimensions in one STA call.
- Bridge `Parametric/ParametricPlateInspector.cs`: independent native readback and captured-owner resolution.
- Host `Persistence/SqliteJobRepository.Parametric.cs`: transactional binding/evidence commit alongside the existing mutation outcome.
- Host `Jobs/CadParametricPlateVerifier.cs`: pure expected-versus-observed checks and read-only native inspection requests.
- Host `Planning/CadParametricPlatePrompt.cs`: explicit deterministic intake through existing CAD job input.

New DTOs:

`CadParametricPlateIntent` has `WidthMm`, `LengthMm`, `ThicknessMm` (double), `SavePath` (string). `CadParametricRoleCapture` has `EntityId` (Guid), `Role` (string), `OwnerKind` (string), `OwnerReferenceBytes` (byte[]), `NativeDimensionFullName` (string, dimensions only). `CadParametricPlateObservation` has `EvidenceVersion` (int, exactly 1), `ModelId`, `SketchEntityId` (Guid), `ConfigurationKey` (string), `FullyDefined`, `RequiredRelationsPresent`, `HasFixedContourEntities` (bool), `ContourEdgeCount`, `ConstructionEdgeCount`, `SolidBodyCount`, `RebuildErrorCount` (int), `Roles` (list of captures), `Dimensions` (list of `CadParametricDimensionObservation`), and `Geometry` (JObject in the existing native bounds/volume shape). `CadParametricDimensionObservation` has `EntityId`, `Role`, `OwnerEntityId` (Guids except Role), `ValueMm` (double), `IsDriving` (bool).

Names are case-sensitive: `width`, `length`, `thickness`; edge roles `bottom`, `right`, `top`, `left`, construction role `centreDiagonal`, boss role `boss`. Width follows local sketch X, length local sketch Y; Top Plane geometry expectations use `CadStandardPlaneFrame`, not a guessed global-axis mapping. Native dimension names are assigned/captured as `AgentWidth`, `AgentLength`, `AgentThickness` and their full native names are captured from returned objects, never invented from D1/Sketch1 conventions.

Persistent bindings use schema 7's existing `EntityReferenceBindings`. For dimension roles, store the owning feature's actual persistent reference and the captured dimension full name in fingerprint format 1, not an assumed persistent reference to IDimension itself. Fingerprint includes role, model/sketch identity, configuration and owning entity identity. Exact owner resolution followed by unique named-dimension readback is mandatory. No new table/column or schema version is needed. Old readers reject the new operation versions rather than interpreting them as legacy geometry.

### Task 1: Pin the parametric contract and compatibility

**Files:** Create Contracts DTOs, `CadParametricPlateContract.cs`, `CadParametricIdentity.cs`, `tests/SolidWorksCadAgent.UnitTests/CadParametricPlateContractTests.cs`; modify `CadPlanV2Preflight.cs`, `CadV2PrismaticGeometry.cs`, `CadPlanDocumentReaderTests.cs`. All Core paths are under `src/SolidWorksCadAgent.Core`; Contracts paths under `src/SolidWorksCadAgent.Contracts`.

**Interfaces:** `CadParametricPlateContract.IsParametric(CadPlanV2Document plan): bool`; `Validate(CadPlanV2Document plan): IReadOnlyList<string>`; `ReadIntent(CadPlanV2Document plan): CadParametricPlateIntent`; `AllowsOperationVersion(string command, int version): bool` (existing catalog versions plus the two new versions; whole-plan validation separately rejects mixed pairs); `CadParametricIdentity.For(Guid modelId, Guid sketchEntityId, string role): Guid`.

- [ ] Write `ValidPlatePreservesVersionPairsAndExpectedGeometry`, `LegacyRectangleAndBossKeepTheirSemantics`, `RejectsPartialMixedAndForeignPlanePlans`, `RejectsNonfiniteUnrepresentableAndOverflowingSizes`, `RoleIdentityRejectsEmptyOrUnknownInputs`. Assertions: new rectangle v2 plus boss v3 accepted only together; six steps are NewPart/CreateSketch/AddRectangle/ExitSketch/Extrude/SavePart, Top Plane and zero centre, explicit non-overwriting save. Legacy rectangle v1/boss v2 still accepted. Front, Right, extra cuts, mixed versions, caller-supplied semantic IDs and unexpected fields rejected. A 120 x 60 x 10 plate has volume 72000 mm3 and Top-frame bounds from the existing frame contract. Values 0, -1, NaN, Infinity, conversion underflow and overflowing area/volume fail.

  Representative assertions after arranging that explicit six-step normalized fixture:
  ```csharp
  Assert.AreEqual(0, CadParametricPlateContract.Validate(plan).Count);
  var intent = CadParametricPlateContract.ReadIntent(plan);
  Assert.AreEqual(120.0, intent.WidthMm);
  Assert.AreEqual(60.0, intent.LengthMm);
  Assert.AreEqual(10.0, intent.ThicknessMm);
  Assert.AreEqual(72000.0, CadV2PrismaticGeometry.Analyze(plan).VolumeMm3);
  ```
- [ ] Run FILTER `FullyQualifiedName~CadParametricPlateContractTests`, NAME `contract-red`; confirm intended red.
- [ ] Implement exact interfaces and DTOs. Keep `CadOperationCatalog`'s legacy meanings; add an explicit parametric operation-version resolver for preflight. Rectangle v2 retains existing centre/width/height parameter names; height corresponds to semantic length. Boss v3 retains depthMm, mapped to thickness. Derive role GUIDs from SHA256 of versioned binary encoding of model GUID, sketch GUID and ordinal role; reject unknown roles. Teach geometry analysis to accept the exact parametric pair without relaxing other prismatic scope. Reject dimensions too small for existing profile tolerances or not representable in native metres with a clear error, rather than silently clamping.
- [ ] Run focused tests green, reader/geometry regressions and complete unit suite; commit `feat: define versioned parametric plate contract`.

### Task 2: Prove native constraints and driving dimensions before Host admission

**Files:** Create Bridge builder and two handler modules above; modify `src/SolidWorksCadAgent.SolidWorksBridge/SolidWorksBridgeFacade.cs`; create `tests/SolidWorksCadAgent.IntegrationTests/ParametricPlateNativeRecipeTests.cs` and unit `ParametricBridgeContractTests.cs`.

**Interfaces:** `ParametricRectangleBuilder.Build(object model, Guid modelId, Guid sketchEntityId, double widthMm, double lengthMm): CadParametricPlateObservation` (STA-only); versioned handlers implement `IVersionedCadCommandHandler` with AddRectangle v2 / Extrude v3. Return captures in `CadCommandResult.Data.parametricCapture` with evidence version 1; no persistence occurs in these handlers. Rectangle capture is the partial sketch observation with width/length; complete three-parameter/body/volume validation occurs after the boss, never on this partial result.

- [ ] Write native tests `AsymmetricRectangleIsFullyDefinedAndWidthDriven`, `ThicknessParameterChangesOnlyDepth`, `CreationRestoresSelectionAndPreferencesOnFailure`. On an owned 120 x 60 x 10 plate: four nonconstruction contour edges, one construction diagonal, fully defined native sketch, three unique driving values 120/60/10, one body, clean rebuild. Changing width to 150 on the test-owned fixture keeps centre, length 60 and thickness 10, and gives volume 90000; changing thickness to 12 gives volume 108000. Tests inspect independent native bounds, not only dimension values. Unit tests require native/versioned context and reject simulation/no-interop dispatch.

  ```csharp
  Assert.IsTrue(observed.FullyDefined);
  Assert.IsTrue(observed.RequiredRelationsPresent);
  Assert.IsFalse(observed.HasFixedContourEntities);
  Assert.AreEqual(4, observed.ContourEdgeCount);
  Assert.AreEqual(1, observed.ConstructionEdgeCount);
  Assert.IsTrue(observed.Dimensions.All(d => d.IsDriving));
  ```
- [ ] Run native recipe tests red before implementation, saving TRX/logs. Do not invoke cloud planning or use a user document.
- [ ] Build four lines explicitly with inference suppressed, capture returned segments/points, add required horizontal/vertical and corner coincidence relations once, add one construction diagonal between opposite corners with midpoint-to-origin placement, then width/length driving dimensions. Use native relation readback to avoid duplicate relations; never apply Fix to the contour. Add dimensions to selected captured objects and set/read native metre values. Temporarily suppress input-on-dimension dialogs only within try/finally restoring the exact previous preference and selection/manager state. Extrude via the existing managed-sketch selection scope; capture and name the actual returned depth dimension on the actual boss feature. Fail on unsupported/unsolved/driven-only results. Register versioned handlers internally but leave production prompt/capability admission disabled.
- [ ] Run recipe tests green, native legacy rectangle smoke, focused bridge units and full units. Commit `feat: create native dimension-driven plate geometry`. Stop if native acceptance fails; no later task may mask this gate.

### Task 3: Commit parameter ownership and outcomes atomically

**Files:** Create `SqliteJobRepository.Parametric.cs`; modify `SqliteJobRepository.V2Sequential.cs`, Host `Planning/CadPlanV2ExecutionReadiness.cs`, `Planning/CadPlanV2ExecutionBinder.cs`, `Jobs/CadV2SequentialDispatchBoundary.cs`; add `ParametricPersistenceTests.cs`, extend `V2SequentialPersistenceTests.cs`.

**Interfaces:** `SqliteJobRepository.CommitV2ParametricOutcomeAsync(Guid jobId, Guid revisionId, string stepKey, Guid modelId, CadCommandResult result, CancellationToken token): Task`. It consumes the pending journal attempt, normalized plan and version-1 capture DTO; existing `IModelReferenceStore.GetEntityBindingAsync` remains the retrieval interface.

- [ ] Write `CapturesBelongToExactApprovedModelAndSketch`, `OutcomeAndRoleBindingsCommitTogether`, `FailedBindingCommitLeavesAttemptUncertainAndCannotReplay`, `RejectsDuplicateForeignOrUnexpectedRoleCaptures`, `SchemaSevenAndHistoricalPlansRemainUnchanged`. Use real SQLite fixtures. Inject a constraint failure during binding insertion and assert no partial Active bindings/outcome success. Wrong model/revision/configuration, empty bytes, foreign role IDs or missing required width/length/thickness capture fail closed.
- [ ] Run FILTER `FullyQualifiedName~ParametricPersistenceTests`, NAME `persistence-red`; verify intended failure.
- [ ] Extend readiness and binder for the exact version pair; use the Host's managed model/sketch and role identity helper, never input-supplied IDs. Persist captures and current journal outcome in one transaction, following existing sequential outcome guards and prospective model revision. Rectangle captures include width/length and its geometry roles; boss captures include boss/thickness. Fingerprints use typed version-1 data; no schema change. Commit only after validated native result/capture matches the plan. On post-dispatch error retain the existing uncertainty/no-replay handling and dispatch fence. Keep native references out of public review DTOs unless needed by existing evidence consumers.
- [ ] Run focused persistence, identity/attempt/sequence regressions and complete unit suite. Commit `feat: persist parametric plate role ownership atomically`.

### Task 4: Inspect native parameters independently and gate saved artifacts

**Files:** Create Bridge inspector and Host verifier modules; modify `SolidWorksBridgeFacade.cs`, `src/SolidWorksCadAgent.Contracts/Cad/CadCommandNames.cs`, Bridge `Commands/InspectionCommandHandlers.cs`, Host `Jobs/CadV2RectangleBossVerifier.cs`, `Jobs/CadV2SavedVerificationRecorder.cs`, `Jobs/CadV2ArtifactAuthorization.cs`; add `CadParametricPlateVerifierTests.cs` and `ParametricPlateReferenceTests.cs` (integration).

**Interfaces:** `ParametricPlateInspector.Inspect(object model, CadModelIdentityRecord identity, Guid sketchEntityId, IReadOnlyList<CadEntityReferenceBinding> bindings): CadParametricPlateObservation` (STA-only); `CadParametricPlateVerifier.Check(CadParametricPlateIntent intent, CadParametricPlateObservation observed): IReadOnlyList<string>`; `VerifyAsync(Guid jobId, Guid revisionId, Guid modelId, CancellationToken token): Task<CadV2RectangleBossVerification>`. Provide a read-only Bridge inspection route through existing executor inspection registration, named `InspectParametricPlate`, carrying only trusted resolved model/sketch context.

- [ ] Write unit cases `CorrectGeometryWithoutRequiredParametersFails`, `DrivenDuplicateWrongOwnerOrWrongValueFails`, `UnderdefinedOrFixedContourFails`, `ConstructionContourOrRebuildErrorFails`, `AllThreeParametersAndGeometryRequired`. Assert missing thickness, duplicate width, width owned by another feature, incorrect configuration, 60 substituted for 120, NaN, underdefined sketch and absent intended relations fail even with correct reported volume. Native references tests resolve owned parameters after save/reopen and reject deleted owner or copied-model collision.

  ```csharp
  Assert.AreEqual(0, CadParametricPlateVerifier.Check(intent, validObserved).Count);
  validObserved.Dimensions.RemoveAll(d => d.Role == "thickness");
  Assert.IsTrue(CadParametricPlateVerifier.Check(intent, validObserved).Count > 0);
  ```
- [ ] Run verifier tests red; run native reference negative tests against owned fixtures and record red failures.
- [ ] Resolve actual persistent owner objects through shared reference rules, locate exactly one captured named dimension on each owner, inspect native driving state, values, intended relations and sketch constrained status. Do not trust command result echoes or store values as current readback. Use parameter tolerance 0.001 mm, existing physical geometry tolerance 0.02 mm and existing volume policy; check finite values before comparisons. Delegate existing bounds/volume/body/rebuild/segment checks to current geometry verifier. Add `V2SavedParametricPlate` evidence for saved parametric jobs bound to plan hash, model/revision, file hash and observation version 1. Require both existing saved geometry provenance and this parameter evidence for parametric artifact authorization. Verify before save and again after exact managed reopen, without releasing native ownership between inspection and save.
- [ ] Run pure tests, native save/reopen reference tests and full unit suite. Commit `feat: verify native plate parameters before completion`.

### Task 5: Expose explicit parametric intake through the approved workflow

**Files:** Create `CadParametricPlatePrompt.cs`; modify Host `Jobs/JobCoordinator.cs`, `Host/AgentRoutes.cs` health metadata and existing operation/readiness metadata where needed; add `ParametricPlateCoordinatorTests.cs`; extend `AgentRoutesTests.cs`, `CadPlanDocumentReaderTests.cs`, `CadV2ArtifactAuthorizationTests.cs`.

**Interfaces:** `CadParametricPlatePrompt.IsRequest(string prompt): bool`; `BuildCandidate(string prompt): string`. Exact intake: `V2 parametric plate: 120 x 60 x 10 mm; save as parametric/plate.sldprt`. Build the six-step candidate using Task 1's version pair, then existing Host normalization. Scope signal `parametricPlateDimensions` is true only for the verified native-capable execution path; simulation reports false.

- [ ] Write `ExplicitIntakeShowsNamedDimensionsAndRequiresApproval`, `UnsupportedScopeFailsBeforeAnyMutation`, `TerminalSubmissionRecoveryDoesNotRecreateParametricPart`, `MissingSavedParameterEvidenceWithholdsArtifact`, `LegacyPromptsAndImagesRetainCurrentFlow`. Assert parsed width/length/thickness 120/60/10 and exact versions, current-revision approval and immutable plan hash; malformed/missing sizes, absolute/traversal/overwrite paths fail before execution. Normal V2 rectangle, photo intake and exact submission UUID recovery retain their regression behavior. No cloud call for the explicit syntax.
- [ ] Run FILTER `FullyQualifiedName~ParametricPlateCoordinatorTests`, NAME `intake-red` plus authorization red cases; verify intended failures.
- [ ] Wire deterministic intake into existing planning/normalization, approval and execution gates, preserving submission hash and planner paths. Publish reviewed parameter labels in the existing plan summary and verified job evidence; no separate Android UI. Fail partial/new version combinations instead of falling back to legacy. Advertise capability only after Tasks 2/4 native gates passed. Do not expose generalized constraints/edits or enable arbitrary legacy provider output as parametric intent.
- [ ] Run route, reader, coordinator, artifact and submission regressions, then full unit suite. Commit `feat: expose approved parametric plate workflow`.

### Task 6: Complete native acceptance, review and packaging

**Files:** Create `tests/SolidWorksCadAgent.IntegrationTests/ParametricPlateAcceptanceTests.cs`; update `README.md`, `docs/CAD_CAPABILITY_MATRIX.md`, `docs/CAD_IMPLEMENTATION_ROADMAP.md`, `docs/CAD_IMPLEMENTATION_STATUS.md`; create `docs/parametric-plate-verification-2026-10-12.md` (use actual verification date if later). Store logs/TRX and native artifact manifest under ignored `artifacts/parametric-plate`.

**Interfaces:** Real Host coordinator and native Bridge from Tasks 1–5; same owned-fixture lifecycle and unrelated-document restoration as `V2PrismaticAcceptanceTests`.

- [ ] Write `ApprovedAsymmetricPlateHasDrivingParametersAfterReopen`, `TestOwnedWidthChangeRegeneratesCentredPlate`, `TestOwnedThicknessChangePreservesSketch`, `MissingOrChangedNativeParameterBlocksFinalization`. Positive fixture input is 120/60/10, volume 72000; width change 150 yields 90000; thickness change 12 on original-width fixture yields 86400. Changes occur only in separate test-owned copies after reopen and do not alter a production completed job's published file. The negative case must preserve native geometry while removing/changing required parameter evidence and prove Host completion/artifact access fails.
- [ ] Run acceptance red for newly uncovered paths before corresponding corrections; apply only changes justified by failures. Run complete Release solution build (zero errors), full units both interop modes (all pass, no skips), complete new native suite and all nine existing `V2PrismaticAcceptanceTests`. Retain exact owned model paths, job DBs, source IDs and SHA256. Do not rerun cold startup if the current retained SOLIDWORKS session is healthy; do not terminate it.
- [ ] Perform one independent whole-branch review after inline implementation, concentrating on Review Focus, specification compliance, identity/version compatibility and truthful native completion. Reproduce and fix material findings with tests. Keep any unresolved limitation explicit; a failed solver/capture gate prevents capability publication.
- [ ] Build and validate Windows native bundle using `scripts/build-windows-test-bundle.ps1`, `validate-windows-test-bundle.ps1`, `test-bundle-capability.ps1` and `test-bundle-file-locks.ps1`. Document exact evidence, deferred device/cloud/installation acceptance and unsupported editing/multiboss/finishing variants. Commit `docs: record verified parametric plate capability`, push branch to `github` remote and create a draft PR against `feature/v2-face-sketches-and-finishing`; wait for applicable CI. Update local handover. No automatic merge or release in this feature increment.

## Native API basis and uncertainty gate

Read the installed SOLIDWORKS 2020 interop signatures before implementation. Metadata inspection confirms `ISketch.GetConstrainedStatus()`, `ISketchRelationManager.AddRelation(object,int)`, `GetRelations(int)`, `IDimension.Name/FullName/DrivenState`, `SetSystemValue3(double,int,object)` and `GetSystemValue3(int,object)`. It does not prove solver behaviour or persistent dimension-owner resolution; Tasks 2 and 4 provide that evidence.

Official [AddDimension2 documentation](https://help.solidworks.com/2020/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDoc2~AddDimension2.html) describes the dimension-input preference. Use the installed API and official Help to check the required relation and solve-state enum values; do not use guessed integer constants or broaden support from compilation alone. Preserve transient preference state even when native creation throws.

## Self-review result

Spec coverage maps to Tasks 1–6: versioned intent and scope (1), native driving construction (2), trusted persistence/no-replay (3), pre/post-save verification and artifact gating (4), reviewed entry point (5), native regression and handover/PR evidence (6). Each Review Focus item has named tests. Schema remains 7 through versioned existing-binding data; legacy plans retain semantics. Production edits, phone testing and installation remain excluded. Exact native solver success is deliberately an early execution gate, not a promise inferred from metadata.
