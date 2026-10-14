using Avalonia.Threading;
using System.Diagnostics;
using System.Net.Http;

namespace Govori;

internal sealed class DictationController : IDisposable
{
    internal IDesktopPlatform Platform { get; }
    internal IGlobalHotkeys Hotkeys { get; }
    internal IAudioRecorder Recorder { get; }
    internal TextInserter Inserter { get; private set; } = null!;
    public Settings Settings { get; private set; }
    public HistoryStore History { get; }
    public UsageStore Usage { get; }
    internal TranscriptionClient Transcriber { get; } = new();
    private readonly SemaphoreSlim networkGate = new(1);
    private readonly Dictionary<string, CancellationTokenSource> processing = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private MainWindow? window;
    private OverlayWindow? overlay;
    private Stopwatch? clock;
    private nint target;
    private bool stopping, cancelled, ready, captureWasEnabled, captureSuspended;
    private int captureVersion;
    public bool Recording { get; private set; }
    internal bool RecordingReady => Recording && ready;
    internal Task<bool>? ReadySoundPlayback { get; private set; }
    internal OverlayWindow? Indicator => overlay;
    public bool Busy => Recording || stopping || processing.Count > 0;
    public bool IsExiting { get; private set; }
    public event Action? SettingsChanged;

    public DictationController(IAudioRecorder? recorder = null)
    {
        Settings = Configuration.LoadSettings(); Localization.Apply(Settings.Language); History = new(); Usage = new();
        Platform = DesktopPlatform.Create(); Recorder = recorder ?? new AudioRecorder();
        Recorder.BecameReady += () => Dispatcher.UIThread.Post(UpdateRecordingReady);
        Hotkeys = Platform.CreateHotkeys(Hotkey.Parse(Settings.Hotkey), Hotkey.Parse(Settings.HistoryHotkey));
        Hotkeys.Pressed += () => Dispatcher.UIThread.Post(StartRecording);
        Hotkeys.Released += () => Dispatcher.UIThread.Post(() => _ = StopRecordingAsync());
        Hotkeys.Cancelled += () => Dispatcher.UIThread.Post(Cancel);
        // Posted after StartRecording, so a recording that was still opening is dropped too.
        Hotkeys.Discarded += () => Dispatcher.UIThread.Post(() => { if (Recording) { cancelled = true; _ = StopRecordingAsync(); } });
        Hotkeys.HistoryRequested += () => Dispatcher.UIThread.Post(() => window?.OpenHistory());
        timer.Tick += (_, _) =>
        {
            if (!Recording) return;
            if (!Recorder.IsRecording) { _ = FailRecordingAsync(Recorder.Error?.Message ?? Localization.T("MicrophoneStopped")); return; }
            if (!ready && clock!.Elapsed.TotalSeconds >= 5)
            {
                _ = FailRecordingAsync(Localization.T("MicrophoneTimeout")); return;
            }
            UpdateRecordingReady();
            overlay?.UpdateLevel(Recorder.CurrentLevel);
            if (clock!.Elapsed.TotalSeconds >= Settings.MaxRecordingSeconds) _ = StopRecordingAsync();
        };
    }
    private void UpdateRecordingReady()
    {
        if (!Recording || ready || !Recorder.IsReady) return;
        ready = true; overlay?.ShowState(target, Settings.IndicatorEdge, "recording"); SetStatus(Localization.T("RecordingReady"));
        ReadySoundPlayback = ReadySound.PlayAsync();
    }
    private async Task FailRecordingAsync(string error)
    {
        if (!Recording || stopping) return;
        cancelled = true; await StopRecordingAsync(); ShowRecordingError(error);
    }
    private void ShowRecordingError(string error)
    {
        window?.UpdateStatus(error, Busy, Recording, error: true);
        overlay?.ShowError(target, Settings.IndicatorEdge, error);
    }
    public void Attach(MainWindow main)
    {
        window = main; Inserter = new TextInserter(Platform, () => window.Clipboard); overlay = new OverlayWindow(Platform);
        SetIdleStatus();
    }
    public void StartRecording()
    {
        if (Recording || stopping || IsExiting) return;
        Configuration.LoadEnvironment();
        if (string.IsNullOrWhiteSpace(Configuration.GetApiKey(Settings.Provider))) { SetStatus(Localization.T("ApiKeyNotFound")); return; }
        if (string.IsNullOrWhiteSpace(Settings.ActiveModel)) { SetStatus(Localization.T("InvalidModel")); window?.OpenSettings(); return; }
        try
        {
            cancelled = false; ready = false; ReadySoundPlayback = null; target = Platform.ForegroundWindow();
            Recorder.Start(Settings.MicrophoneDevice); Recording = true; Hotkeys.Recording = true;
            overlay?.ShowState(target, Settings.IndicatorEdge, "starting");
            clock = Stopwatch.StartNew(); timer.Start(); SetStatus(Localization.T("PreparingMicrophone"));
        }
        catch (Exception ex)
        {
            if (Recording) _ = FailRecordingAsync(ex.Message); else ShowRecordingError(ex.Message);
        }
    }
    public async Task StopRecordingAsync()
    {
        if (!Recording || stopping) return;
        stopping = true; Recording = false; Hotkeys.Recording = false; timer.Stop(); clock?.Stop();
        var destination = target;
        // Processing begins only after usable audio has been checked.
        overlay?.Hide();
        string? recordingError = null;
        try
        {
            var audio = await Recorder.StopAsync();
            if (audio == null) { if (!cancelled) recordingError = Localization.T("NoSpeechRecorded"); return; }
            if (cancelled || audio.Duration < 0.2 || audio.Peak < AudioRecorder.MinimumSignalLevel)
            {
                File.Delete(audio.Path);
                if (cancelled) SetStatus(Localization.T("RecordingCancelled"));
                else recordingError = Localization.T("NoSpeechRecorded");
                return;
            }
            var entry = new HistoryEntry { AudioPath = audio.Path, Duration = audio.Duration, Target = Platform.WindowTitle(destination) };
            History.Add(entry); _ = ProcessAsync(entry, destination, true);
        }
        catch (Exception ex) { recordingError = ex.Message; }
        finally
        {
            stopping = false; if (!Busy) HideIndicator();
            window?.UpdateStatus(null, Busy, Recording);
            if (recordingError != null) ShowRecordingError(recordingError);
        }
    }
    public bool IsProcessing(string id) => processing.ContainsKey(id);
    public void Retry(HistoryEntry entry)
    {
        if (!entry.CanRetry || IsProcessing(entry.Id)) return;
        Configuration.LoadEnvironment(); _ = ProcessAsync(entry, 0, false);
    }
    internal async Task ProcessAsync(HistoryEntry entry, nint destination, bool paste)
    {
        using var cancellation = new CancellationTokenSource(); processing.Add(entry.Id, cancellation);
        bool acquired = false; var settings = Settings.Clone();
        try
        {
            SetStatus(Localization.T("Transcribing")); if (!Recording) overlay?.ShowState(destination, Settings.IndicatorEdge, "processing");
            await networkGate.WaitAsync(cancellation.Token); acquired = true;
            entry.Status = "transcribing"; entry.Error = ""; History.Save();
            var elapsed = Stopwatch.StartNew();
            var text = await Transcriber.TranscribeAsync(entry.AudioPath, settings, Configuration.GetApiKey(settings.Provider), cancellation.Token,
                charge => { charge.HistoryId = entry.Id; Usage.Save(charge); }, entry.Duration);
            entry.Latency = elapsed.Elapsed.TotalSeconds; entry.Text = text;
            entry.Status = string.IsNullOrWhiteSpace(text) ? "empty" : "saved"; History.Save();
            if (!Recording && processing.Count == 1) HideIndicator();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (paste && !Recording)
                {
                    try { entry.Status = (await Inserter.InsertAsync(text, destination, settings.RestoreClipboard, cancellation.Token)).Status; }
                    catch (OperationCanceledException) { entry.Status = "paste_cancelled"; }
                    catch (Exception ex) { entry.Status = "manual_paste"; SetStatus(ex.Message); }
                }
                var audio = entry.AudioPath; entry.AudioPath = ""; History.Save(); try { File.Delete(audio); } catch { }
            }
            SetStatus(entry.StatusLabel);
        }
        catch (OperationCanceledException) { entry.Status = "cancelled"; SetStatus(entry.StatusLabel); }
        catch (Exception ex)
        {
            entry.Status = "error";
            entry.Error = ex is InvalidOperationException ? ex.Message : ex is HttpRequestException ? Localization.T("NetworkError") : Localization.T("TranscriptionError");
            SetStatus(entry.Error);
        }
        finally
        {
            if (acquired) networkGate.Release(); processing.Remove(entry.Id);
            try { History.Save(); } catch { SetStatus(Localization.T("HistorySaveError")); }
            if (!Busy) HideIndicator();
            window?.UpdateStatus(null, Busy, Recording);
        }
    }
    public void Cancel()
    {
        if (Recording) { cancelled = true; _ = StopRecordingAsync(); }
        else foreach (var token in processing.Values.ToArray()) token.Cancel();
    }
    public void CommitSettings(Settings settings)
    {
        if (Recording) throw new InvalidOperationException(Localization.T("FinishRecording"));
        var hotkey = Hotkey.Parse(settings.Hotkey); var history = Hotkey.Parse(settings.HistoryHotkey);
        if (hotkey == history) throw new FormatException(Localization.T("DifferentShortcuts"));
        if (!Providers.IsSupported(settings.Provider)) throw new FormatException(Localization.T("InvalidProvider"));
        if (settings.Provider == Providers.Gemini && !TranscriptionClient.IsGeminiAudioModel(settings.Model)) throw new FormatException(Localization.T("InvalidModel"));
        if (settings.Provider == Providers.LiteLlm && !string.IsNullOrWhiteSpace(settings.LiteLlmBaseUrl)) TranscriptionClient.BaseUri(settings.LiteLlmBaseUrl);
        if (!Localization.IsSupported(settings.Language)) throw new FormatException(Localization.T("InvalidLanguage"));
        settings.Hotkey = hotkey.Label; settings.HistoryHotkey = history.Label;
        var old = Settings; bool startupChanged = settings.StartWithSystem != old.StartWithSystem;
        try
        {
            Hotkeys.Dictation = hotkey; Hotkeys.History = history;
            if (startupChanged) Platform.ConfigureStartup(settings.StartWithSystem);
            Configuration.SaveSettings(settings);
        }
        catch
        {
            Hotkeys.Dictation = Hotkey.Parse(old.Hotkey); Hotkeys.History = Hotkey.Parse(old.HistoryHotkey);
            if (startupChanged) Platform.ConfigureStartup(old.StartWithSystem);
            throw;
        }
        Settings = settings;
        if (settings.Language != old.Language) Localization.Apply(settings.Language);
        SettingsChanged?.Invoke();
        if (!Busy) SetIdleStatus();
    }
    public async Task SetHotkeyCapture(bool capture)
    {
        if (!capture && !captureSuspended) return;
        int version = ++captureVersion;
        if (capture)
        {
            // Keep the original state when capture is restarted while a
            // previous key release is still pending, or focus changes fields.
            if (!captureSuspended) { captureWasEnabled = Hotkeys.Enabled; captureSuspended = true; }
            Hotkeys.Enabled = false;
        }
        else
        {
            while (Platform.ModifiersDown || Platform.IsDown(Hotkeys.Dictation.Key) || Platform.IsDown(Hotkeys.History.Key)) { await Task.Delay(20); if (version != captureVersion || IsExiting) return; }
            if (version == captureVersion && !IsExiting) { Hotkeys.Enabled = captureWasEnabled; captureSuspended = false; }
        }
    }
    public void SetStatus(string text) => window?.UpdateStatus(text, Busy, Recording);
    private void HideIndicator() { if (overlay?.State != "error") overlay?.Hide(); }
    internal void SaveApiKey(string key)
    {
        ApiKeyStore.Save(key, Settings.Provider);
        if (!Busy) SetIdleStatus();
    }
    internal void DeleteApiKey()
    {
        ApiKeyStore.Delete(Settings.Provider);
        if (!Busy) SetIdleStatus();
    }
    private void SetIdleStatus() => SetStatus(string.IsNullOrWhiteSpace(Configuration.GetApiKey(Settings.Provider))
        ? Localization.T("ApiKeyNotFound")
        : Localization.T("Ready"));
    public async Task ExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true; Hotkeys.Enabled = false;
        if (Recording) { cancelled = false; await StopRecordingAsync(); }
        foreach (var token in processing.Values.ToArray()) token.Cancel();
        var wait = Stopwatch.StartNew(); while (processing.Count > 0 && wait.Elapsed.TotalSeconds < 3) await Task.Delay(40);
    }
    public void Dispose()
    {
        IsExiting = true; timer.Stop(); Hotkeys.Dispose();
        // Quit requests (Command+Q, logout) skip ExitAsync; drop the unfinished recording.
        if (Recording) try { if (Recorder.StopAsync().GetAwaiter().GetResult() is { } audio) File.Delete(audio.Path); } catch { }
        Recorder.Dispose(); Platform.Dispose(); Transcriber.Dispose(); overlay?.Close();
    }
}
