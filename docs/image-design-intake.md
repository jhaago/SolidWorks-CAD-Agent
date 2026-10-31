# Image design intake in PC V2

## Use it

Open **Image / Design Intake** in the Windows desktop application. Enter a title and choose **New design**, or open a saved design. Attach PNG/JPG/JPEG references, optionally labelling their views. Select a reference to preview it. Enter an instruction such as “Analyse this bracket” and choose **Analyze / Send**. Continue answering in that same window; additional images belong to the same session.

The Conversation tab retains discussion. The Design brief / revisions tab shows observations and visible text/dimensions separately from inference, assumptions, unknowns and requested dimensions. It also shows constraints, feature intent, materials, suggested views, modelling strategy, capability gaps, confidence and warnings. Prior revisions are read-only. The model asks at most three priority questions per response; unanswered critical questions survive omissions in later model responses. Question resolutions require a stable prior question ID and verbatim evidence present in user text; unknown resolutions require the exact previous unknown and user evidence. Both resolution records are displayed. MissingDimensions is reserved for geometry or requirements necessary for the stated purpose; optional unspecified manufacturing details remain unknowns/warnings for a nominal prototype.

**Discussion never modifies SolidWorks.** Approve Design Brief becomes available after required dimensions and critical questions are resolved. Adding a reference or sending another message clears that approval. **Create CAD Plan** requires the current approved revision and no unsupported features. It opens the existing CAD job workflow, where **Approve Build** is a separate action. Image-derived jobs persist a mandatory manual-approval flag, including across restarts and Auto Mode changes.

After a CAD job is linked, this first milestone freezes the intake session; use existing CAD job revision controls for changes. A future milestone will support returning from a CAD job to revised design discussion while cancelling or superseding its old approval. No arbitrary photo reconstruction, loft/surface/lattice/anatomical scan modelling is claimed.

## Architecture and persistence

Contracts/Design contains DesignSession, DesignReference, DesignMessage, DesignBriefRevision and the typed DesignInterpretation. Core/Ai exposes IImageDesignInterpreter and byte-content request objects. AgentHost/Design owns storage, service and routes and has no SolidWorks session or CAD executor. A planner callback is accessible only after design approval. The OpenAI implementation is replaceable with a local/offline interpreter implementing the same contract.

Design sessions are saved in `%LOCALAPPDATA%/SolidWorksCadAgent/design-intake.db`, separate from the existing jobs database. Session snapshots contain messages, brief revisions and reference metadata; never image bytes or base64. Brief revisions retain reference IDs. Jobs schema version 3 adds RequiresExplicitApproval with a safe default for pre-existing jobs.

States: ImageReceived -> Interpreting -> NeedsClarification / AwaitingDesignApproval -> DesignApproved -> CADPlanCreated. API/provider failures retain the user's message, prior revisions and references, clear approval, set a safe error and return to NeedsClarification. Retrying analysis is explicit. A process crash during planning can leave an unlinked CAD job; no such job can execute automatically. Cross-store planning idempotency is a follow-up milestone.

## Image storage and privacy

Files live only in the configured workspace's `.design-intake/{designId}/{referenceId}.png|.jpg` directories. IDs determine storage paths; supplied names are sanitized metadata. Files are created without overwriting. Ancestors are checked for links/reparse points, and reads verify path, size, decoded format and SHA-256. The store accepts decoded PNG/JPEG only, maximum 4 MiB each, eight references / 16 MiB per session, and 16 million pixels per image. A mismatched extension or malformed image is rejected.

Images and briefs remain local until **Analyze / Send** explicitly sends current images and conversation to the configured OpenAI model. Requests use `store=false`; this setting does not imply a broader provider retention guarantee. Credentials remain in Windows Credential Manager and never enter requests to the local client, SQLite, logs or Git. The provider requests a strict interpretation function with no CAD tools. Responses are limited to 256 KiB and the complete network operation has a 90-second deadline.

References and conversations are retained until manually removed; automatic deletion is not implemented. Close the host before manually removing abandoned design records/files, and preserve records needed for audit. Workspace and `.design-intake` folders are ignored by Git. Do not place private imagery elsewhere in the source tree.

## Loopback API

All routes retain the existing local-host/browser-origin boundary. No new unauthenticated remote route is exposed.

| Method/path | Body/result |
|---|---|
| POST /designs | `{title}` -> new session |
| GET /designs | Saved session array |
| GET /designs/{id} | Session including messages, references and revisions |
| POST /designs/{id}/references | `{fileName,base64,label,viewType}` -> updated session |
| GET /designs/{id}/references/{referenceId} | `{fileName,mediaType,base64}` for preview |
| POST /designs/{id}/messages | `{message}` -> analysis and new brief revision |
| POST /designs/{id}/approve | `{revisionId}` -> current brief approval |
| POST /designs/{id}/plan-cad | `{revisionId}` -> linked CAD job, awaiting separate CAD approval |

Messages are limited to 4,000 characters, titles to 200. Reference upload alone has a 6 MiB JSON envelope; existing routes retain their one-MiB request limit. Full API/domain state is suitable for a later paired Android upload/discussion client; that UI and paired routing are not implemented here.

## Future milestones

1. Harden image intake with cross-store planning idempotency and design-to-CAD revision provenance.
2. Simple supported plate/bracket images to reviewed native CAD.
3. Hand-sketch recognition with explicit uncertain handwriting/dimension confirmation.
4. Multi-view reasoning and correspondence across photographs/sketches.
5. Broader tested native SolidWorks feature vocabulary.
6. STL/OBJ/scan/point-cloud references through format-specific intake processors, using shared reference identity, provenance and discussion/approval objects.
7. Freeform/surface modelling and scan fitting with explicit capabilities and geometry verification.
8. Iterative AI inspection of generated CAD against approved intent.

OpenAI API implementation follows the official [image input guide](https://developers.openai.com/api/docs/guides/images-vision) and [function calling guide](https://developers.openai.com/api/docs/guides/function-calling). Photographs cannot establish hidden geometry, absolute dimensions, anatomical fit or manufacturability on their own.
