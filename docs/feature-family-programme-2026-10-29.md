# Complete feature-family programme

2026-10-30 finishing update: [bounded native fillet/chamfer ports and definition inspection](batched-finishing-ports-2026-10-30.md) supersede the historical excluded-handler status below. Native execution acceptance remains pending.

2026-10-30 update: [exact face/outer-loop inspection, API-version guard and native-enabled compilation](face-boundary-and-api-compatibility-2026-10-30.md) are implemented. Face-feature mutation and real SOLIDWORKS acceptance remain gated. The expanded portable suite passes 213 cases, including the cross-runtime regression coverage.

All useful feature families in the two investigated projects are in the target scope. This is a multi-increment programme, not a statement that every feature is implemented. The [417-capability matrix](CAD_CAPABILITY_MATRIX.md) remains the detailed inventory; its historical evidence is scoped to the scenario and revision recorded there. Do not replace its statuses with a blanket support label.

## Current increment: callable planning tools

`PlanComponentPattern` and `PlanDrawingLayout` are now registered in the existing bridge. They execute without SOLIDWORKS or a document and return `planningOnly: true`, `nativeCreated: false`. Both are direct bridge tools; neither is admitted to the existing AI modelling planner. No new native feature handler or API-version dependency was introduced.

The MIT ports come from the pinned Project A source in `AssemblyPatternMath.cs`, `AssemblyMath.cs` and `DrawingLayout.cs`. [The reuse manifest](../third_party/reuse-manifest.json) records exact hashes and adaptations. Existing MIT notices and distribution checks apply.

### Component patterns

```json
{
  "command": "PlanComponentPattern",
  "parameters": {
    "pattern": "linear",
    "seeds": [{"id": "bolt", "originMm": [10, 20, 30]}],
    "direction": "x", "spacingMm": 25, "count": 3
  }
}
```

This predicts generated origins `[35,20,30]` and `[60,20,30]`, excluding the seed. A two-direction grid adds `secondDirection`, `secondSpacingMm`, `secondCount`. Directions accept the existing signed-axis/vector syntax. Seeds accept an optional proper 3x3 rotation; its row-major storage acts on column vectors. Output `solidWorksArrayData` transposes that rotation for SOLIDWORKS and converts origin translations from mm to metres. It is a prediction, not a native component handle.

```json
{
  "command": "PlanComponentPattern",
  "parameters": {
    "pattern": "circular", "seeds": [{"id": "bolt", "originMm": [20, 0, 0]}],
    "axis": "z", "axisPointMm": [10, 0, 0],
    "angleDegrees": 360, "count": 4, "equalSpacing": true
  }
}
```

The three generated placements rotate origin and orientation by 90, 180 and 270 degrees about the offset axis. Full-circle spacing uses n gaps; partial spans use n−1. `equalSpacing:false` interprets the angle as a step and rejects a repeated full turn. Budgets: 1..32 seeds; at least two instances in the first direction; at most 256 total instances including seeds. Distances are 0.000001..1000000 mm; origins/pivots are bounded to ±1000000000 mm. IDs are unique (case-insensitive); unknown or irrelevant fields, parallel grid axes, shear/reflection/scale and nonfinite inputs fail validation.

### Drawing layout

```json
{
  "command": "PlanDrawingLayout",
  "parameters": {
    "sheetWidthMm": 420, "sheetHeightMm": 297,
    "modelSizeMm": [100, 60, 20],
    "views": ["front", "top", "right", "iso"], "projection": "thirdAngle"
  }
}
```

The output preserves requested order and reports view centres, predicted outline rectangles, chosen scale, fit scale and `fits`. First-angle projection reverses top/right placement. Standard views are front/back/top/bottom/left/right/iso; duplicates are rejected. Automatic scaling reduces the sheet scale to a fitting standard scale when necessary. Explicit `fixedScale` reports `fits:false` if it cannot fit.

Default reservations: 20 mm left margin, 10 mm other margins, title band clamped to 40..60 mm (20% of height). `hasFormat:false` uses 10 mm margins and no title band. `usableMm:[xmin,ymin,xmax,ymax]` specifies an explicit positive area inside the sheet. These are planning assumptions; the actual template, annotations and native view outlines must be read back later. The gap is 15 mm. Sheets are bounded to 50..5000 mm; positive model extents to 0.000001..1000000000 mm; input scales to 0.0001..10000. If no standard scale fits, output remains `fits:false`.

## Coverage and dependency order

| Family | Reuse decision | Current boundary / next implementation |
|---|---|---|
| Runtime, command protocol, document ownership | Retain existing; adapt selected A/B practices | Existing STA, workspace/identity, approvals and checkpoints retained; optional MCP adapter follows verified descriptors |
| Sketch primitives and frames | Retain existing; adapt A/B | Existing primitive handlers; exact extreme-face/outer-loop read-only resolver is implemented; face-local frames and safe mutation contracts remain |
| Relations and driving dimensions | Adapt B plus existing native work | Graph preflight ported; existing bounded plate/hole native dimensions retained; generic graph-to-native constraints and under/over-defined readback next |
| Dimension/feature editing and equations | Retain checkpoints; adapt B | Existing named plate/hole edits plus inventory guard; general definition adapters and safe expression dependency evaluation remain |
| Extrudes and cuts | Retain existing | Existing material-change verification; add more end conditions only with independent geometry checks |
| Fillets/chamfers | Adapt A | Bounded native ports and definition inspection implemented for fresh unsaved parts; saved-part checkpointing, installed native acceptance and planner admission remain pending |
| Revolves, shell, draft, ribs and Hole Wizard | Adapt A | Add bounded version-2020 handlers, driving readback and canonical fixtures after selection/recovery support |
| Patterns, mirrors, multibody and Booleans | Adapt A/B | Component-pattern predictions now ported; native part/assembly patterns and body dependency verification remain |
| Reference planes, axes and coordinate systems | Adapt A | Persistent reference roles and frame verification required; existing standard-plane handling retained |
| Sweeps, lofts and guides | Adapt A/B | Path/profile/guide references and independently verified saved geometry required |
| Surfaces and direct editing | Adapt A | Separate supported definition subsets; fail on opaque or ambiguous topology rather than guessing |
| Configurations, properties and materials | Adapt A | Add document/configuration binding and readback; avoid implicit application-default edits |
| Assemblies and mates | Adapt A | Placement calculations now ported; document kind, component occurrences, mate solve/intent checks and linked-file save/reopen remain |
| Drawings, dimensions, BOM and output | Adapt A | View-layout prediction now ported; native sheet/view/model references, annotation readback and rendered output checks remain |
| Sheet metal and flat patterns | Adapt A | Installed module/rules, bend and thickness checks, flat-pattern readback and manufacturing fixtures required |
| Weldments and cut lists | Adapt A | Profile provenance, body/member identity and cut-list verification required |
| Import/export and delivery | Adapt A/B | Explicit formats, dependencies, units, workspace paths and round-trip checks; retain hash-bound delivery |
| Mass, topology, feature/model inspection | Retain and adapt A/B | Material, bounded tree and topology inspection already improved; expand native definition and geometry measurements |
| Recovery, journal and verification | Retain checkpoints; adapt B | Inventory/material guards added; broaden native failure fixtures and crash/timeout recovery per operation |
| Image and hand-sketch interpretation | Adapt B in isolated local worker | Review dependencies and weight licences separately; produce typed dimensions/relations with uncertainty, then existing model compiler |
| Remote/mobile and conversation | Retain existing | Preserve paired private remote architecture; feed verified observations and editable references into subsequent plans |
| Simulation/FEA, Motion, CAM, Routing, Electrical, Mold, MBD, PDM and estimates | Separate conditional adapters | Some exist upstream, others are broader matrix targets; each requires its own installed products, licence, authority and domain verification |
| Arbitrary Python or VBA execution | Reject direct import | Preserve structured commands and validation; the unrestricted runners bypass those interfaces |

Every native slice must cross the same gates: reviewed 2020 API and licence → validated operation/reference contract → safe native execution → independent inspection/rebuild evidence → save/reopen and failure/recovery tests → AI planner exposure. A registered tool is not itself native certification.

## Verification

- New pure calculations and handler tests: 47 cases, covering grid/circle placements, rotated seeds, native transform conversion, bounds, both projections, all standard views, spacing, fit/no-fit (including zero-height areas with tiny views), structured errors and cancellation.
- Portable MSTest: the combined suite now passes 213 cases, including all 187 cases from this family-planning increment and 26 face-boundary/evidence cases; see the 2026-10-30 update.
- Complete net48 solution and integration-test contract compile succeeded with native interop disabled; existing async-without-await warnings in disabled native tests remain.
- Two facade tests verify the new tools perform zero COM calls; Windows CI runs these alongside the existing full unit suite and bundle/licence checks.
- Native assembly/drawing creation, native rendering and SOLIDWORKS 2020 runtime behaviour are not tested or claimed by this increment.

## Next vertical slices

1. Run the existing plate/hole/edit and topology acceptance fixtures in installed SOLIDWORKS 2020, retaining synthetic artifacts and evidence.
2. Build on the new read-only face/outer-loop resolver: add bounded face frames and safe checkpointed mutation, then finish fillet/chamfer with native readback and saved evidence.
3. Lower generic named sketch constraints/dimensions into native APIs and verify solver status; extend editing without regenerating the entire model.
4. Add core part features and verification one family at a time; share the same contracts with the AI planner.
5. Add document families and reuse the newly ported pattern/layout calculations for native assemblies and drawings.
6. Add manufacturing and local image interpretation; keep specialist engineering products as independently verified adapters.
