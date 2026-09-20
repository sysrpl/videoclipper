#!/bin/bash
set -e

dotnet publish -c Release -r linux-x64 --self-contained -o "$HOME/.local/share/video-clipper"

mkdir -p "$HOME/.local/share/applications"

# App icon for the Mint menu and other launchers.
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
mkdir -p "$ICON_DIR"
cp resources/icon.png "$ICON_DIR/video-clipper.png"

cat > "$HOME/.local/share/applications/video-clipper.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Video Clipper
GenericName=Video Trimmer
Comment=Trim and encode video clips for the web and the Godot engine
Exec=$HOME/.local/share/video-clipper/videoclipper
Path=$HOME/.local/share/video-clipper
Icon=video-clipper
Terminal=false
Categories=AudioVideo;Video;AudioVideoEditing;
Keywords=video;trim;clip;cut;crop;MKV;MP4;OGV;FFmpeg;encode;Godot;
StartupNotify=true
EOF

chmod +x "$HOME/.local/share/video-clipper/videoclipper"
chmod +x "$HOME/.local/share/applications/video-clipper.desktop"
update-desktop-database "$HOME/.local/share/applications"
gtk-update-icon-cache -q -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true

# The program runs ffmpeg and ffprobe for everything it does.
if ! command -v ffmpeg >/dev/null || ! command -v ffprobe >/dev/null; then
    echo "Warning: ffmpeg is not installed. Install it with: sudo apt install ffmpeg"
fi
