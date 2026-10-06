# Image design intake: first vertical slice

The user wants an engineering conversation around reference images before any SolidWorks planning or execution. Continue from local V2, not the older remote V1 branch. Preserve current remote lifecycle and native primitives; do not merge main. Autonomous implementation and checkpoint commits are explicitly authorized.

## Architecture and boundary

Design sessions are separate from executable CAD jobs. A session retains multiple image references, messages, typed interpretation and immutable brief revisions. An interpreter interface in Core accepts images and existing conversation; OpenAI Responses implements it in AgentHost. Design intake has no command executor or SolidWorks session. A narrowly injected callback can create a CAD plan only after current brief approval. Such jobs persist RequiresExplicitApproval and cannot auto-execute even when Auto Mode is enabled.

The brief distinguishes observations, visible text/dimensions, inferences, assumptions, unknowns, missing dimensions, user-specified dimensions, constraints, feature intent, materials, suggested views, modelling strategy, required/unsupported CAD features, confidence, warnings and at most three priority questions. Previous unanswered critical questions survive model omissions. Additional images and discussion invalidate design approval. Unsupported designs remain useful discussions/briefs but cannot enter CAD planning.

## State and storage

ImageReceived -> Interpreting -> NeedsClarification or AwaitingDesignApproval -> DesignApproved -> CADPlanning -> CADPlanCreated. Errors preserve messages, earlier revisions and images, expose a safe retryable error and block approval. Current revision ID must match every approval/planning request. A linked CAD job enters the existing AwaitingApproval state and requires its own approval.

Separate design-intake SQLite storage beside jobs.db contains JSON domain snapshots, never raw image/base64 or secrets. Images reside in configured workspace/.design-intake/{sessionId}/{referenceId}.png or .jpg. Uploads accept PNG/JPEG only, maximum 4 MiB each, eight images/16 MiB per design and 16 million decoded pixels. Decode validates actual format, not extension alone. Generated IDs determine paths, names are metadata only, creation never overwrites, and every ancestor is checked for reparse points. Read revalidates paths/hash and handles missing files safely. Images remain until explicit future retention tooling; document manual removal of abandoned designs without automatic deletion.

## API and UI

Loopback JSON API /designs supports create/list/get, reference upload/read, conversation messages, revision-specific design approval, and explicit CAD planning. Existing general request limits stay unchanged; only the reference-upload route has a bounded 6 MiB envelope. Remote/mobile can later expose these routes through paired authentication; no new unauthenticated remote routes in this milestone.

Desktop opens a practical dedicated intake window from Add Reference Image / Design Intake. Users can create/open sessions, add images, preview, discuss, inspect all brief sections/history, approve design and create a CAD plan. Persistent text states that discussion will not modify SolidWorks. CAD planning hands off to the existing desktop plan/build approval controls.

## Validation and roadmap

Ordinary tests cover malformed/oversized/non-image files, metadata/path safety, multi-reference vision requests, strict structured responses, API failure, same-session conversation, revision history, critical question carryover, stale approval, unsupported geometry and both approval gates including Auto Mode. Ordinary CI must need no SolidWorks. Do not run native tests against the user's open plate.

Future milestones: simple object image-to-CAD; uncertain handwriting/sketch recognition; multiple-view reconstruction; richer native CAD commands; scan/STL/OBJ/point-cloud intake through other interpreters; freeform/surface modelling; iterative CAD inspection. Scan formats should share reference identity/provenance and design discussion while retaining processor-specific payloads. No organic/lattice/anatomical reconstruction in this slice.
