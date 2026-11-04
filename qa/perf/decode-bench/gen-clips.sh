#!/bin/sh
# Runs INSIDE vp/frame-extractor:dev (Debian ffmpeg = production decoder). Usage: gen-clips.sh IMG OUTDIR [SECONDS]
# Makes camera-like test clips: slow pan/zoom over a real photo + temporal sensor noise, 15 fps, High profile,
# 2 B-frames, per-resolution camera-typical bitrate. GOP 2 s (30) = "baseline" decode set, GOP 1 s (15) = keyframe-only set.
set -e
IMG=$1; OUT=$2; DUR=${3:-62}; mkdir -p "$OUT"
R=15
mk() { # mk NAME W H KBPS CODEC GOP
  n=$1; W=$2; H=$3; K=$4; C=$5; G=$6
  [ -f "$OUT/$n.mp4" ] && return
  vf="scale=w='trunc(${W}*1.4/2)*2':h='trunc(${H}*1.4/2)*2':force_original_aspect_ratio=increase,loop=loop=-1:size=1,setpts=N/(${R}*TB),crop=${W}:${H}:x='(iw-${W})/2*(1+0.7*sin(2*PI*t/20))':y='(ih-${H})/2*(1+0.7*sin(2*PI*t/20+1))',noise=alls=5:allf=t+u,format=yuv420p"
  if [ "$C" = h264 ]; then
    enc="-c:v libx264 -profile:v high -preset medium -bf 2 -g $G -keyint_min $G -sc_threshold 0 -b:v ${K}k -maxrate ${K}k -bufsize $((K*2))k"
  else
    enc="-c:v libx265 -preset veryfast -b:v ${K}k -maxrate ${K}k -bufsize $((K*2))k -x265-params keyint=$G:min-keyint=$G:scenecut=0:log-level=error:pools=1 -tag:v hvc1"
  fi
  ffmpeg -hide_banner -loglevel error -y -i "$IMG" -t "$DUR" -vf "$vf" -r $R $enc -an "$OUT/$n.mp4"
}
for spec in 360p:640:360:600 540p:960:540:1200 720p:1280:720:2500 1080p:1920:1080:5000; do
  IFS=: read res W H K <<EOF2
$spec
EOF2
  mk h264_${res}_gop2s $W $H $K h264 30
  mk h264_${res}_gop1s $W $H $K h264 15
done
mk h265_720p_gop2s 1280 720 1500 h265 30
mk h265_1080p_gop2s 1920 1080 3000 h265 30
ls -la "$OUT"
