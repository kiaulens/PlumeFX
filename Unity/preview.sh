#!/bin/bash
# PlumeFX preview: render stills (and optionally videos) of a simulated launch with the real core and shader.
# Usage: preview.sh [video views, e.g. side,chase] [duration s, default 20] [still times, e.g. 30,60,300]
# Environment: UNITY_EXE (Unity 2019.4.18f1), FFMPEG (for videos, default: ffmpeg from PATH), plus the PFX_* variables
# described in Assets/PlumeFX/Preview/PlumePreviewRunner.cs.
P="$(cd "$(dirname "$0")" && pwd)"
UNITY="${UNITY_EXE:-C:/Program Files/Unity/Hub/Editor/2019.4.18f1/Editor/Unity.exe}"
FF="${FFMPEG:-ffmpeg}"
VID="$1"; DUR="${2:-20}"
ARGS=(-batchmode -projectPath "$P" -executeMethod PlumePreviewMenu.Run -quit -logFile "$P/preview.log" -pfxDuration "$DUR")
[ -n "$VID" ] && ARGS+=(-pfxVideo "$VID")
SHOTS="$3"; [ -n "$SHOTS" ] && ARGS+=(-pfxShots "$SHOTS")
"$UNITY" "${ARGS[@]}"
grep -a -i "PLUMEFX_PREVIEW_DONE\|error CS\|Exception\|Shader error" "$P/preview.log" | head -10
cd "$P" && python sheet.py 00.5,01.5,03.0,05.0 >/dev/null && cp Preview/sheet.png Preview/sheet_a.png && python sheet.py 08.0,12.0,16.0,20.0 >/dev/null && cp Preview/sheet.png Preview/sheet_b.png
if [ -n "$VID" ]; then
  IFS=',' read -ra VS <<< "$VID"
  for v in "${VS[@]}"; do
    "$FF" -y -loglevel error -framerate 30 -i "Preview/frames/${v}_%04d.png" -c:v libx264 -pix_fmt yuv420p -crf 20 "Preview/${v}.mp4" && echo "Video: Preview/${v}.mp4"
  done
fi
