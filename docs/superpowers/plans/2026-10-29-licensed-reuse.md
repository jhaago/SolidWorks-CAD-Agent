# Licensed reuse implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline with one final independent review.

**Goal:** Port licensed reusable geometry/constraint components and connect them to inspection/edit workflows.
**Architecture:** Pure Core ports plus thin native Bridge adapters; retain Host/STA/approval paths.
**Tech Stack:** Production C# net48, existing Newtonsoft.Json, MSTest; portable test runner net8 only.
**Spec:** `docs/superpowers/specs/2026-10-29-licensed-reuse-design.md`

## Global constraints

- Preserve SOLIDWORKS 2020 and production net48.
- Include exact upstream MIT notices and pinned file provenance for every copied/ported component.
- Never treat query display indices as persistent references or graph validation as a native solve.
- Preserve existing working parametric/approval/journal behavior; no new planner mutation privileges.

## Review focus

- Zero/nonfinite/overflow vectors, singular/reflected transforms and dimensional-unit mistakes.
- Ambiguous topology and mismatched expected cardinality must fail, never select the first item.
- Unknown/duplicate/malformed constraint references and unsolvable-looking graphs must not be called fully defined.
- Inspection must remain bound/read-only and native edits must preserve the expected feature inventory.
- Upstream licences, transformation notes and new native-testing gaps must be explicit.

### Task 1: Geometry ports

Files: `Core/Geometry/{Vec3,Xform,AxisSpec,EdgeChain}.cs`, third-party notices/manifests,
`UnitTests/ImportedGeometryTests.cs`, portable test links.
- [x] Write/run failing math, transform, axis and chain tests.
- [x] Port source with net48 adaptations, notices and additional finite/rigid validation.
- [x] Run pure tests and net48 build.

### Task 2: Topology inspection

Files: `Core/Geometry/{TopologyQuery,TopologyFacts}.cs`, Bridge topology reader/handler,
command constant/registration, tests and native acceptance assertions.
- [x] Test filters, normals, exact counts, deterministic ordering, invalid/missing facts and budgets.
- [x] Adapt A's query logic to pure facts; add bound-document read-only native adapter.
- [x] Run available tests/builds and compile native-disabled fixtures.

### Task 3: Constraint/inventory ports

Files: `Core/Commands/SketchConstraintGraph.cs`, `Core/Geometry/FeatureInventory.cs`,
Bridge graph handler and checkpoint editor integration, unit/native tests.
- [x] Write/run graph and inventory failure tests.
- [x] Port B's algorithms, add strict input validation and connect actual tools/edit consumers.
- [x] Run portable tests and solution/unit builds; include scoped OLE handling from A.

### Task 4: Review/publication

- [x] Independent whole-branch review and one fix pass for substantive findings.
- [x] Update reuse manifest and Windows acceptance around final scope.
- [ ] Publish the existing authorized development branch; run Windows/portable CI.
- [ ] Record what was copied, what is integrated and what remains blocked by native validation.

## Execution and decisions

Local pure tests140/140, no skips. Full net48 unit-project compilation passes with no
warnings/errors. Source manifest12 entries and both exact MIT texts verified. Native
compile and actual SOLIDWORKS behavior remain pending.

Independent review: no Critical; two Important fixed (packed native edge success flag;
edge feature ownership through adjacent faces). Also fixed a reproduced native-dimension
underflow and oversized exact-count input. Added planned native assertions for length,
cardinality failures and unchanged document state. No second review was dispatched;
regression tests and full available builds verify the fix pass.

Decisions made: use modified compiled ports instead of wholesale hosts (cost: newer
upstream native services still need bounded ports); keep two inspection/preflight tools
outside the mutation-planner whitelist (cost: broad natural-language tool orchestration
remains a separate increment); use documented2011 GetCurveParams3 in the2020 adapter
instead of obsolete packed/signed ranges (cost: must compile/run against installed2020
interop); the OLE filter only retries rejected calls (cost: hung accepted COM calls still
need a watchdog/quarantine increment). Existing first-encounter ancestry display limitation
from the preceding increment remains deferred and is never selection authority.
