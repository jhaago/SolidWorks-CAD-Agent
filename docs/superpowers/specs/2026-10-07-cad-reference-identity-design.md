# CAD model and entity reference identity

**Status:** Proposed design; user approved the recommended direction in conversation on 2026-10-07. This written spec is awaiting user review. No product code or persisted contract has changed.

## Goal

Give future CAD plans a durable, explicit way to name a managed model and its features, sketches, sketch entities, bodies, faces, edges and vertices. References must survive supported save/reopen and Agent restarts, fail clearly when they no longer resolve, and remain separate from SOLIDWORKS COM objects and display names. The first implementation is limited to managed part documents and a created sketch; it does not enable arbitrary face selection or existing-model editing.

## Current repository behavior

The current plan is a `CadPlanningResult` containing ordered `CadCommandEnvelope` values with unversioned `JObject` parameters. `JobCoordinator` stores that plan JSON in `Revisions.PlanJson`; the SQLite schema is version 4 and has no model or entity registry. Historical plan JSON is loaded directly into the same command contracts.

The Host sets `ExecutionId` to `JobId`. `SolidWorksBridgeFacade` uses it to clear or reuse an in-memory `SolidWorksDocumentContext`; that context binds a COM document and checks active-document identity with `IUnknown`. It does not survive process restart and does not identify an entity within the document. `CreateSketch` resolves an origin plane by its localized/display name through `SelectByID2`. `Extrude` and `CutExtrude` rely on the current SOLIDWORKS selection/profile. Create-feature results return input values or a feature display name. Production code has no durable selection resolver; the available edge selection code is draft and unregistered.

## Chosen identity model

Use three separate identity layers:

1. **Model identity:** a Host-generated GUID names one managed CAD model lineage node. It is recorded in a dedicated SOLIDWORKS custom property on Agent-managed documents and mirrored in the Host SQLite model registry. The property is a lookup hint, not a security credential: the registry, current file identity and duplicate detection must agree before the Agent trusts it.
2. **Logical entity identity:** a Host-generated GUID names one feature/sketch/entity within a model identity and configuration context. Plans and clients use this GUID only. It does not encode feature order, display name, topology index or a SOLIDWORKS object pointer.
3. **Native binding:** the Bridge captures and resolves SOLIDWORKS persistent reference bytes for a logical entity. The token stays opaque to Core, the planner, HTTP clients and ordinary job history. It is scoped to a model identity and configuration; it is resolved again on the SOLIDWORKS STA before use.

The Bridge owns COM capture, selection and resolution. A narrow internal reference-store port allows the Bridge to persist or retrieve opaque binding records through the Host’s SQLite repository. The Host owns durable records and lifecycle policy. The planner sees logical IDs and observations only; it never receives native token bytes. Token bytes are copied out of COM before storage I/O; no database work occurs inside the COM callback.

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

## First implementation slice

After this spec and an implementation plan are approved, implement one vertical slice only:

1. Add schema-versioned model and entity-binding persistence with an additive v4-to-new migration and repository tests preserving existing jobs, revisions and plans.
2. For a newly Agent-created part, assign and persist a model ID in both the managed custom property and SQLite registry.
3. Create a sketch, return a Host-assigned logical sketch ID to the internal Bridge reference store, and capture a SOLIDWORKS persistent reference on the STA.
4. Close and reopen the saved owned part, resolve the sketch reference, rename the sketch display name, rebuild, and resolve it again. Confirm the logical ID remains stable.
5. Reject wrong-document, duplicate-model-ID, missing, deleted/suppressed, configuration-mismatched and malformed-token cases with structured failures and zero downstream mutation.

This slice does not update the AI planner, change legacy extrude/cut selection, support arbitrary topology entities, enable user-model edits, implement SaveAs-copy token transfer, or make identity IDs user-editable. Those require later slices and separate native acceptance.

## Verification and capability evidence

Unit/domain tests must cover ID validation, serialization, duplicate detection, repository migration/rollback, legacy plan golden payloads, reference-kind mismatch, missing/stale bindings, and fail-closed behavior. Simulation tests may exercise identity lifecycle and error reporting but cannot verify native reference stability.

Native acceptance uses only a newly created owned part and an isolated output path. Record SOLIDWORKS release/service pack, interop build, template, model ID, logical sketch ID, native result/error codes and artifact hash. Verify capture, save, close, reopen, resolution, sketch rename, rebuild and second resolution. Add negative fixtures for a deleted or suppressed reference and a separate document with the same-named sketch. Leave the user’s existing documents and dirty state untouched. SaveAs-copy behavior is explicitly outside the first verification claim.

The target SOLIDWORKS API family is `IModelDocExtension.GetPersistReference3` / `GetObjectByPersistReference3`; exact 2020 SP0.0 behavior remains a required local probe. Official documentation identifies persistent references as document-object references and provides deleted/suppressed resolution results for topological entities. Older `GetPersistReference` token data is incompatible with the `...3` family, so the registry must version its token format and must never treat token bytes as version-independent. [SOLIDWORKS 2023 GetPersistReference3](https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.interop.sldworks~SolidWorks.interop.sldworks.IModelDocExtension~GetPersistReference3.html), [SOLIDWORKS 2019 GetObjectByPersistReference3](https://help.solidworks.com/2019/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IModelDocExtension~GetObjectByPersistReference3.html).

SaveAs variants have materially different copy/active-document behavior, and copies cannot be assumed to share file references. Initial support must use only the explicit Agent-mediated copy action and assign a child model identity; test any later token transfer rather than infer it. [SOLIDWORKS 2026 Save As](https://help.solidworks.com/2026/english/SolidWorks/sldworks/AFX_HIDD_FILESAVE.htm?id=3.5.14.10), [Opening Document Copies](https://help.solidworks.com/2026/english/SolidWorks/sldworks/t_saveascopyandedit_fundamentals.htm?id=3.5.4.12).

## Risks and explicit limits

- The custom property can be manually edited or cloned. Treat it only as one identifier signal and fail on collisions; it is not authorization or proof of model integrity.
- SOLIDWORKS 2020 SP0.0 may differ in token stability across rebuild, rename, SaveAs or configuration changes. The first native fixture determines supported behavior; unsupported cases stay blocked.
- A Host SQLite registry is local to the Agent installation. Moving a managed part to another computer without the registry requires explicit import/re-registration; portable manifests are deferred.
- Saving the model and updating SQLite are not one atomic transaction. Record pending identity changes, verify the saved model property and artifact hash, and reconcile interrupted saves on reopen; never claim identity registration succeeded from an in-memory update alone.
- Existing part editing remains checkpoint-copy-only until model stamps, observation and recovery are implemented. Persistent references do not provide transaction or rollback guarantees.

## Approval boundary

This document records the approved direction and narrows the first vertical slice. It is not permission to implement the new schema or change command semantics. User review of this written spec is required before preparing an implementation plan. Native evidence is required before marking the new identity/reference capabilities REAL SOLIDWORKS VERIFIED.
