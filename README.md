# Claude G HUB Dictation

Start and stop **Claude Code dictation** with a button on your **Logitech G** headset, mouse or keyboard,
from anywhere: while you are in a game, with Claude behind other windows or minimized.

- Press once: dictation starts in the Claude Code session that is open in the Claude desktop app.
- Talk.
- Press again: dictation stops and the text is in the prompt box. Switch to Claude and press Enter.

Claude never takes focus, so your game keeps running (tested with Minecraft, windowed and fullscreen).

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

1. Download this repository (**Code > Download ZIP**) and unzip it.
2. Double-click **`install.cmd`**.
3. In G HUB, click your device, open **Assignments**, find **Claude > Toggle dictation** and drag it onto a
   button. (On a G733, the mic-mute button works well.)

The bridge runs in the background and starts with Windows. It adds the **Claude** integration to G HUB
whenever G HUB is running.

## Use

1. Open a Claude Code session in the Claude app (Code tab). You can leave the app behind your game or
   minimize it.
2. Press your button, talk, press it again.

If you have two sessions side by side (split view), dictation goes to the one you last typed in.

## Uninstall

Double-click **`uninstall.cmd`**, then reassign the button in G HUB.

## How it works

- **G HUB side.** G HUB runs a local websocket API (`ws://127.0.0.1:9010`) that integrations such as
  Discord and OBS use. The bridge registers a "Claude" action integration there and receives
  "Toggle dictation" whenever the assigned button is pressed.
- **Claude side.** The bridge finds the Claude Code composer's mic button with Windows UI Automation and
  posts a mouse click straight to the Claude window. Posted input does not bring a window to the front.
- **No focus stealing.** Chromium still tries to activate its window when the button takes focus, and
  Windows allows that once you have been idle for a while (a headset button is not keyboard/mouse input).
  Before each click the bridge taps **F24**, a key that no keyboard has, which keeps Windows' focus lock
  on. If Claude still ends up in front, the bridge gives focus straight back.
- **Start or stop.** The bridge reads Windows' microphone usage record to know whether Claude is
  recording. That stays accurate while Claude is hidden, unlike the button's label.
- **Minimized Claude.** Chromium ignores clicks on a minimized window, so on the first press the bridge
  restores Claude without focus and keeps it **behind all other windows** instead of minimized.

## Troubleshooting

- **Nothing happens.** Check `%LOCALAPPDATA%\ClaudeGHubBridge\bridge.log`. Every press is logged with
  the result ("start" or "stop"), whether the mic was on, and which window was in front.
  `no Claude window` means the Claude app is closed; `no dictation button` means no Claude Code session
  is open or the app is not in English.
- **"Claude" is missing in G HUB.** The bridge must be running (it starts with Windows; run
  `install.cmd` again to start it now). Then reopen G HUB.
- **Test without G HUB.** Run `"%LOCALAPPDATA%\ClaudeGHubBridge\ClaudeGHubBridge.exe" --toggle`; it
  presses the button once.

## Known limitations

- English Claude UI only (button labels).
- A minimized Claude window is restored behind your other windows on the first press, not kept minimized.
- One Claude desktop window.

## License

MIT, see [LICENSE](LICENSE).
