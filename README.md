# Govori

A desktop voice-to-text app built with .NET 9, Avalonia and PortAudio. Hold a shortcut, speak, release it, and Govori transcribes your audio with Gemini or LiteLLM and pastes the result into the active text field.

## Features

- Hold-to-record shortcut, a Record button, and local transcription history.
- An 18-pixel indicator: grey while checking input, green after microphone sound is detected, amber while transcribing. A quiet 100 ms chime confirms incoming sound. Failed recordings show a red indicator and an explanation even when the main window is hidden.
- Automatic paste with clipboard restoration and protection against pasting into a different window.
- Retry saved recordings after network errors; successful recordings are deleted.
- English and Russian interfaces. The system language is selected by default; unsupported languages fall back to English. Change **Settings → Language** without restarting. The interface language does not change the language of your dictation.
- Optional startup at login and a tray menu.
- Settings apply automatically. The interface shows field labels and operational errors without explanatory captions.
- Standard **History**, **Spending** and **Settings** tabs; compact settings with key deletion beside the key field and scrollbars that keep a fixed width.
- Choose **Gemini key** or **LiteLLM key** in Settings. Models are selected from a dropdown; the LiteLLM list uses the server's capability metadata and excludes models without audio-to-text support.
- Each new transcription request shows its USD cost. Spending includes daily totals, today, this month and all time. LiteLLM-reported prices take precedence; otherwise server rates or Gemini's standard paid rates provide an estimate marked **≈**. Missing billing data is shown as unknown and excluded from monetary totals. Free-tier Gemini billing may differ. Each retry is counted separately; deleting a transcription keeps its spending record in `usage.json`. Older transcriptions have no recorded price.

## Windows quick start

Build from source with the .NET 9 SDK and PowerShell 7 (`pwsh`). Run the commands from the repository root:

```powershell
./build.ps1
./install.ps1 -StartWithSystem
```

The installer copies the self-contained app to `%LOCALAPPDATA%\Dictophone.CrossPlatform\app`, creates **Govori** shortcuts on the Desktop and in the Start menu, and starts it in the tray. Both shortcuts open the EXE directly. The .NET runtime is not required on the target machine. Startup uses a `Govori.lnk` shortcut in the user's Windows Startup folder pointing to this stable installation path, so moving the source folder does not break it. No administrator access is required.

Open **Settings → Gemini key**, paste your key. It saves automatically after a brief pause and applies immediately and is kept separately from settings in `gemini-key.bin`. Windows encrypts it with DPAPI for the current user; Linux and macOS restrict the file to the current user. **Delete key** removes this saved copy. You can also use `GEMINI_API_KEY` in the environment or `%LOCALAPPDATA%\Dictophone.CrossPlatform\.env`; the key saved in Settings takes precedence, followed by the environment variable and then `.env`. Use `.env.example` as a reference; never commit your actual key.

For LiteLLM, select **Settings → Service → LiteLLM key**, enter your proxy's HTTPS URL (the root or its `/v1` URL). The model list loads automatically. The app reads `LITELLM_API_KEY` automatically, including Windows user and machine environment variables set after the app was launched. Alternatively save the LiteLLM key in Settings; it is stored separately in `litellm-key.bin`. `LITELLM_BASE_URL` can supply the server address. Each service remembers its own model and key. The proxy must expose `/v1/models` and `/v1/model/info` with `mode` and `supports_audio_input` metadata; an unavailable catalog shows an error; reopening Settings retries the request. When `/v1/models` rejects an exhausted key budget, the authenticated metadata catalog still supplies model choices and a spending-limit warning. Failed transcriptions retain audio for retry. Chat models with audio input use `/v1/chat/completions`; dedicated transcription models use `/v1/audio/transcriptions`.

By default, hold **Ctrl+Space** (RightAlt on macOS) to record, speak, release it to transcribe, and press **Ctrl+Alt+J** to open history. **Esc** cancels recording or processing. The indicator turns green once microphone sound is detected; silent buffers do not confirm working input. If no sound is detected within five seconds, recording stops with a visible error. Silent or unusably short recordings are not transcribed. Set your own shortcuts in Settings; they save automatically after you release the keys. Existing installations keep their shortcuts and other settings.

Closing the window hides it in the tray. Click the **Govori** Desktop or Start menu shortcut to open it again, or use the tray menu. A second launch opens the existing app; it does not start another recorder. Choose Exit to quit. Start at login can also be enabled or disabled in Settings. `start.ps1` / `start.cmd` is a compatibility helper that opens the installed app and installs it first if needed. Normal launches of a build from another folder also forward to the installed EXE. Developers can use `--portable` or an explicit `--data-dir` to bypass that forwarding; copies using the same data directory still share one instance. Windows keyboard shortcuts run on a dedicated message thread so a slow interface cannot make Windows remove the keyboard hook.

Paste works in fields that accept Ctrl+V (Command+V on macOS). If the active window changes while Gemini is processing, copy the text from history. Pasting into an elevated Windows app requires Govori to run at the same privilege level. RightAlt may conflict with AltGr on some keyboard layouts.

## Configuration and privacy

User settings, history, recordings and credentials live in the per-user application data directory, outside this repository. On Windows this is `%LOCALAPPDATA%\Dictophone.CrossPlatform`. Portable copies can also read an ignored `.env` beside the executable or in their parent folders.

```dotenv
GEMINI_API_KEY=
GEMINI_MODEL=gemini-2.5-flash
LITELLM_API_KEY=
LITELLM_BASE_URL=
DICTATION_CONTEXT=
```

`DICTATION_CONTEXT` is an optional list of spelling hints for names and technical terms. `GEMINI_MODEL` and `DICTATION_CONTEXT` are read from the selected `.env` file when no saved settings exist; saved settings take precedence afterward. API keys and `LITELLM_BASE_URL` also use the process environment, then Windows user and machine variables, then `.env`. API keys are never bundled into the executable.

Recorded audio and spelling hints are sent to Google's Gemini API or the selected LiteLLM proxy over HTTPS (HTTP is allowed for a local proxy). Pricing, quotas and service latency depend on your API account and model. Recording stops after three minutes by default. Failed or interrupted transcriptions, cancelled processing and responses without text retain audio for retry; deleting a history entry deletes its saved audio. Audio is removed after a non-empty transcription, when recording is cancelled before processing, or when captured audio is too short or quiet. Transcriptions are stored locally in history until you delete them. Processing and retention rules depend on the selected service and its underlying providers.

Speech is captured as mono PCM16 at 16 kHz when the device supports it, with the native device rate as a fallback. Transcription starts after you release the shortcut; it is not streaming transcription.

## Build and verification

```powershell
./build.ps1 -Verify
# Deterministic microphone failure checks (no working microphone required):
./artifacts/publish/win-x64/Govori.CrossPlatform.exe --verify --tray --microphone-errors --data-dir ./artifacts/verification-microphone-errors
# Billing and layout checks with mocked transcription responses:
./artifacts/publish/win-x64/Govori.CrossPlatform.exe --verify --ui-costs-only --data-dir ./artifacts/verification-ui-costs
# Optional live API test with a prepared speech WAV:
./build.ps1 -Verify -LiveAudio path/to/speech.wav
# Install an already built Windows copy:
./install.ps1
```

Build output is ignored under `artifacts/publish/<runtime>`. Verification uses a separate data directory, briefly accesses the microphone, and opens a test input window. It checks shortcuts, language selection and history migration, tray reopening, WAV recording, indicator size, paste safety and clipboard restoration. Reports and UI images are written under `artifacts/verification`. The live API test expects speech containing the number 42 and the word "dictation" (English or Russian).

`build.ps1` accepts `-OutputDirectory`, `-BuildArtifactsDirectory` and `-VerificationDirectory` to keep generated files outside the checkout. `install.ps1 -SourceDirectory <published-directory>` installs a custom build. GitHub Actions builds on Windows, Linux and macOS; hardware and desktop integration checks require a real desktop session.

## Linux and macOS

```bash
bash build.sh
./artifacts/publish/linux-x64/Govori.CrossPlatform
# Or select a runtime explicitly:
bash build.sh osx-arm64
# macOS app bundle (required for microphone access):
bash package-macos.sh
```

The shared interface, recording and Gemini client are cross-platform. Windows desktop integration has been tested. Linux and macOS adapters are implemented and require verification on those systems.

- **Linux / X11:** global shortcuts use XGrabKey; paste uses XTest. Install X11, Xtst, the audio backend libraries used by PortAudio, and Avalonia's desktop dependencies. Ubuntu packages typically include `libx11-6`, `libxtst6`, `libasound2`, `libpulse0` and `libfontconfig1`. Package names vary by distribution.
- **Wayland:** use the Record button, history and copy. Global shortcuts and automatic paste currently require an X11 session.
- **macOS:** run `bash package-macos.sh`, copy `artifacts/macos/<runtime>/Govori.app` to `/Applications` or `~/Applications` and open it. Govori runs in the menu bar. On first start it asks for Accessibility; allow it in System Settings. Govori then intercepts its shortcuts with an event tap, like the Windows keyboard hook, so other applications do not see them. Without Accessibility it falls back to polling key state, which needs Input Monitoring and lets the original key through. The microphone prompt appears with the first recording. The bundle is ad-hoc signed, so macOS asks again after every rebuild, and without an Apple Team ID macOS cannot verify who built the app; build it yourself from source you trust. New installations hold **RightAlt** (right Option) to dictate; Ctrl+Space switches input sources on macOS. Pressing another key while holding RightAlt or RightCtrl (for example Option+arrow) cancels the recording without sending it. Function keys may require Fn. Command+Q quits Govori; closing the window keeps it in the menu bar.

## Repository layout

- `src/Govori.Desktop`: app and platform adapters.
- `src/Govori.Desktop/Localization`: English and Russian string catalogs. Keep catalog keys and formatting placeholders in sync when adding translations.
- `build.ps1`, `build.sh`: self-contained publishing.
- `install.ps1`: per-user Windows installation and optional startup.
- `tools/check-repository.ps1`: checks the working tree and staged files for common secrets, non-empty credential literals, absolute machine paths, unreviewed URL hosts, user data and generated binaries. It also rejects ignored files that are already tracked. Approved hosts are limited to the Gemini endpoint and framework references; review any new host before adding it to the allowlist.

Run `pwsh -File tools/check-repository.ps1` before publishing. The same check runs in CI on all three operating systems. `.gitignore` excludes credentials, recordings, history, machine-specific files and build output; it does not protect files that were already committed. Publish the source through Git. Build output, verification reports and UI images are local artifacts and should not be added to Git or included in a source archive. The checker covers the current working tree and Git index; it does not scan past commits. Before publishing an existing repository, inspect its history separately and revoke any previously exposed credentials.

## License

Licensed under the MIT License. See [LICENSE](LICENSE).
