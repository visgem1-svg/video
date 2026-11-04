#!/bin/sh
# Publishes cam-001..cam-100 to mediamtx (-c copy: no re-encode). One supervisor loop per camera; a dead
# publisher is restarted. Camera N -> profile by (N-1) mod 20: 0-13 A 960x540@15 (70 %), 14-16 B 1280x720@25
# (15 %), 17-19 C 640x360@10 (15 %); clip = n-th clip of the profile (round-robin) + start offset, so streams differ.
#   cam-031..040 are profile S (static scene + noise + crossing object, clips S_1..S_10, started at -ss 0 so events
#   follow assets/S-events.json); every other camera keeps the A/B/C mapping above.
#   Unstable (killed/restarted every random 30-90 s): cam-019 020 047 073 096.
#   cam-020 and cam-073 additionally stay down 20-60 s in ~50 % of the cycles.
RTSP=${RTSP_BASE:-rtsp://mediamtx:8554}
COUNT=${CAMERAS:-100}
VIDEOS=/sim/videos
UNSTABLE=" 19 20 47 73 96 "
LONGDOWN=" 20 73 "

rand() { # rand LO HI  -> integer in [LO,HI]
  r=$(od -An -N2 -tu2 /dev/urandom | tr -d ' ')
  echo $(( $1 + r % ($2 - $1 + 1) ))
}

pick_clip() { # pick_clip N -> prints clip path
  if [ $1 -ge 31 ] && [ $1 -le 40 ]; then echo "$VIDEOS/S_$(( $1 - 30 )).mp4"; return; fi
  r=$(( ($1 - 1) % 20 )); blk=$(( ($1 - 1) / 20 ))
  if   [ $r -le 13 ]; then p=A; k=$(( blk*14 + r ))
  elif [ $r -le 16 ]; then p=B; k=$(( blk*3 + r - 14 ))
  else                     p=C; k=$(( blk*3 + r - 17 )); fi
  set -- $(ls $VIDEOS/${p}_*.mp4)
  n=$#; idx=$(( k % n )); shift $idx
  echo "$1"
}

start_offset() { if [ $1 -ge 31 ] && [ $1 -le 40 ]; then echo 0; else echo $(( ($1 * 3) % 8 )); fi; }

publish_exec() { # publish_exec N CAM : replaces the (sub)shell with ffmpeg so `kill $pid` hits ffmpeg
  exec ffmpeg -hide_banner -loglevel error -re -ss $(start_offset $1) -stream_loop -1 -i "$(pick_clip $1)" \
    -c copy -f rtsp -rtsp_transport tcp "$RTSP/$2"
}
publish() { ( publish_exec "$@" ); }       # foreground, returns when ffmpeg exits

camera() { # camera N
  id=$(printf 'cam-%03d' "$1")
  sleep $(( $1 / 5 ))                              # stagger start-up (<= 20 s)
  case "$UNSTABLE" in *" $1 "*) unstable=1;; *) unstable=0;; esac
  while true; do
    if [ $unstable = 1 ]; then
      publish_exec_bg() { ( publish_exec "$1" "$2" ) & pid=$!; }
      publish_exec_bg "$1" "$id"
      life=$(rand 30 90); sleep "$life"
      kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
      down=1
      case "$LONGDOWN" in *" $1 "*) [ "$(rand 0 1)" = 1 ] && down=$(rand 20 60);; esac
      echo "{\"msg\":\"unstable camera restart\",\"camera_id\":\"$id\",\"up_s\":$life,\"down_s\":$down}"
      sleep "$down"
    else
      publish "$1" "$id"
      echo "{\"msg\":\"publisher exited, restarting\",\"camera_id\":\"$id\"}"
      sleep 2
    fi
  done
}

n=1
while [ $n -le $COUNT ]; do camera $n & n=$((n+1)); done

# Heartbeat file for the container healthcheck: alive while >= 90 % of the ffmpeg publishers run.
need=$(( COUNT * 9 / 10 ))
while true; do
  cnt=0
  for p in /proc/[0-9]*; do
    read -r c < "$p/comm" 2>/dev/null && [ "$c" = ffmpeg ] && cnt=$((cnt+1))
  done
  [ $cnt -ge $need ] && touch /tmp/healthy
  sleep 5
done
