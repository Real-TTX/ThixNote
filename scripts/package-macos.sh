#!/bin/sh
# Packs a published macOS build into ThixNote.app, a zip (for the update of installed versions) and a disk image
# (for the first installation: drag ThixNote into "Programme").
#
#   scripts/package-macos.sh <publish folder> <rid: osx-arm64|osx-x64> <version> <build number> <output folder>
#
# Runs on macOS (codesign, ditto, hdiutil). The document types are the file types of src/ThixNote.Core/installer.json
# (SETUP-08; a test holds them together): ThixNote owns .txts (own document icon) and is offered for .md and .txt under
# "Öffnen mit". Which of them open with ThixNote on a double click, "ThixNote --configure --file-types=…" decides
# (ThixLauncher calls it after copying the bundle; Launch Services, per user).
# Without an Apple Developer ID the app is signed ad hoc (needed on Apple Silicon); Gatekeeper then asks once
# (right click > Öffnen, or System Settings > Privacy & Security).
set -eu

publish="$1"
rid="$2"
version="$3"
build="$4"
out="$5"

root="$(cd "$(dirname "$0")/.." && pwd)"
arch="${rid#osx-}"
major=$(sed -n 's:.*<ThixNoteMajor>\(.*\)</ThixNoteMajor>.*:\1:p' "$root/Directory.Build.props")
minor=$(sed -n 's:.*<ThixNoteMinor>\(.*\)</ThixNoteMinor>.*:\1:p' "$root/Directory.Build.props")
work="$(mktemp -d)"
app="$work/ThixNote.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/ThixNote"

# Icons rendered by tools/IconGen from assets/logo.
cp "$root/assets/icons/thixnote.icns" "$app/Contents/Resources/ThixNote.icns"
cp "$root/assets/icons/thixnote-document.icns" "$app/Contents/Resources/ThixNoteDocument.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>info.schmoldt.thixnote</string>
  <key>CFBundleName</key>
  <string>ThixNote</string>
  <key>CFBundleDisplayName</key>
  <string>ThixNote</string>
  <key>CFBundleExecutable</key>
  <string>ThixNote</string>
  <key>CFBundleIconFile</key>
  <string>ThixNote</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${major}.${minor}.${build}</string>
  <key>CFBundleVersion</key>
  <string>${build}</string>
  <key>CFBundleGetInfoString</key>
  <string>ThixNote ${version}</string>
  <key>NSHumanReadableCopyright</key>
  <string>© Matthias Schmoldt</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>CFBundleDevelopmentRegion</key>
  <string>de</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key>
      <string>Verschlüsselte Notiz</string>
      <key>CFBundleTypeExtensions</key>
      <array><string>txts</string></array>
      <key>CFBundleTypeIconFile</key>
      <string>ThixNoteDocument</string>
      <key>CFBundleTypeRole</key>
      <string>Editor</string>
      <key>LSHandlerRank</key>
      <string>Owner</string>
    </dict>
    <dict>
      <key>CFBundleTypeName</key>
      <string>Markdown-Notiz</string>
      <key>CFBundleTypeExtensions</key>
      <array><string>md</string></array>
      <key>CFBundleTypeRole</key>
      <string>Editor</string>
      <key>LSHandlerRank</key>
      <string>Alternate</string>
    </dict>
    <dict>
      <key>CFBundleTypeName</key>
      <string>Textnotiz</string>
      <key>CFBundleTypeExtensions</key>
      <array><string>txt</string></array>
      <key>CFBundleTypeRole</key>
      <string>Editor</string>
      <key>LSHandlerRank</key>
      <string>Alternate</string>
    </dict>
  </array>
</dict>
</plist>
PLIST

# Ad-hoc signature: Apple Silicon runs only signed code. A Developer ID signature + notarization can replace it later.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# Update package (MacAppBundle unpacks it with ditto) and disk image for the first installation.
ditto -c -k --keepParent "$app" "$out/ThixNote-macOS-${arch}.zip"

dmg_root="$work/dmg"
mkdir -p "$dmg_root"
cp -R "$app" "$dmg_root/"
ln -s /Applications "$dmg_root/Programme"
hdiutil create -volname "ThixNote" -srcfolder "$dmg_root" -ov -format UDZO "$out/ThixNote-macOS-${arch}.dmg" >/dev/null

rm -rf "$work"
ls -l "$out"
