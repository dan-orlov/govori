using Avalonia.Controls;
using System.Runtime.InteropServices;
using System.Security;

namespace Govori;

internal sealed class MacPlatform : IDesktopPlatform
{
    public string? Limitation => !Quartz.Trusted() ? Localization.T("MacAccessibility") : MacKeyTap.Running ? null : !Quartz.PreflightListen() ? Localization.T("MacInputMonitoring") : Localization.T("MacLimitation");
    internal static bool Trusted => Quartz.Trusted();
    public nint ForegroundWindow()
    {
        var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Selector("sharedWorkspace"));
        var application = ObjC.Send(workspace, ObjC.Selector("frontmostApplication"));
        return (nint)ObjC.SendInt(application, ObjC.Selector("processIdentifier"));
    }
    public string WindowTitle(nint window) => window == 0 ? "" : Localization.T("MacApplication", window);
    public bool IsOwnWindow(nint window) => window == Environment.ProcessId;
    internal static readonly Dictionary<int, ushort> Keycodes = new()
    {
        [0x41]=0,[0x53]=1,[0x44]=2,[0x46]=3,[0x48]=4,[0x47]=5,[0x5A]=6,[0x58]=7,[0x43]=8,[0x56]=9,[0x42]=11,
        [0x51]=12,[0x57]=13,[0x45]=14,[0x52]=15,[0x59]=16,[0x54]=17,[0x31]=18,[0x32]=19,[0x33]=20,[0x34]=21,[0x36]=22,[0x35]=23,
        [0x39]=25,[0x37]=26,[0x38]=28,[0x30]=29,[0x4F]=31,[0x55]=32,[0x49]=34,[0x50]=35,[0x4C]=37,[0x4A]=38,[0x4B]=40,[0x4E]=45,[0x4D]=46,
        [0x20]=49,[0x1B]=53,[0x5B]=55,[0x5C]=54,[0x10]=56,[0xA0]=56,[0xA1]=60,[0x14]=57,[0x12]=58,[0xA4]=58,[0xA5]=61,[0x11]=59,[0xA2]=59,[0xA3]=62,
        [0x70]=122,[0x71]=120,[0x72]=99,[0x73]=118,[0x74]=96,[0x75]=97,[0x76]=98,[0x77]=100,[0x78]=101,[0x79]=109,[0x7A]=103,[0x7B]=111,
        [0x7C]=105,[0x7D]=107,[0x7E]=113,[0x7F]=106,[0x80]=64,[0x81]=79,[0x82]=80,[0x83]=90
    };
    public bool IsDown(int key)
    {
        bool Has(int vk) => Keycodes.TryGetValue(vk, out var code) && Quartz.KeyState(0, code);
        return key switch { 0x11 => Has(0xA2) || Has(0xA3), 0x12 => Has(0xA4) || Has(0xA5), 0x10 => Has(0xA0) || Has(0xA1), _ => Has(key) };
    }
    // kCGEventSourceStateCombinedSessionState, kCGEventKeyDown
    internal static uint KeyDownCount() => Quartz.KeyDownCount(0, 10);
    public bool ModifiersDown => IsDown(0x11) || IsDown(0x12) || IsDown(0x10) || IsDown(0x5B) || IsDown(0x5C);
    // Without these calls macOS never lists Govori under Accessibility and
    // Input Monitoring, and shortcuts and paste fail silently. Accessibility
    // also allows the key tap; Input Monitoring is only needed for polling.
    public void RequestPermissions()
    {
        if (!Quartz.Trusted()) Quartz.PromptTrust();
        else if (!MacKeyTap.Running && !Quartz.PreflightListen()) Quartz.RequestListen();
    }
    public void Paste()
    {
        if (!Quartz.Trusted()) throw new InvalidOperationException(Localization.T("MacAccessibility"));
        foreach (var down in new[] { true, false })
        {
            var e = Quartz.KeyboardEvent(0, 9, down);
            if (e == 0) throw new InvalidOperationException(Localization.T("MacPasteFailed"));
            try { Quartz.SetFlags(e, 1ul << 20); Quartz.Post(0, e); }
            finally { Quartz.Release(e); }
        }
    }
    public IGlobalHotkeys CreateHotkeys(Hotkey dictation, Hotkey history) => new MacHotkeys(this, dictation, history);
    public void ConfigureStartup(bool enabled)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "local.govori.crossplatform.plist");
        if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string executable = SecurityElement.Escape(Environment.ProcessPath)!;
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>local.govori.crossplatform</string><key>ProgramArguments</key><array><string>{executable}</string><string>--tray</string></array><key>EnvironmentVariables</key><dict><key>DOTNET_EnableDiagnostics</key><string>0</string></dict><key>RunAtLoad</key><true/></dict></plist>");
    }
    public void ConfigureOverlay(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor == "NSWindow") ObjC.SendBool(handle.Handle, ObjC.Selector("setIgnoresMouseEvents:"), true);
    }
    public void Dispose() { }
    private static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";
        [DllImport(Lib, EntryPoint="objc_getClass")] internal static extern nint Class(string name);
        [DllImport(Lib, EntryPoint="sel_registerName")] internal static extern nint Selector(string name);
        [DllImport(Lib, EntryPoint="objc_msgSend")] internal static extern nint Send(nint receiver, nint selector);
        [DllImport(Lib, EntryPoint="objc_msgSend")] internal static extern int SendInt(nint receiver, nint selector);
        [DllImport(Lib, EntryPoint="objc_msgSend")] internal static extern void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
    }
    private static class Quartz
    {
        private const string Lib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        [DllImport(Lib, EntryPoint="CGEventSourceCounterForEventType")] internal static extern uint KeyDownCount(int source, uint type);
        [DllImport(Lib, EntryPoint="CGEventSourceKeyState")][return:MarshalAs(UnmanagedType.I1)] internal static extern bool KeyState(int source, ushort key);
        [DllImport(Lib, EntryPoint="CGEventCreateKeyboardEvent")] internal static extern nint KeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
        [DllImport(Lib, EntryPoint="CGEventSetFlags")] internal static extern void SetFlags(nint e, ulong flags);
        [DllImport(Lib, EntryPoint="CGEventPost")] internal static extern void Post(int location, nint e);
        [DllImport(Lib, EntryPoint="AXIsProcessTrusted")][return:MarshalAs(UnmanagedType.I1)] internal static extern bool Trusted();
        [DllImport(Lib, EntryPoint="AXIsProcessTrustedWithOptions")][return:MarshalAs(UnmanagedType.I1)] private static extern bool TrustedWithOptions(nint options);
        [DllImport(Lib, EntryPoint="CGPreflightListenEventAccess")][return:MarshalAs(UnmanagedType.I1)] internal static extern bool PreflightListen();
        [DllImport(Lib, EntryPoint="CGRequestListenEventAccess")][return:MarshalAs(UnmanagedType.I1)] internal static extern bool RequestListen();
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(CoreFoundation, EntryPoint="CFRelease")] internal static extern void Release(nint value);
        [DllImport(CoreFoundation, EntryPoint="CFDictionaryCreate")] private static extern nint DictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallBacks, nint valueCallBacks);
        // AXIsProcessTrustedWithOptions({kAXTrustedCheckOptionPrompt: true}) shows the system prompt and lists the app.
        internal static void PromptTrust()
        {
            nint services = NativeLibrary.Load(Lib), foundation = NativeLibrary.Load(CoreFoundation);
            nint key = Marshal.ReadIntPtr(NativeLibrary.GetExport(services, "kAXTrustedCheckOptionPrompt"));
            nint value = Marshal.ReadIntPtr(NativeLibrary.GetExport(foundation, "kCFBooleanTrue"));
            nint options = DictionaryCreate(0, [key], [value], 1, NativeLibrary.GetExport(foundation, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(foundation, "kCFTypeDictionaryValueCallBacks"));
            if (options == 0) return;
            try { TrustedWithOptions(options); }
            finally { Release(options); }
        }
    }
}

internal sealed class MacHotkeys : HotkeyState
{
    // Key codes back to virtual keys; modifiers that share a code map to the specific left key.
    private static readonly Dictionary<int, int> Keys = MacPlatform.Keycodes.GroupBy(p => (int)p.Value).ToDictionary(g => g.Key, g => g.Max(p => p.Key));
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;
    private MacKeyTap? tap;
    public MacHotkeys(IDesktopPlatform platform, Hotkey dictation, Hotkey history) : base(platform, dictation, history)
    {
        Validate(dictation); Validate(history); tap = MacKeyTap.TryStart(OnKey); loop = Task.Run(Pump);
    }
    public override Hotkey Dictation { get => base.Dictation; set { Validate(value); base.Dictation = value; } }
    public override Hotkey History { get => base.History; set { Validate(value); base.History = value; } }
    private static void Validate(Hotkey hotkey) { if (!MacPlatform.Keycodes.ContainsKey(hotkey.Key)) throw new FormatException(Localization.T("MacUnsupportedKey")); }
    private bool OnKey(int code, bool down, bool up, bool injected, ulong flags)
    {
        if (!Keys.TryGetValue(code, out int key)) { if (down && !injected) CancelChord(); return false; }
        var hotkey = Dictation.Key == History.Key ? null : key == Dictation.Key ? Dictation : key == History.Key ? History : null;
        return HandleKey(key, down, up, injected, hotkey?.Matches((flags & 0x40000) != 0, (flags & 0x80000) != 0, (flags & 0x20000) != 0, (flags & 0x100000) != 0));
    }
    // Without Accessibility there is no key tap: poll key state instead. The
    // original key then still reaches the active application.
    private async Task Pump()
    {
        var previous = new HashSet<int>();
        uint? keyDowns = null;
        try
        {
            for (int tick = 0; !cancellation.IsCancellationRequested; tick++)
            {
                // Accessibility can be granted while Govori runs.
                if (tap is { Alive: false }) { tap.Dispose(); tap = null; }
                if (tap == null && tick % 100 == 0 && MacPlatform.Trusted) tap = MacKeyTap.TryStart(OnKey);
                if (tap != null) { previous.Clear(); keyDowns = null; await Task.Delay(1000, cancellation.Token); continue; }
                foreach (int key in new[] { Dictation.Key, History.Key, 0x1B, 0x11, 0xA3, 0x12, 0xA5, 0x10, 0xA1, 0x5B, 0x5C }.Distinct())
                {
                    bool down = Platform.IsDown(key), was = previous.Contains(key);
                    if (down != was) HandleKey(key, down, !down, false);
                    if (down) previous.Add(key); else previous.Remove(key);
                }
                // Only the keys above are polled. Count key-down events to notice
                // chords such as right Option+arrow while RightAlt is held.
                if (!Holding) keyDowns = null;
                else if (keyDowns is not { } before) keyDowns = MacPlatform.KeyDownCount();
                else if (MacPlatform.KeyDownCount() != before) CancelChord();
                await Task.Delay(10, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public override void Dispose() { cancellation.Cancel(); loop.GetAwaiter().GetResult(); tap?.Dispose(); cancellation.Dispose(); }
}

// An event tap sees keys before other applications, so Govori can swallow its
// shortcuts like the Windows keyboard hook does. It requires Accessibility.
internal sealed class MacKeyTap : IDisposable
{
    internal delegate bool Handler(int keycode, bool down, bool up, bool injected, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Callback(nint proxy, uint type, nint e, nint user);
    // Device-dependent flag bits tell which modifier changed in a flags-changed event.
    private static readonly Dictionary<int, ulong> ModifierBits = new() { [59] = 0x1, [56] = 0x2, [60] = 0x4, [55] = 0x8, [54] = 0x10, [58] = 0x20, [61] = 0x40, [62] = 0x2000 };
    internal static bool Running { get; private set; }
    private readonly Handler handler;
    private readonly Callback callback;
    private readonly Thread thread;
    private volatile bool stopping;
    private nint port, loop;
    internal bool Alive => thread.IsAlive;
    private MacKeyTap(Handler handler)
    {
        this.handler = handler; callback = OnEvent;
        using var started = new ManualResetEventSlim();
        thread = new Thread(() => Run(started)) { IsBackground = true, Name = "Govori key tap" };
        thread.Start(); started.Wait();
    }
    internal static MacKeyTap? TryStart(Handler handler)
    {
        var tap = new MacKeyTap(handler);
        if (tap.port == 0) { tap.thread.Join(); return null; }
        Running = true; return tap;
    }
    private void Run(ManualResetEventSlim started)
    {
        // Key down, key up, flags changed, and mouse button downs (they also end a RightAlt chord).
        const ulong events = (1ul << 10) | (1ul << 11) | (1ul << 12) | (1ul << 1) | (1ul << 3) | (1ul << 25);
        // The HID level comes before system shortcuts such as Ctrl+Space.
        port = Native.TapCreate(0, 0, 0, events, callback, 0);
        if (port == 0) port = Native.TapCreate(1, 0, 0, events, callback, 0);
        if (port == 0) { started.Set(); return; }
        nint source = Native.CreateRunLoopSource(0, port, 0);
        loop = Native.Retain(Native.CurrentRunLoop());
        Native.AddSource(loop, source, Native.CommonModes);
        Native.TapEnable(port, true);
        started.Set();
        // A stop request can arrive before the loop runs, and an invalidated tap finishes it.
        while (!stopping && Native.RunInMode(Native.DefaultMode, 1, false) != 1) { }
        Native.TapEnable(port, false); Native.Invalidate(port); Native.Release(source); Native.Release(port);
    }
    private nint OnEvent(nint proxy, uint type, nint e, nint user)
    {
        // macOS disables a tap after a timeout or user input; turn it back on.
        if (type is 0xFFFFFFFE or 0xFFFFFFFF) { Native.TapEnable(port, true); return e; }
        try
        {
            bool injected = Native.IntegerField(e, 41) == Environment.ProcessId; // kCGEventSourceUnixProcessID: our own Command+V
            ulong flags = Native.Flags(e);
            if (type is 1 or 3 or 25) { handler(-1, true, false, injected, flags); return e; }
            int code = (int)Native.IntegerField(e, 9); // kCGKeyboardEventKeycode
            bool down = type == 10, up = type == 11;
            if (type == 12)
            {
                if (!ModifierBits.TryGetValue(code, out ulong bit)) return e;
                down = (flags & bit) != 0; up = !down;
            }
            // Returning null drops the event before other applications see it.
            return handler(code, down, up, injected, flags) ? 0 : e;
        }
        catch { return e; }
    }
    public void Dispose()
    {
        stopping = true; Running = false;
        Native.StopRunLoop(loop); thread.Join(1000); Native.Release(loop);
    }
    private static class Native
    {
        private const string Services = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(Services, EntryPoint="CGEventTapCreate")] internal static extern nint TapCreate(uint tap, uint place, uint options, ulong events, Callback callback, nint user);
        [DllImport(Services, EntryPoint="CGEventTapEnable")] internal static extern void TapEnable(nint tap, [MarshalAs(UnmanagedType.I1)] bool enable);
        [DllImport(Services, EntryPoint="CGEventGetIntegerValueField")] internal static extern long IntegerField(nint e, uint field);
        [DllImport(Services, EntryPoint="CGEventGetFlags")] internal static extern ulong Flags(nint e);
        [DllImport(Foundation, EntryPoint="CFMachPortCreateRunLoopSource")] internal static extern nint CreateRunLoopSource(nint allocator, nint port, nint order);
        [DllImport(Foundation, EntryPoint="CFMachPortInvalidate")] internal static extern void Invalidate(nint port);
        [DllImport(Foundation, EntryPoint="CFRunLoopGetCurrent")] internal static extern nint CurrentRunLoop();
        [DllImport(Foundation, EntryPoint="CFRunLoopAddSource")] internal static extern void AddSource(nint loop, nint source, nint mode);
        [DllImport(Foundation, EntryPoint="CFRunLoopRunInMode")] internal static extern int RunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
        [DllImport(Foundation, EntryPoint="CFRetain")] internal static extern nint Retain(nint value);
        [DllImport(Foundation, EntryPoint="CFRunLoopStop")] internal static extern void StopRunLoop(nint loop);
        [DllImport(Foundation, EntryPoint="CFRelease")] internal static extern void Release(nint value);
        internal static nint CommonModes => Constant("kCFRunLoopCommonModes");
        internal static nint DefaultMode => Constant("kCFRunLoopDefaultMode");
        private static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Foundation), name));
    }
}
