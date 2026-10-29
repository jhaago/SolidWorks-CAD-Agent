# Open-source verification integration plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan inline.

**Goal:** Add measurable feature outcomes and complete bounded feature inspection to the existing agent.
**Architecture:** Pure evidence/traversal helpers plus thin native adapters, called by existing handlers.
**Tech Stack:** C#, production net48, SOLIDWORKS 2020 COM, MSTest; portable tests net8 only.
**Spec:** `docs/superpowers/specs/2026-10-29-open-source-verification-design.md`

## Global constraints

- Production remains .NET Framework 4.8 and SOLIDWORKS 2020 compatible.
- Existing planner/approval/reference interfaces remain in use.
- No destructive cleanup or implicit retry; partial geometry is explicitly reported.
- No third-party implementation code is copied; provenance and licence decisions are documented.

## Review focus

- Invalid native arrays or non-finite/overflow volume must stop success.
- Shared handlers must protect legacy, V2 and parametric boss/cut callers.
- Duplicate subfeatures must not hide cyclic/truncated inspection.
- Failure before mutation must prevent execution; failure after mutation must disclose partial state.
- Tests must distinguish portable policy evidence from Windows/native evidence.

### Task 1: Material evidence and feature gate

Files: new `Inspection/SolidMaterialSnapshot.cs`, `Inspection/MaterialChangeVerifier.cs`,
`Inspection/NativeFeatureMaterialVerifier.cs`; modify feature and inspection handlers.

- [x] Write failing `MaterialChangeVerifierTests` and portable test project linking exact production sources.
- [x] Observe failure, implement immutable mm³ evidence and strict material-change validation.
- [x] Integrate body mass reads and before/after checks into shared boss/cut handlers, preserving result fields.
- [x] Run selected portable tests; commit. Full net48 suite remains a Windows gate.

### Task 2: Bounded nested inspection

Files: new `Inspection/BoundedFeatureTraversal.cs`, modify `SolidWorksInspector.cs`,
new `FeatureTraversalTests.cs`.

- [x] Write failing synthetic graph tests for nested nodes, duplicates, cycles, limits and ordering.
- [x] Observe failure; implement traversal and wire native feature/subfeature APIs.
- [x] Run portable tests, build available targets; commit.

### Task 3: Assessment and acceptance

- [x] Document pinned source comparison, A/B/C/D decisions and layer diagram.
- [x] Provide synthetic native acceptance tests/procedure using existing named plate/hole/edit paths.
- [x] Attempt full suite, build all available targets and obtain independent review; resolve both Important findings.
- [x] Record exact verification boundaries and outstanding Windows/SOLIDWORKS checks.

## Execution record

Independent execution was explicitly authorized in the user's brief; no design approval gate was added.
Baseline and final net48 solution builds passed without interop (five existing warnings).
Final full net48 unit-project compilation passed without warnings/errors.
Official portable MSTest executable passed 53/53, no skips; full net48 execution aborted
in the environment's VSTest host before discovery. Native compilation and physical
acceptance remain unexecuted. See the assessment and Windows handoff for evidence.

Review found two Important issues: incomplete native mass arrays and post-mutation result
projection failures. New tests failed before their fixes; both are covered in the final
passing run. One Minor is deferred: first-encounter parent/depth can differ from native
owner ancestry for repeated features. Inspection errors still count; never use these
display fields as entity identity.

Decisions: independently implement the material/traversal pattern rather than importing
a second runtime/COM host; preserve native creation state on failure instead of deleting
features without a verified restoration contract. Cost: no automatic creation rollback
and native verification remains required. Publication was blocked by automatic approval
review, so the branch stays local pending explicit permission to push to the public repository.
