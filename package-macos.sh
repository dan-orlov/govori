#!/usr/bin/env bash
# macOS grants Microphone, Accessibility and Input Monitoring access to an app
# bundle; a bare executable cannot use the microphone without usage metadata.
set -euo pipefail
cd "$(dirname "$0")"
case "$(uname -m)" in
  arm64) default_rid=osx-arm64 ;;
  *) default_rid=osx-x64 ;;
esac
rid="${1:-$default_rid}"
bash build.sh "$rid"
publish="artifacts/publish/$rid"
app="artifacts/macos/$rid/Govori.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$publish/Govori.CrossPlatform" "$publish"/*.dylib "$app/Contents/MacOS/"
iconset="artifacts/macos/$rid/govori.iconset"
rm -rf "$iconset" && mkdir -p "$iconset"
for size in 16 32 128 256; do
  sips -z "$size" "$size" src/Govori.Desktop/Assets/govori.png --out "$iconset/icon_${size}x${size}.png" >/dev/null
  if [ $((size * 2)) -le 256 ]; then
    sips -z $((size * 2)) $((size * 2)) src/Govori.Desktop/Assets/govori.png --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  fi
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/govori.icns"
rm -rf "$iconset"
# LSEnvironment closes the .NET debugger and diagnostics channels, through which
# other processes could run code with Govori's permissions.
# LSUIElement keeps Govori in the menu bar like the Windows tray app.
# NSAppSleepDisabled keeps App Nap from slowing the hotkey polling.
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>io.github.anton2riot.govori</string>
  <key>CFBundleName</key><string>Govori</string>
  <key>CFBundleDisplayName</key><string>Govori</string>
  <key>CFBundleExecutable</key><string>Govori.CrossPlatform</string>
  <key>CFBundleIconFile</key><string>govori</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.6</string>
  <key>LSEnvironment</key><dict><key>DOTNET_EnableDiagnostics</key><string>0</string></dict>
  <key>LSUIElement</key><true/>
  <key>NSAppSleepDisabled</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Govori records your voice while you hold the dictation shortcut and sends it to Google Gemini for transcription.</string>
</dict>
</plist>
PLIST
plutil -lint "$app/Contents/Info.plist" >/dev/null
# Ad-hoc signature: macOS asks for permissions again after every rebuild.
codesign --force --deep --sign - "$app"
echo "Built: $app"
