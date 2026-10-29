# Claude G HUB Dictation

Start and stop **Claude Code dictation** with a button on your **Logitech G** headset, mouse or keyboard,
from anywhere: while you are in a game, with Claude behind other windows or minimized.

- Press once: dictation starts in the Claude Code session that is open in the Claude desktop app
  (about half a second after the press).
- Talk.
- Press again: dictation stops and the text is in the prompt box. Switch to Claude and press Enter.

Claude never takes focus, so your game keeps running (tested with Minecraft, windowed and fullscreen).

**Current version: 1.1** (see [what's new](CHANGELOG.md)).

> Unofficial community project. Not made, endorsed or supported by Anthropic or Logitech.
> "Claude" and "Logitech G HUB" are trademarks of their owners. Nothing from either app is included in
> this repository; the Claude logo shown in G HUB is read from your own Claude install.

## Requirements

- Windows 10 or 11
- [Logitech G HUB](https://www.logitechg.com/innovation/g-hub.html) and a G HUB device with an assignable button
- The [Claude desktop app](https://claude.ai/download) with dictation available, set to **English** (the
  bridge finds the mic button by its English label)

No other downloads: the installer compiles the bridge with the C# compiler that ships with Windows.

## Install

1. Download the [latest release](../../releases/latest) (or **Code > Download ZIP**) and unzip it.
2. Double-click **`install.cmd`**.
3. In G HUB, click your device, open **Assignments**, find **Claude > Toggle dictation** and drag it onto a
   button. (On a G733, the mic-mute button works well.)

The bridge runs in the background and starts with Windows. It adds the **Claude** integration to G HUB
whenever G HUB is running.

**Updating from 1.0:** download 1.1 and run `install.cmd` again. Your G HUB button keeps working.

## Use

1. Open a Claude Code session in the Claude app (Code tab). You can leave the app behind your game or
   minimize it.
2. Press your button, talk, press it again.

If you have two sessions side by side (split view), a start goes to the one you last typed in and a stop
goes to the one that is recording.

## Uninstall

Double-click **`uninstall.cmd`**, then reassign the button in G HUB.

## How it works

- **G HUB side.** G HUB runs a local websocket API (`ws://127.0.0.1:9010`) that integrations such as
  Discord and OBS use. The bridge registers a "Claude" action integration there and receives
  "Toggle dictation" whenever the assigned button is pressed.
- **Claude side.** The bridge finds the Claude Code composer's mic button with Windows UI Automation and
  posts a mouse click straight to the Claude window. Posted input does not bring a window to the front.
  The click is released next to the button, so Claude counts exactly one toggle.
- **No focus stealing.** Chromium still tries to activate its window when the button takes focus, and
  Windows allows that once you have been idle for a while (a headset button is not keyboard/mouse input).
  Before each click the bridge taps **F24**, a key that no keyboard has, which keeps Windows' focus lock
  on. If Claude still ends up in front, the bridge gives focus straight back.
- **Start or stop.** The bridge reads Windows' microphone usage record to know whether Claude is
  recording. That stays accurate while Claude is hidden, unlike the button's label.
- **Minimized Claude.** Chromium ignores clicks on a minimized window, so on the first press the bridge
  restores Claude without focus and keeps it **behind all other windows** instead of minimized.
- **Headsets that wake up with a dead mic.** Some wireless headsets (seen on a G733) sometimes power on
  with a microphone that sends pure silence until it is reset. When G HUB reports a headset turning on,
  the bridge sends G HUB's battery check. If a dictation start within 15 minutes still hears pure
  silence, it sends it again and then briefly mutes and unmutes the headset's mic through G HUB, which
  brings it back. You may hear the headset's battery announcement or mute tones when this happens.

## Troubleshooting

- **Look at the log:** `%LOCALAPPDATA%\ClaudeGHubBridge\bridge.log`. Every press is numbered and checked
  at 0.6 s, 2 s and 5 s (a `WARNING` means the microphone did not do what was expected). It also shows
  which microphone Claude records from and its level, headset on/off from G HUB, and Claude's dictation
  messages. `no Claude window` means the Claude app is closed; `no dictation button` means no Claude Code
  session is open or the app is not in English.
- **"Claude" is missing in G HUB.** The bridge must be running (it starts with Windows; run
  `install.cmd` again to start it now). Then reopen G HUB.
- **Test without G HUB.** Run `"%LOCALAPPDATA%\ClaudeGHubBridge\ClaudeGHubBridge.exe" --toggle`; it
  presses the button once.

**Privacy:** the log never leaves your PC. It contains timestamps, microphone and headset names, audio
levels (not audio), and the title of the window in front when you press the button (for example a game or
browser tab title). It rotates at 8 MB; delete it any time.

## Known limitations

- English Claude UI only (button labels).
- A minimized Claude window is restored behind your other windows on the first press, not kept minimized.
- Starting again within about a second of stopping takes ~1.5 s (Claude ignores the button while it
  finishes the previous transcript; the bridge clicks again).
- One Claude desktop window.

## License

MIT, see [LICENSE](LICENSE).
