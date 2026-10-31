# Local upstream backend prototype

Application main was inspected at `a11982c` (no subsequent main commits at session start).
The existing application implementation and default backend remain intact.
The engine's full foundation lives in
https://github.com/jhaago/Solidworks-CAD-Engine . Its integration guide records
provenance, runtime, test evidence, recovery and exact Windows acceptance commands.

## Build and local use

Requires .NET 10 SDK to build the shared .NET Standard adapter; the application
Core/host stays .NET Framework 4.8. Both branches must be checked out to enable
this prototype. No engine source is copied into the app and no new engine COM
scheduler runs inside the app.

```powershell
$engineRoot = (Resolve-Path '..\Solidworks-CAD-Engine').Path
dotnet build src/SolidWorksCadAgent.Core/SolidWorksCadAgent.Core.csproj -c Release -p:CadEngineRoot="$engineRoot"
dotnet run --project tests/SolidWorksCadAgent.BackendTests -p:CadEngineRoot="$engineRoot"
# Default legacy build excludes the opt-in facade and shared adapter reference:
dotnet build src/SolidWorksCadAgent.Core/SolidWorksCadAgent.Core.csproj -c Release
```

`build/UpstreamEngine.commit` pins engine integration revision
`e7405f1041a9c84970f1a91b2abad9888eadd867` used by CI.
The local facade is `Core/Backends/UpstreamBackendJob.cs`. It requires explicit
`EngineOptions.Enabled = true`, a nonempty persisted job id, a local journal
path, and trusted engine executable configuration. It exposes discovered
schemas and generic invocation; there is no per-feature wrapper or public
HTTP tool-execution route. A call site can create it with `StartAsync`, inspect
`Tools`, then invoke an already approved sequence with `InvokeApprovedToolAsync`.
That name is a caller precondition: this prototype does not itself implement
approval storage. It is intentionally **not wired into JobCoordinator or the UI**.

The facade owns one backend and one lease for its entire lifetime. Never
construct it while holding the legacy lease, and never invoke a legacy
executor inside its sequence. The engine has its own upstream ownership
checks; that is not yet a substitute for the application's checkpointed
editing of existing managed models.

## Inspected boundaries and next integration changes

| Responsibility | Current application boundary | Required upstream integration |
|---|---|---|
| Conversation/design intake | Core/Ai/CadPlanningContracts; AgentHost/Ai/OpenAiCadPlanningProvider; AgentHost/Design/DesignIntakeService | Remain in app. Plan against a catalog snapshot, not invented tool contracts. |
| Approved plan/commands | Core/Commands/CadPlanDocumentReader, CadPlanV2Document, CadPlanningCommandContract; Contracts/Cad | Add a separately versioned upstream plan containing backend id, pin/catalog hash, discovered tool names and arguments; do not feed upstream tools into legacy envelopes. |
| Job execution/approval | AgentHost/Jobs/JobCoordinator; Core/Jobs/ApprovalPolicy and JobStateMachine | Persist immutable backend selection and approval hash on JobRevision. Route once before choosing a runner/lease. |
| Journal/uncertain outcomes | AgentHost/Persistence/SqliteJobRepository.V2Attempts and V2*Outcome; Jobs/CadV2SixStepRunner | Import adapter dispatch/result JSON into existing attempt records; retain errors/content/metadata. A missing response is uncertain, never a retry instruction. |
| Native lease/manual remote control | Core/Remote/CadNativeControlLease; Core/Remote/RemoteSessionCoordinator | Same per-user FileShare.None path. Updated legacy lease also refuses `.recovery-required` after acquiring the lock. |
| Document identity/protected editing | Core/References/CadParametricIdentity, IModelReferenceStore, ICheckpointedParametricEditExecutor; AgentHost/Jobs/CadParametricEditRunner | Bind engine handles to managed ids only within the owning job. Extend verified-copy/checkpoint/reopen protocols before editing existing models through upstream. |
| Inspection/verification | SolidWorksBridge/Inspection; AgentHost/Jobs verification records | Preserve native upstream inspection JSON and app engineering oracles. Discovered tools are not automatically native-verified capabilities. |
| Artifacts | AgentHost/Jobs/CadV2ArtifactAuthorization, CadParametricEditArtifactAuthorization; Host/AgentRoutes | Reuse workspace and saved-file authorization; verify file identity/hash/evidence before publishing an upstream artifact. |
| Desktop/remote | Desktop/Api and Ui/JobPresentation; RemoteAgent/Host/RemoteRoutes | Keep existing authenticated job/status/artifact flows. No remote raw MCP execution endpoint. |

The local JSONL journal is supporting evidence; successful generic calls do
not yet advance a production application's job state or authorize artifacts.
Never report ReadyForReview using this facade alone.

## Safety and recovery

The shared adapter flushes dispatch intent to the local recovery marker and
journal before sending tools/call. Cancellation after dispatch, timeout,
worker loss or ambiguous server errors block subsequent use of that session.
The durable marker also blocks new upstream sessions and updated legacy jobs.
Inspect journal/native state and resolve pending CAD work before an operator
archives evidence and clears the exact marker. No automatic replay, restart,
fallback or marker-clearing endpoint exists. Original user documents are
never part of the acceptance scenario.

VBA automation is excluded/disabled by the adapter, regardless of inherited
environment. Default application builds do not enable the facade. The old
`ValidateSolidWorks2020Interop` target name and error-code prefixes are kept
for script compatibility, but version-28 rejection is removed; missing and
mismatched local interop assemblies still fail clearly. Historical 2020-only
reference-compile scripts remain historical probes, not integration prerequisites.

## Evidence from this session

- Shared adapter Release build and acceptance runner build: passed.
- .NET Framework 4.8 Core with `CadEngineRoot`: passed, zero warnings/errors.
- Core without opt-in: see engine TEST-RESULTS.md for the final observed run.
- Application backend boundary tests: 7/7; existing portable suite: 240/240
  when built for the available .NET 10 runtime (source target remains .NET 8).
- Engine baseline upstream unit suite: 209/220 on Linux; custom naming policy
  brings it to 215/226 with the same 11 Windows-platform failures preserved.
- Real engine initialization/native part creation/native edits: not executed.
  Full engine compile requires installed proprietary interops; this host is Linux.

## Adoption order

Prove the Windows synthetic plate and edit first; then admit upstream jobs
through a backend-pinned approved runner. Add managed-document/checkpoint and
artifact integration before saved-model edits. Expand tool families from
native evidence and stop new legacy feature ports. Retire overlapping legacy
handlers only after equivalent workflows pass, retaining explicit backend
selection during migration. Project B reconstruction/constraint algorithms
remain later isolated candidates, without adopting a second engine.
