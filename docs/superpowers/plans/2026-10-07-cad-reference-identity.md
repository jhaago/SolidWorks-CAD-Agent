# CAD Reference Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task by task. The user approved the recommended direction and inline execution on 2026-10-07.

**Goal:** Give newly Agent-created part documents a durable model ID and a logical sketch ID backed by a SOLIDWORKS persistent reference that resolves after save, close, reopen, sketch rename and rebuild.

**Architecture:** Keep the existing Host → command executor → SolidWorksBridge → STA path. Core defines identity records and a narrow `IModelReferenceStore`; the Host’s SQLite repository owns durable records and generates IDs; Bridge alone reads custom properties and captures/resolves opaque native reference bytes. Historical command JSON remains unchanged, and the planner does not receive native tokens.

**Tech Stack:** C# / .NET Framework 4.8, Newtonsoft.Json, SQLite (`System.Data.SQLite`), MSTest, SOLIDWORKS 2020 SP0.0 interop.

**Spec:** `docs/superpowers/specs/2026-10-07-cad-reference-identity-design.md`

## Global Constraints

- “`NewPart` creates an Agent-managed document and assigns a fresh model ID before any sketch or feature is created.”
- “An existing user document is read without adding Agent metadata. Before future mutation, the user-approved workflow creates an owned working copy inside the configured CAD workspace and assigns that copy a fresh model ID. The source remains untouched.”
- “The token stays opaque to Core, the planner, HTTP clients and ordinary job history.”
- “A manual file copy duplicates the custom property. If two artifacts claim one model ID, both are treated as an identity collision.”
- “Do not add native tokens or trusted identity fields to historical plan JSON, and do not reinterpret a missing reference as ‘current selection.’”
- “Migration is additive: retain the existing `CadPlanningResult`/`CadCommandEnvelope` deserializer for historical rows, introduce new model/reference tables and a versioned plan document, and migrate SQLite from schema v4 transactionally with rollback on failure.”
- “Native acceptance uses only a newly created owned part and an isolated output path.”
- Keep COM calls on the existing SOLIDWORKS STA path; do not add reference tokens to planner payloads, HTTP responses, command-history JSON or logs.
- Preserve the existing uncommitted 1g-b/1g-c/1g-d work. Current source HEAD at planning time is `a6fefb8` plus that worktree state.

## Review Focus

- A v4 database with historical `PlanJson` must migrate without rewriting or losing jobs, revisions or approvals. Pin this in Task 2’s v4 migration test.
- A copied file with the same custom ModelId must not bind to the source record while both files exist. Pin this in Task 3’s duplicate-ID Host/Bridge test.
- A missing, deleted, suppressed or malformed native token must fail before an operation mutates the model. Pin missing/malformed in Task 2/4 domain tests and deleted/suppressed in Task 5’s opt-in native fixture.
- A document with a same-named sketch must not satisfy another document’s logical reference. Pin this in Task 5’s wrong-document and duplicate-name native cases.
- An existing user document without Agent metadata must remain untouched and keep legacy command behavior. Pin this in Task 3’s Bridge compatibility test and Task 5’s original-document save-flag assertion.

## File Map

- `src/SolidWorksCadAgent.Contracts/Cad/CadModelReferenceContracts.cs` — serialized persistence DTOs and identity/status enums; no COM types.
- `src/SolidWorksCadAgent.Contracts/Cad/CadCommandEnvelope.cs` — trusted execution-only model/output IDs marked `JsonIgnore`; the serialized plan envelope stays byte-compatible.
- `src/SolidWorksCadAgent.Core/References/IModelReferenceStore.cs` — async storage port consumed by Host and Bridge.
- `src/SolidWorksCadAgent.AgentHost/Persistence/Schema.sql` — additive `ManagedModels` and `EntityReferenceBindings` tables.
- `src/SolidWorksCadAgent.AgentHost/Persistence/SqliteJobRepository.cs` — transactional v4→v5 migration and store methods.
- `src/SolidWorksCadAgent.AgentHost/Jobs/JobCoordinator.cs` — generate IDs and create pending identity records only for approved real-mode `NewPart` and `CreateSketch` execution; attach them as execution-only envelope context.
- `src/SolidWorksCadAgent.AgentHost/Program.cs` — inject the repository/reference store into the real Bridge composition.
- `src/SolidWorksCadAgent.SolidWorksBridge/Session/SolidWorksDocumentContext.cs` — bind model identity to the already-guarded COM document context.
- `src/SolidWorksCadAgent.SolidWorksBridge/Commands/DocumentCommandHandlers.cs` and `FileCommandHandlers.cs` — assign/read/verify the custom ModelId property for new and managed-opened documents; preserve unmanaged legacy paths.
- `src/SolidWorksCadAgent.SolidWorksBridge/Commands/SketchCommandHandlers.cs` — capture the created sketch’s opaque persistent reference and return only the logical ID.
- `src/SolidWorksCadAgent.SolidWorksBridge/References/SolidWorksReferenceResolver.cs` — Bridge-only capture and resolution with native type/status checks.
- `tests/SolidWorksCadAgent.UnitTests/SqliteJobRepositoryTests.cs` and new `ModelReferenceStoreTests.cs` — migration, round-trip, collision, malformed/stale-reference and legacy-JSON coverage.
- `tests/SolidWorksCadAgent.IntegrationTests/ModelReferenceCapabilityTests.cs` — opt-in native acceptance using a unique owned workspace, SOLIDWORKS STA and original-document preservation pattern from `PrismaticCapabilityTests`.
- `docs/CAD_CAPABILITY_MATRIX.md`, `docs/CAD_IMPLEMENTATION_STATUS.md`, `docs/CAD_IMPLEMENTATION_ROADMAP.md` — record only verified slice scope and evidence after implementation.

## Interfaces

Add `CadModelIdentityRecord` with `ModelId`, `ParentModelId`, `DocumentKind`, `Status`, `CustomPropertyKey`, `CanonicalPath`, `LastSavedSha256`, `CurrentModelRevisionId`, `ConfigurationKey`, `SolidWorksRevision`, `RegistryVersion`, `CreatedUtc` and `UpdatedUtc`. Model statuses are `Pending`, `ActiveUnsaved`, `ActiveSaved`, `IdentityCollision`, `NeedsRegistration` and `Uncertain`. Generate an initial `CurrentModelRevisionId` when the pending model row is created and advance it after each successful managed mutation; the sketch binding records the post-create revision.

Add `CadEntityReferenceBinding` with `ModelId`, `EntityId`, `EntityKind`, `ConfigurationKey`, `NativeObjectKind`, `ReferenceFormatVersion`, `NativeReferenceBytes`, `CreatedAtModelRevisionId`, `LastResolvedModelRevisionId`, `SemanticFingerprintJson`, `Status` and timestamps. The persisted status values are `Pending`, `Active`, `Suppressed`, `Deleted`, `Stale`, `Ambiguous`, `Unresolved` and `Uncertain`.

Define `IModelReferenceStore` in Core with these async methods and cancellation parameters:

```csharp
Task RegisterModelAsync(CadModelIdentityRecord model, CancellationToken token);
Task<CadModelIdentityRecord> GetModelAsync(Guid modelId, CancellationToken token);
Task<CadModelIdentityRecord> FindModelByCanonicalPathAsync(string canonicalPath, CancellationToken token);
Task UpdateModelAsync(CadModelIdentityRecord model, CancellationToken token);
Task AddEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token);
Task<CadEntityReferenceBinding> GetEntityBindingAsync(Guid modelId, Guid entityId, string configurationKey, CancellationToken token);
Task UpdateEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token);
```

Model IDs and entity IDs are Host-generated. Extend `CadCommandEnvelope` with `[JsonIgnore] Guid? ManagedModelId` and `[JsonIgnore] Guid? OutputEntityId` for dispatch-only context. These values are assigned after approved plan loading and never appear in planner input, persisted plan JSON or client-supplied parameters. The Host persists a pending row before native mutation. Bridge success stores the token outside the STA callback and returns only the logical ID; failures leave the row `Uncertain` or `Unresolved` and stop execution.

Use the document custom property key `SolidWorksCadAgent.ModelId`, text type, with SOLIDWORKS 2020 `ICustomPropertyManager.Add3` using `swCustomPropertyOnlyIfNew` and `Get6`. Never overwrite an existing property on `NewPart`; verify the stored value equals the Host-provided GUID. On `OpenPart`, an unmanaged document without the property stays on the legacy path. A managed ID whose stored canonical path still exists at another path is a collision; fail before binding. A move is accepted only when the old path is absent and the file hash matches the registry. A changed hash requires explicit re-registration and is not silently adopted. [SOLIDWORKS 2020 Add3](https://help.solidworks.com/2020/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ICustomPropertyManager~Add3.html), [swCustomPropertyOnlyIfNew](https://help.solidworks.com/2020/english/api/swconst/SolidWorks.Interop.swconst~SolidWorks.Interop.swconst.swCustomPropertyAddOption_e.html?id=2.10.1.200).

Use SQLite `PRAGMA user_version = 5`. New tables have primary key `ModelId` and composite key `(ModelId, EntityId, ConfigurationKey)` respectively; store token bytes as `BLOB`, keep an index on canonical model path, and reject duplicate IDs. DDL, v1–v4 compatibility migrations and the version update execute in one transaction (connection pragmas that SQLite forbids inside a transaction remain outside it).

## Execution Plan

### Task 1: Probe SOLIDWORKS sketch-reference behavior

**Files:** Create `tests/SolidWorksCadAgent.IntegrationTests/ModelReferenceCapabilityTests.cs`; use the existing `PrismaticCapabilityTests` ownership/cleanup pattern.

- [x] **Step 1: Add an opt-in native test** named `PersistentSketchReference_ResolvesAfterSaveReopenRenameAndRebuild`. Set `SOLIDWORKS_RUN_REFERENCE_TESTS=1` as the opt-in and require `SOLIDWORKS_EXPECTED_YEAR=2020`. Create a new part, create a rectangle sketch, capture the sketch object and `GetPersistReference3` bytes inside `InvokeWithApplicationAsync`, exit the sketch, save under a unique subdirectory of `C:\SolidWorks-CAD-Agent\Workspace\capability-tests`, close only the owned document, reopen it, resolve through `GetObjectByPersistReference3`, rename the sketch feature in the test fixture, rebuild and resolve again.
- [x] **Step 2: Compile the integration project with interop enabled.** Run `dotnet build tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj --no-restore -m:1 -p:SolidWorksInteropAvailable=true -p:SolidWorksApiPath="C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"`. Expected: PASS with no compile errors.
- [x] **Step 3: Run the controlled native probe on the installed SOLIDWORKS 2020 session.** Run `$env:SOLIDWORKS_EXPECTED_YEAR='2020'; $env:SOLIDWORKS_RUN_REFERENCE_TESTS='1'; dotnet test tests/SolidWorksCadAgent.IntegrationTests/SolidWorksCadAgent.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ModelReferenceCapabilityTests"`. Expected: sketch reference resolves before and after close/reopen, rename and rebuild; the user’s prior document stays open with its original path/save flag; only the owned test file is created and cleaned up/retained as evidence.
- [x] **Step 4: Stop if the probe fails.** The final controlled probe passed; failures during fixture bring-up were test-assertion, title-tracking and file-lock issues, corrected before acceptance. The native API resolved the captured sketch reference with `swPersistReferencedObject_Ok` before and after reopen/rename/rebuild.

### Task 2: Add versioned identity contracts and SQLite registry

**Files:** Create the DTO and Core interface above; modify `Schema.sql`, `SqliteJobRepository.cs` and `SqliteJobRepositoryTests.cs`; create `ModelReferenceStoreTests.cs`.

**Interfaces:** Repository implements `IModelReferenceStore`; methods follow the exact signatures above. Binding byte arrays are cloned at repository boundaries and never returned in job/revision DTOs.

- [x] **Step 1: Write failing model/reference store tests.** Added the v4 migration-preservation/rollback, duplicate model, opaque-byte round-trip, unresolved-token retention, and configuration-scope tests.
- [x] **Step 2: Run the selected tests to verify failure.** The selected tests failed before the contracts/store implementation as expected.
- [x] **Step 3: Add DTOs and the Core port.** Added validated persistence records/statuses and `IModelReferenceStore`; no COM/planner types.
- [x] **Step 4: Add the v4→v5 migration and repository implementation.** Added both tables and transactional migration, keeping historical job/plan JSON unchanged.
- [x] **Step 5: Run store and migration tests.** Focused model-reference tests passed 6/6; SQLite/repository tests passed 15/15. Full suite was 433/434 with the established `HttpListener` platform exception.
- [x] **Step 6: Commit the tested contracts and persistence task.** Commit `10ea19b feat: add persistent CAD identity registry`.

### Task 3: Bind managed model identity to the Host and Bridge

**Files:** Modify `CadCommandEnvelope.cs`, `JobCoordinator.cs`, `Program.cs`, `DocumentCommandHandlers.cs`, `FileCommandHandlers.cs`, `SolidWorksDocumentContext.cs`, `SolidWorksBridgeFacade.cs`; add focused tests.

**Interfaces:** `CadCommandEnvelope.ManagedModelId` and `.OutputEntityId` are nullable GUIDs marked `[JsonIgnore]`. `SolidWorksBridgeFacade` accepts the `IModelReferenceStore`. The repository is passed at the existing Program composition point. Old constructor overloads remain for direct legacy/test callers without identity support; any command carrying identity execution metadata must fail explicitly if no reference store is configured.

- [x] **Step 1: Add failing legacy-compatibility and identity tests.** Added JSON golden-compatibility, real/simulation coordinator, uncertain-failure, and Bridge fail-before-STA tests. The controlled native fixture proves legacy `NewPart` remains unstamped and legacy `OpenPart` still binds an unmanaged owned document.
- [x] **Step 2: Run the selected tests to verify failure.** The first selected run failed on missing execution-only envelope fields, as expected.
- [x] **Step 3: Add execution-only envelope context and Host ID lifecycle.** Real approved NewPart now persists a pending Host ID before dispatch; each managed CreateSketch receives a pending logical output ID; save records path/hash; failures become uncertain and are not replayed.
- [x] **Step 4: Write/read the managed property in the Bridge.** Add3/Get6 stamping and path/hash/registry validation are behind the Bridge/STA boundary; old constructors remain for legacy callers and identity metadata without a store fails closed.
- [x] **Step 5: Run focused Host, Bridge and JSON regression tests.** Focused identity/store/Bridge/preflight tests passed 20/20; controlled native SOLIDWORKS 2020 tests passed 2/2; both solution build modes passed with 0 warnings/errors; full suite was 442/443 with only the pre-existing HttpListener platform limitation. Explicit `OpenPart` followed by mutation/save/rebuild is now rejected before approval until a managed-copy workflow exists.
- [ ] **Step 6: Commit the model identity lifecycle task.** Not done: `JobCoordinator.cs`, `JobCoordinatorTests.cs` and related documentation also contain pre-existing user-owned increment 1g changes. They remain uncommitted to avoid folding or rewriting that work; source revision is `10ea19b66ebd7b21fe5e14ad5b5f269ce8a1e6c9` plus the dirty tree.

### Task 4: Capture and resolve logical sketch bindings

**Files:** Modify `SketchCommandHandlers.cs` and `SolidWorksDocumentContext.cs`; create `SolidWorksCadAgent.SolidWorksBridge/References/SolidWorksReferenceResolver.cs`; add unit tests.

**Interfaces:** Resolver captures from the active sketch on the STA and returns a value object containing copied `byte[] NativeReferenceBytes`, `NativeObjectKind`, and native status. In SOLIDWORKS 2020, the resolver must accept the persistent reference result as an `IFeature` wrapper only if `GetTypeName2()` is `ProfileFeature` and `GetSpecificFeature2()` exposes `ISketch`. It resolves a `CadEntityReferenceBinding` only against the bound `ModelDoc2` and returns success/status/kind diagnostics; it does not return a COM object outside Bridge. The handler persists captured bytes through `IModelReferenceStore` only after the STA callback completes and returns `{ entityId }` to the normal command result.

- [x] **Step 1: Add failing tests for missing identity, bad token, wrong model and wrong kind.** `SolidWorksReferenceResolverTests` cover the fail-closed binding and token cases.
- [x] **Step 2: Run focused tests to verify failure.** The new tests failed for missing resolver/facade behavior before production code was added.
- [x] **Step 3: Capture the native sketch token.** Managed `CreateSketch` copies `GetPersistReference3` bytes from `ActiveSketch` on the STA.
- [x] **Step 4: Persist and return only the logical ID.** A binding becomes `Active` only after token storage; post-creation failure returns an uncertain result and stops the Host plan.
- [x] **Step 5: Resolve against the reopened bound document.** `GetObjectByPersistReference3` runs on the STA and validates native status, model/configuration and sketch kind. The positive created-sketch case passed on SOLIDWORKS 2020.
- [x] **Step 6: Run focused Core/Bridge tests and both interop build paths.** New focused tests passed 7/7; full suite passed 450/450 outside the restricted sandbox; both solution builds passed with 0 warnings/errors.
- [ ] **Step 7: Commit the sketch binding task.** Left uncommitted because shared files also contain the pre-existing user-owned increment 1g changes. Preserve them; source revision remains `10ea19b66ebd7b21fe5e14ad5b5f269ce8a1e6c9` plus the dirty tree.

### Task 5: Complete controlled native acceptance and handoff evidence

**Files:** Finish `ModelReferenceCapabilityTests.cs`; update only evidence-backed rows in `CAD_CAPABILITY_MATRIX.md`, `CAD_IMPLEMENTATION_STATUS.md` and `CAD_IMPLEMENTATION_ROADMAP.md`.

- [x] **Step 1: Extend the native fixture with the production capture/store/resolver path.** Same-named wrong-document, manual-copy collision and deleted-sketch failures are asserted. SOLIDWORKS 2020 returned native status `1` for the deleted sketch. No downstream feature consumer exists yet, so plan-level blocking remains outside this acceptance.
- [x] **Step 2: Run the opt-in test safely on SOLIDWORKS 2020.** The final controlled run passed 3/3 using isolated owned files, restored the original active document/save flag and closed test-owned documents. See `model-reference-task5-verified.trx` and the handoff for artifact hashes.
- [x] **Step 3: Run the full automated suite.** Full unit suite passed 450/450 outside the restricted sandbox; neither tests nor skips were weakened. Both solution build modes passed with zero warnings/errors.
- [x] **Step 4: Update evidence honestly.** E17 and the handoff record exact scope, native status, source revision, uncommitted state and remaining topology/selection limits.
- [x] **Step 5: Review the final diff.** `git diff --check` passed (line-ending notices only). Task 5 changed the Bridge facade, opt-in native test and related documentation; earlier dirty work was preserved. Historical plan JSON and user CAD files were untouched. COM remains inside Bridge/STA. The handoff names the remaining consumer-selection and plan-level acceptance limits.

## Verification Commands

```powershell
dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ModelReferenceStoreTests|FullyQualifiedName~ManagedModelIdentity|FullyQualifiedName~SolidWorksReferenceResolver"
dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj --no-restore --disable-build-servers -m:1 --verbosity minimal
dotnet build SolidWorksCadAgent.sln --no-restore -m:1 -p:SolidWorksInteropAvailable=false
dotnet build SolidWorksCadAgent.sln --no-restore -m:1 -p:SolidWorksInteropAvailable=true -p:SolidWorksApiPath="C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
```

Real SOLIDWORKS test is opt-in and runs only after the native probe/build steps in Tasks 1 and 5. Simulation, compilation and the presence of integration-test code do not establish native verification.
