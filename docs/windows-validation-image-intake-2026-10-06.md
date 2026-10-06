# Image intake validation — 6 October 2026

## Branch inspection

GitHub contained feature/v1-solidworks-2020 at a2d260b and main at 191313e (22 September, design/implementation plans). The newer local feature/v2-expanded-cad line was at 43538a2, with native line/arc, desktop revisions/history and remote integration. Tested phone lifecycle work was preserved as 5d5acbe before creating feature/v2-image-design-intake. Main was not merged or changed.

## Automated validation

- Complete ordinary suite: 308 passed, zero failures, .NET Framework 4.8. Tests require no SolidWorks installation.
- Integration-test project compiled without interop; native acceptance tests were deliberately not run against the user's active model.
- New coverage includes PNG/JPEG decoding/metadata, malformed/unsupported/oversized/changed/missing files, traversal, multiple reference inputs, strict Responses schema, observed/inferred/unknown fields, refusal/HTTP/malformed/oversized/stalled responses, cancellation, same-session discussion/revisions, user evidence resolution, stale approval, unsupported CAD, image upload route limits, desktop plan button, and persisted explicit CAD approval despite Auto Mode.
- Windows native-enabled package built and passed bundle validator: artifacts/V2-ImageIntake-r3-2026-10-06/SolidWorksCadAgent-Windows-Test-Bundle.zip.
- GitHub CI for 9ee3db6 passed: https://github.com/jhaago/SolidWorks-CAD-Agent/actions/runs/37418100130 . Later prompt/documentation checkpoint requires its own CI run.
- Local builds reported NU1900 because vulnerability-feed retrieval was unavailable. Compilation/tests succeeded; no claim is made that dependency vulnerability audit completed.

## Live provider / PC acceptance

Used a generated synthetic front-view drawing of a rectangle with a central circle, not a user's private image. The existing Windows Credential Manager credential and configured model were reused without printing secrets.

Design 82965583-c7ff-42b3-b43a-a77b877f7bc2 retained one reference and four brief revisions in the same session. First analysis observed the rectangle/hole, distinguished visual centering from a dimensional constraint, and asked three critical questions. Natural dimension answers produced a 100 x 60 x 10 mm plate brief with a centered diameter-20 through-all hole.

Live testing exposed omission of answered questions and optional manufacturing details incorrectly treated as blocking dimensions. Evidence-based ResolvedQuestions and a scoped MissingDimensions prompt were implemented and tested. Final state was AwaitingDesignApproval, with no missing geometry dimensions, no outstanding critical questions, no unsupported features, no approved revision and no CAD job. Material/tolerances/finish remained explicitly unspecified. No CAD planning or execution was authorized by this live test.

Updated Host and Desktop run from the r3 native bundle. SOLIDWORKS 2020 SP0.0 remains attached to Plate-001.SLDPRT. Existing phone job 497263ba-8352-4e07-92e4-5898c24d1456 remains ReadyForReview and its saved plate still exists. Private HTTPS unauthenticated agent status returned 401 after deployment. Pairing files were not altered.

## Limits and next acceptance

Desktop layout was rendered and inspected, and an actual plan-button event was tested with a fake HTTP provider. Manual user interaction with arbitrary photographs and approved image-derived native execution remain acceptance tasks. Existing native execution remains covered by earlier PC V2 evidence; no new arbitrary reconstruction claim is made.

Image intake currently uses the desktop/loopback API. Android image upload/discussion and scan/mesh formats are future milestones. A linked CAD job freezes intake for this slice; existing CAD revision controls handle later changes. Cross-store planning idempotency after a crash remains documented follow-up work.
