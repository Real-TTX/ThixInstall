#!/bin/sh
# Packs a published macOS build into ThixInstall.app, a zip (for the update of installed versions) and a disk image
# (for the first installation: drag ThixInstall into "Programme").
#
#   scripts/package-macos.sh <publish folder> <rid: osx-arm64|osx-x64> <version> <build number> <output folder>
#
# Runs on macOS (iconutil, codesign, ditto, hdiutil). The app lives in the menu bar (LSUIElement: no Dock icon).
# Without an Apple Developer ID the app is signed ad hoc (needed on Apple Silicon); Gatekeeper then asks once
# (right click > Öffnen, or System Settings > Privacy & Security). Installers that need root (.pkg, scripts in the
# system context) ask for an administrator through macOS itself.
set -eu

publish="$1"
rid="$2"
version="$3"
build="$4"
out="$5"

root="$(cd "$(dirname "$0")/.." && pwd)"
arch="${rid#osx-}"
major=$(sed -n 's:.*<ThixInstallMajor>\(.*\)</ThixInstallMajor>.*:\1:p' "$root/Directory.Build.props")
minor=$(sed -n 's:.*<ThixInstallMinor>\(.*\)</ThixInstallMinor>.*:\1:p' "$root/Directory.Build.props")
work="$(mktemp -d)"
app="$work/ThixInstall.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/ThixInstall"

# App icon from the rendered branding (tools/render-icons.js): the macOS variant keeps Apple's margin around the tile.
iconset="$work/ThixInstall.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  cp "$root/branding/png/thixinstall-mac-${size}.png" "$iconset/icon_${size}x${size}.png"
  double=$((size * 2))
  cp "$root/branding/png/thixinstall-mac-${double}.png" "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/ThixInstall.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>info.schmoldt.thixinstall</string>
  <key>CFBundleName</key>
  <string>ThixInstall</string>
  <key>CFBundleDisplayName</key>
  <string>ThixInstall</string>
  <key>CFBundleExecutable</key>
  <string>ThixInstall</string>
  <key>CFBundleIconFile</key>
  <string>ThixInstall</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${major}.${minor}.${build}</string>
  <key>CFBundleVersion</key>
  <string>${build}</string>
  <key>CFBundleGetInfoString</key>
  <string>ThixInstall ${version}</string>
  <key>NSHumanReadableCopyright</key>
  <string>© Matthias Schmoldt</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>LSUIElement</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>CFBundleDevelopmentRegion</key>
  <string>de</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: Apple Silicon runs only signed code. A Developer ID signature + notarization can replace it later.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# Update package (MacAppBundle unpacks it with ditto) and disk image for the first installation.
ditto -c -k --keepParent "$app" "$out/ThixInstall-macOS-${arch}.zip"

dmg_root="$work/dmg"
mkdir -p "$dmg_root"
cp -R "$app" "$dmg_root/"
ln -s /Applications "$dmg_root/Programme"
hdiutil create -volname "ThixInstall" -srcfolder "$dmg_root" -ov -format UDZO "$out/ThixInstall-macOS-${arch}.dmg" >/dev/null

rm -rf "$work"
ls -l "$out"
