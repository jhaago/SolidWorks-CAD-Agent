# Master CAD capability matrix

Review date: 2026-10-07. Authoritative expansion checklist for this repository. Read with [architecture](CAD_AGENT_ARCHITECTURE.md) and [roadmap](CAD_IMPLEMENTATION_ROADMAP.md).

Baseline for this continuation: committed HEAD `179f81db2bc7ace560408cbe7739f6049eda7b0a` plus reviewed uncommitted increment-2 slices recorded in [the handoff](CAD_IMPLEMENTATION_STATUS.md). Historical verified rows describe only their bounded native scenarios; a successful build does not certify new face-sketch or finishing behavior or deployment readiness.

## Status and evidence policy

| Status | Meaning |
|---|---|
| NOT ASSESSED | Product-level presence was checked, but detailed specialist feasibility/API/version/license assessment remains open. Not executable. |
| UNSUPPORTED | No exposed end-to-end implementation was found for the row's user capability. API existence or test-only code does not establish support. |
| PLANNED | No delivered support; explicitly scheduled in this blueprint or its gated domain expansion. No schedule commitment implied. |
| PARTIAL | Some code/workflow exists, but required scope, wiring, reliability or evidence is incomplete. Notes define the exact boundary. |
| IMPLEMENTED | Reachable end-to-end implementation for stated scope; no sufficient passing evidence recorded. |
| AUTOMATED TESTED | Relevant passing automated evidence exists for the cited revision/scope. Does not imply native kernel execution. |
| SIMULATION TESTED | A supported simulated workflow actually passed. No claim about native geometry. |
| REAL SOLIDWORKS VERIFIED | A real execution record supports this bounded scenario on the named version, with relevant result checks. Never assigned solely from code/tests existing. |

Statuses are a compact headline, not a single ladder: simulation and native evidence are independent. `PARTIAL` takes precedence when a broad row is only partly delivered. Narrow native-verified rows may still have incomplete sibling variants. Rows cite evidence keys below; **— means no test/simulation/native evidence claimed**, not “passed.” Unsupported/planned rows have no production implementation location; their mechanism column is a candidate API family, not a validated implementation recipe. Exact overload, release availability, license and options must be checked before implementation.

For each promoted row record revision/build, test names, date, runtime/SP/template/locale, result, artifact/hash, assertions, and limits. Do not copy a family status to every variant. Full promotion criteria and test hierarchy are in the architecture document.

Priorities: **P0** reliability foundation; **P1** editable sketch/core part; **P2** broader parts and downstream engineering; **P3** advanced surfaces/reconstruction; **P4** separate specialists. Prerequisites are hard capability or architecture gates, not toolbar order.

## Dependency gates

| Gate | Requirement | Roadmap delivery |
|---|---|---|
| G0 | Coherent build; versioned capability/operation contract, runtime/mode support and sequence preflight | Increment 1 |
| G1 | Document/entity identity, explicit selections and tested coordinate frames | Increments 2 and 4 |
| G2 | Model snapshots, native readback, dependency/coverage inspection and change detection | Increment 3, extended per domain |
| G3 | Typed sketch/entities/relations/dimensions/feature parameters and intended dependency graph | Increments 6 and 7 |
| G4 | Intended-versus-observed assertions, rebuild checks and verified artifact finalization | Increment 5, extended per capability |
| G5 | Attempt journal, checkpoint/restore, mutation outcomes and safe edit policy | Increment 8 |
| G6 | Multiple document kinds/configurations/occurrences plus import/export and downstream artifact policies | Later roadmap phase P4 |

Gate dependencies are bootstrap slices, not a requirement to build every future observation before the first part. Repeated capabilities across domains cross-reference the same implementation; rows do not imply duplicate modules.

## Implementation and evidence keys

All paths below are relative to repository root. These pointers apply to every row carrying the key.

| Location key | Production implementation or explicitly test-only location |
|---|---|
| DOC-H | `src/SolidWorksCadAgent.SolidWorksBridge/Commands/DocumentCommandHandlers.cs` |
| FILE-H | `src/SolidWorksCadAgent.SolidWorksBridge/Commands/FileCommandHandlers.cs` |
| SK-H | `src/SolidWorksCadAgent.SolidWorksBridge/Commands/SketchCommandHandlers.cs`, `AdvancedSketchCommandHandlers.cs` |
| PROFILE-H | `src/SolidWorksCadAgent.Core/Commands/PrismaticProfileGeometry.cs`; Bridge `Commands/PrismaticProfileCommandHandlers.cs` |
| FT-H | `src/SolidWorksCadAgent.SolidWorksBridge/Commands/FeatureCommandHandlers.cs` |
| OBS-H | `src/SolidWorksCadAgent.SolidWorksBridge/Inspection/SolidWorksInspector.cs`, `PreciseBoundsAccumulator.cs`, `Commands/InspectionCommandHandlers.cs` |
| SESSION | `src/SolidWorksCadAgent.SolidWorksBridge/Session/` and `Threading/SolidWorksStaDispatcher.cs` |
| HOST | `src/SolidWorksCadAgent.AgentHost/Jobs/JobCoordinator.cs`, `Persistence/SqliteJobRepository.cs`, `Host/AgentRoutes.cs` |
| AI-H | `src/SolidWorksCadAgent.AgentHost/Ai/OpenAiCadPlanningProvider.cs`; Core `Commands/CadPlanningCommandContract.cs` |
| IMAGE-H | `src/SolidWorksCadAgent.AgentHost/Design/`, `Ai/OpenAiImageDesignInterpreter.cs`; Contracts `Design/DesignContracts.cs`; Desktop `DesignIntakeForm.cs` |
| SIM | `src/SolidWorksCadAgent.AgentHost/Simulation/SimulatedCadCommandExecutor.cs`, `Planning/DeterministicCadPlanningProvider.cs` |
| WIP-H | Draft `Core/Commands/FaceFeatureValidation.cs`, retained command-name constants, excluded Bridge `Commands/EdgeFinishingCommandHandlers.cs`, and face/finishing native acceptance scenarios. Reconciliation removed draft commands from planner and registry; handler remains excluded pending selector and frame implementation. |

| Evidence | Automated coverage / recorded run | Simulation evidence | Real SOLIDWORKS evidence / limitations |
|---|---|---|---|
| E01 | `CadContractsTests`, `UnitConverterTests`, `SolidWorksBridgeCommandValidationTests`, `PreciseBoundsTests`, `CadResultJsonRegressionTests`; historical N1/N2 reports | Basic plate via `SimulatedCadCommandExecutorTests`; not native file creation | `AcceptancePlateTests`, `DocumentTargetTests`; four-pass N1 TRX inspected, including plate and 10×10×1 block. 2020 SP0.0 report, save/reopen, topology/volume/bounds |
| E02 | `PrismaticProfileTests`, `PrismaticBridgeTests`; [362-test historical report](windows-validation-prismatic-2026-10-06.md) | `PrismaticSimulationTests` checks honest refusal of custom solids/blind pockets; does NOT verify their geometry | `PrismaticCapabilityTests`: rotated slot/hexagonal blind pocket and rotated hex boss; historical two-pass report and retained files present. 2020 SP0.0 only |
| E03 | Registry/bridge validation, `DocumentTargetTests`, `WorkspacePolicyTests`, `ApprovalPolicyTests`; historical suites | Workflow/validation only where supported | N1 switched-document protection; no generic persistent-selection certification |
| E04 | `NativePlateInspection.cs`, `PrismaticCapabilityTests.cs`, `AdvancedSketchTests.cs` test-only inspection | None for general observation | Native test oracles inspect volume/sketches/hole topology; not production API support |
| E05 | `SolidWorksNormalLaunchTests`, `SolidWorksCompatibilityTests`, foundation/package tests | `SimulatedSolidWorksSession` is synthetic status only | [Lifecycle validation](windows-validation-2026-10-05.md) records launch/reattach; year-level label is not per-capability certification |
| E06 | `AdvancedSketchTests` (unit project), shared primitive validation; [235-test historical report](windows-validation-v2-2026-10-06.md) | Acknowledges line/arc, explicitly rejects custom-profile solids | Native semicircle saved/reopened, actual passed N1 TRX; asymmetric arc direction not independently distinguished |
| E07 | `JobCoordinatorTests`, `JobStateMachineTests`, `OpenAiCadPlanningProviderTests`, Desktop/client/routes/repository tests; historical suites | `SimulatedCadCommandExecutorTests` and coordinator fixtures | Recorded live baseline plate workflow; prismatic live planning stopped at AwaitingApproval. Generic planning/editing is not assigned native status |
| E08 | Image interpreter/intake/store/routes/client tests in [308-test report](windows-validation-image-intake-2026-10-06.md) | Fake interpreter/HTTP/service tests, not geometric simulation | Synthetic image clarification tested live with provider; stopped before design approval/CAD planning. No image-to-native certification |
| E09 | `SimulatedCadCommandExecutorTests`, `PrismaticSimulationTests`, composition/coordinator tests | Basic plate state workflow passes historically; no B-rep, reference or regeneration solver | None; simulation never promotes native verification |
| E10 | `tests/SolidWorksCadAgent.UnitTests/Remote/` and remote client tests | Fake input/HTTP/session lifecycle | Manual phone/desktop evidence remains scenario-specific in remote docs, not CAD geometry evidence |
| E11 | [2026-10-07 desktop follow-up](windows-validation-desktop-smoke-2026-10-07.md): 419/419 unit tests; artifact open-handle regression; native bundle validation; image-backed Host clarification/replan | No geometric simulation claim | Installed native Host produced a prismatic part with clean body/bounds/rebuild checks and served its saved bytes while SOLIDWORKS held it open. Installed image-backed planner recognized a synthetic sketch and resolved clarification, but no image-derived CAD commands ran. Paired phone paths remain untested. |
| E12 | `SimulationSavePlan_RequiresClarificationBeforeApprovalOrExecution`, `TypedLegacySubsetExecutesWithSamePersistedAndDispatchedParameters`, lifecycle/coordinator focused tests; full unit suite; both interop build modes | Mode-aware preflight rejects simulation `SavePart` before auto execution; no new geometry claim | Real-mode plan review exercised with a recording executor only; no SOLIDWORKS session or native geometry scenario was part of this policy change. |
| E13 | Simulation profile and feature preflight tests in `JobCoordinatorTests`; 42/42 focused and 426/426 full unit suite outside restricted sandbox | Rectangle boss and circular ThroughAll cut still complete; unsupported profile/feature pairs and blind cuts stop before dispatch | No native SOLIDWORKS scenario; this is simulator capability preflight only. |
| E14 | `SimulationOpenPart_RequiresClarificationBeforeExecutorCalls`, `RealOpenPartPlan_RemainsAwaitingApprovalWithLegacyPath`; 44/44 focused tests; 428/428 full unit suite outside restricted sandbox | Simulation `OpenPart` is rejected before dispatch; supported simulated plate flow remains covered by E13 | Real-mode path plan was validated/persisted with a recording executor only. No SOLIDWORKS session was opened; this is mode-policy evidence, not native verification. |
| E15 | `ManagedModelIdentityTests`, `ModelReferenceStoreTests`, `SolidWorksBridgeCommandValidationTests`, `CadPlanLifecycleValidatorTests`; full unit suite result is recorded in the handoff | No new geometry simulation claim; Host lifecycle and fail-closed records are exercised with controlled executors | `ModelReferenceCapabilityTests`: SOLIDWORKS 2020 SP0.0 passed managed ID property stamp/readback, save/hash, close/reopen registry validation, and legacy no-stamp OpenPart. Original active document/save flag and test-owned document cleanup assertions passed. Retained owned artifact: `C:\SolidWorks-CAD-Agent\Workspace\capability-tests\f9456cad5e4446cda50f1748b0da8011\managed-identity-reopen.SLDPRT`; SHA-256 `640A56BFE5D9544E0AACB5137DE15D814CFA1B1B107078117F48DF4CB22B28A7`. Duplicate-ID, missing-property and hash-mismatch rejection paths have not yet had native acceptance. |
| E16 | `SolidWorksReferenceResolverTests`: 7/7 focused; 450/450 full unit suite outside the restricted sandbox; both interop build modes 0 warnings/errors | No geometric simulation claim; tests cover missing/wrong binding, copied token storage, uncertain persistence, bad token/configuration and native status classification | `ManagedSketchReference_ProductionCaptureAndResolveSurviveReopenRenameRebuild`: 1/1 passed on SOLIDWORKS 2020 SP0.0 with production Bridge capture/store/resolver. Logical sketch ID `b44c292c-120b-4270-a831-d43ac61bff0e` resolved with native status 0 before save, after close/reopen and after rename/rebuild. The fixture asserted original active-document/save-flag preservation and closure of all test-owned documents. Owned artifact: `C:\SolidWorks-CAD-Agent\Workspace\capability-tests\3cec0b8afb1e4b04ab221c1e30ad17b9\managed-sketch-reference.sldprt`; saved SHA-256 `83009eaffd069a6e93e977e4b220fa9126a90a9a709c4417c152866f35b4efda`. TRX: `tests/SolidWorksCadAgent.IntegrationTests/TestResults/managed-sketch-reference-final.trx`. The later negative native cases are recorded in E17. |
| E17 | `SolidWorksReferenceResolverTests` plus 450/450 full unit suite and both interop builds in the Task 5 handoff; rejected-OpenPart identity regression is covered by the opt-in native fixture | No geometric simulation claim; no new planner or selection command | SOLIDWORKS 2020 SP0.0 `ModelReferenceCapabilityTests` passed 3/3. The production resolver rejected a same-named sketch in another owned document (`DOCUMENT_TARGET_CHANGED`); a byte-identical owned file copy was rejected as `MODEL_ID_COLLISION`, closed, and left the original binding usable; deleting the owned sketch made resolution fail with `SKETCH_REFERENCE_INVALID`, native status `1`, without replacing its stored token or recreating geometry. The fixture asserted original-document/save-flag preservation and owned-document cleanup. Final TRX and artifact hash are in the handoff. No downstream feature consumer exists yet, so plan-level mutation blocking is not certified by this case. |
| E18 | Approval-time revalidation positive/negative unit tests; full unit suite 451/451; both interop builds succeeded with zero warnings/errors | No simulation geometry claim; the new selection scope is Bridge-internal and is not a planner command | SOLIDWORKS 2020 SP0.0 controlled `ModelReferenceCapabilityTests` passed 3/3. After managed reopen and a Host-style feature-tree inspection, the same logical sketch resolved and was selected once with mark 4. The scope cleared selection after success and an action failure; wrong-document and deleted-sketch attempts did not run the action, and the deleted case left no selection. TRX: `tests/SolidWorksCadAgent.IntegrationTests/TestResults/model-reference-selection-scope-final.trx`. No downstream feature creation or general topology selection was verified. |
| E19 | Version-1 feature contract regression tests: direct Bridge and simulator `Extrude` reject `sketchEntityId`; historical unversioned `Extrude`/`CutExtrude` JSON remains valid and does not serialize a version field. Full unit suite 454/454; both full solution build modes 0 warnings/errors. | The simulator's failed direct call did not consume the pending rectangle profile; a following valid extrusion succeeded. This is a contract guard, not geometric verification. | Controlled owned-document SOLIDWORKS 2020 `PrismaticCapabilityTests` passed 2/2 after the validator change, verifying established v1 extrude/cut geometry. TRX: `tests/SolidWorksCadAgent.IntegrationTests/TestResults/prismatic-v1-contract-regression.trx`. No v2 plan/consumer native claim. |
| E20 | `CadPlanDocumentReaderTests` (including unknown/overflow/missing/case-invalid root versions, duplicate JSON fields, unsupported operation versions, duplicate step/output keys, dangling/forward/wrong-kind references, unsupported fields/document lifecycle); Host approval tests distinguish malformed candidates from valid v2 candidates. Focused 18/18; full unit suite 472/472; full solution interop-disabled and interop-enabled builds each 0 warnings/errors. | Candidate preflight only; v2 plans are not lowered or executed in simulation. Historical unversioned plan parsing and approval tests remain covered. | No new native geometry or COM behavior. Existing v1 native evidence in E19 remains scoped to its prior scenario; no v2 feature consumer is native verified. |
| E21 | `VersionTwoPlannerCandidatesAreNormalizedAndPersistedReviewOnlyPerRevision`, `CadPlanDocumentReaderTests`; focused set 20/20; full unit suite 474/474; both full solution interop build modes 0 warnings/errors. | Host-owned sketch IDs are stored on producers and matching inputs. Revisions receive fresh IDs; v2 stays in clarification even with Auto Mode; no simulator or native execution claim. Planner-supplied IDs and persisted ID/key mismatches fail preflight. | No COM or geometry change. The current OpenAI and deterministic planners do not implement the optional versioned-plan interface, so no current user planner emits v2. |
| E22 | Task 7d-b registry/Bridge/simulation dispatch tests; focused set 26/26 at that slice, with full suite/build evidence in the handoff. The current Bridge test now expects the registered v2 Extrude path to require a reference store and execution ID. | Simulation still rejects v2 before recording a call or changing geometry; legacy v1 extrusion remains supported. | This key covers the earlier no-fallback boundary only. See E23 for the newly registered native v2 Extrude consumer. |
| E23 | `CadCommandRegistryTests`, `CadPlanDocumentReaderTests`, `SolidWorksBridgeCommandValidationTests` and lifecycle tests; final full unit suite 487/487, both build modes 0 warnings/errors. Host binder rejects unnormalized/tampered IDs; strict v2 JSON alias and per-sketch dependency tests pass; Bridge rejects missing execution IDs and keeps v2 Cut unregistered. TRX in the handoff. | No v2 geometry simulation; Host still holds v2 plans for review only. | SOLIDWORKS 2020 SP0.0 controlled `ModelReferenceCapabilityTests` final reviewed-source 4/4 (TRX `consecutive-slices-native-final-review.trx`): v2 Extrude consumed the logical sketch ID, created one 20×10×5 mm/1000 mm³ boss, rebuilt cleanly, cleared selection, saved without overwrite, rejected wrong document/deleted sketch and a different execution ID without feature mutation, and preserved original document state. Isolated owned artifact/hash in the handoff. No approved v2 Host plan or v2 Cut claim. |
| WIP | `FaceFeatureValidation.cs`, excluded `EdgeFinishingCommandHandlers.cs`, and face/finishing tests remain draft material | No registered simulation implementation; `CreateSketchOnFace`, `FilletEdges`, and `ChamferEdges` are intentionally not in planner or executor registry | Post-reconciliation builds succeed in both interop modes; 410/410 unit tests pass outside the restricted sandbox. Native face/finishing scenarios remain pending and have not been run. Draft code presence is not support evidence. |

Source review found no existing comprehensive matrix; [prismatic capability notes](prismatic-cad-capabilities.md) remain useful detailed usage/evidence for that slice. This master checklist supersedes their role as a project-wide inventory.

## Document and environment

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| DOC-001 | Create part | REAL SOLIDWORKS VERIFIED | DOC-H: ISldWorks.NewDocument | Default part template | P0 | E01 | Historical native scope: default template, owned new part. |
| DOC-002 | Create assembly | PLANNED | ISldWorks.NewDocument / IAssemblyDoc | G0–G6 | P2 | — | Same capability as ASM-001; requires new document kind and workspace format policy. |
| DOC-003 | Create drawing | PLANNED | ISldWorks.NewDocument / IDrawingDoc | G0–G6 | P2 | — | Same capability as DRW-001; requires model/configuration binding and drawing template. |
| DOC-004 | Resolve default part template | REAL SOLIDWORKS VERIFIED | DOC-H: GetUserPreferenceStringValue | Installed template | P0 | E01 | Missing/invalid template fails; arbitrary template selection is not exposed. |
| DOC-005 | Manage templates and locale-independent origin planes | PLANNED | Template profile / feature references | G0, G1 | P1 | — | Record template hash, locale, frames and defaults. |
| DOC-006 | Millimetre geometry input / metre conversion | REAL SOLIDWORKS VERIFIED | Core UnitConverter / Bridge | G0 | P0 | E01 | Physical 10×10×1 block verified; document display units are separate. |
| DOC-007 | Set/read document display units | PLANNED | IModelDoc2 unit preferences | G0, G2 | P1 | — | Current units depend on template. |
| DOC-008 | Document properties and options | UNSUPPORTED | IModelDoc2 / IModelDocExtension | G0, G2 | P2 | — | Typed scoped options; do not mutate application defaults implicitly. |
| DOC-009 | Open native part | REAL SOLIDWORKS VERIFIED | FILE-H: OpenDoc6 | Workspace policy | P0 | E01,E02 | Only .sldprt in workspace; bind returned document. |
| DOC-010 | Save native part | REAL SOLIDWORKS VERIFIED | FILE-H: IModelDocExtension.SaveAs | Owned part / workspace | P0 | E01,E02,E11 | Native save and open-document artifact delivery tested; immutable revision finalization is still missing. |
| DOC-011 | Save as separate native part path | REAL SOLIDWORKS VERIFIED | FILE-H: SaveAs | DOC-010 | P0 | E01,E02 | Server-controlled overwrite; no generic copy/pack-and-go policy. |
| DOC-012 | Close owned document | REAL SOLIDWORKS VERIFIED | FILE-H: ISldWorks.CloseDoc | Document binding | P0 | E01,E02 | Native owned close/reopen tested; general dirty-document policy missing. |
| DOC-013 | Rebuild changed features only | UNSUPPORTED | IModelDoc2.EditRebuild3 | G0, G2 | P0 | — | Current Rebuild command always uses ForceRebuild3. |
| DOC-014 | Force rebuild / report result | REAL SOLIDWORKS VERIFIED | DOC-H: ForceRebuild3(false) | Owned part | P0 | E01,E02 | Part fixture scope; warning/error inspection is separate. |
| DOC-015 | Undo supported operation group | PLANNED | IModelDocExtension undo recording | G5 | P1 | — | Must prove restore; not universal transaction support. |
| DOC-016 | Restore checkpoint after failure | PLANNED | Host checkpoint policy / native reopen | G1, G2, G4, G5 | P0 | — | Current startup fail-stop does not restore CAD. |
| DOC-017 | Inspect feature tree | REAL SOLIDWORKS VERIFIED | OBS-H: IFirstFeature/GetNextFeature | Owned part | P0 | E01,E02 | Top-level name/type/error list only; see OBS-001/002. |
| DOC-018 | Traverse whole model structure | PLANNED | IFeature / IBody2 / sketch traversal | G1, G2 | P0 | — | Nested sketches, hierarchy and coverage needed. |
| DOC-019 | Explicit robust entity selections | PARTIAL | SK-H; WIP-H / SelectByID2, IEntity.Select4 | G1 | P0 | E03,WIP | Origin-plane name selection works; no generic selection scope. |
| DOC-020 | Persistent entity identification | PARTIAL | Bridge `SolidWorksReferenceResolver` and `SolidWorksSketchSelectionScope`; GetPersistReference3 / GetObjectByPersistReference3 | G1 | P0 | E15–E18 | Managed sketch capture, resolution and one Bridge-internal selection scope are native verified on SOLIDWORKS 2020. Broad entity/topology identity and feature consumers remain unimplemented. |
| DOC-021 | Custom properties | PLANNED | ICustomPropertyManager | G0, G2 | P2 | — | Model/configuration scope and typed values. |
| DOC-022 | Assign/read material | PLANNED | IPartDoc material methods | G0, G2 | P2 | — | Record material database and configuration; not mechanical certification. |
| DOC-023 | Appearances / display states | UNSUPPORTED | Appearance/render material interfaces | G1, G2 | P3 | — | Visual presentation, not geometry evidence. |
| DOC-024 | Mass / centre of mass / inertia | PARTIAL | Native test inspectors only; IMassProperty / body mass | G2 | P0 | E04 | Volume is tested natively; no runtime mass-properties command or material-aware mass. |
| DOC-025 | Precise bounding box | REAL SOLIDWORKS VERIFIED | OBS-H: IBody2.GetExtremePoint | Owned solid part | P0 | E01,E02 | Visible solid bodies; oriented bounds and hidden-body policy missing. |
| DOC-026 | Geometric measurement | PLANNED | IMeasure / analytic body and entity data | G1, G2 | P0 | — | Distance/angle/radius/area with frame and tolerance. |
| DOC-027 | Attach / launch SolidWorks | REAL SOLIDWORKS VERIFIED | SESSION: ROT / normal executable launch | Native-enabled build | P0 | E05 | Historical 2020 lifecycle evidence; startup probing bounded, arbitrary COM calls are not. |
| DOC-028 | Guard owned active document identity | REAL SOLIDWORKS VERIFIED | SESSION: SolidWorksDocumentContext | DOC-001 or DOC-009 | P0 | E01,E03 | COM identity and Host ExecutionId; does not detect edits inside same document. |
| DOC-029 | Runtime compatibility / capability gating | PARTIAL | SESSION: SolidWorksCompatibility | G0 | P0 | E05 | Year classification exists; no per-operation execution enforcement. |
| DOC-030 | Workspace path / overwrite control | AUTOMATED TESTED | Core WorkspacePolicy / Host approval | G0 | P0 | E03 | Traversal/reparse/extension checks; .sldprt only. Historical tests; no fresh suite claim. |
| DOC-031 | Verified immutable artifact publication | PARTIAL | HOST: AgentRoutes.ArtifactAsync; manifest absent | G2, G4 | P0 | E11 | Open-document download works for the tested saved part, but no immutable revision manifest exists; G5 subsequently adds crash reconciliation. |
| DOC-032 | Document configuration / dirty state tracking | PLANNED | IModelDoc2 / IConfigurationManager | G1, G2 | P0 | — | Needed for safe edits and external-change detection. |
| DOC-033 | Manual-control / automation mutation lease | PLANNED | Host + RemoteAgent interlock | G1, G2 | P0 | — | Remote input currently independent of CAD execution gate. |
| DOC-034 | Pack and Go / referenced-document packaging | UNSUPPORTED | IPackAndGo | G1, G6, ASM-001 | P3 | — | Important omitted workflow: portable assemblies and drawings. |
| DOC-035 | Document events / external mutation detection | PLANNED | SOLIDWORKS event adapters + snapshot stamps | G1, G2 | P0 | — | Combine events with preflight validation; do not trust events alone. |
| DOC-036 | Stamp and validate identity of a new Agent-managed part | PARTIAL | Host `JobCoordinator` + Bridge `ICustomPropertyManager.Add3/Get6` + SQLite `ManagedModels` | G0, DOC-028 | P0 | E15,E17 | Managed NewPart/save/reopen/hash and manual-copy model-ID collision rejection are native verified on SOLIDWORKS 2020 SP0.0. Move, changed-hash and missing-property rejection still need native acceptance. |

## 2D sketch geometry

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| SK-001 | Create line | REAL SOLIDWORKS VERIFIED | SK-H: CreateLine | Active origin-plane sketch | P1 | E06 | Native semicircle fixture; explicit endpoints, no returned durable entity ID. |
| SK-002 | Create circle | REAL SOLIDWORKS VERIFIED | SK-H: CreateCircleByRadius | Active origin-plane sketch | P1 | E01 | Centred circular through-hole fixture; no driving diameter dimension. |
| SK-003 | Create centre rectangle | REAL SOLIDWORKS VERIFIED | SK-H: CreateCenterRectangle | Active origin-plane sketch | P1 | E01 | Construction diagonals exist; explicit managed dimensions/relations absent. |
| SK-004 | Create centre/start/end arc | REAL SOLIDWORKS VERIFIED | SK-H: CreateArc | Active origin-plane sketch | P1 | E06 | Semicircle verified; asymmetric clockwise/counterclockwise certification still needed. |
| SK-005 | Create rotated straight obround slot | REAL SOLIDWORKS VERIFIED | PROFILE-H: lines + arcs | SK-001, SK-004 | P1 | E02 | Total tip-to-tip length; rotated native fixture. Curved slots unsupported. |
| SK-006 | Create regular polygon | REAL SOLIDWORKS VERIFIED | PROFILE-H: line decomposition | SK-001 | P1 | E02 | 3–32 sides validated; circumcircle diameter; hexagon scenarios verified. |
| SK-007 | Centre line | PLANNED | ISketchManager / ISketchSegment | G1, G3 | P1 | — | Explicit construction flag and semantic axis. |
| SK-008 | General construction geometry | PLANNED | ISketchSegment.ConstructionGeometry | G1, G3 | P1 | — | Rectangle's incidental construction lines are not this capability. |
| SK-009 | Corner / rotated rectangle variants | PLANNED | ISketchManager / ISketchSegment | G1, G3 | P1 | — | Separate parameters and constraints, not inferred from centre rectangle. |
| SK-010 | Ellipse / elliptical arc | UNSUPPORTED | ISketchManager / ISketchSegment | G0, G1, G3 | P1 | — | No exposed implementation found. |
| SK-011 | Spline / control polygon | UNSUPPORTED | ISketchManager / ISketchSegment | G0, G1, G3 | P3 | — | No exposed implementation found. |
| SK-012 | Sketch point | PLANNED | ISketchManager / ISketchSegment | G0, G1, G3 | P1 | — | No exposed implementation found. |
| SK-013 | Trim entities | UNSUPPORTED | ISketchManager / ISketchSegment | G1, G2, G3, G5 | P2 | — | Topology-changing edit; rebind entity IDs. |
| SK-014 | Extend entities | UNSUPPORTED | ISketchManager / ISketchSegment | G1, G2, G3, G5 | P2 | — | No exposed implementation found. |
| SK-015 | Offset entities / chain | PLANNED | ISketchManager / ISketchSegment | G1, G2, G3 | P2 | — | Distance direction, closure and self-intersection checks. |
| SK-016 | Convert model edges into sketch | PLANNED | ISketchManager / ISketchSegment | G1, G2, G3 | P2 | — | Native external reference and dependency provenance. |
| SK-017 | Mirror sketch entities | PLANNED | ISketchManager / ISketchSegment | G1, G3 | P2 | — | Explicit centreline and relation preservation. |
| SK-018 | Linear sketch pattern | PLANNED | ISketchManager / ISketchSegment | G1, G3 | P2 | — | Seed IDs, count and pitch parameters. |
| SK-019 | Circular sketch pattern | PLANNED | ISketchManager / ISketchSegment | G1, G3 | P2 | — | Centre/axis, angle and count parameters. |
| SK-020 | Sketch text | UNSUPPORTED | ISketchManager / ISketchSegment | G0, G1, G3 | P3 | — | Fonts and text availability affect reproducibility. |
| SK-021 | Curved / centrepoint-arc slots | UNSUPPORTED | ISketchManager / ISketchSegment | SK-004, G3 | P2 | — | No exposed implementation found. |
| SK-022 | Sketch blocks / reusable profiles | UNSUPPORTED | ISketchBlockDefinition / ISketchBlockInstance | G1, G3 | P3 | — | Important omitted reusable sketch domain. |
| SK-023 | Sketch fillet / chamfer | UNSUPPORTED | ISketchManager / ISketchSegment | G1, G2, G3 | P2 | — | Distinct from solid edge finishing. |
| SK-024 | Contour closure / self-intersection diagnosis | PLANNED | Core profile topology + native sketch checks | G1, G2, G3 | P1 | — | Endpoint coincidence in coordinates is insufficient solver evidence. |
| SK-025 | Enter / exit origin-plane sketch | REAL SOLIDWORKS VERIFIED | SK-H: InsertSketch | Owned part / named plane | P1 | E01,E06 | Three plane names allowed; historical geometry evidence mostly Top Plane. |
| SK-026 | Sketch pictures as modelling references | UNSUPPORTED | ISketchPicture | G1, G3 | P3 | — | Different from PNG/JPEG design intake. |

## Sketch constraints

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| SC-001 | Horizontal relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-002 | Vertical relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-003 | Coincident relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-004 | Concentric relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-005 | Tangent relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-006 | Parallel relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-007 | Perpendicular relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-008 | Equal relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-009 | Collinear relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-010 | Midpoint relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-011 | Symmetric relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | No exposed implementation found. |
| SC-012 | Fixed relation | PLANNED | ISketchRelationManager / ISketchRelation | G1, G2, G3; SK-001/002 | P1 | — | Use only when intended; do not freeze every entity to fake parametric design. |
| SC-013 | Fully define supported sketch | PLANNED | ISketchManager.FullyDefineSketch + intent constraints | SC-001/002/003, DIM-001 | P1 | — | Native helper requires solve-state/readback validation. |
| SC-014 | Detect under-defined sketch | PLANNED | ISketch solve status / observation | G2, G3 | P1 | — | No exposed implementation found. |
| SC-015 | Detect over-defined sketch | PLANNED | ISketch solve status / observation | G2, G3 | P1 | — | No exposed implementation found. |
| SC-016 | Diagnose conflicting relations | PLANNED | Relation graph + native solve feedback | SC-014/015 | P1 | — | Return conflicting IDs; no unsupported promise of full solver explanations. |
| SC-017 | Read/edit/delete a relation | PLANNED | ISketchRelationManager | G1, G2, G3, G5 | P1 | — | Preserve other constraints and dependent features. |

## Sketch dimensions and equations

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| DIM-001 | Linear driving dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | No exposed implementation found. |
| DIM-002 | Horizontal dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | No exposed implementation found. |
| DIM-003 | Vertical dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | No exposed implementation found. |
| DIM-004 | Angular dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | Explicit angle units and orientation. |
| DIM-005 | Radius dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | No exposed implementation found. |
| DIM-006 | Diameter dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | Input circle diameter currently sets geometry, not a managed dimension. |
| DIM-007 | Driven / reference dimension | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | Driving and driven values require different authority. |
| DIM-008 | Driving / driven state management | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G2, G3 | P1 | — | No exposed implementation found. |
| DIM-009 | Stable semantic dimension names | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1, G3 | P1 | — | Native display name is metadata; application parameter ID is identity. |
| DIM-010 | Edit dimension and regenerate | PLANNED | IDimension / IDisplayDimension / IModelDoc2 | G1–G5 | P1 | — | Read back actual value and downstream result. |
| DIM-011 | Equations | PLANNED | IEquationMgr | G1, G3, G5 | P1 | — | Restricted typed expressions, units and cycle checks. |
| DIM-012 | Global variables | PLANNED | IEquationMgr | G1, G3 | P1 | — | Scope/configuration and dependencies explicit. |
| DIM-013 | Dimension tolerances | UNSUPPORTED | IDimensionTolerance | G1, G3 | P1 | — | Manufacturing intent beyond nominal geometry. |
| DIM-014 | Ordinate / baseline dimensions | UNSUPPORTED | IDimension / IDisplayDimension / IModelDoc2 | G1, G3 | P1 | — | Distinct from linear dimension creation. |

## Reference geometry and coordinate systems

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| REF-001 | Create offset / angled / datum plane | PLANNED | IFeatureManager / reference feature data | G0, G1, G2 | P1 | — | No exposed implementation found. |
| REF-002 | Reference axis | PLANNED | IFeatureManager / reference feature data | G0, G1, G2 | P1 | — | No exposed implementation found. |
| REF-003 | Reference point | PLANNED | IFeatureManager / reference feature data | G0, G1, G2 | P1 | — | No exposed implementation found. |
| REF-004 | Coordinate system | PLANNED | IFeatureManager / reference feature data | G0, G1, G2 | P1 | — | Handedness, units and transforms persisted. |
| REF-005 | Sketch on origin plane | REAL SOLIDWORKS VERIFIED | SK-H / SelectByID2 | DOC-001 | P1 | E01,E06 | English plane names; see SK-025. |
| REF-006 | Sketch on unique planar body face | PLANNED | WIP draft validator only; command not registered or advertised | G1, G2 | P1 | — | Face selector, frame transform, context lifecycle and primitive mapping remain incomplete; no execution claim. |
| REF-007 | 3D sketch | PLANNED | ISketchManager / 3D sketch mode | G1, G3 | P2 | — | Separate 3D entity/constraint semantics. |
| REF-008 | Derived sketch / reference geometry | UNSUPPORTED | IFeatureManager / reference feature data | G1, G3, G6 | P3 | — | Source identity and update policy. |
| REF-009 | Arbitrary-plane sketch frame transforms | PLANNED | ISketch.ModelToSketchTransform / IMathUtility | G1, G2 | P1 | — | All-axis, rotated, mirrored and translated tests. |
| REF-010 | Projected / composite curves | UNSUPPORTED | IFeatureManager / curve feature data | G1, G3 | P2 | — | Unlock sweep/loft guides. |
| REF-011 | Helix / spiral | PLANNED | IFeatureManager / helix data | REF-002, G3 | P2 | — | Pitch/height/handedness; prerequisite for spring/thread-like sweeps. |

## Core solid modelling

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| FT-001 | Blind boss extrude | REAL SOLIDWORKS VERIFIED | FT-H: FeatureExtrusion2; Bridge `VersionTwoExtrudeCommandHandler` | Closed sketch exited; v2 requires managed sketch/model/job IDs | P1 | E01,E02,E06,E19,E23 | Version-1 current-selection and a direct version-2 managed-sketch 5 mm boss are separately native verified. V2 Host plan approval/execution and managed depth edit remain unavailable. |
| FT-002 | Through-all cut extrude | REAL SOLIDWORKS VERIFIED | FT-H: FeatureCut4 | Body + closed sketch | P1 | E01,E02,E19 | Version-1 fixed direction/current selection; extra reference parameters remain rejected. No v2 explicit-profile or arbitrary opening-side support. |
| FT-003 | Blind cut / pocket | REAL SOLIDWORKS VERIFIED | FT-H: FeatureCut4 | Body + closed sketch | P1 | E02 | Origin-plane underside pocket 3 mm scenario; face-based directions not implemented. |
| FT-004 | Mid-plane / reversed / up-to-surface extrusion | PLANNED | Extrude feature data | G1–G4 | P2 | — | Separate scope from blind boss. |
| FT-005 | Thin-feature extrusion | PLANNED | Extrude feature data | G1–G4 | P2 | — | Wall thickness and open-profile intent. |
| FT-010 | Revolve boss | PLANNED | IFeatureManager / revolve data | REF-002, G3, G4 | P2 | — | Axis and closed profile explicit. |
| FT-011 | Revolved cut | PLANNED | Revolve feature data | FT-010, solid body | P2 | — | No exposed implementation found. |
| FT-020 | Sweep boss | PLANNED | Sweep feature data | REF-010, G3, G4 | P3 | — | Profile/path IDs and twist/orientation. |
| FT-021 | Swept cut | PLANNED | Sweep feature data | FT-020, solid body | P3 | — | No exposed implementation found. |
| FT-022 | Loft boss | PLANNED | Loft feature data | REF-001, G3, G4 | P3 | — | Ordered sections, guides and correspondence. |
| FT-023 | Lofted cut | PLANNED | Loft feature data | FT-022, solid body | P3 | — | No exposed implementation found. |
| FT-024 | Boundary boss / cut | UNSUPPORTED | Boundary feature data | REF-010, G3, G4 | P3 | — | No exposed implementation found. |
| FT-030 | Plain drilled hole by sketch/cut | REAL SOLIDWORKS VERIFIED | SK-H + FT-H | SK-002, FT-002 | P1 | E01 | Centred through-hole fixture; no semantic hole feature metadata. |
| FT-031 | Hole Wizard standard holes | PLANNED | IFeatureManager HoleWizard / hole data | G1–G4 | P2 | — | Standard/type/size/thread and placement schema required. |
| FT-032 | Tapped / cosmetic thread definition | UNSUPPORTED | Hole/thread feature data | FT-031 | P2 | — | Manufacturing thread designation distinct from helical geometry. |
| FT-040 | Uniform fillet on selected face outer edges | PLANNED | Excluded WIP handler draft: FeatureFillet3 | G1, G2, G4 | P1 | — | Planner and executor do not expose this command; semantic face/edge resolver and native evidence are absent. |
| FT-041 | Equal-leg 45-degree chamfer on face edges | PLANNED | Excluded WIP handler draft: InsertFeatureChamfer | G1, G2, G4 | P1 | — | Planner and executor do not expose this command; semantic face/edge resolver and native evidence are absent. |
| FT-042 | General/variable-radius/face/full-round fillets | UNSUPPORTED | Fillet feature data | FT-040, G1, G4 | P3 | — | Certify separate variants; do not inherit simple-fillet status. |
| FT-043 | General chamfer variants | UNSUPPORTED | Chamfer feature data | FT-041, G1, G4 | P3 | — | No exposed implementation found. |
| FT-050 | Shell / wall thickness | PLANNED | Shell feature data | G1–G5 | P2 | — | Remove-face references, inside/outside and thickness validation. |
| FT-051 | Draft | PLANNED | Draft feature data | REF-001/002, G1–G4 | P2 | — | No exposed implementation found. |
| FT-052 | Rib | PLANNED | Rib feature data | G1–G4 | P2 | — | No exposed implementation found. |
| FT-053 | Wrap | UNSUPPORTED | Wrap feature data | G1–G4 | P3 | — | No exposed implementation found. |
| FT-054 | Dome | UNSUPPORTED | Dome feature data | G1–G4 | P3 | — | No exposed implementation found. |
| FT-055 | Indent | UNSUPPORTED | Indent feature data | FT-064, G1–G4 | P3 | — | No exposed implementation found. |
| FT-056 | Flex / deform | UNSUPPORTED | Flex/deform feature data | G1–G4 | P3 | — | Useful only with explicit deformation intent and geometric checks. |
| FT-060 | Combine bodies / union | PLANNED | Combine feature data | FT-064, G1, G4 | P2 | — | No exposed implementation found. |
| FT-061 | Split bodies | PLANNED | Split feature data | FT-064, G1, G4 | P2 | — | No exposed implementation found. |
| FT-062 | Move/copy bodies | PLANNED | Move/copy body feature data | FT-064, G1, G4 | P2 | — | No exposed implementation found. |
| FT-063 | Delete/keep bodies | PLANNED | Delete body feature data | FT-064, G1, G4, G5 | P2 | — | No exposed implementation found. |
| FT-064 | Create/manage multibody parts | PLANNED | IPartDoc / IBody2 / feature scope | G1–G4 | P2 | — | Current runtime verifier demands one visible solid body. |
| FT-065 | Boolean subtract/intersect | PLANNED | Combine/body operations | FT-064, G1, G4 | P2 | — | Prefer native editable feature when possible. |
| FT-066 | Insert/derive part and save bodies | UNSUPPORTED | IPartDoc / save-body features | G1, G5, G6 | P3 | — | Linked-file provenance and update policy. |

## Patterns and replication

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| PAT-001 | Linear feature pattern | PLANNED | IFeatureManager / pattern feature data | G1, G3, G4, G5 | P2 | — | Seed ID, direction, count, spacing and resulting instances. |
| PAT-002 | Circular feature pattern | PLANNED | IFeatureManager / pattern feature data | REF-002, G1, G3, G4 | P2 | — | Axis, angle and count preserve intended pattern semantics. |
| PAT-003 | Sketch-driven pattern | PLANNED | IFeatureManager / pattern feature data | G1, G3, G4; sketch points | P2 | — | No exposed implementation found. |
| PAT-004 | Curve-driven pattern | UNSUPPORTED | IFeatureManager / pattern feature data | REF-010, G1, G3, G4 | P2 | — | No exposed implementation found. |
| PAT-005 | Table-driven pattern | UNSUPPORTED | IFeatureManager / pattern feature data | G1, G3, G4, G5 | P2 | — | External table provenance and coordinate units. |
| PAT-006 | Variable pattern | UNSUPPORTED | IFeatureManager / pattern feature data | G1, G3, G4, G5 | P2 | — | Per-instance parameter changes. |
| PAT-007 | Mirror feature | PLANNED | IFeatureManager / pattern feature data | REF-001, G1, G3, G4 | P2 | — | No exposed implementation found. |
| PAT-008 | Mirror body | PLANNED | IFeatureManager / pattern feature data | FT-064, REF-001, G4 | P2 | — | No exposed implementation found. |
| PAT-009 | Pattern bodies | PLANNED | IFeatureManager / pattern feature data | FT-064, G1, G3, G4 | P2 | — | No exposed implementation found. |
| PAT-010 | Component patterns | PLANNED | IAssemblyDoc / component pattern data | ASM-001/002, G6 | P2 | — | See ASM-019; occurrence identities required. |
| PAT-011 | Pattern count / pitch edit | PLANNED | IFeatureManager / pattern feature data | PAT-001/002, EDIT-004 | P2 | — | “Four holes to six” edits pattern definition, not six unrelated cuts. |

## Surface modelling

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| SUR-001 | Extruded surface | PLANNED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-002 | Revolved surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | REF-002, G1–G4 | P3 | — | No exposed implementation found. |
| SUR-003 | Swept surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | REF-010, G1–G4 | P3 | — | No exposed implementation found. |
| SUR-004 | Lofted surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | REF-001/010, G1–G4 | P3 | — | No exposed implementation found. |
| SUR-005 | Boundary surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-006 | Planar surface | PLANNED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-007 | Offset surface | PLANNED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-008 | Ruled surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-009 | Filled surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-010 | Trim surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-011 | Extend surface | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | No exposed implementation found. |
| SUR-012 | Knit surfaces | PLANNED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | Closure/gaps/tolerance and resulting body type. |
| SUR-013 | Untrim surface where available | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1–G4; surface-aware observation | P3 | — | Confirm baseline version and supported topology. |
| SUR-014 | Thicken surface | PLANNED | IFeatureManager / surface feature data / ISurface | SUR-001/006/012, G4 | P3 | — | Solid/surface conversion evidence. |
| SUR-015 | Replace face | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1, G2, G4, G5 | P3 | — | Also DE-004; shared operation. |
| SUR-016 | Delete face / patch / fill | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | G1, G2, G4, G5 | P3 | — | Also DE-003; distinguish variants. |
| SUR-017 | Surface/solid conversion workflow | PLANNED | IFeatureManager / surface feature data / ISurface | SUR-012/014, FT-064 | P3 | — | Cannot use current solid-only verifier unchanged. |
| SUR-018 | Intersection curves / section geometry | UNSUPPORTED | IFeatureManager / surface feature data / ISurface | REF-010, G1, G2 | P3 | — | Useful omitted model-analysis primitive. |

## Direct editing

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| DE-001 | Move face | PLANNED | IFeatureManager / direct-edit feature data | G1, G2, G4, G5 | P2 | — | Imported geometry and downstream topology risks. |
| DE-002 | Offset face | PLANNED | IFeatureManager / direct-edit feature data | G1, G2, G4, G5 | P2 | — | Explicit sign/frame and wall checks. |
| DE-003 | Delete face | UNSUPPORTED | IFeatureManager / direct-edit feature data | G1, G2, G4, G5 | P2 | — | Shared with SUR-016. |
| DE-004 | Replace face | UNSUPPORTED | IFeatureManager / direct-edit feature data | G1, G2, G4, G5 | P2 | — | Shared with SUR-015. |
| DE-005 | Scale bodies | UNSUPPORTED | IFeatureManager / direct-edit feature data | G1, G2, G4, G5 | P2 | — | Units, centroid/origin and native feature policy. |
| DE-006 | Move body | PLANNED | IFeatureManager / direct-edit feature data | FT-062 | P2 | — | Shared implementation with FT-062. |
| DE-007 | Copy body | PLANNED | IFeatureManager / direct-edit feature data | FT-062 | P2 | — | Distinct output body IDs. |
| DE-008 | Suppress/unsuppress feature | PLANNED | IFeatureManager / direct-edit feature data | EDIT-005/006 | P2 | — | Configuration-scoped native state. |
| DE-009 | Edit feature parameters | PLANNED | IFeatureManager / direct-edit feature data | EDIT-004 | P2 | — | Prefer native feature definitions. |
| DE-010 | Edit dimensions | PLANNED | IFeatureManager / direct-edit feature data | DIM-010 | P2 | — | Preserve driven/driving semantics. |
| DE-011 | Edit sketch geometry | PLANNED | IFeatureManager / direct-edit feature data | EDIT-002 | P2 | — | Constraint-aware edit. |
| DE-012 | Replace feature/entity references | PLANNED | IFeatureManager / direct-edit feature data | EDIT-008 | P2 | — | Ambiguous replacements require clarification. |

## Configurations and parametric families

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| CFG-001 | Create/select/list configurations | PLANNED | IConfigurationManager / IConfiguration / IEquationMgr | G1, G3, G5 | P2 | — | No exposed implementation found. |
| CFG-002 | Derived configurations | PLANNED | IConfigurationManager / IConfiguration / IEquationMgr | CFG-001 | P2 | — | No exposed implementation found. |
| CFG-003 | Configuration-specific dimensions | PLANNED | IConfigurationManager / IConfiguration / IEquationMgr | CFG-001, DIM-010 | P2 | — | No exposed implementation found. |
| CFG-004 | Configuration-specific suppression | PLANNED | IConfigurationManager / IConfiguration / IEquationMgr | CFG-001, EDIT-005/006 | P2 | — | No exposed implementation found. |
| CFG-005 | Configuration-specific properties | PLANNED | IConfigurationManager / IConfiguration / IEquationMgr | CFG-001, DOC-021 | P2 | — | No exposed implementation found. |
| CFG-006 | Configuration equations | PLANNED | IEquationMgr | CFG-001, DIM-011 | P2 | — | No exposed implementation found. |
| CFG-007 | Configuration global variables | PLANNED | IEquationMgr | CFG-001, DIM-012 | P2 | — | No exposed implementation found. |
| CFG-008 | Design tables | UNSUPPORTED | IDesignTable | CFG-001/003/004 | P2 | — | Spreadsheet dependency, evaluation and reproducible sources. |
| CFG-009 | Family regeneration / validation | PLANNED | Host family runner + verifiers | CFG-001/003, G4 | P2 | — | Validate every generated configuration; no cross-configuration success inference. |

## Assemblies

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| ASM-001 | Create assembly document | PLANNED | ISldWorks.NewDocument / IAssemblyDoc | G0–G6 | P2 | — | Same document capability as DOC-002. |
| ASM-002 | Insert component | PLANNED | IAssemblyDoc / IComponent2 | ASM-001 | P2 | — | Document and occurrence IDs differ. |
| ASM-003 | Create component in context | UNSUPPORTED | IAssemblyDoc / IComponent2 / mate feature data | ASM-001/002, external-reference policy | P2 | — | No exposed implementation found. |
| ASM-004 | Fix component | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-002 | P2 | — | No exposed implementation found. |
| ASM-005 | Float component | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-002 | P2 | — | No exposed implementation found. |
| ASM-006 | Apply/inspect component transforms | PLANNED | IComponent2.Transform2 / IMathTransform | ASM-002, REF-004 | P2 | — | Assembly/part frame composition. |
| ASM-007 | Coincident mate | PLANNED | Mate feature data | ASM-002, G1, G4 | P2 | — | No exposed implementation found. |
| ASM-008 | Concentric mate | PLANNED | Mate feature data | ASM-002, G1, G4 | P2 | — | No exposed implementation found. |
| ASM-009 | Distance mate | PLANNED | Mate feature data | ASM-002, DIM-001 | P2 | — | No exposed implementation found. |
| ASM-010 | Angle mate | PLANNED | Mate feature data | ASM-002, DIM-004 | P2 | — | No exposed implementation found. |
| ASM-011 | Parallel mate | PLANNED | Mate feature data | ASM-002, G1 | P2 | — | No exposed implementation found. |
| ASM-012 | Perpendicular mate | PLANNED | Mate feature data | ASM-002, G1 | P2 | — | No exposed implementation found. |
| ASM-013 | Tangent mate | PLANNED | Mate feature data | ASM-002, G1 | P2 | — | No exposed implementation found. |
| ASM-014 | Advanced mates | UNSUPPORTED | Advanced mate data | ASM-007/008/017 | P2 | — | Split width/symmetric/path/limit variants before implementation. |
| ASM-015 | Mechanical mates | UNSUPPORTED | Mechanical mate data | ASM-007/008/017 | P2 | — | Gear/rack/screw/cam variants need separate semantics and tests. |
| ASM-016 | Edit mate | PLANNED | IFeature.ModifyDefinition / mate data | ASM-007/008, G5 | P2 | — | No exposed implementation found. |
| ASM-017 | Diagnose mate failures / degrees of freedom | PLANNED | Mate/component observation | ASM-007/008, G2, G4 | P2 | — | Under-/over-constraint and missing reference evidence. |
| ASM-018 | Subassemblies / flexible vs rigid | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-001/002/006 | P2 | — | Nested occurrence identity and transform stack. |
| ASM-019 | Component patterns | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-002/006, PAT-010 | P2 | — | No exposed implementation found. |
| ASM-020 | Mirror components | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-002/006, REF-001 | P2 | — | Handedness and new/derived part policy. |
| ASM-021 | Assembly features | UNSUPPORTED | IAssemblyDoc / IComponent2 / mate feature data | ASM-001/002, G3, G4 | P2 | — | Affected component scope explicit. |
| ASM-022 | Assembly configurations | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | CFG-001, ASM-002 | P2 | — | No exposed implementation found. |
| ASM-023 | Top-down design | UNSUPPORTED | IAssemblyDoc / IComponent2 / mate feature data | ASM-003/024 | P2 | — | Cross-document dependency and update policy. |
| ASM-024 | Inspect/manage external references | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | G1, G2, G5, ASM-002 | P2 | — | Prevent uncontrolled external updates. |
| ASM-025 | Replace component | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-002/016/024, G5 | P2 | — | Rebind mates; fail ambiguous references. |
| ASM-026 | Suppress/unsuppress component | PLANNED | IAssemblyDoc / IComponent2 / mate feature data | ASM-002/022, G5 | P2 | — | No exposed implementation found. |
| ASM-027 | Interference detection | PLANNED | IAssemblyDoc interference APIs | ASM-002/006, G4 | P2 | — | Ignore intentional overlaps only via explicit rule. |
| ASM-028 | Clearance checking | PLANNED | Assembly measurement / clearance APIs | ASM-002/006, DOC-026 | P2 | — | No exposed implementation found. |
| ASM-029 | Exploded views | UNSUPPORTED | Configuration/explode data | ASM-002/022 | P2 | — | No exposed implementation found. |
| ASM-030 | BOM information extraction | PLANNED | IComponent2 / configurations / custom properties | ASM-002, DOC-021 | P2 | — | Quantity/configuration/part number; drawing BOM is DRW-019. |
| ASM-031 | Lightweight/resolved/suppressed loading states | PLANNED | IComponent2 / IAssemblyDoc | ASM-002, G2 | P2 | — | Important omitted prerequisite for reliable assembly inspection. |
| ASM-032 | Large assembly / SpeedPak policy | UNSUPPORTED | Assembly/configuration interfaces | ASM-031 | P2 | — | Performance and observation coverage, not core first slice. |

## Drawings

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| DRW-001 | Create drawing from model | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DOC-003, G6 | P2 | — | Explicit source model/configuration and update state. |
| DRW-002 | Sheets / sheet formats | PLANNED | ISheet | DRW-001 | P2 | — | No exposed implementation found. |
| DRW-003 | Drawing templates | PLANNED | ISldWorks.NewDocument / ISheet | DOC-005, DRW-001 | P2 | — | No exposed implementation found. |
| DRW-004 | Standard model views | PLANNED | IDrawingDoc / IView | DRW-001 | P2 | — | Orientation, projection standard and scale. |
| DRW-005 | Projected views | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004 | P2 | — | No exposed implementation found. |
| DRW-006 | Section views | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-007 | Detail views | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-008 | Auxiliary views | UNSUPPORTED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-009 | Broken views | UNSUPPORTED | IDrawingDoc / IView / annotation and table interfaces | DRW-004 | P2 | — | No exposed implementation found. |
| DRW-010 | Drawing dimensions | PLANNED | IDisplayDimension / annotations | DRW-004, G1, DIM-001 | P2 | — | Drawing dimensions do not imply sketch driving dimensions. |
| DRW-011 | Import model items | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, DIM-001 | P2 | — | No exposed implementation found. |
| DRW-012 | Hole callouts | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, FT-031 | P2 | — | Thread/standard metadata required for standard callouts. |
| DRW-013 | Centre marks | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-014 | Centre lines | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-015 | Notes | PLANNED | INote / IAnnotation | DRW-004 | P2 | — | No exposed implementation found. |
| DRW-016 | Balloons | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-004, ASM-030 | P2 | — | No exposed implementation found. |
| DRW-017 | Auto-balloon | PLANNED | IDrawingDoc / IView / annotation and table interfaces | DRW-016, ASM-030 | P2 | — | No exposed implementation found. |
| DRW-018 | Revision tables | UNSUPPORTED | IRevisionTableAnnotation | DRW-002 | P2 | — | No exposed implementation found. |
| DRW-019 | BOM table | PLANNED | IBomTableAnnotation | DRW-004, ASM-030 | P2 | — | No exposed implementation found. |
| DRW-020 | General tables | UNSUPPORTED | ITableAnnotation | DRW-002 | P2 | — | No exposed implementation found. |
| DRW-021 | GD&T | UNSUPPORTED | IGtol / annotations | DRW-004, G1 | P2 | — | Standards, datum relationships and human review. |
| DRW-022 | Datum symbols | UNSUPPORTED | Datum annotation interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-023 | Surface finish symbols | UNSUPPORTED | Surface finish annotation interfaces | DRW-004, G1 | P2 | — | No exposed implementation found. |
| DRW-024 | Weld symbols | UNSUPPORTED | Weld symbol interfaces | DRW-004, WLD-001 | P2 | — | No exposed implementation found. |
| DRW-025 | Title block / property links | PLANNED | INote / custom property links | DRW-003, DOC-021 | P2 | — | No exposed implementation found. |
| DRW-026 | PDF output | PLANNED | IExportPdfData / SaveAs | DRW-001, G6 | P2 | — | Sheet selection and post-export checks. |
| DRW-027 | DXF output | PLANNED | Drawing SaveAs/export options | DRW-001, G6 | P2 | — | No exposed implementation found. |
| DRW-028 | DWG output | PLANNED | Drawing SaveAs/export options | DRW-001, G6 | P2 | — | No exposed implementation found. |
| DRW-029 | Update/dangling annotation diagnosis | PLANNED | IView / IAnnotation | DRW-004/010, G2, G4 | P2 | — | Important omitted drawing-modification prerequisite. |
| DRW-030 | Projection standard / scale / sheet units | PLANNED | ISheet / IView | DRW-001 | P2 | — | First-angle vs third-angle must be explicit. |

## Sheet metal

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| SM-001 | Base flange / tab | PLANNED | IFeatureManager / sheet-metal feature data | G1–G5; thickness and bend-rule model | P2 | — | No exposed implementation found. |
| SM-002 | Convert solid to sheet metal | PLANNED | IFeatureManager / sheet-metal feature data | FT-064, G1–G5 | P2 | — | No exposed implementation found. |
| SM-003 | Edge flange | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, G1 | P2 | — | No exposed implementation found. |
| SM-004 | Miter flange | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, G3 | P2 | — | No exposed implementation found. |
| SM-005 | Hem | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, G1 | P2 | — | No exposed implementation found. |
| SM-006 | Jog | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, G3 | P2 | — | No exposed implementation found. |
| SM-007 | Sketched bend | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, G3 | P2 | — | No exposed implementation found. |
| SM-008 | Lofted bend | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, FT-022 | P2 | — | No exposed implementation found. |
| SM-009 | Swept flange | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, FT-020 | P2 | — | No exposed implementation found. |
| SM-010 | Rip | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, G1 | P2 | — | No exposed implementation found. |
| SM-011 | Closed corner | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-003, G1 | P2 | — | No exposed implementation found. |
| SM-012 | Corner relief | PLANNED | IFeatureManager / sheet-metal feature data | SM-003, G1 | P2 | — | No exposed implementation found. |
| SM-013 | Break corner | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, G1 | P2 | — | No exposed implementation found. |
| SM-014 | Normal cut | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, FT-003 | P2 | — | No exposed implementation found. |
| SM-015 | Unfold | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, G1 | P2 | — | No exposed implementation found. |
| SM-016 | Fold | PLANNED | IFeatureManager / sheet-metal feature data | SM-015 | P2 | — | No exposed implementation found. |
| SM-017 | Flatten / flat-pattern state | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, G4 | P2 | — | Verify developed shape and folded/flat configuration association. |
| SM-018 | Forming tools | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, reusable tool definitions | P2 | — | No exposed implementation found. |
| SM-019 | Tab and slot | UNSUPPORTED | IFeatureManager / sheet-metal feature data | SM-001, G1 | P2 | — | Version/support options require baseline certification. |
| SM-020 | Bend radius parameter | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, DIM-010 | P2 | — | No exposed implementation found. |
| SM-021 | K-factor | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, G3 | P2 | — | Manufacturing input, not inferred from image. |
| SM-022 | Bend allowance / deduction / bend tables | PLANNED | IFeatureManager / sheet-metal feature data | SM-001, G3 | P2 | — | Keep rule provenance and units. |
| SM-023 | Flat-pattern DXF/DWG export | PLANNED | IPartDoc.ExportToDWG2 family | SM-017, G6 | P2 | — | Layer/scale/bend-line policy; independent flat geometry checks. |
| SM-024 | Gauge tables / material thickness rules | UNSUPPORTED | Sheet-metal data and table resources | SM-001, DOC-022 | P2 | — | Important omitted manufacturing configuration. |

## Weldments and structural modelling

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| WLD-001 | Create weldment | PLANNED | IFeatureManager / structural-member / body-folder data | FT-064 | P2 | — | No exposed implementation found. |
| WLD-002 | 2D layout sketch | PLANNED | IFeatureManager / structural-member / body-folder data | WLD-001, G3 | P2 | — | Native 2D primitive support alone is not a weldment layout workflow. |
| WLD-003 | 3D layout sketch | PLANNED | IFeatureManager / structural-member / body-folder data | WLD-001, REF-007 | P2 | — | No exposed implementation found. |
| WLD-004 | Structural members | PLANNED | IFeatureManager / structural-member / body-folder data | WLD-001/002, G1 | P2 | — | No exposed implementation found. |
| WLD-005 | Member groups | PLANNED | IFeatureManager / structural-member / body-folder data | WLD-004 | P2 | — | Group/corner treatment and profile orientation. |
| WLD-006 | Trim/extend structural members | PLANNED | IFeatureManager / structural-member / body-folder data | WLD-004, G1, G4 | P2 | — | No exposed implementation found. |
| WLD-007 | Gussets | UNSUPPORTED | IFeatureManager / structural-member / body-folder data | WLD-004, G1 | P2 | — | No exposed implementation found. |
| WLD-008 | End caps | UNSUPPORTED | IFeatureManager / structural-member / body-folder data | WLD-004, G1 | P2 | — | No exposed implementation found. |
| WLD-009 | Weld beads / cosmetic welds | UNSUPPORTED | IFeatureManager / structural-member / body-folder data | WLD-004 | P2 | — | Physical bead geometry vs annotation explicit. |
| WLD-010 | Cut lists | PLANNED | IBodyFolder / custom properties | WLD-004, DOC-021 | P2 | — | Update/rebuild and per-body identity. |
| WLD-011 | Custom profiles / profile library | PLANNED | Library profile resource + member data | WLD-004, G3 | P2 | — | File hash, units and orientation. |
| WLD-012 | As-welded / as-machined states | UNSUPPORTED | IFeatureManager / structural-member / body-folder data | WLD-001, CFG-001 | P2 | — | No exposed implementation found. |
| WLD-013 | Weldment drawings | PLANNED | IFeatureManager / structural-member / body-folder data | WLD-010, DRW-001 | P2 | — | No exposed implementation found. |

## Import and export

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| IO-001 | Native part open/save | REAL SOLIDWORKS VERIFIED | FILE-H | DOC-009/010/011 | P2 | E01,E02 | Only .sldprt is currently permitted. |
| IO-002 | Native assembly open/save | PLANNED | IAssemblyDoc / file adapter | ASM-001, G6 | P2 | — | No exposed implementation found. |
| IO-003 | Native drawing open/save | PLANNED | IDrawingDoc / file adapter | DRW-001, G6 | P2 | — | No exposed implementation found. |
| IO-004 | STEP import | PLANNED | Format-specific import data | G1, G2, G4, G6 | P2 | — | Units/body diagnostics; imported solid does not recover parametric intent. |
| IO-005 | STEP export | PLANNED | SaveAs / export options | G4, G6 | P2 | — | Schema and units; topology/round-trip checks. |
| IO-006 | Parasolid import/export | PLANNED | Native kernel-format adapter | G4, G6 | P2 | — | Version and tolerance policy. |
| IO-007 | IGES import/export | UNSUPPORTED | Format-specific adapter | G4, G6 | P2 | — | Surface/solid limitations explicit. |
| IO-008 | STL export | PLANNED | Tessellation export options | G4, G6 | P2 | — | Units, resolution, mesh closure; mesh is not editable feature history. |
| IO-009 | STL / mesh import | UNSUPPORTED | Mesh import options | G2, G6 | P2 | — | Graphics/mesh/solid distinctions and supported versions. |
| IO-010 | 3MF import/export | UNSUPPORTED | Format-specific adapter | G2, G6 | P2 | — | Verify baseline availability and metadata fidelity. |
| IO-011 | DXF import to sketch/drawing | PLANNED | DXF/DWG import data | G3, G6 | P2 | — | Scale, layers, spline conversion and constraints. |
| IO-012 | DXF export | PLANNED | Part/drawing format adapter | G4, G6 | P2 | — | Sheet-metal export is SM-023; drawing export DRW-027. |
| IO-013 | DWG import/export | PLANNED | DXF/DWG adapter | G3, G6 | P2 | — | Different drawing/sketch workflows require separate tests. |
| IO-014 | SAT / ACIS import/export where supported | UNSUPPORTED | Format adapter | G2, G6 | P2 | — | Confirm version-specific options; no support inferred from extension. |
| IO-015 | PDF export | PLANNED | IExportPdfData | DRW-026 | P2 | — | Drawing output; not PDF geometry reconstruction. |
| IO-016 | Image export / native render capture | PLANNED | Model view / SaveAs image options | G2, G6 | P2 | — | Presentation/preview evidence, not geometric proof. |
| IO-017 | Import diagnostics | PLANNED | Import feature/body diagnostics | IO-004, G2 | P2 | — | Report healed vs unresolved topology. |
| IO-018 | Geometry repair / healing | UNSUPPORTED | Native repair interfaces where available | IO-017, G4, G5 | P2 | — | Bounded repair and before/after deviation checks. |
| IO-019 | 3D Interconnect / linked import lifecycle | UNSUPPORTED | Import/external-reference policy | IO-004, ASM-024 | P2 | — | Important omitted linked-source workflow; certify version/options. |
| IO-020 | Export quality / round-trip verification | PLANNED | Independent import/measurement fixtures | G4, G6 | P2 | — | File existence alone is insufficient. |

## Model understanding and inspection

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| OBS-001 | Read top-level feature tree | REAL SOLIDWORKS VERIFIED | OBS-H | DOC-017 | P0 | E01,E02 | Names, native types, errors/warnings; not full graph. |
| OBS-002 | Read nested/consumed feature hierarchy | PARTIAL | Native acceptance inspectors only | G1, G2 | P0 | E04 | Prismatic test recurses sketches; not production observation capability. |
| OBS-003 | Identify feature types | REAL SOLIDWORKS VERIFIED | OBS-H: GetTypeName2/GetTypeName | OBS-001 | P0 | E01,E02 | ICE wrapper handling; unsupported feature semantics remain unknown. |
| OBS-004 | Inspect feature parameters/definitions | PLANNED | IFeature.GetDefinition / typed data | G1, G2 | P0 | — | Generic names/types do not expose definitions. |
| OBS-005 | Inspect sketches / supports / solve state | PARTIAL | Native acceptance inspectors only | G1, G2 | P0 | E04 | Runtime sketch observation absent. |
| OBS-006 | Inspect sketch geometry | PARTIAL | Test-only ISketch.GetSketchSegments | G1, G2 | P0 | E04 | Line endpoints/arcs in native fixtures; no public structured observation. |
| OBS-007 | Inspect dimensions | PLANNED | IDimension / IDisplayDimension | G1, G2 | P0 | — | No exposed implementation found. |
| OBS-008 | Inspect relations/constraints | PLANNED | ISketch.RelationManager | G1, G2 | P0 | — | No exposed implementation found. |
| OBS-009 | Count visible solid bodies | REAL SOLIDWORKS VERIFIED | OBS-H: IPartDoc.GetBodies2 | Owned part | P0 | E01,E02 | Current helper uses visibleOnly=true. |
| OBS-010 | Inspect all bodies / types / visibility | PLANNED | IPartDoc / IBody2 | G1, G2 | P0 | — | Include hidden and surface bodies with explicit scope. |
| OBS-011 | Inspect faces / analytic surfaces | PARTIAL | NativePlateInspection test helper | G1, G2 | P0 | E04 | Test-only hole evidence; runtime native face resolver missing in WIP. |
| OBS-012 | Inspect edges / curves | PARTIAL | NativePlateInspection test helper | G1, G2 | P0 | E04 | Test-only circular-edge measurements; generic runtime API absent. |
| OBS-013 | Inspect vertices | PLANNED | IVertex | G1, G2 | P0 | — | No exposed implementation found. |
| OBS-014 | Measure geometric distances/angles/areas | PLANNED | IMeasure / analytic descriptors | DOC-026, G1, G2 | P0 | — | No exposed implementation found. |
| OBS-015 | Precise global bounds | REAL SOLIDWORKS VERIFIED | OBS-H: GetExtremePoint | DOC-025 | P0 | E01,E02 | No oriented box, datum-frame or full hidden-body policy. |
| OBS-016 | Mass/volume/centre/inertia observations | PARTIAL | Test-only mass inspection | DOC-024, G2 | P0 | E04 | Production verifier lacks volume; material-aware mass is not implemented. |
| OBS-017 | Interference observation | PLANNED | Assembly interference APIs | ASM-027 | P0 | — | No exposed implementation found. |
| OBS-018 | Determine feature dependencies | PLANNED | IFeature.GetParents/GetChildren | G1, G2 | P0 | — | Direct links plus transitive closure and unknown external edges. |
| OBS-019 | Inspect parent/child relationships | PLANNED | IFeature.GetParents/GetChildren | OBS-018 | P0 | — | Tree hierarchy and dependency edges are distinct. |
| OBS-020 | Inspect reference bindings / resolution state | PLANNED | Native persistent reference resolver | DOC-020, G1 | P0 | — | No exposed implementation found. |
| OBS-021 | Detect native failed features | REAL SOLIDWORKS VERIFIED | OBS-H: GetErrorCode2 / ForceRebuild3 | Owned part | P0 | E01,E02 | Top-level checks; fixtures show clean rebuilds, not exhaustive failure diagnosis. |
| OBS-022 | Detect suppressed features | PLANNED | IFeature suppression APIs | G1, G2 | P0 | — | Suppression is configuration-specific. |
| OBS-023 | Build safe existing-model summary | PLANNED | Snapshot + graph + coverage | OBS-004–008, OBS-018–022 | P0 | — | Unknown features remain opaque, not silently editable. |
| OBS-024 | Compare before/after model snapshots | PLANNED | Core observation diff | G1, G2, G4 | P0 | — | Topology, parameter, frame and revision-aware differences. |
| OBS-025 | Inspect native warnings / rebuild result | REAL SOLIDWORKS VERIFIED | OBS-H | DOC-014 | P0 | E01,E02 | Warning flags exposed; policy does not currently fail all warnings. |
| OBS-026 | Validate model observation completeness | PLANNED | Core coverage schema | G2 | P0 | — | Unknown/unavailable differs from empty/absent. |

## Editing existing models

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| EDIT-001 | Change existing named dimensions | PLANNED | IDimension / native regeneration | DIM-010, OBS-007 | P1 | — | No current in-place or copy-based parametric edit command. |
| EDIT-002 | Modify existing sketch geometry | PLANNED | ISketch entity edit APIs | OBS-005/006, G3, G5 | P1 | — | No exposed implementation found. |
| EDIT-003 | Change existing sketch constraints | PLANNED | ISketchRelationManager | SC-017, G5 | P1 | — | No exposed implementation found. |
| EDIT-004 | Edit feature parameters | PLANNED | IFeature.GetDefinition/ModifyDefinition | OBS-004, G3, G5 | P1 | — | Begin with boss depth and cut depth/end condition. |
| EDIT-005 | Suppress feature | PLANNED | IFeature suppression APIs | OBS-018/022, G5 | P1 | — | No exposed implementation found. |
| EDIT-006 | Unsuppress feature | PLANNED | IFeature suppression APIs | EDIT-005, G4 | P1 | — | No exposed implementation found. |
| EDIT-007 | Reorder features where safe | UNSUPPORTED | Native reorder API + dependency preflight | OBS-018, G5 | P1 | — | Reject dependency violations and uncertain external effects. |
| EDIT-008 | Replace references safely | PLANNED | Typed feature references / ModifyDefinition | DOC-020, OBS-020, G5 | P1 | — | No exposed implementation found. |
| EDIT-009 | Modify configurations | PLANNED | Configuration-aware model delta | CFG-001/003/004, G5 | P1 | — | No exposed implementation found. |
| EDIT-010 | Add features to existing geometry | PARTIAL | OpenPart + existing creation handlers | G1–G5 | P1 | E03 | Mechanisms exist; no certified existing-model snapshot/dependency workflow. |
| EDIT-011 | Remove features safely | PLANNED | Native delete + dependency policy | OBS-018, G5 | P1 | — | Explicit downstream impact and recovery required. |
| EDIT-012 | Compute downstream impact before edit | PLANNED | Intended + observed dependency graphs | OBS-018/019, G3 | P1 | — | No exposed implementation found. |
| EDIT-013 | Conversational replacement model revision | AUTOMATED TESTED | HOST: JobCoordinator.ReplanAsync | Current ReadyForReview job | P1 | E07 | Builds NEW part and separate filename; not editing the previous feature tree. |
| EDIT-014 | Preserve/compare unknown imported features | PLANNED | Opaque observed nodes + conservative edit policy | OBS-023, G5 | P1 | — | Do not reconstruct unknown feature intent automatically. |
| EDIT-015 | Reconcile externally edited model | PLANNED | Model revision stamps / observation diff | DOC-035, OBS-024 | P1 | — | Invalidate stale approvals and resolve conflicts. |

## Agent-specific intelligent CAD operations

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| AG-001 | Structured CAD command-plan generation | AUTOMATED TESTED | AI-H: OpenAiCadPlanningProvider; Core CadOperationCatalog; typed lowering for NewPart/CreateSketch/AddCircle/AddLine | Current catalog | P0 | E07 + catalog and LegacyCadOperationAdapterTests | Strict outer schema, descriptor-driven prompt text, parameter JSON strings; 4 operations lower into typed internal values; live prismatic proposal recorded, unexecuted. |
| AG-002 | Validate command names and parameters before execution | AUTOMATED TESTED | Core CadOperationCatalog + CadPlanningCommandContract + LegacyCadOperationAdapter | G0 | P0 | E03,E07 + catalog and adapter tests | Typed line lowering reuses finite-coordinate and >0.000001 mm endpoint separation checks; other typed subset validation remains per-command; not whole-plan feasibility. |
| AG-003 | Whole-plan state/reference/dependency validation | PARTIAL | Core CadPlanLifecycleValidator, CadPlanV2Preflight + Host normalizer and JobCoordinator approval/execution gates | G0 | P0 | E12–E15,E18–E22 + lifecycle validator and coordinator sequence tests | V1 plans are revalidated before approval/execution. Candidate and normalized persisted v2 references/lifecycle are validated; Host assigns and matches IDs, but v2 plans remain non-executable. Does not prove arbitrary line/arc closure or all runtime combinations. |
| AG-004 | Semantic geometry reference representation | PLANNED | No exposed semantic reference contract; WIP selectors are not advertised | G1, G2 | P0 | — | No complete resolver or persisted semantic references. |
| AG-005 | Select the top face in agreed frame | PLANNED | WIP-H design only; command not advertised | G1, G2 | P0 | — | Define top relative to design coordinates, then implement and verify deterministic resolution. |
| AG-006 | Select largest cylindrical face | PLANNED | Analytic surface query / deterministic resolver | OBS-011, G1, G2 | P2 | — | Explicit area metric, body scope and tie handling. |
| AG-007 | Use centre/axis of an identified hole | PLANNED | Hole/cylinder/arc observation + references | OBS-011/012, G1, G2 | P0 | — | No exposed implementation found. |
| AG-008 | Place hole 20 mm from identified left edge | PLANNED | Named dimension/constraint to semantic boundary | AG-007, DIM-002, G3 | P1 | — | Clarify edge/frame and preserve margin under edit. |
| AG-009 | Make selected holes concentric | PLANNED | Constraint/feature/mate intent lowering | SC-004, G1, G3 | P1 | — | Within-sketch relation differs from cross-feature alignment. |
| AG-010 | Increase wall thickness preserving intent | PLANNED | Typed parameter delta + wall verifier | FT-050 or SM-001, G3–G5 | P2 | — | No exposed implementation found. |
| AG-011 | Make bracket wider preserving hole intent | PLANNED | Parameter graph delta + verification | DIM-010, EDIT-012, G4, G5 | P1 | — | No exposed implementation found. |
| AG-012 | Move hole group by dimensioned offset | PLANNED | Seed/pattern delta with agreed frame | EDIT-001/004, PAT-001, G4 | P1 | — | No exposed implementation found. |
| AG-013 | Change pattern from four holes to six | PLANNED | Pattern count delta | PAT-011, G4, G5 | P2 | — | No exposed implementation found. |
| AG-014 | Feature dependency reasoning | PLANNED | Intended/observed graph comparison | OBS-018, G3 | P0 | — | No exposed implementation found. |
| AG-015 | Design-intent preservation checks | PLANNED | Constraint/parameter invariants | G3, G4 | P0 | — | Centred vs edge margin vs fixed spacing must be explicit. |
| AG-016 | Automatic reference resolution | PARTIAL | Core `CadPlanV2Preflight`; Host normalizer/binder; Bridge resolver, selection scope and v2 Extrude handler; semantic candidate report planned | DOC-020, G1, G2 | P0 | E16–E23 | A direct v2 Extrude consumes a Host-normalized logical sketch ID and is native verified for one owned part. Host does not yet approve/execute v2 plans; face/edge/semantic rebind remains unsupported. |
| AG-017 | Operation precondition checks | PARTIAL | Core lifecycle/v2 preflight and version-aware registry; Host binder and review-only gate; Bridge execution-ID/document/selection guard | G0, G1 | P0 | E03,E12–E14,E18–E23 + lifecycle, adapter and coordinator tests | V1 protections remain. V2 Extrude validates parameters, model/sketch/job identity and active document before mutation; wrong-document/deleted sketch/cross-job native rejection is verified. Whole-plan v2 executability, Cut direction and recovery remain open. |
| AG-018 | Post-operation readback verification | PLANNED | Bridge observations + Core assertions | G2, G4 | P0 | — | Returned input JSON is not native readback. |
| AG-019 | Model validation against approved intent | PARTIAL | HOST: fixed body/bounds/rebuild checks | G2, G4 | P0 | E07 | Current checks can accept wrong-but-positive geometry. |
| AG-020 | Structured failure diagnosis | PARTIAL | CadError / handler codes / native feature errors | G2, G5 | P0 | E03 | Code/stage/message/detail exist; storage drops stage/detail; no causal diagnosis. |
| AG-021 | Fail-stop and restart without replay | AUTOMATED TESTED | HOST: coordinator/repository recovery | Job history | P0 | E07 | Marks interrupted jobs Failed; does not undo or reconcile native state. |
| AG-022 | Verified rollback / correction | PLANNED | Host checkpoint/recovery policy | G2, G4, G5 | P0 | — | No exposed implementation found. |
| AG-023 | Retry with bounded alternative strategy | PLANNED | Recovery policy + reapproval rules | AG-022 | P0 | — | No blind mutation retries; uncertain outcome requires reconciliation. |
| AG-024 | Alternative modelling strategy selection | UNSUPPORTED | Engineering planning with capability costs | G0–G5 | P3 | — | Later; compare verified strategies and preserve approved design. |
| AG-025 | Conversational modification of existing model | PLANNED | Model delta planner + revision/diff approval | EDIT-001/004/012, G5 | P1 | — | Existing chat revisions currently rebuild new parts. |
| AG-026 | Revision-bound CAD plan approval | AUTOMATED TESTED | HOST: ApprovalPolicy + JobCoordinator | Persisted job revision | P0 | E07 | Stale revision checks; bind future approval to base model stamp as well. |
| AG-027 | Pre-build review/preview of plan | PARTIAL | Desktop plan display / image brief UI | G0 | P1 | E07,E08 | Text/structured review exists; geometric dry-run preview absent. |
| AG-028 | Independent expected-vs-actual geometry comparison | PLANNED | Core assertions + Bridge observations | G2, G4 | P0 | — | Independent test oracle must also remain. |
| AG-029 | Execution idempotency / crash reconciliation | PLANNED | Attempt journal + checkpoint provenance | G1, G5 | P0 | — | Current job IDs are not operation idempotency keys. |
| AG-030 | Manufacturing-rule validation | PLANNED | Typed engineering rule sets + measurements | G2–G4 | P2 | — | Wall/hole/bend/accessibility rules; no fabrication safety inference. |
| AG-031 | Intent provenance / assumption management | PARTIAL | DesignInterpretation / plan assumptions | G0, G3 | P1 | E08 | Prose provenance exists; typed parameter/evidence links missing. |
| AG-032 | Multi-step engineering clarification | AUTOMATED TESTED | HOST design intake / job revisions | Current UI and provider | P0 | E07,E08 | Evidence-backed answers; quoted text provenance is not full semantic proof. |
| AG-033 | Simulation workflow with honest unsupported outcomes | SIMULATION TESTED | SIM: SimulatedCadCommandExecutor + Host versioned-plan preflight | Simulation mode | P0 | E09,E21,E22 | Plate only; refuses native output, custom-profile solids and blind pockets. Version-2 feature plans are held at clarification with a persisted limitation, and the executor rejects versioned plans before simulation mutation; these guards do not establish v2 execution support. |
| AG-034 | Remote paired CAD lifecycle | AUTOMATED TESTED | RemoteAgent narrow forwarding / Host | Pairing + Host | P2 | E10,E11 | Installed Host image-backed jobs and clarification passed; paired-phone gallery/camera and download are still unverified. Manual remote desktop control is separate from CAD observation. |

## Image / hand-sketch to CAD

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| IMG-001 | PNG/JPEG image ingestion | AUTOMATED TESTED | IMAGE-H: ReferenceImageStore | Workspace image policy | P3 | E08 | Decode, size/type/path/hash checks; actual reference upload used historically. |
| IMG-002 | Hand-sketch ingestion as raster | PARTIAL | IMAGE-H: same PNG/JPEG path | IMG-001 | P3 | E08 | Input route accepts raster; representative handwriting corpus not verified. |
| IMG-003 | Primitive recognition | PARTIAL | IMAGE-H / AI-H: multimodal interpretation | IMG-001 | P3 | E08,E11 | Synthetic rectangle/hole interpreted in an installed normal CAD job; no typed recognized-primitive geometry. |
| IMG-004 | Line recognition | PARTIAL | IMAGE-H: observations/inferences | IMG-003 | P3 | E08 | Descriptive evidence only; no vector extraction accuracy claim. |
| IMG-005 | Circle/arc recognition | PARTIAL | IMAGE-H: observations/inferences | IMG-003 | P3 | E08 | Synthetic central circle; general arcs/occlusion not certified. |
| IMG-006 | Dimension recognition | PARTIAL | IMAGE-H: VisibleDimensions; AI-H normal job | IMG-001 | P3 | E08,E11 | Installed planner read 100 × 60 mm and Ø20 labels from one synthetic image; no calibrated OCR accuracy. |
| IMG-007 | Annotation recognition | PARTIAL | IMAGE-H: observations / visible text | IMG-001 | P3 | E08 | No structured annotation-to-entity links. |
| IMG-008 | Text recognition | PARTIAL | IMAGE-H: VisibleText | IMG-001 | P3 | E08 | Multimodal descriptive extraction; handwriting benchmark absent. |
| IMG-009 | Perspective correction | UNSUPPORTED | Image transform/hypothesis stage | IMG-001 | P3 | — | No implemented rectification pipeline. |
| IMG-010 | Distortion handling / camera calibration | UNSUPPORTED | Calibration/uncertainty stage | IMG-009 | P3 | — | Avoid false precision. |
| IMG-011 | Scale inference with confidence | UNSUPPORTED | Evidence-linked scale hypothesis | IMG-006/009 | P3 | — | User confirmation or reliable dimension reference required. |
| IMG-012 | Ambiguity detection | AUTOMATED TESTED | IMAGE-H: Unknowns/Questions; AI-H normal job | IMG-001 | P3 | E08,E11 | Installed normal image job asked about hole location and cut type; completeness of perception not guaranteed. |
| IMG-013 | Missing-dimension detection | AUTOMATED TESTED | IMAGE-H: MissingDimensions; AI-H normal job | IMG-001 | P3 | E08,E11 | Installed normal image job requested absent thickness; no arbitrary reconstruction guarantee. |
| IMG-014 | Clarification dialogue / preserved answers | AUTOMATED TESTED | IMAGE-H: DesignIntakeService | IMG-012/013 | P3 | E08 | Critical questions retained; resolution evidence checked against user text. |
| IMG-015 | Typed structured sketch representation | PLANNED | Contracts sketch/recognition DTOs | G1, G3, IMG-003 | P3 | — | Current brief strings are not geometry definitions. |
| IMG-016 | Constraint inference | PARTIAL | IMAGE-H: Constraints strings | IMG-015, G3 | P3 | E08 | Descriptive guesses only; native solver validation still required. |
| IMG-017 | Design-intent inference | PARTIAL | IMAGE-H: FeatureIntent/assumptions | IMG-014/015 | P3 | E08 | Provenance and approved hypotheses required. |
| IMG-018 | Feature inference | PARTIAL | IMAGE-H: RequiredCadFeatures | IMG-015/017 | P3 | E08 | No typed verified feature graph. |
| IMG-019 | Feature-order inference | PARTIAL | IMAGE-H modelling strategy / CAD planner | IMG-018, G3 | P3 | E08 | Prose strategy/order proposal; no dependency correctness proof. |
| IMG-020 | CAD-plan generation from approved brief | AUTOMATED TESTED | IMAGE-H: PlanCadAsync → Host planner | IMG-022, AG-001 | P3 | E08 | Mocked boundary tested; historical live image test stopped before CAD planning. |
| IMG-021 | Geometric preview before execution | PLANNED | Plan-to-preview renderer with uncertainty | IMG-015/020, G4 | P3 | — | Current brief/plan text review is not geometric preview. |
| IMG-022 | User design approval/modification before modelling | AUTOMATED TESTED | IMAGE-H: revisioned brief + separate build approval | IMG-014 | P3 | E08 | Explicit approval preserved even in Auto Mode. |
| IMG-023 | Multiple views / image registration consistency | PARTIAL | IMAGE-H multiple references + ViewType | IMG-001/009 | P3 | E08 | Multiple inputs supported; geometric view reconciliation absent. |
| IMG-024 | Reference provenance / integrity | AUTOMATED TESTED | IMAGE-H: SHA-256, metadata, reread checks | IMG-001 | P3 | E08 | Does not establish accuracy of interpreted dimensions. |
| IMG-025 | End-to-end image-derived native CAD acceptance | PLANNED | Intake → typed plan → Bridge → verifier | IMG-015/020/022, G1–G5 | P3 | — | No retained evidence for approved arbitrary image-to-native execution. |
| IMG-026 | Hidden geometry hypotheses / user resolution | PLANNED | Engineering interpretation + evidence graph | IMG-014/017 | P3 | — | Keep alternatives explicit; never silently invent hidden dimensions. |
| IMG-027 | Scan / point-cloud / reverse engineering | UNSUPPORTED | Separate import/recognition workflow | IO-009, G2 | P3 | — | Additional omitted input domain; beyond raster intake. |

## Separate specialist domains

| ID | User capability / sub-capability | Status | Location / mechanism | Prerequisites | Priority | Evidence | Scope / version / notes |
|---|---|---|---|---|---|---|---|
| SPEC-001 | Simulation / FEA studies | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Keep separate from mock execution mode. No current product reliance found. |
| SPEC-002 | CAM / toolpath generation | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Historical CAMWorks crash interaction is environmental evidence, not an integration dependency. |
| SPEC-003 | Routing / piping / tubing / harnesses | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Separate routing libraries, rules and licenses. |
| SPEC-004 | Electrical integration | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Separate electrical product/data model. |
| SPEC-005 | Mold Tools / tooling workflows | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Parting lines, shut-off surfaces and tooling split deserve separate requirements. |
| SPEC-006 | MBD / 3D PMI / DimXpert | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Distinct from core 2D drawing annotations. |
| SPEC-007 | PDM check-in/out / revisions / lifecycle | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Separate vault authority, locks and artifact lifecycle; do not infer file-save authorization. |
| SPEC-008 | Motion studies / kinematic analysis | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Additional omitted specialist domain, separate from basic assembly mates. |
| SPEC-009 | Costing / sustainability / manufacturing estimates | NOT ASSESSED | Separate adapter/license/validation assessment required | Verified core, domain-specific requirements | P4 | — | Separate assumptions, data sources and licenses. |

## Version and scope maintenance

The historical native baseline is SOLIDWORKS Premium 2020 SP0.0. Newer API documentation is explanatory, not support certification. All new features require exact installed-interop signature checks, native-enabled compilation, and version/SP/template evidence before native status promotion. Multi-body, surface, assembly and drawing workflows require different verification policies from the current single-visible-solid rule. Formats beyond `.sldprt` require scoped workspace/export changes.

Additional core domains explicitly included above are helix/projected/composite curves, sketch blocks/pictures, dimension tolerances, Pack and Go, linked imports, document events, assembly loading states, drawing dangling-reference checks, manufacturing rule provenance, gauge tables, and point-cloud/reverse engineering. Specialist products remain separate; no current source dependency justifies placing them ahead of reliable core modelling.

When a row becomes too broad to certify, retain its ID as a family and add stable variant IDs; never silently change the meaning of an existing ID. Update dependencies and roadmap alongside status. Report evidence gaps honestly, including simulator refusal and unsupported observation coverage.
