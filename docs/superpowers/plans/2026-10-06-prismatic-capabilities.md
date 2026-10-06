# V2 prismatic capabilities implementation plan

> For agentic workers: use superpowers:executing-plans with test-first development. Independent geometry, bridge and opt-in acceptance tasks may use superpowers:dispatching-parallel-agents.

Goal: native slots, regular polygons and blind cuts accessible through the existing approved CAD planner.
Architecture: extend the command whitelist, share provider-neutral validated sketch geometry, register native handlers, and preserve honest simulation limitations.
Tech stack: C#, net48, WinForms, MSTest, SOLIDWORKS 2020 COM.
Spec: `docs/superpowers/specs/2026-10-06-prismatic-capabilities-design.md`.

## Constraints and review focus

Preserve ownership, workspace, approvals and user's open documents. Slots use overall length; polygons use circumcircle diameter. Numerical precision must not collapse generated segments. Blind cuts begin at the base plane, not an inferred face. Normal CI remains independent of SOLIDWORKS.

- [x] Inspect branches, latest commits and command architecture; run 308-test baseline.
- [ ] Contract/geometry: failing tests, strict profile/cut validators, analytic closed rotated profiles, planner protocol.
- [ ] Bridge: failing registration/validation tests, owned active-sketch handlers, AddToDB restoration, blind cut end condition and unit conversion.
- [ ] Simulation/planning: test and implement explicit native-only rejection; expose current capabilities through shared schema/context and communicate origin-plane limitations.
- [ ] Native acceptance: isolated slot through-cut, polygon blind pocket and polygon boss; volume/bounds/features/rebuild/save/reopen; preserve original document.
- [ ] Full unit tests, solution/integration compile, review, bundle/deploy, documentation and feature-branch commits/push. Do not merge main.
