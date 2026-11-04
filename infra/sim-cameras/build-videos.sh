#!/bin/sh
# Builds the base clips at image-build time. Pan/zoom over real images only (no synthetic shapes).
# Seamless 8 s loops (sine pan, period 8 s), H.264 baseline, GOP = 1 s, -bf 0.
# Profiles:  A 960x540@15 (70 % of cameras)  B 1280x720@25 (15 %)  C 640x360@10 (15 %)
# Variants per image: v0 plain, v1 mirrored, v2 mirrored + hue shift. Pan amplitude, zoom and phase differ per
# clip. The supervisor adds a start offset (-ss) so two cameras never show the same frame at the same time.
#  S  static scene (profile S, cam-031..040): fixed image + temporal sensor noise + a cut-out object (car / person from
#     assets) crossing for 3 s three times per 60 s loop (events deterministic, see s_start; `build-videos.sh --events`
#     prints the JSON stored in assets/S-events.json). S_1..6 960x540@15, S_7..8 640x360@10, S_9..10 1280x720@25.
set -e
# s_start CLIP K -> event start second (K=0..2): 5+20K plus a clip-specific jitter in [-3,3]
s_start() { echo $(( 5 + 20*$2 + ($1*3 + $2*5) % 7 - 3 )); }
S_SCENES="street parking1 jam javaplein quebec lobby parking2 crossing station zeedijk"
s_geom() { # s_geom IDX -> "W H FPS KBPS"
  if [ "$1" -le 6 ]; then echo "960 540 15 400"; elif [ "$1" -le 8 ]; then echo "640 360 10 200"; else echo "1280 720 25 800"; fi
}
if [ "$1" = "--events" ]; then
  printf '{
  "note": "Times are seconds from the start of the 60 s loop (a publisher restart restarts the loop at 0; cam-031..040 are published with -ss 0). Each event = object visible 3 s, entering from off-screen at start.",
  "loop_s": 60,
  "cameras": {
'
  i=1
  for sc in $S_SCENES; do
    set -- $(s_geom $i); w=$1; h=$2; r=$3
    ev=""; for k in 0 1 2; do st=$(s_start $i $k); obj=car; [ $(( (i+k) % 2 )) -eq 1 ] && obj=person
      ev="$ev{\"start\": $st, \"end\": $((st+3)), \"object\": \"$obj\"}"; [ $k -lt 2 ] && ev="$ev, "; done
    printf '    "cam-%03d": {"clip": "S_%d", "scene": "%s", "width": %d, "height": %d, "fps": %d, "events": [%s]}' $((30+i)) $i $sc $w $h $r "$ev"
    [ $i -lt 10 ] && printf ',
' || printf '
'
    i=$((i+1))
  done
  printf '  }
}
'; exit 0
fi
SRC=$1; OUT=$2; mkdir -p "$OUT"
IMGS=$(cd "$SRC" && ls *.jpg *.png | sort)

mk() { # mk NAME FILE W H FPS KBPS VARIANT IDX
  name=$1; f=$2; W=$3; H=$4; R=$5; K=$6; v=$7; idx=$8
  amp=$(awk -v i="$idx" 'BEGIN{printf "%.2f", 0.70 + 0.05*(i%6)}')
  zoom=$(awk -v i="$idx" 'BEGIN{printf "%.2f", 1.35 + 0.05*(i%7)}')
  ph=$(awk -v i="$idx" 'BEGIN{printf "%.2f", (i%8)*0.7}')
  post="format=yuv420p"
  [ "$v" -ge 1 ] && post="hflip,$post"
  [ "$v" -ge 2 ] && post="hue=h=$(( (idx*47 + 60) % 360 )):s=1.15,$post"
  # The still is scaled ONCE, then repeated (loop filter) and cropped per frame: much cheaper than -loop 1.
  ffmpeg -hide_banner -loglevel error -y -i "$SRC/$f" -t 8 \
    -vf "scale=w='trunc(${W}*${zoom}/2)*2':h='trunc(${H}*${zoom}/2)*2':force_original_aspect_ratio=increase,loop=loop=-1:size=1,setpts=N/(${R}*TB),crop=${W}:${H}:x='(iw-${W})/2*(1+${amp}*sin(2*PI*t/8+${ph}))':y='(ih-${H})/2*(1+${amp}*sin(2*PI*t/8+${ph}+1))',${post}" \
    -r "$R" -c:v libx264 -profile:v baseline -level 4.0 -preset veryfast -bf 0 -g "$R" -keyint_min "$R" \
    -sc_threshold 0 -b:v ${K}k -maxrate $((K*12/10))k -bufsize $((K*2))k -an -movflags +faststart "$OUT/$name.mp4"
}

mk_s() { # mk_s IDX SCENE : static scene + noise + crossing object
  idx=$1; sc=$2; set -- $(s_geom $idx)
  W=$1; H=$2; R=$3; K=$4
  fc="[0:v]scale=${W}:${H}:force_original_aspect_ratio=increase,crop=${W}:${H},setsar=1,loop=loop=$((61*R)):size=1,setpts=N/(${R}*TB)[bg]"
  fc="$fc;[1:v]crop=1010:520:110:150,scale=trunc(${SCALE_CAR:-0.30}*${W}/2)*2:-2,format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='255*clip((1-pow(pow(abs((X-W/2)/(W/2)),4)+pow(abs((Y-H/2)/(H/2)),4),0.25))*12,0,1)',loop=loop=$((61*R)):size=1,setpts=N/(${R}*TB),split=3[c0][c1][c2]"
  fc="$fc;[2:v]crop=390:280:70:60,scale=trunc(${SCALE_PERSON:-0.22}*${W}/2)*2:-2,format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='255*clip((1-pow(pow(abs((X-W/2)/(W/2)),4)+pow(abs((Y-H/2)/(H/2)),4),0.25))*12,0,1)',loop=loop=$((61*R)):size=1,setpts=N/(${R}*TB),split=3[p0][p1][p2]"
  prev=bg; used=" "
  for k in 0 1 2; do
    st=$(s_start $idx $k); o=c; [ $(( (idx+k) % 2 )) -eq 1 ] && o=p
    if [ $(( (idx+k) % 3 )) -eq 0 ]; then x="x='-overlay_w+(main_w+overlay_w)*(t-${st})/3'"; else x="x='main_w-(main_w+overlay_w)*(t-${st})/3'"; fi
    y="y='main_h*(0.30+0.12*$(( (idx+k) % 4 )))'"
    fc="$fc;[$prev][$o$k]overlay=${x}:${y}:enable='between(t,${st},${st}+3)':eof_action=pass[v$k]"; prev=v$k; used="$used$o$k "
  done
  for o in c0 c1 c2 p0 p1 p2; do case "$used" in *" $o "*) ;; *) fc="$fc;[$o]nullsink";; esac; done
  fc="$fc;[$prev]noise=alls=5:allf=t+u,format=yuv420p[out]"
  ffmpeg -hide_banner -loglevel error -y -i "$SRC/$sc.jpg" -i "$SRC/car.jpg"     -i "$SRC/messi.jpg" -filter_complex "$fc" -map "[out]" -t 60     -r "$R" -c:v libx264 -profile:v baseline -level 4.0 -preset veryfast -bf 0 -g "$R" -keyint_min "$R"     -sc_threshold 0 -b:v ${K}k -maxrate $((K*12/10))k -bufsize $((K*2))k -an -movflags +faststart "$OUT/S_${idx}.mp4"
}

n=0
throttle() { n=$((n+1)); [ $((n % 4)) -eq 0 ] && wait; return 0; }   # 4 encodes in parallel

i=0
for f in $IMGS; do
  i=$((i+1))
  for v in 0 1 2; do mk "A_${i}_$v" "$f" 960 540 15 500 $v $((i*3+v)) & throttle; done
  # B (1280x720@25) for the first 6 images (2 variants), C (640x360@10) for the first 12 images
  if [ $i -le 6 ]; then for v in 0 1; do mk "B_${i}_$v" "$f" 1280 720 25 1000 $v $((i*5+v)) & throttle; done; fi
  if [ $i -le 12 ]; then mk "C_${i}_0" "$f" 640 360 10 250 $((i%3)) $((i*7)) & throttle; fi
done
i=0
for sc in $S_SCENES; do i=$((i+1)); mk_s $i $sc & throttle; done
wait
cnt=$(ls "$OUT" | wc -l); du -sh "$OUT"; echo "clips: $cnt"; [ "$cnt" -ge 82 ] || { echo "missing clips"; exit 1; }
