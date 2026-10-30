# Feature Families Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Make upstream assembly placement and drawing layout calculations callable and tested while defining the remaining native feature programme.

**Architecture:** Pure bounded Newtonsoft contracts and algorithms in Core, two COM-free handlers in the existing Bridge registry. Native adapters and AI mutation exposure retain their existing gates.

**Tech Stack:** C# net48; portable net8 MSTest; existing Windows CI.

**Spec:** docs/superpowers/specs/2026-10-29-feature-families-design.md

## Global Constraints

- SOLIDWORKS 2020 and net48 remain mandatory.
- Pattern origins/distances are mm; SOLIDWORKS ArrayData translations are metres.
- Planning-only tools must perform zero COM calls and never claim native creation.
- Retain exact MIT notices and pinned source hashes; no upstream installer execution.
- Preserve existing modelling planner allowlist and document safety.

## Review Focus

- Wrong matrix convention rotates geometry the wrong way: assert a known rotated point using both returned representations.
- Duplicate seed at 360 degrees: assert full/partial-circle endpoints and reject repeated turns.
- Nonfinite/overflow/oversized requests: reject before allocations or COM.
- Drawing projection reversed or views overlapping: assert both projection directions, margins, gap and scale.
- Planning output mistaken for native support: bridge result flags, unchanged allowlist and documentation tests.

### Task 1: Family planning tools and programme

**Files:** Core/Geometry/ComponentPatternPlan.cs and StandardDrawingLayout.cs; Bridge/Commands/FamilyPlanningCommandHandlers.cs; existing command names, facade, contract tests and portable test project; third_party/reuse-manifest.json; docs/feature-family-programme-2026-10-29.md.

**Interfaces:** `ComponentPatternPlan.Create(JObject)` and `StandardDrawingLayout.Create(JObject)` return serializable bounded plan DTOs or throw ArgumentException. Handlers return INVALID_PARAMETERS before execution for invalid inputs and propagate cancellation.

- [x] Write tests for exact linear grid origins, rotated seeds, circular axis offsets, full/partial circle, transformation equivalence; reject invalid counts/axes/matrices/fields/units and excessive work.
- [x] Write drawing tests for projection, seven orientations, isometric size, 15 mm gaps, fixed/automatic scales, title-block reservation, no-fit, invalid extents/scales/usable areas.
- [x] Build portable tests and observe missing-type failures.
- [x] Port the named upstream algorithms with strict contracts and explicit planning-only output; register handlers.
- [x] Run portable suite and compile full solution; add bridge tests proving no COM and unchanged planner exposure.
- [x] Update pinned MIT provenance and feature-family programme. Review the increment, resolve important findings with regression tests, publish to the authorized draft PR and check CI.

Publication and Windows CI results are recorded on draft PR #7 after this source revision is published. Native SOLIDWORKS acceptance remains a separate outstanding gate.
