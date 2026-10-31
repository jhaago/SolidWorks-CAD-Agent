# Licensed components carried into the CAD agent

2026-10-30 finishing update: [bounded native fillet/chamfer ports and definition inspection](batched-finishing-ports-2026-10-30.md) supersede the historical excluded-handler status below. Native execution acceptance remains pending.

2026-10-30 update: [exact face-boundary inspection and version-2020 native-enabled compilation](face-boundary-and-api-compatibility-2026-10-30.md) now extend this work. The full solution compiles against 28.1.0.74 references; actual SOLIDWORKS runtime acceptance remains pending. Earlier native-disabled limitations below describe that earlier validation stage.

This pass follows the user's explicit instruction to copy useful implementations from
both reviewed repositories. It extends the existing isolated `feature/open-source-verification-2020`
branch; the net48 Host/Core/Contracts/Bridge/Desktop boundaries remain intact.

## What was copied or translated and where it is used

| Upstream source | Ported component | Actual consumer |
|---|---|---|
| A: `Geometry/Vec3.cs` | Finite vector algebra and stable direction normalization | Topology filters, axis parsing, chains and transforms |
| A: `Geometry/Xform.cs` | SOLIDWORKS row-vector rotation/translation/scale math | Existing `CadStandardPlaneFrame` model/bounds calculations now use it |
| A: `Geometry/AxisSpec.cs` | Signed axes and arbitrary direction vectors | Geometric query parser |
| A: `Services/EdgeChain.cs` | Straight-edge connectivity, reversal, corner and closed-loop ordering | `InspectTopology` optional `orderChain` evidence |
| A: `Geometry/TopoQuery.cs`, `Model/EntityRef.cs` | Conjunctive filters, signed normals, unsigned parallel directions, radius/length, extreme reference points and deterministic ordering | Registered read-only `InspectTopology` command; exact expected-match cardinality, no first-match fallback |
| A: `Geometry/Topology.cs`, `Com/SwExt.cs` | Typed surface/curve facts | Native part-only reader on the existing bound-document STA; includes hidden solids, old API calls and checked edge evaluation |
| A: `Com/OleMessageFilter.cs`, selected OLE declarations | Bounded SERVERCALL_RETRYLATER handling | Existing STA dispatcher scopes/restores the OLE filter around every operation; cancellation stops busy-call retry |
| B: `automation/parametric.py` relation map and `_validate_constraint_graph` | Named entity/reference graph validation, translated to C# | Registered COM-free `ValidateSketchConstraintGraph` command, with added arity, endpoint, units, work budgets and Fix opt-in checks |
| B: `automation/shape_edit_compatibility.py` | Added/removed/type-changed feature inventory comparison | Checkpointed parameter editor checks before mutation, after mutation, after saved inspection and after restoration |

Here **A** is `soheil-mp/solidworks-mcp` at
`f450dabcd02a9a7ba705ee9e18a77aaeee9fe802`; **B** is `czuryk/SolidworksMCP` at
`eccb8af4aa76416baf2a5ca99b256da19b73d9b6`. The
[machine-readable manifest](../third_party/reuse-manifest.json) records original file
SHA256 hashes, port destinations and modifications. These are modified/translated
implementations, not unmodified upstream packages. The exact full MIT licence for
each project is retained under `third_party`; the notices and manifest are included
in Windows binary bundles and validated there.

## Usable structured tools

Both commands use the existing bridge command envelope/registry. They are intentionally
excluded from the current AI mutation-plan whitelist: adding read-only developer tools
does not authorize broader native modelling commands or bypass plan admission. They
can be called by code using `SolidWorksBridgeFacade.ExecuteAsync`. No new HTTP listener
or remote-control privilege is introduced.

```json
{
  "command": "InspectTopology",
  "parameters": {
    "kind": "Edge",
    "where": {"curve": "line", "parallelTo": "y", "lengthMm": 5},
    "expect": 4
  }
}
```

Available filters are `surface`, `curve`, `normal`, `parallelTo`, `radiusMm`,
`radiusTolMm`, `lengthMm`, `lengthTolMm`, `extreme`, `feature`, `body` and
`angleTolDeg`. Kind is `Face` or `Edge`; kind-incompatible/unknown fields fail before
COM. `expect` is an optional exact count from0 to256. Listings with more than256 matches
fail instead of silently truncating. Native enumeration is bounded to20,000 entities.
`orderChain:true` on an edge query returns index order, edge reversal, closed/open status
and corner count for one measured straight-edge chain, or fails on ambiguity/disconnection.

Query results contain geometry, units and hashed inspection IDs. Those IDs are
**inspection evidence, not persistent selection handles** accepted by other commands.
Face reference positions are approximate face-box centers; `extreme` compares the
reported reference points, not exact global extrema or a guaranteed interior pick point.
Edge positions are native parameter-midpoint evaluations with packed success checking.
The port uses `GetCurveParams3`, documented since SOLIDWORKS2011, instead of the
obsolete packed/signed `GetCurveParams2` range. Native compile/behavior remains a2020 acceptance gate.
Face-normal filters are signed; edge/cylinder parallel filters accept either direction.
An edge's feature filter uses both adjacent-face owners. Missing ownership evidence
cannot prove a successful zero-match result.

```json
{
  "command": "ValidateSketchConstraintGraph",
  "parameters": {
    "entities": [{"id":"base","type":"line"},{"id":"side","type":"line"}],
    "constraints": [
      {"type":"horizontal","entities":["base"]},
      {"type":"coincident","entities":["base.end","side.start"]}
    ],
    "dimensions": [{"id":"width","type":"length","entities":["base"],"value":100,"unit":"mm"}],
    "solveTarget":"fully_defined"
  }
}
```

This validates a **symbolic graph**. It does not know actual geometry coordinates,
solve conflicts, create relations, or prove full definition. Results explicitly include
`nativeSolved:false`. Native construction must later verify relation attachments,
driving dimensions, solver status and saved/reopened regeneration. Supported symbolic
entities: line/arc/circle/point; point refs include valid `.start`, `.end`, `.center`,
and explicit `origin`. Hidden implicit axes are rejected; declare construction lines.
Upstream's12 relation codes are included with target/arity validation. Fix relations
require `allowFixed:true`; this is an explicit tracing choice, not design-intent recovery.
Length dimensions use mm; angles use deg; positive finite values must survive native
unit conversion. Work is bounded to256 entities,1024 relations and256 dimensions.

## Runtime and recovery changes

The copied OLE policy retries only a native `SERVERCALL_RETRYLATER` refusal, at150ms
intervals within a10-second window and while the caller is not cancelled. It does
not replay an accepted operation or retry arbitrary COM exceptions. Registration and
restoration occur on the same existing STA, preserving any previous message filter.
This is not an in-flight COM timeout, idle STA message pump, modal watchdog, or a
guarantee that a hung accepted call can be cancelled.

Feature inventory comparison is an additional check of names/types, not a replacement
for existing persistent references, source authorization, dimensions, geometry or
checkpoint hashes. An unexpected feature addition/removal/type change blocks the edit
and uses the existing failure/recovery machinery. No automatic ownership migration or
destructive cleanup was imported.

## Review and tests

The pure-port tests include upstream mathematical oracles and additional invalid-input
cases, exact cardinality, relation references/arity/units, duplicate inventory names,
packed native evaluation status and bounded rejected-call policy. Existing standard-plane
tests and new registered-tool tests stay in the full Windows suite. Production remains
net48; the selected COM-free runner remains net8.

The first independent review found no Critical issues and two Important gaps:
unchecked packed edge-evaluation status and absent adjacent feature ownership for edge
queries. Both were corrected, with regression cases. An underflowing dimension and an
oversized integer `expect` regression were also reproduced and fixed. Native acceptance
assertions were expanded for lengths, wrong counts, ownership, hidden solids and unchanged
selection/sketch/dirty/configuration state. The reviewer independently matched every
original source hash and both licence texts to the pinned upstream checkouts.

Native-enabled compilation and actual SOLIDWORKS 2020 execution are still pending.
Windows CI can exercise OLE registration and restore around managed callbacks without
SOLIDWORKS, but does not validate rejected calls against the actual CAD COM server.
See the execution plan record and CI results for final test counts.

Local final evidence: official portable MSTest runner140/140 passed with zero skips;
full net48 unit-project compilation passed without warnings/errors; complete solution
compilation remains without installed interop. A transient .NET CLI `Process.GetStat`
failure was avoided by invoking the installed MSBuild assembly directly. This is an
environment issue, not a passing full Windows runtime-suite result.

## What remains for subsequent ports

| Subsystem | Why it is not copied wholesale | Concrete next step |
|---|---|---|
| Native face/edge mutation | Inspection IDs must not become stale or ambiguous selection authority | Build persistent, revision-bound entity resolution and immediate selection scopes; then native-test one fillet/chamfer and face sketch |
| General parametric construction | Symbolic graphs alone cannot establish constraint solvability/attachment | Versioned named-entity creation and relation/dimension execution with native solver/readback and checkpoints |
| Patterns/revolve/sweep/loft | Existing V2 geometry admission and independent verification are deliberately bounded | Port one2020 signature/selection recipe at a time, together with analytical/native acceptance |
| Assemblies, drawings, sheet metal, simulation | Large feature, COM and configuration dependencies; newer upstream live-test baselines | Extract focused operations after entity resolution; keep separate capability gates and avoid certifying engineering analyses from API success |
| Image tracing and neural workers | Scientific runtime/weight licensing, process boundary and design-intent uncertainty | Separate local image worker; primitive/scale/dimension extraction, reverse-image checks and reviewed brief before native construction |
| Full upstream hosts and raw Python/VBA | Duplicate our COM owner and bypass existing structured control paths | Optional thin MCP adapter over our Host later; retain our planner validation and current approval |

The remaining source is still available at pinned revisions, so later ports can follow
the manifest without redoing discovery. This increment imports functioning reusable
components with callers and tests; it does not claim that every advertised upstream
CAD capability has been integrated or certified.
