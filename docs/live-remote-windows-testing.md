# First live remote workstation test

This milestone adds manual primary-monitor viewing and input through a separate Windows RemoteAgent and the Android Remote screen. It does not add live AI desktop control. Android Home/Jobs and demo Assist/Agent remain simulated. Remote capture, real input, emergency hotkey and mobile-data access require the physical test below; CI does not certify them.

## Prerequisites and setup

Use Windows 10/11, .NET Framework 4.8 and an ordinary signed-in interactive desktop. Keep Windows awake, unlocked and the Remote Agent window open. Run RemoteAgent as your ordinary user, outside an elevated terminal. Lock screens, UAC prompts, elevated applications and other Windows sessions are unsupported. Only the primary monitor is shown. SOLIDWORKS and an OpenAI key are unnecessary for manual Remote testing.

1. Install [Tailscale for Windows](https://tailscale.com/download/windows) and [Tailscale for Android](https://tailscale.com/download/android). Sign both devices into your own private network and connect them. The Android app uses ordinary HTTPS; no Tailscale SDK or account credentials are built into it.
2. Extract the complete Windows bundle. In an Administrator PowerShell prompt, run `Setup\register-remote-agent-url.ps1` once. If that prompt uses a different account, pass `-User "YOURPC\YourOrdinaryUser"`. The script reserves only `http://127.0.0.1:5079/` and never kills a process or replaces an existing reservation.
3. Launch `RemoteAgent\SolidWorksCadAgent.RemoteAgent.exe` as the ordinary user. Leave its local stop window accessible. The CAD Agent Host on port 53741 stays separate and must not be exposed remotely.
4. In an Administrator PowerShell prompt configure the private proxy:

   ```powershell
   tailscale serve --bg http://127.0.0.1:5079
   tailscale serve status
   ```

   Follow Tailscale's HTTPS-certificate consent if requested. Use the resulting `https://yourpc.your-network.ts.net` origin in Android Settings, with no additional path. Confirm Serve reports access within your private network. Do not use Funnel or router port forwarding. If you already use Serve for other services, inspect its configuration first; do not replace an existing root mapping accidentally.
5. Install the new debug APK. In Windows Remote Agent click the pairing action and copy its one-time code. In Android Settings enter the HTTPS address and code, then **Pair with Windows**. Accept the named Android device locally in the Windows window. Pairing expires after two minutes and cannot approve itself. Codes and device credentials must not be put in logs, screenshots or Git.
6. Open Android **Remote**, then **Connect live workstation**. The initial connection is view-only. Wait for a current desktop image and press **Resume Control** to send manual input. For later launches, Settings → **Use saved live workstation** loads the encrypted pairing; the app starts disconnected.

## Normal use and cleanup

Touch and drag the fitted image for primary-button input. Right click and scroll act at the image centre; **Send key** sends an allowlisted key press such as Enter, Escape, A, Shift or F1. Arbitrary text entry and clipboard are unavailable. Images are JPEG, at most five per second, with a 1600-pixel longest edge. This is a first test build, not a low-latency video product.

Android **Stop Remote** or **Disconnect** closes its session. Leaving the Remote screen, backgrounding the app or rotating releases input; reconnect always returns to view-only until another explicit Resume Control. On Windows, **Stop Remote** and **Ctrl+Alt+F12** revoke the current session and release injected held input. Close the Remote Agent window to stop the service. Windows automatically revokes a session after a missing three-second heartbeat; do not rely on an offline phone button for immediate stopping.

Remove a paired device in Windows to revoke its credential and any active session. Android **Forget saved pairing** removes its encrypted local record while disconnected; also remove the Windows device entry. Pairings survive app/agent restarts, sessions do not. Windows stores protected verifiers under `%LOCALAPPDATA%\SolidWorksCadAgent\Remote\devices.dat`; Android uses encrypted records in its non-backup directory. Neither store contains an OpenAI key.

To stop the Serve mapping for this service:

```powershell
tailscale serve --https=443 off
tailscale serve status
```

Use this only if port 443's mapping is the dedicated Remote Agent setup. For complete removal, close RemoteAgent, remove its paired devices and optionally remove its loopback reservation from an elevated prompt with `netsh http delete urlacl url=http://127.0.0.1:5079/`. No script installs Tailscale, signs you in or kills processes automatically.

## Troubleshooting

- **Cannot reserve loopback address:** run the setup script elevated for the correct ordinary user; inspect `netsh http show urlacl url=http://127.0.0.1:5079/`. Close an older `SolidWorksCadAgent.RemoteAgent.exe` if present.
- **Connection unavailable:** check RemoteAgent is open, Tailscale is connected on both devices, Serve status names the correct origin and HTTPS is enabled. Android retries at 1, 2, 4, 8, then 15 seconds only while Remote is visible. Pairing errors require a fresh code.
- **Unauthorized or revoked:** remove the stale pairing on the phone and pair again with local Windows approval. **Busy** means another live session still owns the workstation; use the local stop control before reconnecting.
- **Stale image/control disabled:** wait for current frames, then Resume Control. Screen lock, UAC or changes to monitor layout/scaling can suspend control. Return to the ordinary desktop and reconnect.
- **Input uncertain:** the event is not retried. Check the desktop and explicitly Resume Control. If release cannot be confirmed, the Windows service fails closed.
- **Bundle build access denied:** close Agent Host, Desktop and RemoteAgent before rebuilding; the script names their process IDs when available. It never kills them.

## Pending physical acceptance — record results individually

1. On the unlocked Windows desktop verify the displayed pixels and cursor match the primary monitor; test normal Windows apps first.
2. After Resume Control verify primary click, drag, mouse-up, right click, scroll and allowlisted key down/up. Confirm inputs never occur while view-only.
3. While dragging/holding a key, test Android background, navigation, rotation and disconnect; then Windows Stop Remote, Ctrl+Alt+F12, network loss and device revocation. Check no injected key/button stays held and reconnect never restores authority automatically.
4. Test Windows lock/UAC/elevated windows, changing primary monitor, negative monitor origins and Windows DPI scaling. Confirm stale coordinates/control are rejected and the local stop remains usable.
5. Restart the agent, phone app and private proxy; check saved pairing and view-only reconnect. Revoke a device and confirm it cannot reconnect. Test two phones competing for the single session.
6. Turn phone Wi-Fi off and test over mobile data with Tailscale connected. Record frame freshness, control reliability and reconnect behavior. No mobile-data acceptance is claimed by CI.
7. Separately build a **NativeSolidWorksInterop** bundle on the SOLIDWORKS 2020 PC and run the native plate acceptance test. Reverify one solid body, 100 × 60 × 10 mm bounds, clean rebuild/no feature errors, native boss and cut, deterministic Ø20 through-hole evidence, then save/close/reopen/rebuild and reverify. The native plate and millimetre-scale tests passed on 2026-10-05, including save/reopen; see windows-validation-2026-10-05.md. Repeat those checks after changes to native commands. The CI compile-only bundle cannot perform native COM geometry.

Tailscale command guidance was checked against the official [Serve reference](https://tailscale.com/docs/reference/tailscale-cli/serve) and [examples](https://tailscale.com/docs/reference/examples/serve) on 2026-10-05.
