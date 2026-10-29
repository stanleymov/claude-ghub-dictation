// Claude <-> Logitech G HUB bridge.
// Registers a "Claude" action integration with the G HUB agent (ws://127.0.0.1:9010) and, when
// G HUB invokes "Toggle dictation", clicks the dictation button of the Claude Code session shown in
// the Claude desktop window. The click is posted to the window, so Claude can stay behind a game,
// covered or minimized, and never takes focus. UI Automation is only used to find the button.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;

static class Bridge
{
    const string Version = "1.1";
    const string Identifier = "claude_dictation";
    const string ActionToggle = "toggle_dictation";
    const string T = "type.googleapis.com/logi.protocol.integrations.";

    // Mic button labels in the Claude composer (en-US).
    const string LabelIdle = "Press and hold to record";
    const string LabelActive = "Stop dictation";

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeGHubBridge");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object LogLock = new object();
    static string integrationGuid;
    static int pressCount;

    [STAThread]
    static int Main(string[] args)
    {
        // Log numbers the same way on every PC (no "0,5" vs "0.5").
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // UIA rectangles and posted mouse coordinates must both be in physical pixels.
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Directory.CreateDirectory(Dir);
        // --toggle: one press without G HUB, for testing.
        if (args.Length > 0 && args[0] == "--toggle") { Log("manual toggle: " + ToggleDictation(0)); return 0; }

        bool created;
        using (new Mutex(true, "Local\\ClaudeGHubBridge", out created))
        {
            if (!created) return 0;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("CRASH: " + e.ExceptionObject);
            Log("bridge " + Version + " started (" + Environment.OSVersion + ", Claude " + ClaudeVersion() + ")");
            Diag.Start(Log);
            while (true)
            {
                try { RunSession().Wait(); }
                catch (Exception e) { Log("session ended: " + Flatten(e)); }
                Thread.Sleep(5000);
            }
        }
    }

    static async Task RunSession()
    {
        using (var ws = new ClientWebSocket())
        {
            ws.Options.AddSubProtocol("json");
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:9010"), CancellationToken.None);
            Log("connected to G HUB");
            socket = ws;

            var instance = Guid.NewGuid().ToString();
            await Send(ws, "SET", "/api/v1/integration/register", new Dictionary<string, object> {
                { "@type", T + "SDKIntegration" }, { "integrationIdentifier", Identifier }, { "name", "Claude" },
                { "author", "StanleyMOV" },
                { "description", "Control Claude Code dictation from your Logitech G devices." },
                { "icon", Icon() }, { "integrationType", "ACTION" } });
            await Send(ws, "SET", "/api/v1/integration/activate", new Dictionary<string, object> {
                { "@type", T + "InstanceInfo" }, { "integrationIdentifier", Identifier },
                { "instanceGuid", instance }, { "sdkType", "ACTION" } });
            await Send(ws, "SUBSCRIBE", "/api/v1/integration/sdk/action/invoke", null);

            var buffer = new byte[1 << 16];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage);
                await Handle(ws, sb.ToString(), instance);
            }
        }
    }

    static async Task Handle(ClientWebSocket ws, string text, string instance)
    {
        var msg = Json.Deserialize<Dictionary<string, object>>(text);
        var path = msg.ContainsKey("path") ? msg["path"] as string : null;
        TaskCompletionSource<Dictionary<string, object>> waiter;
        var msgId = msg.ContainsKey("msgId") ? msg["msgId"] as string : null;
        if (msgId != null && Pending.TryRemove(msgId, out waiter)) { waiter.TrySetResult(msg); return; }
        if (path != null && DeviceEventPaths.Contains(path))
        {
            if (path == "/devices/state/activated")
            {
                deviceActivatedAt = DateTime.Now;
                var info = msg.ContainsKey("payload") ? msg["payload"] as Dictionary<string, object> : null;
                if (info != null && (info.ContainsKey("deviceType") ? info["deviceType"] as string : null) == "HEADSET")
                {
                    activatedHeadset = (string)info["id"];
                    kickedAfterSilence = false;
                    var id = activatedHeadset;
                    var _ = Task.Run(() => { Thread.Sleep(2000); Kick(id, "headset turned on"); });
                }
            }
            var body = msg.ContainsKey("payload") ? Json.Serialize(msg["payload"]) : "";
            Log("G HUB event: " + path + " " + (body.Length > 400 ? body.Substring(0, 400) + "..." : body));
            return;
        }
        var payload = msg.ContainsKey("payload") ? msg["payload"] as Dictionary<string, object> : null;

        if (path == "/api/v1/integration/activate" && payload != null && payload.ContainsKey("integrationGuid"))
        {
            // The action list can only be registered against the guid G HUB assigned on activation.
            integrationGuid = (string)payload["integrationGuid"];
            Log("activated as " + integrationGuid);
            foreach (var p in DeviceEventPaths) await Send(ws, "SUBSCRIBE", p, null);
            var _ = Task.Run(() => HeadsetPoller(ws));
            await Send(ws, "SET", "/api/v1/integration/sdk/action", new Dictionary<string, object> {
                { "@type", T + "Integration.Action.Register" },
                { "actions", new object[] { new Dictionary<string, object> { { "name", "Toggle dictation" }, { "actionId", ActionToggle } } } },
                { "instance", new Dictionary<string, object> { { "integrationGuid", integrationGuid }, { "instanceGuid", instance }, { "sdkType", "ACTION" } } } });
            return;
        }
        if (msg.ContainsKey("result"))
        {
            var res = msg["result"] as Dictionary<string, object>;
            if (res != null && (string)res["code"] != "SUCCESS") Log(path + " failed: " + res["code"] + " " + res["what"]);
            return;
        }
        if (path == "/api/v1/integration/sdk/action/invoke" && payload != null
            && (string)payload["integrationGuid"] == integrationGuid && (string)payload["actionId"] == ActionToggle)
        {
            var id = Interlocked.Increment(ref pressCount);
            Log(string.Format("press #{0}: received from G HUB", id));
            var t = new Thread(() =>
            {
                try { Log(string.Format("press #{0}: {1}", id, ToggleDictation(id))); }
                catch (Exception e) { Log(string.Format("press #{0}: ERROR {1}", id, e)); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return;
        }
        if (path != null && path != "/api/v1/integration/sdk/action/invoke") Log("G HUB message: " + msg["verb"] + " " + path);
    }

    static readonly object ToggleLock = new object();

    // Clicks the composer mic button: stops an active dictation if there is one, otherwise starts one.
    // The click is posted to Claude's window as mouse messages, so Claude never takes focus. UI Automation
    // is only used to read the button's label and position (its Toggle/Invoke actions activate the window).
    static string ToggleDictation(int id)
    {
        var clock = Stopwatch.StartNew();
        lock (ToggleLock)
        {
            var waited = clock.ElapsedMilliseconds;
            var windows = ClaudeWindows();
            if (windows.Count == 0) return "no Claude window";
            var handles = windows.Select(w => new IntPtr(w.Current.NativeWindowHandle)).ToList();
            var previous = Native.GetForegroundWindow();
            var idle = Native.IdleSeconds();
            // Diagnostics that need G HUB or a full window scan run beside the click, never before it.
            Background(() => Log(string.Format("press #{0}: context: user idle {1:0.0} s, in front '{2}' | headset: {3} ({4})",
                id, idle, Native.Title(previous), HeadsetState(), SinceActivation())));
            var result = ToggleIn(windows);
            result += string.Format(" | clicked {0} ms after the press reached the bridge{1}", clock.ElapsedMilliseconds,
                waited > 50 ? string.Format(" (waited {0} ms for the previous press)", waited) : "");
            if (!handles.Contains(previous)) Background(() => { var g = GuardFocus(previous, handles); if (g != "") Log(string.Format("press #{0}:{1}", id, g)); });
            if (result.StartsWith("start") || result.StartsWith("stop")) Verify(id, result.StartsWith("start"), windows);
            Background(() => RefreshCaches(windows));
            return result;
        }
    }

    static void Background(Action a)
    {
        var t = new Thread(() => { try { a(); } catch (Exception e) { Log("background error: " + e.Message); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Checks after a press whether Claude's microphone really changed, and records what Claude shows.
    static int latestVerified;

    static void Verify(int id, bool expectOn, List<AutomationElement> windows)
    {
        latestVerified = id;
        var t = new Thread(() =>
        {
            try
            {
                foreach (var ms in new[] { 600, 2000, 5000 })
                {
                    Thread.Sleep(ms == 600 ? 600 : ms == 2000 ? 1400 : 3000);
                    if (latestVerified != id) { Log(string.Format("press #{0}: later checks skipped (press #{1} came after)", id, latestVerified)); return; }
                    var mic = Native.ClaudeMicInUse();
                    var caps = Diag.ClaudeCaptureSessions();
                    var capText = caps.Count == 0 ? "no capture stream" : string.Join("; ", caps.Select(c =>
                        string.Format("'{0}' {1} level {2:0.000}{3}", c.Device, c.State == 1 ? "active" : "inactive", c.Peak, c.Mute ? " DEVICE MUTED" : "")));
                    var ok = mic == expectOn;
                    if (ms == 2000 && expectOn && mic == true && activatedHeadset != null && !kickedAfterSilence
                        && (DateTime.Now - deviceActivatedAt).TotalMinutes < 15
                        && caps.Count > 0 && caps.All(c => c.State != 1 || c.Peak <= 0.0001f))
                    {
                        kickedAfterSilence = true;
                        Kick(activatedHeadset, string.Format("press #{0} hears pure silence {1:0} s after the headset turned on", id, (DateTime.Now - deviceActivatedAt).TotalSeconds));
                        Thread.Sleep(3000);
                        var after = Diag.ClaudeCaptureSessions();
                        if (Native.ClaudeMicInUse() == true && after.Count > 0 && after.All(c => c.State != 1 || c.Peak <= 0.0001f))
                            MuteToggleKick(activatedHeadset, id);
                        else
                            Log(string.Format("press #{0}: after the battery check the mic {1}", id,
                                after.Any(c => c.State == 1 && c.Peak > 0.0001f) ? "has signal again" : "is no longer recording"));
                    }
                    Log(string.Format("press #{0}: +{1}ms mic {2} (expected {3}){4} | {5} | Claude shows: {6}", id, ms,
                        mic == true ? "ON" : mic == false ? "off" : "unknown", expectOn ? "ON" : "off",
                        ok ? "" : " WARNING not reached", capText, DictationMessages(windows)));
                }
            }
            catch (Exception e) { Log(string.Format("press #{0}: verify error {1}", id, e.Message)); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Dictation-related text Claude currently shows (errors, toasts, the mic button label).
    static string DictationMessages(List<AutomationElement> windows)
    {
        // Claude's own dictation strings (en-US), matched from the start so session titles and paths are ignored.
        var words = new[] { "Dictation", "Starting dictation", "Finishing dictation", "Stop dictation", "Press and hold to record",
            "Couldn\u2019t start dictation", "Couldn't start dictation", "Claude needs microphone", "Turn on microphone",
            "You\u2019re offline", "Voice mode", "To use dictation", "Your device, not this site" };
        var found = new List<string>();
        foreach (var w in windows)
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string n;
                try { n = e.Current.Name; } catch { continue; }
                if (string.IsNullOrEmpty(n) || n.Length > 160) continue;
                if (words.Any(k => n.StartsWith(k, StringComparison.Ordinal)) && !found.Contains(n)) found.Add(n);
                if (found.Count >= 6) break;
            }
        return found.Count == 0 ? "-" : string.Join(" / ", found.Select(f => "\"" + f + "\""));
    }

    static string ClaudeVersion()
    {
        try
        {
            var p = Process.GetProcessesByName("claude").Select(x => { try { return x.MainModule.FileName; } catch { return null; } })
                .FirstOrDefault(f => f != null && f.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0);
            return p == null ? "not running" : FileVersionInfo.GetVersionInfo(p).ProductVersion;
        }
        catch { return "?"; }
    }

    // Chromium tries to activate its window when the click focuses the button. Native.Click keeps Windows'
    // foreground lock armed so that attempt fails; if Claude still ends up in front, hand focus straight back.
    static string GuardFocus(IntPtr previous, List<IntPtr> claude)
    {
        for (int i = 0; i < 16; i++)
        {
            var fg = Native.GetForegroundWindow();
            if (claude.Contains(fg))
            {
                var held = Stopwatch.StartNew();
                Native.GiveForeground(previous);
                Native.SendToBack(fg);
                while (claude.Contains(Native.GetForegroundWindow()) && held.ElapsedMilliseconds < 2000) Thread.Sleep(5);
                return string.Format(" [focus guard: Claude took focus {0} ms after the click; handed back in {1} ms{2}]",
                    i * 50, held.ElapsedMilliseconds, claude.Contains(Native.GetForegroundWindow()) ? ", FAILED - Claude still in front" : "");
            }
            Thread.Sleep(50);
        }
        return "";
    }

    // Where the last dictation was started: the window and the mic's centre relative to that window. With split
    // view there is one composer per pane, and while Claude is hidden their labels are stale, so a stop must go
    // back to the same composer; clicking the other pane's mic would stop dictation and start a new one there.
    static IntPtr startedWindow;
    static System.Windows.Point startedAt;
    static DateTime startedTime;
    static DateTime stoppedTime;

    // Claude ignores the mic button while dictation is connecting (~0.5 s after a start) and while it finishes
    // the transcript after a stop (usually well under a second). A start right after a stop is clicked at once
    // and clicked again if the microphone did not open.
    const double FinishingSeconds = 3;

    static string ToggleIn(List<AutomationElement> windows)
    {
        // Windows' microphone record is accurate even while Claude is hidden, unlike the button labels.
        bool? micOn = Native.ClaudeMicInUse();
        foreach (var w in windows)
        {
            var h = new IntPtr(w.Current.NativeWindowHandle);
            var note = "";
            // A minimized Chromium window ignores input. Restoring it is only focus-safe once (Chromium remembers
            // the click's focus and re-activates on a later restore), so it stays parked behind other windows.
            if (Native.IsIconic(h)) { Native.Park(h); note = " (was minimized, parked behind other windows)"; }
            var mics = MicButtons(w);
            if (mics.Count == 0) continue;

            var win = Native.WindowRect(h);
            var active = mics.FirstOrDefault(b => b.Current.Name == LabelActive);
            // Just after a start the microphone may not be open yet; a quick second press is still a stop.
            bool recentStart = startedWindow == h && (DateTime.Now - startedTime).TotalSeconds < 3;
            bool stopping = micOn.HasValue ? micOn.Value || recentStart : active != null;

            if (stopping && micOn == false && recentStart)
            {
                // A quick stop: wait until the microphone is actually open so Claude is past "connecting".
                for (int i = 0; i < 50 && Native.ClaudeMicInUse() != true; i++) Thread.Sleep(50);
                Thread.Sleep(250);
                note += " (waited for start to connect)";
            }
            bool justStopped = !stopping && (DateTime.Now - stoppedTime).TotalSeconds < FinishingSeconds;

            AutomationElement target;
            if (stopping)
                target = active
                    ?? (startedWindow == h ? Closest(mics, new System.Windows.Point(win.X + startedAt.X, win.Y + startedAt.Y)) : null)
                    ?? ComposerInUse(w, mics);
            else
                target = ComposerInUse(w, mics);

            var rect = target.Current.BoundingRectangle;
            var release = ReleasePoint(w, rect);
            Native.Click(h, rect, release);
            if (justStopped)
            {
                // Claude may still have been finishing the previous transcript and ignored the click.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var waitUntil = DateTime.Now.AddMilliseconds(1100);
                    while (DateTime.Now < waitUntil && Native.ClaudeMicInUse() != true) Thread.Sleep(30);
                    if (Native.ClaudeMicInUse() == true) break;
                    Native.Click(h, rect, release);
                    note += " (clicked again: Claude was still finishing the previous dictation)";
                }
            }
            if (!stopping)
            {
                startedWindow = h;
                startedAt = new System.Windows.Point(rect.X + rect.Width / 2 - win.X, rect.Y + rect.Height / 2 - win.Y);
                startedTime = DateTime.Now;
            }
            else { startedWindow = IntPtr.Zero; stoppedTime = DateTime.Now; }

            // Diagnostics for presses that misbehave: where the click went and what covered Claude.
            var fg = Native.GetForegroundWindow();
            var diag = string.Format(" | mic {0} | {1} composer(s), clicked {2:0},{3:0} released {6:0},{7:0} | window {4} | in front: {5}",
                micOn.HasValue ? (micOn.Value ? "on" : "off") : "unknown", mics.Count, rect.X, rect.Y,
                Native.Describe(h), fg == h ? "Claude" : Native.Title(fg), release.X, release.Y);
            return (stopping ? "stop" : "start") + note + diag;
        }
        return "no dictation button (is a Claude Code session open?)";
    }

    // Claude toggles dictation on pointerdown and relies on the matching pointerup landing on the same button to
    // ignore the click that follows. While Claude is visible the button re-renders between the two, the click
    // counts as a second toggle, and dictation restarts after it stops. Releasing on empty space next to the
    // button means no click reaches it. Never release on the session Stop button left of the mic.
    static System.Windows.Point ReleasePoint(AutomationElement w, System.Windows.Rect mic)
    {
        var key = string.Format("{0:0},{1:0},{2:0},{3:0}", mic.X, mic.Y, mic.Width, mic.Height);
        System.Windows.Point cached;
        lock (CacheLock) if (ReleaseCache.TryGetValue(key, out cached)) return cached;
        var point = FindReleasePoint(w, mic);
        lock (CacheLock) ReleaseCache[key] = point;
        return point;
    }

    static System.Windows.Point FindReleasePoint(AutomationElement w, System.Windows.Rect mic)
    {
        var candidates = new[] {
            new System.Windows.Point(mic.Right + 12, mic.Y + mic.Height / 2),
            new System.Windows.Point(mic.X + mic.Width / 2, mic.Y - 8),
        };
        var interactive = new HashSet<ControlType> { ControlType.Button, ControlType.Edit, ControlType.Hyperlink,
            ControlType.CheckBox, ControlType.ComboBox, ControlType.MenuItem, ControlType.RadioButton, ControlType.TabItem,
            ControlType.ListItem, ControlType.SplitButton };
        var win = w.Current.BoundingRectangle;
        var all = w.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList();
        foreach (var p in candidates)
        {
            if (!win.Contains(p)) continue;
            bool hit = all.Any(e =>
            {
                try { var c = e.Current; return interactive.Contains(c.ControlType) && c.BoundingRectangle.Contains(p); }
                catch { return false; }
            });
            if (!hit) return p;
        }
        return new System.Windows.Point(mic.X + mic.Width / 2, mic.Y + mic.Height / 2);
    }

    // The last mic buttons found, per window; checked with two cheap property reads before use.
    static readonly Dictionary<IntPtr, List<AutomationElement>> MicCache = new Dictionary<IntPtr, List<AutomationElement>>();
    static readonly Dictionary<string, System.Windows.Point> ReleaseCache = new Dictionary<string, System.Windows.Point>();
    static readonly object CacheLock = new object();

    static bool IsMicLabel(string n) { return n == LabelIdle || n == LabelActive || n.StartsWith("Finishing dictation"); }

    static List<AutomationElement> CachedMics(IntPtr h)
    {
        List<AutomationElement> mics;
        lock (CacheLock) if (!MicCache.TryGetValue(h, out mics)) return null;
        try
        {
            foreach (var b in mics)
                if (!IsMicLabel(b.Current.Name) || b.Current.BoundingRectangle.IsEmpty) return null;
            return mics;
        }
        catch { return null; }
    }

    // Full searches, run after each press so the next press can use the caches.
    static void RefreshCaches(List<AutomationElement> windows)
    {
        foreach (var w in windows)
        {
            var h = new IntPtr(w.Current.NativeWindowHandle);
            var mics = FindMicButtons(w);
            lock (CacheLock) MicCache[h] = mics;
            foreach (var m in mics) ReleasePoint(w, m.Current.BoundingRectangle);
        }
    }

    static List<AutomationElement> MicButtons(AutomationElement w)
    {
        var cached = CachedMics(new IntPtr(w.Current.NativeWindowHandle));
        if (cached != null) return cached;
        var found = FindMicButtons(w);
        lock (CacheLock) MicCache[new IntPtr(w.Current.NativeWindowHandle)] = found;
        return found;
    }

    static List<AutomationElement> FindMicButtons(AutomationElement w)
    {
        // Chromium builds its accessibility tree on the first UIA query, so retry briefly.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var mics = w.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .Where(b => b.Current.Name == LabelIdle || b.Current.Name == LabelActive || b.Current.Name.StartsWith("Finishing dictation"))
                .ToList();
            if (mics.Count > 0) return mics;
            Thread.Sleep(200);
        }
        return new List<AutomationElement>();
    }

    static AutomationElement Closest(List<AutomationElement> mics, System.Windows.Point p)
    {
        return mics.OrderBy(b =>
        {
            var r = b.Current.BoundingRectangle;
            double dx = r.X + r.Width / 2 - p.X, dy = r.Y + r.Height / 2 - p.Y;
            return dx * dx + dy * dy;
        }).First();
    }

    // The composer the user is working in: the one holding keyboard focus (its prompt or its mic button).
    static AutomationElement ComposerInUse(AutomationElement w, List<AutomationElement> mics)
    {
        if (mics.Count == 1) return mics[0];
        try
        {
            var focused = w.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Prompt"),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))));
            if (focused != null)
            {
                var r = focused.Current.BoundingRectangle;
                return Closest(mics, new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            }
        }
        catch { }
        return mics[0];
    }

    static List<AutomationElement> cachedWindows;

    static List<AutomationElement> ClaudeWindows()
    {
        var c = cachedWindows;
        if (c != null && c.Count > 0 && c.All(w => { try { return Native.IsWindow(new IntPtr(w.Current.NativeWindowHandle)); } catch { return false; } }))
            return c;
        return cachedWindows = FindClaudeWindows();
    }

    static List<AutomationElement> FindClaudeWindows()
    {
        var pids = new HashSet<int>(Process.GetProcessesByName("claude")
            .Where(p => { try { return p.MainModule.FileName.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0 || p.MainModule.FileName.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } })
            .Select(p => p.Id));
        var list = new List<AutomationElement>();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1")))
            if (pids.Contains(w.Current.ProcessId)) list.Add(w);
        return list;
    }

    static string Icon()
    {
        var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "claude.png");
        return File.Exists(f) ? Convert.ToBase64String(File.ReadAllBytes(f)) : "";
    }

    static readonly SemaphoreSlim SendGate = new SemaphoreSlim(1, 1);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> Pending =
        new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
    static ClientWebSocket socket;
    static DateTime deviceActivatedAt = DateTime.MinValue;
    static string activatedHeadset;
    static bool kickedAfterSilence;

    // Some wireless headsets (seen on a G733) power on with a microphone that sends digital silence until the
    // headset does something like its battery announcement. G HUB's battery check triggers the same thing.
    // Stage 2: mute and unmute the headset's own microphone, forcing its capture path to re-initialise.
    static void MuteToggleKick(string deviceId, int pressId)
    {
        Func<bool, object> mute = m => new Dictionary<string, object> {
            { "@type", "type.googleapis.com/logi.protocol.audio.Volume" }, { "channel", "INPUT" }, { "muted", m } };
        var on = Request("/audio/" + deviceId + "/mute", mute(true), 2000, "SET");
        Thread.Sleep(300);
        var off = Request("/audio/" + deviceId + "/mute", mute(false), 2000, "SET");
        var check = Request("/audio/" + deviceId + "/mute", new Dictionary<string, object> {
            { "@type", "type.googleapis.com/logi.protocol.audio.Volume" }, { "channel", "INPUT" } });
        Thread.Sleep(2000);
        var caps = Diag.ClaudeCaptureSessions();
        Log(string.Format("press #{0}: kick 2: headset mic mute toggled (mute {1}, unmute {2}, now {3}) -> mic {4}", pressId,
            Ok(on) ? "ok" : "failed", Ok(off) ? "ok" : "failed",
            Ok(check) ? (Equals(Field(check, "muted"), true) ? "MUTED" : "not muted") : "unknown",
            caps.Any(c => c.State == 1 && c.Peak > 0.0001f) ? "has signal again" : "still silent"));
    }

    static void Kick(string deviceId, string why)
    {
        var ws = socket;
        if (ws == null || ws.State != WebSocketState.Open) return;
        try
        {
            var r = Request("/battery/" + deviceId + "/check", null, 3000, "SET");
            Log(string.Format("kick: battery check sent to {0} ({1}) -> {2}", deviceId, why, Ok(r) ? "ok" : "no answer/failed"));
        }
        catch (Exception e) { Log("kick: failed " + e.Message); }
    }

    static string SinceActivation()
    {
        return deviceActivatedAt == DateTime.MinValue ? "no device turned on since bridge start"
            : string.Format("a device turned on {0:0.0} s ago", (DateTime.Now - deviceActivatedAt).TotalSeconds);
    }

    // G HUB broadcasts on these paths when a device (e.g. a wireless headset) turns on/off or its battery changes.
    static readonly HashSet<string> DeviceEventPaths = new HashSet<string> {
        "/devices/state/changed", "/devices/state/activated", "/devices/state/deactivated",
        "/battery/state/changed", "/receivers/state/changed", "/devices/notification" };

    static Task Send(ClientWebSocket ws, string verb, string path, object payload) { return Send(ws, verb, path, payload, Guid.NewGuid().ToString()); }

    static async Task Send(ClientWebSocket ws, string verb, string path, object payload, string msgId)
    {
        var m = new Dictionary<string, object> { { "msgId", msgId }, { "verb", verb }, { "path", path } };
        if (payload != null) m["payload"] = payload;
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(m));
        await SendGate.WaitAsync();
        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { SendGate.Release(); }
    }

    // GET from G HUB and wait for the answer (null on timeout or when not connected).
    static Dictionary<string, object> Request(string path, object payload, int timeoutMs = 1500, string verb = "GET")
    {
        var ws = socket;
        if (ws == null || ws.State != WebSocketState.Open) return null;
        var id = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        Pending[id] = tcs;
        try
        {
            Send(ws, verb, path, payload, id).Wait(timeoutMs);
            return tcs.Task.Wait(timeoutMs) ? tcs.Task.Result : null;
        }
        catch { return null; }
        finally { Pending.TryRemove(id, out tcs); }
    }

    static object Field(Dictionary<string, object> msg, string name)
    {
        if (msg == null || !msg.ContainsKey("payload")) return null;
        var p = msg["payload"] as Dictionary<string, object>;
        return p != null && p.ContainsKey(name) ? p[name] : null;
    }

    static bool Ok(Dictionary<string, object> msg)
    {
        var r = msg == null || !msg.ContainsKey("result") ? null : msg["result"] as Dictionary<string, object>;
        return r != null && (string)r["code"] == "SUCCESS";
    }

    // Each G HUB headset: battery (unreachable when the headset is off) and its own mic mute/gain.
    static string HeadsetState()
    {
        try
        {
            var list = Request("/devices/list", null);
            var infos = Field(list, "deviceInfos") as System.Collections.ArrayList;
            if (infos == null) return "G HUB not reachable";
            var parts = new List<string>();
            foreach (Dictionary<string, object> d in infos)
            {
                if ((d.ContainsKey("deviceType") ? d["deviceType"] as string : null) != "HEADSET") continue;
                var id = (string)d["id"];
                var name = d.ContainsKey("displayName") ? (string)d["displayName"] : id;
                var bat = Request("/battery/" + id + "/state", null);
                var input = new Dictionary<string, object> { { "@type", "type.googleapis.com/logi.protocol.audio.Volume" }, { "channel", "INPUT" } };
                var mute = Request("/audio/" + id + "/mute", input);
                var vol = Request("/audio/" + id + "/volume", input);
                parts.Add(string.Format("{0} [{1}] battery {2}{3}, mic {4}, gain {5}", name, d.ContainsKey("state") ? d["state"] : "?",
                    Ok(bat) ? Field(bat, "percentage") + "%" : "unreachable",
                    Ok(bat) && Equals(Field(bat, "charging"), true) ? " charging" : "",
                    Ok(mute) ? (Equals(Field(mute, "muted"), true) ? "MUTED in headset" : "not muted") : "mute unknown",
                    Ok(vol) && Field(vol, "gain") != null ? string.Format("{0:0}%", Convert.ToDouble(Field(vol, "gain")) * 100) : "?"));
            }
            return parts.Count == 0 ? "no G HUB headset" : string.Join("; ", parts);
        }
        catch (Exception e) { return "headset query failed: " + e.Message; }
    }

    // Logs the headset state at start and whenever it changes (checked every 10 s).
    static void HeadsetPoller(ClientWebSocket ws)
    {
        string last = null;
        while (ws.State == WebSocketState.Open)
        {
            var now = HeadsetState();
            // battery percentage drifts; compare without it
            var key = System.Text.RegularExpressions.Regex.Replace(now, @"battery \d+%", "battery");
            if (key != last) { Log("headset: " + now); last = key; }
            Thread.Sleep(10000);
        }
    }

    static string Flatten(Exception e) { return (e is AggregateException ? e.InnerException ?? e : e).Message; }

    static void Log(string s)
    {
        lock (LogLock)
        {
            var f = Path.Combine(Dir, "bridge.log");
            if (File.Exists(f) && new FileInfo(f).Length > 8 * 1024 * 1024)
            {
                var old = Path.Combine(Dir, "bridge.1.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(f, old);
            }
            File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ", System.Globalization.CultureInfo.InvariantCulture) + s + Environment.NewLine);
        }
    }
}

static class Native
{
    public const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    public const uint LWA_ALPHA = 2;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    public const int SW_SHOWNOACTIVATE = 4;
    const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MK_LBUTTON = 1;
    const byte VK_F24 = 0x87, VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    // Windows lets any app take the foreground once the user has been idle for ForegroundLockTimeout
    // (a headset button is not keyboard/mouse input). A no-op F24 tap counts as fresh input to the
    // foreground app, which re-arms the lock so Chromium's activation attempts are refused.
    public static void ArmForegroundLock()
    {
        keybd_event(VK_F24, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F24, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(30);
    }

    const int WPF_RESTORETOMAXIMIZED = 2;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);

    // Un-minimizes a window without activating it and puts it behind every other window, at full work-area
    // size if it was maximized before. It is transparent while it moves so it never flashes on top.
    public static void Park(IntPtr h)
    {
        var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
        GetWindowPlacement(h, ref wp);
        bool maximized = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        uint key = 0, flags = 0; byte alpha = 255;
        bool hadAlpha = (ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(h, out key, out alpha, out flags);

        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
        ArmForegroundLock();
        ShowWindow(h, SW_SHOWNOACTIVATE);
        if (maximized)
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi);
            SetWindowPos(h, HWND_BOTTOM, mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top, SWP_NOACTIVATE);
        }
        else SendToBack(h);
        SetWindowLong(h, GWL_EXSTYLE, ex);
        if (hadAlpha) SetLayeredWindowAttributes(h, key, alpha, flags);
        Thread.Sleep(400); // let Chromium show the page and lay it out before reading the button position
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

    public static string Describe(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        return string.Format("{0},{1}-{2},{3}{4}{5}", r.Left, r.Top, r.Right, r.Bottom,
            IsIconic(h) ? " minimized" : "", IsWindowVisible(h) ? "" : " hidden");
    }

    public static System.Windows.Rect WindowRect(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        return new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    // Whether Claude currently holds the microphone, from Windows' privacy usage record (LastUsedTimeStop is 0
    // while in use). Null when Windows has no record for Claude.
    public static bool? ClaudeMicInUse()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        bool? result = null;
        using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(store))
        {
            if (root == null) return null;
            var keys = root.GetSubKeyNames().Where(n => n.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)).ToList();
            using (var np = root.OpenSubKey("NonPackaged"))
                if (np != null) keys.AddRange(np.GetSubKeyNames().Where(n => n.EndsWith("#claude.exe", StringComparison.OrdinalIgnoreCase)).Select(n => @"NonPackaged\" + n));
            foreach (var name in keys)
                using (var k = root.OpenSubKey(name))
                {
                    if (k == null) continue;
                    var start = Convert.ToInt64(k.GetValue("LastUsedTimeStart", 0L));
                    var stop = Convert.ToInt64(k.GetValue("LastUsedTimeStop", 0L));
                    if (start == 0) continue;
                    if (stop == 0) return true;
                    result = false;
                }
        }
        return result;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // Seconds since the last keyboard/mouse input (our own F24 taps count too).
    public static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = 8 };
        GetLastInputInfo(ref li);
        return unchecked(Environment.TickCount - (int)li.dwTime) / 1000.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);

    public static void SendToBack(IntPtr h)
    {
        SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // An Alt tap is what Windows accepts as permission for a background process to set the foreground.
    public static void GiveForeground(IntPtr h)
    {
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(h);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }

    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    // Posts a left press at the centre of a screen rectangle and the release at releaseAt. Posted input does
    // not activate the window.
    public static void Click(IntPtr h, System.Windows.Rect screen, System.Windows.Point releaseAt)
    {
        var p = new POINT { X = (int)(screen.X + screen.Width / 2), Y = (int)(screen.Y + screen.Height / 2) };
        ScreenToClient(h, ref p);
        var lp = new IntPtr((p.Y << 16) | (p.X & 0xFFFF));
        var r = new POINT { X = (int)releaseAt.X, Y = (int)releaseAt.Y };
        ScreenToClient(h, ref r);
        var lpUp = new IntPtr((r.Y << 16) | (r.X & 0xFFFF));
        ArmForegroundLock();
        PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, lp);
        PostMessage(h, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), lp);
        Thread.Sleep(40); // well under the 500 ms that Claude treats as press-and-hold
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, lpUp);
    }
}
