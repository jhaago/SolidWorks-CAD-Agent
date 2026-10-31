# Batched Finishing Ports Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add real bounded fillet/chamfer execution and saved definition inspection using a shared safe porting path.

**Architecture:** Existing Bridge owning STA, exact face resolver and material verifier; pure permission/intent/geometry checks; native creation/readback adapted together from pinned MIT source. Existing AI and checkpointed saved-part editing remain unchanged.

**Tech Stack:** C# net48, version28.1.0.74 references, portable MSTest and Windows CI.

**Spec:** docs/superpowers/specs/2026-10-30-batched-finishing-ports-design.md

## Global Constraints

- SOLIDWORKS 2020/net48; no new runtime, API or production dependency.
- Finishing only on newly created, unsaved, bound agent parts; opened/saved/quarantined parts fail before selection.
- One native mutation, no retries/deletion rollback; quarantine uncertain failures.
- Outer straight edges with orthogonal planar neighbors; simple uniform fillet or 45-degree chamfer, no tangent propagation.
- Preserve native type/rebuild/material/body-count/inventory and definition/geometry evidence; meaningful unique names.
- Keep finishing outside the existing AI planner until its verification and native acceptance are complete.

## Review Focus

- A generic/opened/saved document must not inherit fresh-part eligibility.
- Post-call/readback/cleanup failures must disclose partial state and quarantine subsequent finishing.
- Wrong radius/angle/count/propagation or missing geometry must never pass definition verification.
- Holes/inner edges and unrelated existing features must survive; exact selected identities must match.
- Native-enabled compilation is distinct from actual geometry/save-reopen acceptance.

### Task 1: Shared intent, geometry and document eligibility

**Files:** Core Commands/FreshPartMutationGuard.cs, Geometry/EdgeFinishingIntent.cs; Bridge document context/NewPart binding; portable/unit tests.

**Interfaces:** `FreshPartMutationGuard.RequireUnsaved(path)`; bind-created/existing/quarantine transitions. `EdgeFinishingIntent` validates definition snapshots, single-feature inventory difference, round radii and planar chamfer legs using COM-free geometry.

- [x] Write failing tests for document eligibility transitions, definition mismatch, geometry/radius/leg failures and exact feature additions.
- [x] Run RED, implement minimal pure checks, run portable GREEN and document-context tests.

### Task 2: Actual native ports and inspection

**Files:** existing EdgeFinishingCommandHandlers.cs; NativeEdgeFinishingInspector.cs; NativeFeatureMaterialVerifier.cs; GetEdgeFinishingFeatures handler; registry/project/contracts; feature-port tests and native acceptance; provenance/docs.

**Interfaces:** Extend shared material verifier with optional expected native types, native inspection and material-evidence hooks; existing callers retain defaults. Both native handlers use the same port lifecycle. `GetEdgeFinishingFeatures` returns COM-free definitions without selection/rebuild.

- [x] Write failing registration/invalid-before-COM/context tests; implement native creation and strict readback/geometry checks.
- [x] Compile all project source sets with native interop disabled and enabled against actual 2020 references; run portable tests. Standard full Windows MSBuild remains a separate CI check.
- [x] Add isolated native plate/through-hole and saved-file rejection/save-reopen fixtures; retain unimplemented face-sketch test boundary.
- [x] Record pinned source hashes/licences and implementation boundaries; review and fix important findings.
- [ ] Publish to PR #7 using the explicit publication approval received 2026-10-31.
- [ ] Run Windows unit/bundle/licence/API-guard/native-compile checks and record results; native runtime fixtures remain opt-in.
