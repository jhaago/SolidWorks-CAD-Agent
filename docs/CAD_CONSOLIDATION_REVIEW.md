# CAD source consolidation review

Reviewed 2026-10-08 against local commit `820f2bfaff402c4977a9e9365ba8926ece4b1ff4`, branch `feature/v2-face-sketches-and-finishing`, and the uncommitted 7d-f / 7d-g1–g4 changes. Two independent read-only reviews covered persistence/dispatch and readiness/Bridge compatibility; the main review verified their findings, reran checks and reproduced the failure below. The original review changed no implementation. The subsequently authorized 7d-g4-r correction and consolidation are recorded here alongside that review history.

## Recommendation

**Original review: fix R1 before consolidating/uploading the complete latest bundle.** The readiness, observation, additive v2 persistence and native identity result changes are suitable development infrastructure. The original dispatch boundary did not satisfy its stated no-replay acceptance. V2 approval/execution remains closed in `JobCoordinator`, so the issue was not exposed through the current public job lifecycle, but was reproduced through the new public boundary class.

After R1 and its regression tests pass, publish source to the existing feature branch as a reviewed foundation update. This is not acceptance for enabling v2 jobs, deploying binaries, or merging the entire feature branch into main.

## R1 — high priority: uncertainty persistence failure permits replay

Location: `src/SolidWorksCadAgent.AgentHost/Jobs/CadV2NewPartDispatchBoundary.cs:118` (swallowed persistence failure), and `:65` (unconditional removal of the attempt guard). The related transaction is `SqliteJobRepository.V2Attempts.cs:270`.

1. Begin with a Prepared attempt, Pending model and real Executing job.
2. Dispatch NewPart; its outcome becomes ambiguous, for example an executor throws after mutation or loses its result.
3. The database fails while marking the attempt Uncertain. The transaction rolls back, leaving both records unchanged.
4. The boundary swallows the persistence exception and removes its in-process guard.
5. Another boundary instance accepts the same Prepared attempt and dispatches NewPart again.

**Reproduced on the current compiled source** with an isolated SQLite database, an injected trigger aborting the Uncertain update and a fake executor representing lost native output. Two boundary calls produced:

```text
Executor calls=2; attempt=Prepared; model=Pending
CONFIRMED: a failed uncertainty write allows replay in the same process.
```

No SOLIDWORKS COM calls or user documents were used. The diagnostic source/executable/database are under ignored `artifacts/consolidation-review/`; they are local evidence, not files to upload.

Recommended bounded fix (Task 7d-g4-r): once dispatch begins, keep a process-wide terminal fence if durable closure cannot be confirmed. Release pre-dispatch claims normally; never release an ambiguously mutated attempt merely because writing Uncertain failed. Preserve the original executor/commit error while recording the persistence failure in diagnostics. Startup recovery must still convert unresolved Prepared attempts to Uncertain before any dispatch. Add fault-injection tests for failed Uncertain persistence after executor exception/null output, retry through a new boundary instance, and recovery after reopening the repository. A persisted Dispatching state/claim is a possible later design for multiple Host processes, requiring an explicit schema and recovery migration.

## Verification evidence

### R1 correction — Task 7d-g4-r

The user authorized the correction and source consolidation on 2026-10-08. The boundary now retains a process-wide terminal fence after dispatch if writing Uncertain fails; it releases the guard on pre-dispatch rejection or confirmed durable Applied/Uncertain outcomes. It rethrows the original error with the persistence exception attached as `Exception.Data["CadV2UncertainPersistenceFailure"]`. No persisted state/schema or COM behavior changed. Retained fences deliberately last until process exit; startup recovery must succeed before dispatch in a new Host. Multi-process fencing remains a separate requirement.

Real-SQLite fault tests cover missing output, executor exception and failed Applied commit with failed Uncertain writes, cross-instance retries, and reopened-repository recovery. A positive test covers retry after pre-dispatch rejection. Baseline was 10/10; new tests first produced 3 failures/11 passes against the old boundary, and the corrected focused suite passed 14/14. The initial failure is retained below as review history; it is not the corrected source's acceptance result.

**Final corrected-source acceptance:** full unit suites passed **511/511** outside the sandbox with interop disabled and enabled; TRX files are `task-7d-g4-r-interop-disabled.trx` and `task-7d-g4-r-interop-enabled.trx` under the unit test results directory. Both full solution builds passed with **0 warnings/errors**. Independent bounded review found no correctness issue and separately executed focused **14/14**. R1 is corrected for the documented single-Host boundary. The source bundle is suitable for the existing feature branch; no native v2 Host job or deployment acceptance is implied.

| Check | Actual result |
|---|---|
| Full native-enabled unit suite outside sandbox | **507/507 passed**, no skips; `tests/SolidWorksCadAgent.UnitTests/TestResults/consolidation-review-2026-10-08-unsandboxed.trx` |
| Full interop-disabled unit suite outside sandbox | **507/507 passed**, no skips; `tests/SolidWorksCadAgent.UnitTests/TestResults/consolidation-review-interop-disabled.trx` |
| Native-enabled full solution build | **0 warnings / 0 errors** |
| Interop-disabled full solution build | **0 warnings / 0 errors** |
| Full suite inside sandbox | 506 passed / 1 failed at the existing `HttpListener` platform boundary; confirmed environmental by the unsandboxed run |
| Git whitespace check | `git diff --check` passed; only line-ending normalization notices |
| R1 isolated fault probe | Replay reproduced; this scenario is missing from the current passing suite |
| Existing controlled native identity evidence | Reviewed the passed 1/1 g3 TRX. No new native acceptance run; no end-to-end v2 Host job claim |

The passing suite does not invalidate R1: its existing uncertainty tests assume the Uncertain database write succeeds.

## Architecture and scope assessment

- Preserve the registry/availability seam, current versioned executor, execution-only identity context and Bridge STA boundary. The latest helpers do not change planner advertisement or v1 job approval behavior.
- Preserve current-plan hash, ownership, Pending model and atomic Applied activation checks. Additive schema v6 migration and rollback tests preserve historical plan/command rows.
- The inspector's separate reads are observations, not an atomic authorization snapshot. Readiness checks caller-supplied JSON/IDs for structure and registration; they do not establish job provenance. Documentation already identifies these limits.
- Multi-process dispatch fencing, later sketch output commitment, feature-consumer gates, SavePart finalization and end-to-end v2 job acceptance remain future integration requirements. No broad CAD expansion is needed to address R1.
- Useful non-blocking test improvement: migrate a populated v5 ManagedModels/EntityReferenceBindings fixture, including native token bytes, and assert those identity rows survive unchanged. Current v5 migration fixtures create those tables empty.

## GitHub and repository state

Live branch hashes were read from GitHub during this review:

- SolidWorks `feature/v2-face-sketches-and-finishing` is already at **820f2bf**. The latest slices remain uncommitted locally and therefore are not uploaded.
- SolidWorks `main` is **191313e**; the feature branch is 202 commits ahead of that ancestor. This review covers the latest local bundle, not an audit of all 202 commits for a main-branch merge.
- Android `feature/phone-first-v2` is **301b337**, matching the clean local checkout; no separate Android source upload is pending. Android `main` remains **edf3ae3**. This comparison does not certify an APK release.

For consolidation, explicitly include the new untracked implementation/test files along with tracked schema, Bridge and documentation changes. Keep the unrelated whitespace-only `PrismaticCapabilityTests.cs` edit out of the consolidation commit and leave it intact locally. Existing ignore rules cover generated binaries, test results, databases, CAD documents and review artifacts; no binary or state files are part of the intended source bundle.

Before a later deployed release, back up the agent database and define rollback handling: schema v6 is additive, but the old Host rejects a database with a newer user_version. Do not replace a running build during source consolidation.

## Order to execute

1. Complete Task 7d-g4-r and its failed-persistence/retry regression tests.
2. Rerun the relevant tests and both build modes; re-review the small fix.
3. Correct the capability/handoff acceptance evidence and assemble a source-only commit, preserving unrelated work.
4. Push the reviewed commit to the existing SolidWorks feature branch when instructed to publish.
5. Continue 7d-g5 sketch-attempt/binding preparation. Keep v2 approval closed until its separate integration acceptance is met.

The original review performed no commit, push, merge, deployment or running-binary replacement. The user subsequently authorized the fix and source consolidation. This source commit includes the reviewed 7d-f / 7d-g1–g4-r bundle and updated handoff; its identity is available in Git history. Publication targets only the existing feature branch. Main merge, binary deployment and further roadmap development remain outside this consolidation. The unrelated Prismatic whitespace edit is preserved locally and excluded.
