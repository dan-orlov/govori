using Avalonia.Controls;

namespace Govori;

internal interface IDesktopPlatform : IDisposable
{
    string? Limitation { get; }
    nint ForegroundWindow();
    string WindowTitle(nint window);
    bool IsOwnWindow(nint window);
    bool IsDown(int key);
    bool ModifiersDown { get; }
    void Paste();
    void RequestPermissions() { }
    IGlobalHotkeys CreateHotkeys(Hotkey dictation, Hotkey history);
    void ConfigureStartup(bool enabled);
    void ConfigureOverlay(Window window);
}

internal interface IGlobalHotkeys : IDisposable
{
    Hotkey Dictation { get; set; }
    Hotkey History { get; set; }
    bool Enabled { get; set; }
    bool Recording { get; set; }
    event Action? Pressed, Released, Cancelled, Discarded, HistoryRequested;
    // The platform's Limitation may have changed, for example after a permission was granted.
    event Action? LimitationChanged;
}

internal static class DesktopPlatform
{
    public static IDesktopPlatform Create() => OperatingSystem.IsWindows()
        ? new WindowsPlatform(Configuration.DataDirectory == Configuration.DefaultDataDirectory ? null : Path.Combine(Configuration.DataDirectory, "startup"))
        : OperatingSystem.IsMacOS() ? new MacPlatform() : new LinuxPlatform();
}

public sealed record Hotkey(int Key, bool Ctrl, bool Alt, bool Shift, bool Win, string Label)
{
    public static Hotkey Parse(string text)
    {
        bool ctrl = false, alt = false, shift = false, win = false; int key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToUpperInvariant();
            if (token is "CTRL" or "CONTROL") { ctrl = true; continue; }
            if (token == "ALT") { alt = true; continue; }
            if (token == "SHIFT") { shift = true; continue; }
            if (token is "WIN" or "SUPER" or "CMD") { win = true; continue; }
            if (key != 0) throw new FormatException(Localization.T("HotkeyOneKey"));
            if (token.StartsWith('F') && int.TryParse(token[1..], out var f) && f is >= 1 and <= 24) key = 0x70 + f - 1;
            else key = token switch { "RIGHTALT" or "RALT" => 0xA5, "RIGHTCTRL" or "RCTRL" => 0xA3, "CAPSLOCK" => 0x14, "SPACE" => 0x20, _ when token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]) => token[0], _ => throw new FormatException(Localization.T("HotkeyExamples")) };
        }
        if (key == 0 || ((key is >= 0x30 and <= 0x5A || key == 0x20) && !(ctrl || alt || win))) throw new FormatException(Localization.T("HotkeyNeedsModifier"));
        string name = key switch { 0xA5 => "RightAlt", 0xA3 => "RightCtrl", 0x14 => "CapsLock", 0x20 => "Space", >= 0x70 and <= 0x87 => "F" + (key - 0x70 + 1), _ => ((char)key).ToString() };
        string label = string.Join('+', new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, win ? "Win" : null, name }.Where(x => x != null));
        return new(key, ctrl, alt, shift, win, label);
    }
    // RightAlt and RightCtrl are modifiers themselves. Polled key state (macOS)
    // already reports them as held, so they must not count as extra modifiers.
    internal bool Matches(IDesktopPlatform platform) => Matches(platform.IsDown(0x11), platform.IsDown(0x12), platform.IsDown(0x10), platform.IsDown(0x5B) || platform.IsDown(0x5C));
    internal bool Matches(bool ctrl, bool alt, bool shift, bool win) => (Key == 0xA3 || Ctrl == ctrl) && (Key == 0xA5 || Alt == alt) && Shift == shift && Win == win;
}

internal abstract class HotkeyState : IGlobalHotkeys
{
    // RightAlt and RightCtrl are also typing modifiers (Option+arrow, AltGr).
    // Wait this long before starting so that chords never open the microphone.
    internal static readonly TimeSpan ChordDelay = TimeSpan.FromMilliseconds(200);
    private readonly HashSet<int> swallowed = [];
    // Keys arrive on a hook or tap thread while the chord delay runs on the pool.
    private readonly object gate = new();
    private bool holding, started;
    private int holds;
    private volatile bool enabled = true, recording;
    private volatile Hotkey dictation = null!, history = null!;
    protected readonly IDesktopPlatform Platform;
    public virtual Hotkey Dictation { get => dictation; set => dictation = value; }
    public virtual Hotkey History { get => history; set => history = value; }
    public bool Enabled { get => enabled; set => enabled = value; }
    public bool Recording { get => recording; set => recording = value; }
    public event Action? Pressed, Released, Cancelled, Discarded, HistoryRequested, LimitationChanged;
    protected HotkeyState(IDesktopPlatform platform, Hotkey dictation, Hotkey history) { Platform = platform; Dictation = dictation; History = history; }
    internal bool HandleKey(int key, bool down, bool up, bool injected, bool? match = null)
    {
        if (injected) return false;
        lock (gate)
        {
            if (up && holding && ((Dictation.Ctrl && key is 0x11 or 0xA2 or 0xA3) || (Dictation.Alt && key is 0x12 or 0xA4 or 0xA5) || (Dictation.Shift && key is 0x10 or 0xA0 or 0xA1) || (Dictation.Win && key is 0x5B or 0x5C))) Release();
            if (up && swallowed.Remove(key)) { if (key == Dictation.Key && holding) Release(); return true; }
            if (!down) return false;
            // Esc also drops a hold that has not started; once started it cancels below.
            if (key == 0x1B ? !started : key is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)) CancelChord();
            if (swallowed.Contains(key)) return true;
            if (Recording && key == 0x1B) { swallowed.Add(key); holding = started = false; Recording = false; Cancelled?.Invoke(); return true; }
            if (!Enabled) return false;
            if (key == History.Key && (match ?? History.Matches(Platform))) { swallowed.Add(key); HistoryRequested?.Invoke(); return true; }
            if (key == Dictation.Key && (match ?? Dictation.Matches(Platform)))
            {
                swallowed.Add(key); holding = true; int hold = ++holds;
                if (Dictation.Key is 0xA3 or 0xA5) _ = Task.Delay(ChordDelay).ContinueWith(_ => { lock (gate) if (holding && hold == holds) Start(); });
                else Start();
                return true;
            }
            return false;
        }
    }
    private void Start() { started = true; Pressed?.Invoke(); }
    private void Release() { holding = false; if (started) { started = false; Released?.Invoke(); } }
    protected bool Holding => holding;
    protected void OnLimitationChanged() => LimitationChanged?.Invoke();
    // Another key or a click while RightAlt or RightCtrl is held as the
    // dictation key makes a chord: drop the recording instead of sending it.
    protected void CancelChord()
    {
        lock (gate)
        {
            if (!holding || Dictation.Key is not (0xA3 or 0xA5)) return;
            holding = false;
            // Esc right after a chord must not cancel again: that would stop in-flight transcriptions.
            if (started) { started = false; Recording = false; Discarded?.Invoke(); }
        }
    }
    public abstract void Dispose();
}
