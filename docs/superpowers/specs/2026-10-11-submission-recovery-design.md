# Submission recovery design proposal

Status: approved by the user on 2026-10-12 ("Let's do it"), following the
written proposal and recommendation to use a phone-generated UUID as the job ID.
No implementation is included.

## Purpose and acceptance

The user approved developing a reliable phone-to-native-part workflow and
durable submission recovery. Native startup and the nine current V2 scenarios
now pass; physical-phone acceptance is explicitly deferred.

Today Android creates a temporary `pending:UUID` task, but sends only the prompt
and optional image. If the response is lost, it attempts to associate the pending
task with the workstation's active job by prompt equality. Two requests can have
identical text and different images or intent, so this cannot prove identity.

Success means a lost receipt or repeated submission is associated with exactly
one persisted job. Reusing an identity with changed input is rejected. Recovery
does not repeat planning or native commands, bypass approval, or attach a pending
phone request to an unrelated job with the same prompt.

## Identity options

**Preferred: the phone-generated UUID becomes the job ID.** Add the optional
`clientSubmissionId` to the existing creation request. Requests without it retain
server-generated job IDs. Existing job-detail routes already provide exact-ID
lookup, so there is no extra lookup endpoint or public identity mapping.

Alternative: retain a separate client submission ID and server-generated job
ID, with a durable mapping and lookup endpoint. This gives more freedom to
change internal job identifiers later, at the cost of another protocol route,
mapping lifecycle and migration. The current job UUID already meets the lookup
requirements; no current requirement needs that extra layer.

The remaining sections describe the approved phone-generated job-ID option.

## Host data and API contract

- Android generates a nonempty UUID before sending a new task. Both local job
  creation routes accept the optional `clientSubmissionId`; RemoteAgent forwards
  its validated canonical UUID with the existing prompt/image whitelist.
- Reject a malformed or empty supplied UUID with HTTP 400. Omission preserves
  the existing behavior. Identity is not authentication: all current local and
  remote session authorization remains required.
- Add an immutable, nullable submission-input hash to the persisted job record.
  Existing jobs migrate with no hash and cannot be claimed as a client submission.
  New client-identified jobs store their ID, initial input hash and optional image
  atomically in the same SQLite transaction.
- The hash covers a versioned, unambiguous encoding of the exact trimmed prompt,
  image presence, media type and SHA-256 of validated image bytes. Do not hash
  mutable revision text or current settings. Do not put prompts/images into logs.
- For a matching ID and hash, return the persisted snapshot in its current state.
  For a different hash or an existing legacy job without a submission hash,
  return HTTP 409 `SUBMISSION_CONFLICT`. Never rewrite its input or existing state.
- The repository atomically distinguishes creation from an existing receipt.
  Only the creator queues/plans the job; concurrent duplicates release any
  reserved queue slot and return the existing receipt. Duplicate recovery must
  remain available when new-submission slots are full.
- Restart recovery keeps the existing fail-stop behavior for interrupted jobs.
  Repeating a completed, cancelled or failed submission returns that job and
  never starts planning/execution again. A deliberate new task gets a new UUID.
- Advertise `clientSubmissionIds: true` in Host health and propagate it through
  the existing RemoteAgent status response. Older clients continue to work.

## Android recovery

- Use the feature only when the workstation advertises support. Keep the legacy
  workflow compatible with older workstations and label its existing recovery
  limit in developer documentation; do not claim exact-ID recovery for them.
- Save the pending UUID and cancellation intent, scoped to the paired endpoint
  and device, in app-private storage before the POST. The new store retains no
  credentials, prompt or photo. It supports recovering an accepted request after
  Android process recreation, without introducing a persisted image retry queue.
- Send the same UUID and original input for any retry. A returned receipt must
  have that UUID; an unexpected ID is an unconfirmed outcome, never a substitute
  job. Recover by GET of that exact job, including after session renewal/reconnect.
- For supported submissions, never use prompt equality or an arbitrary active
  workstation job as proof of acceptance. Two identical prompts with different
  UUIDs remain independent jobs.
- A 404 during uncertain recovery is not proof that the original request was
  rejected. Keep the identity and uncertainty. A bounded retry may reuse the same
  UUID only while its original input remains in memory, the foreground session is
  authorized, and cancellation was not requested. After process recreation,
  recover by exact-ID reads; do not invent or reconstruct a missing image payload.
- Known pre-acceptance rejection ends the pending request. Conflicting input,
  repeated transport failure and unavailable receipts remain clearly surfaced;
  uncertainty must not silently create a new UUID or enable duplicate submission.
- Preserve existing generation/renewal ownership checks so an old callback cannot
  overwrite a newer task. A stop requested before receipt discovery stays bound
  to this UUID and is applied to the recovered job, never to another active job.
- Retain the tracked UUID until its terminal state is confirmed. Then clear the
  tracked record. Device revocation/unpairing clears its local tracking record.

## Verification

Host and persistence tests must prove:

1. Sequential, concurrent and post-restart repeats return one job and invoke the
   planner once; new UUIDs with identical prompts remain separate requests.
2. Changed prompt/image/media type, malformed/empty IDs and claiming an existing
   legacy job fail closed. Existing database migration and no-ID requests work.
3. Terminal and interrupted jobs are returned without replay; duplicates still
   resolve when the planning queue is full. Approval/native boundaries remain.

Remote and Android tests must prove:

1. The proxy forwards a validated ID, rejects invalid IDs and preserves auth.
2. Lost receipts recover the exact UUID even when another active job has the same
   prompt. Unexpected receipt IDs and transient 404s do not switch jobs.
3. Renewal, reconnect, Android recreation and stop-before-receipt retain identity
   and cancellation intent. Old callbacks cannot modify a newer submission.
4. Unsupported older workstations remain compatible without asserting the new
   guarantee. Missing image payload after recreation cannot cause an invented
   resend. Retries reuse the original identity and input.

After focused red/green regressions, run both Windows interop unit modes, Android
unit/build/lint/emulator checks, native V2 acceptance and bundle validation.
Physical phone, camera/gallery, remote input and mobile network acceptance remain
deferred under the user's instruction. Merge/release remains a separate step.

## Scope boundaries

No new CAD operation, automatic approval, native mutation replay, authentication
scheme, provider configuration or deployment is part of this change. A general
offline prompt/image outbox, background resubmission and cross-device task handoff
are outside scope. The existing Host, proxy, job routes and SQLite job lifecycle
remain the application boundaries.
