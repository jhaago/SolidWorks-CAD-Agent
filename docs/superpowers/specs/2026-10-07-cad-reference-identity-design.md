# CAD model and entity reference identity

**Status:** User-approved reference-identity direction (2026-10-07). Schema v5, managed identity, native sketch capture/resolution and a Bridge-internal one-sketch selection scope are implemented. The version-2 feature-consumer contract below is the proposed migration design; no version-2 plan or feature command is executable or planner-advertised yet.

## Goal

Give future CAD plans a durable, explicit way to name a managed model and its features, sketches, sketch entities, bodies, faces, edges and vertices. References must survive supported save/reopen and Agent restarts, fail clearly when they no longer resolve, and remain separate from SOLIDWORKS COM objects and display names. The first implementation is limited to managed part documents and a created sketch; it does not enable arbitrary face selection or existing-model editing.

## Current repository behavior

The plan remains a `CadPlanningResult` containing ordered `CadCommandEnvelope` values with unversioned `JObject` parameters. `JobCoordinator` stores plan JSON in `Revisions.PlanJson`; schema v5 adds model and entity-reference registries without rewriting historical plan JSON. For approved real-mode `NewPart`, the Host creates a pending model identity and execution-only model/sketch IDs; the Bridge stamps new managed documents and validates managed `OpenPart` against registry path/hash before binding. Explicitly opened existing parts are read-only in plan preflight. Managed `CreateSketch` now captures/stores a persistent token and exposes the logical ID after successful storage.

The Host sets `ExecutionId` to `JobId`. `SolidWorksBridgeFacade` uses it to clear or reuse an in-memory `SolidWorksDocumentContext`; that context binds a COM document and checks active-document identity with `IUnknown`. It does not survive process restart and does not identify an entity within the document. `CreateSketch` resolves an origin plane by its localized/display name through `SelectByID2`. `Extrude` and `CutExtrude` rely on the current SOLIDWORKS selection/profile. Create-feature results return input values or a feature display name. Production code has no durable selection resolver; the available edge selection code is draft and unregistered.

## Chosen identity model

Use three separate identity layers:

1. **Model identity:** a Host-generated GUID names one managed CAD model lineage node. It is recorded in a dedicated SOLIDWORKS custom property on Agent-managed documents and mirrored in the Host SQLite model registry. The property is a lookup hint, not a security credential: the registry, current file identity and duplicate detection must agree before the Agent trusts it.
2. **Logical entity identity:** a Host-generated GUID names one feature/sketch/entity within a model identity and configuration context. Plans and clients use this GUID only. It does not encode feature order, display name, topology index or a SOLIDWORKS object pointer.
3. **Native binding:** the Bridge captures and resolves SOLIDWORKS persistent reference bytes for a logical entity. The token stays opaque to Core, the planner, HTTP clients and ordinary job history. It is scoped to a model identity and configuration; it is resolved again on the SOLIDWORKS STA before use.

The Bridge owns COM capture, selection and resolution. A narrow internal reference-store port allows the Bridge to persist or retrieve opaque binding records through the Host’s SQLite repository. The port also looks up managed models by canonical path so a managed file that loses its custom property is not mistaken for an unmanaged legacy file; duplicate path matches are an identity ambiguity and fail closed. This uses the existing path index and adds no persisted columns. The Host owns durable records and lifecycle policy. The planner sees logical IDs and observations only; it never receives native token bytes. Token bytes are copied out of COM before storage I/O; no database work occurs inside the COM callback.

An entity binding contains at least: schema version, model ID, logical entity ID, entity kind, configuration key, native object kind, opaque persistent-reference bytes, first-bound model revision, last-successful-resolution revision, optional semantic fingerprint for diagnosis, and status. Status distinguishes `Active`, `Suppressed`, `Deleted`, `Stale`, `Ambiguous`, and `Unresolved`. The registry may retain old binding versions for diagnosis; a failed resolution never silently overwrites the last known token.

The model ID property and the Host registry are not sufficient alone. The Host also records the managed file identity/path, file hash at the last accepted checkpoint, runtime/configuration context, lineage parent, and registry version. File paths and hashes are change-detection evidence, not entity identity. A changed hash or native model stamp invalidates an approval's base-model check; it does not automatically invalidate every logical entity ID.

## Managed-document policy

- `NewPart` creates an Agent-managed document and assigns a fresh model ID before any sketch or feature is created.
- An existing user document is read without adding Agent metadata. Before future mutation, the user-approved workflow creates an owned working copy inside the configured CAD workspace and assigns that copy a fresh model ID. The source remains untouched. Entity references are not inferred from file paths or feature names during adoption.
- A normal rename/move of a managed artifact retains its model ID if the property, registry, and file-identity checks agree. A missing or conflicting registry record requires explicit re-registration; path equality alone cannot restore identity.
- An Agent-mediated SaveAs-as-copy creates a child model ID and lineage record. It receives no inherited entity bindings in the initial implementation. Native token portability to a copy is not assumed. The copied document’s property is updated only as part of that explicit, authorized copy operation.
- A manual file copy duplicates the custom property. If two artifacts claim one model ID, both are treated as an identity collision. Do not open the ambiguous binding for modification or select a winner by path; ask the user to register/fork one copy, assigning it a new model ID and rebuilding its bindings.
- If a managed file loses its custom property, the Agent must stop reference-dependent mutation and offer explicit repair after verifying the registry and file context. Never silently create a new identity and bind old plans to it.

## Resolution and invalidation rules

Resolve `(ModelId, EntityId, ConfigurationKey)` against the currently bound document on the STA. Confirm the active COM document belongs to the requested model context before resolving. Verify native object kind and selection count/type/mark before the consuming operation, and clear temporary selection state in `finally`.

SOLIDWORKS persistent references are a lookup mechanism, not a guarantee that an entity survives every topology change. The API reports resolved, suppressed or deleted outcomes; the API documentation describes these states for topological references. If a token does not resolve or resolves to an incompatible object, mark the binding stale/unresolved and return a structured error with model/entity IDs, command stage and native resolution status. Do not substitute a face with the same index, name, area or approximate location.

Semantic predicates (surface class, axis/normal, location, area/radius, adjacency and expected count) are observations and candidate-generation tools. They may support an explicit rebind flow only when the candidate report is unique and the approved policy permits that exact repair. Semantic similarity is not identity. Ambiguous candidates always stop for clarification.

Every approved edit plan is bound to `(PlanRevisionId, ModelId, BaseModelRevisionId, ReferenceContractVersion)`. Before mutation, verify the active document and base model stamp still match. During an Agent-owned operation sequence, bindings are re-resolved or read back after topology-changing operations as required by that capability. Model revision advance does not by itself erase entity IDs; resolution evidence updates the binding revision. External edits, unknown native changes, configuration mismatch, or failed re-resolution block mutation until the model is inspected and the plan is revised.

## Plan and persisted-data compatibility

Do not add native tokens or trusted identity fields to historical plan JSON, and do not reinterpret a missing reference as “current selection.” Existing plans remain on their version-1 command path with their historical selection semantics and existing protections.

Before planner-produced reference-bearing plans are accepted, add a distinct, explicitly versioned plan representation. Version 2 should represent operations, operation-local output keys and typed logical references. Output keys are scoped to one plan; the Host normalizes them to GUIDs and persists the normalized approved plan before execution. The model may propose symbolic links inside the candidate plan, but it cannot choose authoritative model IDs, native tokens, workspace paths or overwrite authority. Unsupported versions and dangling/duplicate output keys are rejected before approval. Approval is revision-bound and is invalidated if the base model or reference contract changes.

Migration is additive: retain the existing `CadPlanningResult`/`CadCommandEnvelope` deserializer for historical rows, introduce new model/reference tables and a versioned plan document, and migrate SQLite from schema v4 transactionally with rollback on failure. Do not rewrite old `PlanJson`, auto-attach current native selections, or invent entity bindings for historical executions. Old jobs remain viewable and do not become eligible for reference-based edits merely because the Host has migrated.

## Proposed version-2 feature-consumer contract (next migration)

**Decision to review:** keep the existing unversioned `CadPlanningResult` and `CadCommandEnvelope` as version 1. A missing version never means “use a logical sketch ID.” Introduce a distinct plan document with required root `planVersion: 2`; do not put `operationVersion` or `sketchEntityId` inside the version-1 `Parameters` object. The latter is already rejected by planner validation and, after the current contract-hardening slice, by direct Bridge and simulation `Extrude` execution. The version-2 plan and executor are not implemented in this slice.

The alternatives were (a) adding an optional sketch GUID to the existing `Extrude`/`CutExtrude` parameters, and (b) changing a version field on the existing envelope alone. Both fail the same-plan case: the Host currently assigns the sketch GUID only when `CreateSketch` executes. They also risk making historical plans change meaning or letting a v2-shaped request reach a v1 current-selection handler. A distinct version-2 plan with a plan-local output key solves the planning-time dependency, while retaining the existing command and registry boundaries for execution.

A candidate v2 plan uses ordered steps. Each step has a unique `stepKey`, exact command name, explicit `operationVersion` and validated parameters. A `CreateSketch` step may declare one `outputKey` of kind `Sketch`; version-2 `Extrude` and `CutExtrude` require exactly one `inputs.profileSketch` with `{kind:"Sketch", outputKey:"..."}`. The output key is scoped only to that plan revision. It is a symbolic dependency, never a native token, feature display name, entity index or trusted model ID. A minimal candidate is:

```json
{
  "planVersion": 2,
  "steps": [
    {"stepKey":"s1","command":"NewPart","operationVersion":1,"parameters":{}},
    {"stepKey":"s2","command":"CreateSketch","operationVersion":1,"parameters":{"plane":"Top Plane"},"outputKey":"plate-profile"},
    {"stepKey":"s3","command":"AddRectangle","operationVersion":1,"parameters":{"centerXmm":0,"centerYmm":0,"widthMm":100,"heightMm":60}},
    {"stepKey":"s4","command":"ExitSketch","operationVersion":1,"parameters":{}},
    {"stepKey":"s5","command":"Extrude","operationVersion":2,"parameters":{"depthMm":10},"inputs":{"profileSketch":{"kind":"Sketch","outputKey":"plate-profile"}}}
  ]
}
```

Before offering approval, the Host validates the whole candidate: exact supported plan/operation versions; unique step/output keys; references to a prior, closed sketch in the same planned model and configuration; supported profile geometry; a single consumer for this first feature path; and no dangling, forward, wrong-kind or duplicate references. The Host then assigns each output a GUID and persists an immutable normalized approved v2 plan in the existing `Revisions.PlanJson` column. In that normalized form, `inputs.profileSketch` contains the Host-generated entity GUID and retains the symbolic key only as review evidence. The model ID remains Host-controlled; the planner cannot assign it. No SQLite schema migration is needed solely for this plan shape, but every plan reader, approval path, revision renderer and execution path must dispatch by the root version before v2 can be persisted. Unknown root versions and malformed normalized plans fail before approval or mutation; they never fall back to the v1 deserializer.

For execution, the Host passes the resolved GUID as trusted execution context to a **version-aware extension of the existing command registry**. A version-2 feature handler must call the Bridge selection scope and create the feature within that same STA callback. It must never lower to the legacy `Extrude` or `CutExtrude` handler: those handlers still consume SOLIDWORKS' current selection. `CadOperationDescriptor.OperationVersion == 1` remains the only advertised planner operation until the v2 handler, Host normalization, simulation policy and native verifier are all registered consistently. Simulation must reject v2 if it cannot verify its geometry; it must not silently execute v1. A native failure after feature creation begins is uncertain, stops the plan and is never retried blindly.

Migration acceptance requires golden historical plan JSON to deserialize and execute with the same v1 semantics; v1 requests carrying reference fields to fail before native dispatch; unknown/missing v2 versions, duplicate/dangling/wrong-kind/output-order references and mixed-model references to fail before approval; and a valid normalized v2 plan to select the intended owned sketch even when another sketch or stale selection exists. Native tests must verify feature geometry, rebuild, no selection leak, document/configuration mismatch and deleted reference rejection. Only then can the v2 capability be planner-advertised or marked native verified.

## First implementation slice

After this spec and an implementation plan are approved, implement one vertical slice only:

1. Add schema-versioned model and entity-binding persistence with an additive v4-to-new migration and repository tests preserving existing jobs, revisions and plans.
2. For a newly Agent-created part, assign and persist a model ID in both the managed custom property and SQLite registry.
3. Create a sketch, return a Host-assigned logical sketch ID to the internal Bridge reference store, and capture a SOLIDWORKS persistent reference on the STA.
4. Close and reopen the saved owned part, resolve the sketch reference, rename the sketch display name, rebuild, and resolve it again. Confirm the logical ID remains stable.
5. Reject wrong-document, duplicate-model-ID, missing, deleted-sketch, configuration-mismatched and malformed-token cases with structured failures and zero downstream mutation. Unit-test the `Suppressed`/`Deleted` native status mapping; the API documents those status enumerators for topological references, which are outside this sketch-only slice.

The initial migration and managed model stamp/save/reopen lifecycle are implemented. The remaining reference slice does not update the AI planner, change legacy extrude/cut selection, support arbitrary topology entities, enable existing-model edits, implement SaveAs-copy token transfer, or make identity IDs user-editable. Those require later slices and separate native acceptance.

## Verification and capability evidence

Unit/domain tests must cover ID validation, serialization, duplicate detection, repository migration/rollback, legacy plan golden payloads, reference-kind mismatch, missing/stale bindings, and fail-closed behavior. Simulation tests may exercise identity lifecycle and error reporting but cannot verify native reference stability.

Native acceptance uses only a newly created owned part and an isolated output path. Record SOLIDWORKS release/service pack, interop build, template, model ID, logical sketch ID, native result/error codes and artifact hash. Verify capture, save, close, reopen, resolution, sketch rename, rebuild and second resolution. Add negative fixtures for a deleted sketch reference and a separate document with the same-named sketch. Unit-test suppressed/deleted status translation; native topology status scenarios wait until face/edge references are implemented. Leave the user’s existing documents and dirty state untouched. SaveAs-copy behavior is explicitly outside the first verification claim.

The target SOLIDWORKS API family is `IModelDocExtension.GetPersistReference3` / `GetObjectByPersistReference3`. In the controlled SOLIDWORKS 2020 probe, a captured sketch resolves as an `IFeature` wrapper whose type is `ProfileFeature` and whose `GetSpecificFeature2()` exposes `ISketch`; the Bridge resolver must validate that interface shape. Official documentation identifies persistent references as document-object references and provides deleted/suppressed resolution results for topological entities. Older `GetPersistReference` token data is incompatible with the `...3` family, so the registry must version its token format and must never treat token bytes as version-independent. [SOLIDWORKS 2023 GetPersistReference3](https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.interop.sldworks~SolidWorks.interop.sldworks.IModelDocExtension~GetPersistReference3.html), [SOLIDWORKS 2019 GetObjectByPersistReference3](https://help.solidworks.com/2019/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IModelDocExtension~GetObjectByPersistReference3.html).

SaveAs variants have materially different copy/active-document behavior, and copies cannot be assumed to share file references. Initial support must use only the explicit Agent-mediated copy action and assign a child model identity; test any later token transfer rather than infer it. [SOLIDWORKS 2026 Save As](https://help.solidworks.com/2026/english/SolidWorks/sldworks/AFX_HIDD_FILESAVE.htm?id=3.5.14.10), [Opening Document Copies](https://help.solidworks.com/2026/english/SolidWorks/sldworks/t_saveascopyandedit_fundamentals.htm?id=3.5.4.12).

## Risks and explicit limits

- The custom property can be manually edited or cloned. Treat it only as one identifier signal and fail on collisions; it is not authorization or proof of model integrity.
- SOLIDWORKS 2020 SP0.0 may differ in token stability across rebuild, rename, SaveAs or configuration changes. The first native fixture determines supported behavior; unsupported cases stay blocked.
- A Host SQLite registry is local to the Agent installation. Moving a managed part to another computer without the registry requires explicit import/re-registration; portable manifests are deferred.
- Saving the model and updating SQLite are not one atomic transaction. Record pending identity changes, verify the saved model property and artifact hash, and reconcile interrupted saves on reopen; never claim identity registration succeeded from an in-memory update alone.
- Existing part editing remains checkpoint-copy-only until model stamps, observation and recovery are implemented. Persistent references do not provide transaction or rollback guarantees.

## Approval boundary

The user approved implementation of this direction and the bounded inline plan. That authorization does not extend to a broad CAD sprint, unreviewed changes to persistent schema/reference semantics, existing-model mutation, or SaveAs-copy lineage. Native evidence remains required before marking each scenario-specific identity/reference capability REAL SOLIDWORKS VERIFIED.
