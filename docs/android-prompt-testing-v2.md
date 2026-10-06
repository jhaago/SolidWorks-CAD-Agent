# Android prompt testing with PC V2

PC V2 includes the remote AgentHost integration from PC commit a2d260b and supports Android feature/android-v0 commit 28966384ec5938ae26f75fb27a7d9ecdf558a7d7. Use the Android **Remote** screen for real workstation jobs. Home/Jobs are demo views. This test uses a text CAD prompt; photograph input is not part of this build.

The native Windows bundle is `artifacts/V2-Android-2026-10-06/SolidWorksCadAgent-Windows-Test-Bundle.zip`. The downloaded Android debug APK is in this chat workspace under `phone-test/Android-28966384/app-debug.apk`; its successful GitHub build is https://github.com/jhaago/SolidWorks-CAD-Agent-Android/actions/runs/37391712050.

## Pair and submit

1. Connect Tailscale on Android and Windows with the same account. Keep Windows unlocked and awake. Leave AgentHost, Desktop, RemoteAgent and SOLIDWORKS running as the ordinary user.
2. Enable HTTPS/Serve for the private Tailscale network. Check `tailscale serve status` and use its HTTPS origin, without a path, in Android Settings. Only RemoteAgent on loopback 5079 is proxied. AgentHost on 53741 remains local.
3. In Windows RemoteAgent generate a pairing code. Enter the HTTPS origin and code in Android Settings, choose Pair with Windows, and accept the named phone in the Windows RemoteAgent window. Do not record codes or device credentials in logs.
4. Open Android Remote and Connect live workstation. Select Agent mode. Submit this prompt, choosing a new filename for repeated tests:

   > Create a new native SOLIDWORKS part. Sketch a centred 100 x 60 mm rectangle on Top Plane and extrude 10 mm. Add one centred diameter 20 mm Through All cut. Rebuild and save as phone-tests/Plate-001.sldprt inside the configured workspace, without overwriting. Leave existing documents untouched.

5. On Windows Desktop, refresh the job history, select the phone's job, review the plan and approve it. If clarification is requested, answer it on the PC before approval. AutoMode remains off for this test.
6. Verify the phone observes the job progressing to ReadyForReview and the PC shows the native part. Review it on the PC and complete the job. Confirm one solid body, 100 x 60 x 10 mm bounds, centred diameter20 through-hole, clean rebuild and saved native SLDPRT.
7. Separately test Stop AI during planning and execution. Wait for confirmed cancellation before selecting Manual mode and taking control. A native CAD command already in progress may finish before cancellation is observed.

## Validation recorded on 2026-10-06

- Release bundle built with NativeSolidWorksInterop against installed SOLIDWORKS 2020 SP0.0.
- 246 unit tests passed. Four native integration tests passed: plate save/reopen, millimetre scale, semicircular line-and-arc profile save/reopen, and document targeting.
- Live cloud planning used the existing Windows Credential Manager credential. Submission through the actual RemoteAgent-to-Host proxy returned HTTP202 with a persisted New job in119ms; planning subsequently reached AwaitingApproval. Test jobs were cancelled and executed no CAD commands.
- Final actual proxy status returned HTTP200 in109ms, including the unattached SOLIDWORKS case. The final Host attached successfully to SOLIDWORKS.
- Duplicate Host startup exited1 and preserved the first Host's AwaitingApproval job. Restart recovery marks interrupted work Failed and never replays native CAD commands.
- Android APK archive SHA256 matched the GitHub artifact digest: c765de7ba19b6826fcf88b3467528ace591572678aadd5bd758efca65bc8be39.
- Physical phone pairing, remote prompt submission, Stop AI and mobile-data behavior still require the test above. Local proxy smoke tests do not certify a phone connection.

## Protocol and recovery

Android's non-frame transport deadline is two seconds. The PC proxy forwards prompt creation to `/jobs/submit`, which persists the job and returns its ID before cloud planning. Android polls that ID while background planning continues. Four background submissions may be pending at once; cancellation prevents subsequent CAD commands but a cloud call already running retains its slot until it completes.

Status reads run concurrently with750ms upstream deadlines. Native status uses the last successful result when SOLIDWORKS's dispatcher is busy; that cached native status can be temporarily stale. Job status is read independently. Background errors and interrupted process recovery are reflected in persisted job state.

Tailscale setup uses its [official Serve reference](https://tailscale.com/docs/reference/tailscale-cli/serve), checked on2026-10-06. Serve is private to the Tailscale network; Funnel is unnecessary.
