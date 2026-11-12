# Changelog

## 1.2 (2026-11-12)

- **Watchdog: the bridge keeps itself running.** A per-user Task Scheduler task (`ClaudeGHubBridge`) starts
  the bridge at logon and checks every 5 minutes, starting it again if it stopped for any reason. It replaces
  the old startup (Run key) entry. No admin rights needed; `uninstall.cmd` removes it.
- **Claude updates can no longer stop it.** The bridge is started by Task Scheduler, not by the installer's
  window, so it never belongs to whatever ran the installer.
- **Installing from a terminal inside the Claude app works now.** Windows gives packaged apps a private,
  redirected copy of AppData, so a bridge installed from inside Claude was invisible to Windows' startup and
  Task Scheduler. The installer detects this and points the task at the real folder, and the bridge keeps its
  log next to its exe.

## 1.1.2 (2026-11-12)

- **Works with Claude 5.** Claude 5 no longer shows up in Windows' "app is using the microphone" record,
  which the bridge used to tell a start from a stop. The bridge now watches Claude's own recording stream
  (Windows Core Audio) instead, which is accurate while Claude is hidden behind a game. Before this fix a
  quick second press on Claude 5 could be clicked twice and switch dictation straight back off.

## 1.1.1 (2026-11-09)

- **Heartbeat in the log.** Every 10 minutes the bridge writes an `alive:` line (uptime, presses, whether
  G HUB is connected), and it logs when it exits. If the button ever stops doing anything, the log shows
  when the bridge stopped.
- **Install note.** Run `install.cmd` by double-clicking it in File Explorer. A bridge started from a
  terminal inside the Claude desktop app (for example by Claude Code) counts as part of the Claude app,
  and Windows closes it, silently, when Claude updates. It then stays off until the next login.

## 1.1 (2026-10-29)

### Faster
- **Dictation starts about 0.3-0.5 s after the button press** (was up to ~4 s). The click now goes out
  first; logging, the focus check and the button search run in the background, and the bridge remembers
  where the mic button is.
- **No fixed wait after a stop.** Starting again right after stopping clicks immediately; if Claude was
  still finishing the previous transcript and ignored it, the bridge clicks once more.

### More reliable
- **No more stop-then-restart.** One press could stop dictation and then start it again ~2 s later. The
  click is now released next to the button, so Claude sees exactly one toggle.
- **Split view.** With two sessions side by side, a stop always goes to the session that is recording and
  a start goes to the one you last typed in.
- **Start or stop is decided from Windows' microphone record** instead of the button label, which goes
  stale while Claude is hidden behind a game.
- **Wireless headsets that wake up with a dead mic.** Some headsets (seen on a G733) sometimes power on
  with a microphone that sends pure silence until it is reset. When G HUB reports a headset turning on,
  the bridge sends G HUB's battery check; if a dictation start within 15 minutes still hears pure
  silence, it sends it again and then briefly mutes and unmutes the headset's mic through G HUB, which
  brings it back. The mic always ends up unmuted.
- **Crash-proof presses.** An error in one press is logged instead of stopping the bridge.

### Diagnostics
- A much more detailed `bridge.log`: every press is numbered and checked at 0.6 s, 2 s and 5 s; Claude's
  microphone on/off; which device Claude records from and its level; microphones appearing/disappearing;
  headset on/off, battery and mic mute from G HUB; Claude's dictation messages; focus-guard timing.
  The log stays on your PC and rotates at 8 MB.

## 1.0 (2026-10-25)

- First release: "Claude > Toggle dictation" action in G HUB, clicks Claude Code's mic button without
  taking focus, works with Claude behind a game or minimized.
