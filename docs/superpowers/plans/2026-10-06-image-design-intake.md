# Image Design Intake Implementation Plan

> For agentic workers: use executing-plans for integration and dispatch independent provider/storage/UI tasks in parallel.

**Goal:** A persisted image-based engineering conversation produces revisioned briefs and passes approved supported designs to the existing CAD planner.

**Architecture:** Core provider abstraction and Contracts domain objects; AgentHost owns storage/orchestration/provider/routes; Desktop consumes the loopback API. Discussion never holds a CAD executor.

**Tech stack:** Existing .NET Framework 4.8, WinForms, Newtonsoft JSON, SQLite and OpenAI Responses.

**Spec:** ../specs/2026-10-06-image-design-intake-design.md

## Global constraints

- Base V2 checkpoint 5d5acbe, dedicated feature/v2-image-design-intake; no main merge.
- Existing credential reuse authorized; no keys/images/base64 in logs/Git/SQLite.
- Four MiB/image, eight images, sixteen MiB/design, sixteen million pixels, three priority questions.
- Separate brief and CAD approvals; persistent explicit CAD approval even in Auto Mode.

## Review focus

- Provider drops critical questions: preserve unanswered ones and prevent approval.
- Later reference/reply races with approval: serialize mutation and invalidate current approval.
- Unsupported features: brief still useful; CAD planning refused.
- Restart/settings enable Auto Mode: explicit-approval flag remains persisted.
- Untrusted image names/paths or changed files: ID paths, reparse rejection, decode/hash checks.

## Tasks

- [x] Inspect local/remote branches and latest architecture, preserve tested V2 checkpoint, create feature branch.
- [x] Define DesignSession, DesignReference, DesignBriefRevision, DesignInterpretation and IImageDesignInterpreter contracts.
- [ ] Test and implement ReferenceImageStore, DesignSessionRepository and DesignIntakeService in AgentHost/Design, using fakes for vision and the planner callback.
- [ ] Test and implement OpenAiImageDesignInterpreter: multiple multimodal inputs, same conversation, strict schema and safe failure.
- [ ] Test and persist CadJob.RequiresExplicitApproval; extend CreateAndPlanAsync with a server-controlled flag; ApprovalPolicy must reject Auto Mode before CAD approval.
- [ ] Implement DesignRoutes, route-specific bounded uploads and Program composition. Route tests prove no CAD work during discussion and before approval.
- [ ] Implement DesignIntakeClient and DesignIntakeForm; wire desktop attachment entry and existing CAD job view.
- [ ] Run ordinary tests, compile-only integration checks and full native-enabled bundle build without altering the user's SolidWorks document.
- [ ] Review all changes, document usage/API/privacy/roadmap, commit useful checkpoints and publish feature branch only if normal repository access permits.

Tests: dotnet test tests/SolidWorksCadAgent.UnitTests/SolidWorksCadAgent.UnitTests.csproj with ordinary compile-only bridge; targeted filters during each task. Expected zero failures. Build all projects via existing Windows test bundle script using detected SolidWorks interop, without running native acceptance tests.
