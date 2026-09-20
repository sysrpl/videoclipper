#!/bin/bash
set -e

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_NAME="Video Clipper"
ARCH="${1:-arm64}"                # pass x64 for Intel Macs
RID="osx-$ARCH"
APP_DIR="$HOME/Applications/$APP_NAME.app"
CONTENTS_DIR="$APP_DIR/Contents"

# macOS app icons are .icns, built from a set of PNG sizes via the
# built-in sips/iconutil tools.
ICONSET="$REPO_DIR/resources/icon.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size "$REPO_DIR/resources/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$REPO_DIR/resources/icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$REPO_DIR/resources/icon.icns"
rm -rf "$ICONSET"

rm -rf "$APP_DIR"
mkdir -p "$CONTENTS_DIR/MacOS" "$CONTENTS_DIR/Resources"

dotnet publish "$REPO_DIR/videoclipper.csproj" -c Release -r "$RID" --self-contained -o "$CONTENTS_DIR/MacOS"

cp "$REPO_DIR/resources/icon.icns" "$CONTENTS_DIR/Resources/icon.icns"

cat > "$CONTENTS_DIR/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>com.sysrpl.videoclipper</string>
    <key>CFBundleVersion</key>
    <string>0.1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>0.1.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>videoclipper</string>
    <key>CFBundleIconFile</key>
    <string>icon.icns</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

chmod +x "$CONTENTS_DIR/MacOS/videoclipper"

echo "Published to: $APP_DIR"
echo "It will show in Launchpad and Spotlight as \"$APP_NAME\"."
echo "Move it to /Applications instead of ~/Applications if you want it available to all users on this Mac."

# The program runs ffmpeg and ffprobe for everything it does.
if ! command -v ffmpeg >/dev/null || ! command -v ffprobe >/dev/null; then
    echo "Warning: ffmpeg is not installed. Install it with: brew install ffmpeg"
fi
