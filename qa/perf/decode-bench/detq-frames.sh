#!/bin/sh
# Runs inside vp/frame-extractor:dev. Usage: detq-frames.sh ASSETDIR OUTDIR. For 3 photos: 12 s pan clip (960x540@15, 1200 kbps
# x264 High, GOP 2 s, noise) -> production chain frames at 3 fps (640 wide, mjpeg q5) decoded baseline (A) and with -skip_loop_filter all (B).
set -e
A=$1; O=$2; mkdir -p $O
for img in zeedijk station crossing; do
  mkdir -p $O/$img/A $O/$img/B
  ffmpeg -hide_banner -loglevel error -y -i $A/$img.jpg -t 12 -vf "scale=w=1344:h=756:force_original_aspect_ratio=increase,loop=loop=-1:size=1,setpts=N/(15*TB),crop=960:540:x='(iw-960)/2*(1+0.7*sin(2*PI*t/12))':y='(ih-540)/2*(1+0.7*sin(2*PI*t/12+1))',noise=alls=5:allf=t+u,format=yuv420p" -r 15 -c:v libx264 -profile:v high -preset medium -bf 2 -g 30 -b:v 1200k -maxrate 1200k -bufsize 2400k -an $O/$img.mp4
  for v in A B; do
    opt=""; [ $v = B ] && opt="-skip_loop_filter all"
    ffmpeg -hide_banner -loglevel error -y -threads 1 $opt -i $O/$img.mp4 -an -vf "fps=3,scale='min(640,iw)':-2" -c:v mjpeg -q:v 5 -threads 1 $O/$img/$v/%03d.jpg
  done
  ffmpeg -hide_banner -i $O/$img/A/%03d.jpg -i $O/$img/B/%03d.jpg -lavfi psnr -f null - 2>&1 | grep -o "average:[0-9.a-z]*" | sed "s/^/PSNR $img /"
done
