# Face sketches and finishing implementation plan

> For agentic workers: use executing-plans and test-driven-development. Independent validation, finishing handlers and native acceptance tasks use dispatching-parallel-agents.

Goal: correct intentional face pocket placement and provide native perimeter fillets/chamfers.
Architecture: deterministic geometric face selectors and per-execution sketch frames behind the existing owned-document bridge; shared strict command contracts feed planner and image design capability awareness.
Tech stack: C#, net48, SOLIDWORKS 2020 COM, MSTest.
Spec: `docs/superpowers/specs/2026-10-06-face-finishing-design.md`.

Constraints: preserve approvals, mm conversion, fail-stop execution, workspace policy and original user document. Never guess ambiguous faces, reuse stale geometry indices, or include hole loops in perimeter finishing.

- [ ] Baseline 362 ordinary tests; inspect branches and installed native signatures.
- [ ] Contract tests and exact face/finishing schemas; cut direction validation.
- [ ] Owned sketch-frame lifecycle and testable deterministic coordinate frame.
- [ ] Native unique-face resolver, face-bound sketch creation and native transform mapping for every supported primitive; into-body direction.
- [ ] Native perimeter-only fillet/chamfer handlers, registration and validation tests.
- [ ] Honest simulation and planner awareness of face placement and finishing limits.
- [ ] Isolated native physical acceptance for top/bottom offset pockets and hole-preserving finishing; save/reopen and original-part preservation.
- [ ] Complete ordinary tests, no-interop build, review, Windows bundle/deploy, documentation and feature-branch publication. Main stays unchanged.
