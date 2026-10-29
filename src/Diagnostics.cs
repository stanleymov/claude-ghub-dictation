// Diagnostics for the Claude G HUB bridge: a background recorder that writes to bridge.log what the
// microphone side is doing, so presses that "don't go through" can be explained afterwards:
//   - capture devices appearing, disappearing or changing state (e.g. a wireless headset turning on)
//   - the default recording device changing
//   - Claude's microphone on/off as Windows records it (CapabilityAccessManager)
//   - while Claude records: which device its capture stream is on and the level that stream receives
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

static class Diag
{
    static Action<string> log;
    static readonly object Gate = new object();
    static IMMDeviceEnumerator enumerator;
    static NotificationClient notifications;

    public static void Start(Action<string> logger)
    {
        log = logger;
        var t = new Thread(Run) { IsBackground = true, Name = "diag" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    static void Run()
    {
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            LogDevices("startup");
            notifications = new NotificationClient();
            enumerator.RegisterEndpointNotificationCallback(notifications);
        }
        catch (Exception e) { log("diag: audio setup failed: " + e.Message); }

        bool? lastMic = null;
        var window = new LevelWindow();
        while (true)
        {
            try
            {
                var mic = Native.ClaudeMicInUse();
                if (mic != lastMic)
                {
                    log("diag: Claude microphone " + (mic == true ? "ON" : mic == false ? "off" : "unknown") + " (Windows usage record)");
                    if (mic == true) window = new LevelWindow();
                    else if (lastMic == true) window.Flush(log, true);
                    lastMic = mic;
                }
                if (mic == true)
                {
                    window.Add(ClaudeCaptureSessions());
                    if (window.Due) window.Flush(log, false);
                }
                Thread.Sleep(mic == true ? 100 : 250);
            }
            catch (Exception e) { log("diag: loop error: " + e.Message); Thread.Sleep(1000); }
        }
    }

    // One line per active capture device, plus the defaults.
    public static void LogDevices(string why)
    {
        try
        {
            lock (Gate)
            {
                var parts = new List<string>();
                foreach (var d in CaptureDevices(DEVICE_STATE_ACTIVE))
                    parts.Add(string.Format("'{0}' mute={1} vol={2:0}%", d.Name, d.Mute, d.Volume * 100));
                log(string.Format("diag: capture devices ({0}): {1} | default: '{2}' | communications default: '{3}'",
                    why, parts.Count == 0 ? "none active" : string.Join("; ", parts),
                    DefaultName(ERole.eConsole), DefaultName(ERole.eCommunications)));
            }
        }
        catch (Exception e) { log("diag: device listing failed: " + e.Message); }
    }

    public class Device { public string Id, Name; public bool Mute; public float Volume; public IMMDevice Raw; }

    static List<Device> CaptureDevices(uint stateMask)
    {
        var list = new List<Device>();
        IMMDeviceCollection col;
        enumerator.EnumAudioEndpoints(EDataFlow.eCapture, stateMask, out col);
        uint n; col.GetCount(out n);
        for (uint i = 0; i < n; i++)
        {
            IMMDevice dev; col.Item(i, out dev);
            var d = new Device { Raw = dev };
            dev.GetId(out d.Id);
            d.Name = FriendlyName(dev);
            try
            {
                object o; var iid = typeof(IAudioEndpointVolume).GUID;
                dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
                var vol = (IAudioEndpointVolume)o;
                bool mute; vol.GetMute(out mute); d.Mute = mute;
                float v; vol.GetMasterVolumeLevelScalar(out v); d.Volume = v;
            }
            catch { }
            list.Add(d);
        }
        return list;
    }

    static string DefaultName(ERole role)
    {
        try { IMMDevice d; enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, role, out d); return FriendlyName(d); }
        catch { return "none"; }
    }

    public static string NameOf(string id)
    {
        try { IMMDevice d; enumerator.GetDevice(id, out d); return FriendlyName(d); } catch { return id; }
    }

    static string FriendlyName(IMMDevice dev)
    {
        IPropertyStore store;
        if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != 0) return "?";
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        PROPVARIANT v;
        if (store.GetValue(ref key, out v) != 0) return "?";
        try { return v.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.p) : "?"; }
        finally { PropVariantClear(ref v); }
    }

    // Capture streams owned by any claude.exe process, across all active capture devices.
    public class Capture { public string Device; public int Pid; public int State; public float Peak; public bool Mute; }

    static HashSet<int> ClaudePids()
    {
        return new HashSet<int>(Process.GetProcessesByName("claude").Select(p => p.Id));
    }

    public static List<Capture> ClaudeCaptureSessions()
    {
        var result = new List<Capture>();
        var pids = ClaudePids();
        lock (Gate)
        {
            foreach (var d in CaptureDevices(DEVICE_STATE_ACTIVE))
            {
                try
                {
                    object o; var iid = typeof(IAudioSessionManager2).GUID;
                    d.Raw.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
                    var mgr = (IAudioSessionManager2)o;
                    IAudioSessionEnumerator sessions; mgr.GetSessionEnumerator(out sessions);
                    int count; sessions.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl2 s; sessions.GetSession(i, out s);
                        uint pid; s.GetProcessId(out pid);
                        if (!pids.Contains((int)pid)) continue;
                        int state; s.GetState(out state);
                        float peak = -1;
                        var meter = s as IAudioMeterInformation;
                        if (meter != null) meter.GetPeakValue(out peak);
                        result.Add(new Capture { Device = d.Name, Pid = (int)pid, State = state, Peak = peak, Mute = d.Mute });
                    }
                }
                catch { }
            }
        }
        return result;
    }

    // Summarises Claude's capture level every 2 s while its microphone is on.
    class LevelWindow
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, List<float>> peaks = new Dictionary<string, List<float>>();
        int samples, samplesWithoutStream;
        double silentSince = -1;
        bool warnedNoStream, warnedSilence;
        public bool Due { get { return clock.ElapsedMilliseconds >= 2000; } }

        public void Add(List<Capture> caps)
        {
            samples++;
            var active = caps.Where(c => c.State == 1 /* AudioSessionStateActive */).ToList();
            if (active.Count == 0) samplesWithoutStream++;
            foreach (var c in active)
            {
                var key = c.Device + (c.Mute ? " [DEVICE MUTED]" : "");
                if (!peaks.ContainsKey(key)) peaks[key] = new List<float>();
                peaks[key].Add(c.Peak);
            }
            var loudest = active.Count == 0 ? 0f : active.Max(c => c.Peak);
            var t = TotalSeconds;
            if (active.Count > 0 && loudest <= 0.0001f) { if (silentSince < 0) silentSince = t; }
            else silentSince = -1;
        }

        double TotalSeconds { get { return total.Elapsed.TotalSeconds; } }
        readonly Stopwatch total = Stopwatch.StartNew();

        public void Flush(Action<string> log, bool final)
        {
            if (samples > 0)
            {
                var parts = peaks.Select(kv => string.Format("'{0}' peak {1:0.000} avg {2:0.000} voice {3:0}%",
                    kv.Key, kv.Value.Max(), kv.Value.Average(), 100.0 * kv.Value.Count(p => p > 0.02f) / kv.Value.Count));
                log(string.Format("diag: Claude capture{0}: {1}", final ? " (final)" : "",
                    peaks.Count == 0 ? "NO ACTIVE CAPTURE STREAM" : string.Join("; ", parts)));
                if (samplesWithoutStream == samples && !warnedNoStream && TotalSeconds > 1.5)
                {
                    log("diag: WARNING Claude says the microphone is on, but it has no active capture stream on any device");
                    warnedNoStream = true;
                }
                if (silentSince >= 0 && TotalSeconds - silentSince > 3 && !warnedSilence)
                {
                    log(string.Format("diag: WARNING Claude is recording pure silence (level 0.000 for {0:0} s) - wrong/disconnected/muted device?", TotalSeconds - silentSince));
                    warnedSilence = true;
                }
            }
            peaks.Clear(); samples = 0; samplesWithoutStream = 0; clock.Restart();
        }
    }

    [ComVisible(true)]
    class NotificationClient : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, uint state)
        {
            ThreadPool.QueueUserWorkItem(_ => { log(string.Format("diag: device '{0}' state -> {1}", NameOf(id), StateName(state))); LogDevices("after state change"); });
        }
        public void OnDeviceAdded(string id) { ThreadPool.QueueUserWorkItem(_ => log("diag: device added '" + NameOf(id) + "'")); }
        public void OnDeviceRemoved(string id) { ThreadPool.QueueUserWorkItem(_ => log("diag: device removed " + id)); }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string id)
        {
            if (flow != EDataFlow.eCapture) return;
            ThreadPool.QueueUserWorkItem(_ => log(string.Format("diag: default recording device ({0}) -> '{1}'", role, id == null ? "none" : NameOf(id))));
        }
        public void OnPropertyValueChanged(string id, PROPERTYKEY key) { }
    }

    static string StateName(uint s)
    {
        switch (s) { case 1: return "active"; case 2: return "disabled"; case 4: return "not present"; case 8: return "unplugged"; default: return s.ToString(); }
    }

    // ---- Core Audio interop -------------------------------------------------------------------------
    const uint DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pvar);

    public enum EDataFlow { eRender, eCapture, eAll }
    public enum ERole { eConsole, eMultimedia, eCommunications }

    [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
