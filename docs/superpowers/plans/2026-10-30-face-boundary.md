# Face Boundary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inspect a unique exact extreme planar face and its outer boundary without changing the model.

**Architecture:** Pure bounded decisions in Core; typed SW2020-era native resolver and existing bridge handler. No mutation-planner expansion.

**Tech Stack:** C# net48, Newtonsoft, portable MSTest and Windows CI.

**Spec:** docs/superpowers/specs/2026-10-30-face-boundary-design.md

## Global Constraints

- SOLIDWORKS 2020/net48; preserve owning STA, bound-document and existing interfaces.
- One solid body including hidden solids; face tolerance 0.00001 mm, normal tolerance 0.00000001.
- At most 20000 faces, 4096 loops, 20000 loop-edge visits, 256 outer edges.
- Read-only: no Select, ClearSelection, rebuild or model mutation; IDs remain inspection evidence.
- Do not register the unfinished native face-sketch or finishing handlers.

## Review Focus

- Pocket floors or tilted faces must not masquerade as exterior extreme faces.
- Split coplanar faces and multiple bodies must fail, not pick the first.
- Hole boundaries must remain excluded from the outer edge result.
- Malformed normals, five-value XYZUV reads, duplicate identities and oversized native arrays fail closed.
- Read-only tool flags, unchanged document state, and native acceptance limitations stay explicit.

### Task 1: Exact selector and inspection tool

**Files:** Core/Geometry/AxisAlignedFaceSelection.cs, FaceBoundaryLoops.cs; Bridge/Inspection/NativeFaceSelector.cs; Bridge/Commands/FaceBoundaryInspectionCommandHandler.cs; names/registry, portable/unit/native tests and acceptance docs.

**Interfaces:** `AxisAlignedFaceSelection.Choose(faces, axis, side, minMm, maxMm)` returns exactly one PlanarFaceFact or throws TopologyQueryException. `FaceBoundaryLoops.Outer(loops)` returns only the unique outer loop. Native `Resolve(model,axis,side)` returns Face2 for internal same-operation consumers; `Inspect` returns COM-free evidence. `OuterEdges` returns only native outer-loop edges within the current STA operation.

- [ ] Write failing pure tests: all six sides, pockets, inward/tilted faces, ambiguity, invalid evidence, loop ordering, holes, duplicates, budgets.
- [ ] Run red build/tests; implement bounded pure selectors.
- [ ] Add read-only native resolver/handler, registration and invalid-before-COM/allowlist tests.
- [ ] Add isolated SW2020 hidden-body/multi-body and through-hole save/reopen acceptance assertions.
- [ ] Run portable tests/full compile, inspect native API documentation, review changes and fix important findings with regression tests.
- [ ] Add a build-time version-28/matching-reference guard and synthetic metadata guard tests; add a hash-pinned reference-only native compile check with output outside the shipped bundle.
- [ ] Publish to the authorized draft PR; run Windows unit/bundle checks; document native acceptance still pending.
