# Decode + motion-gate benchmark (contract 9.9 s6)

Host: AMD Ryzen 7 PRO 4750GE (16 threads), laptop ~90 % busy with the live stack, so everything is **CPU time
(rusage user+sys of the ffmpeg child via wait4), not wall clock**. Decoder: Debian `ffmpeg 7.1.5-0+deb13u1` (image
`vp/frame-extractor:dev`, has libx264/libx265). Container `--cpus=1`, 3 repeats interleaved, median + min..max.
Scripts: `run.sh` (orchestrates), `gen-clips.sh`, `bench.py`, `calibrate.py`, `detq-frames.sh` + `detq.py`, `gate-live.sh`.

## B. Decode cost (cpu-s per source-s)
Chain = production: `-threads 1 [opt] -i clip -vf fps=3,scale='min(640,iw)':-2 -c:v mjpeg -q:v 5 -threads 1 -f image2pipe -` -> discarded.
Clips: 62 s, **15 fps**, High profile, 2 B-frames, camera-like bitrates (360p 600k / 540p 1.2M / 720p 2.5M / 1080p 5M kbps; H.265 1.5M/3M),
pan/zoom over a real photo + temporal sensor noise. GOP 2 s for baseline/flag, GOP 1 s for keyframe-only. Cost scales ~linearly with source fps.
"cores/100" = median x 100 cameras (all full-rate).

| res | codec | baseline | `-skip_loop_filter all` | keyframe-only GOP1s, fps=3 | keyframe-only, fps=1 |
|---|---|---|---|---|---|
| 360p | h264 | 0.068 (6.8 cores/100; +-2 %) | 0.063 (-8 %) | 0.031 | 0.021 |
| 540p | h264 | 0.122 (12.2; spread 6 %) | 0.104 (-15 %; spread 12 %) | 0.048 | 0.034 |
| 720p | h264 | 0.195 (19.5) | 0.180 (-8 %) | 0.070 | 0.051 |
| 1080p | h264 | 0.404 (40.4) | 0.369 (-9 %) | 0.116 | 0.091 |
| 720p | h265 | 0.246 (24.6) | 0.203 (-17 %) | n/t | n/t |
| 1080p | h265 | 0.507 (50.7) | 0.404 (-20 %) | n/t | n/t |

Spread (max-min)/median is 0.5-12 % (full list in `results.json`). Keyframe-only is NOT a 3 fps option on real cameras (keyframe interval
1-4 s, uncontrollable); the table only shows the floor. Not tested: H.265 keyframe-only.

### `-skip_loop_filter all`: detection quality
108 frames (3 pan clips: zeedijk, station, crossing; 960x540, 1.2 Mbps, production chain), detector `/detect_objects`, baseline vs flag decode of the same stream.
PSNR baseline-vs-flag after the 640 px MJPEG: 48.2-50.0 dB.

| | baseline | flag | delta |
|---|---|---|---|
| objects (all) | 765 | 756 | -9 (-1.2 %) |
| objects conf >= 0.5 | 646 | 644 | -2 (-0.3 %) |
| matched (IoU>=0.5, same class) | 750 | | only-baseline 15, only-flag 6 |
| frames with count diff | 21 / 108 | | mean -0.08 objects/frame |
| matched confidence delta | mean -0.0004, sd 0.016, range -0.083..+0.144 | | |

Verdict: loss is within noise at conf >= 0.5. Saves 8-20 % decode CPU (more on H.265). Recommendation: default `FFMPEG_SKIP_LOOP_FILTER=on`
for >= 720p; caveat: only 3 images / one bitrate tested, nothing below 600 kbps.

## C. Motion-gate calibration
Clips: S_1..S_10 (60 s, 30 events: 10 clips x 3, car/person 22-30 % of width, 3 s crossing) and 6 moving pan/zoom clips (cam-001/002/003/010/015/018 clips, 32 s).
Plus 4 "small object" variants of S_1..4 (person 7 % / car 10 % of width, ~0.6 % of frame area). Metrics: skipped % of fps=3 samples; event hit = >= 2 emitted
frames within 1 s of start; false trigger = emitted frame outside [start, end+1 s] with `max=0` (pure noise / keyframe-refresh flicker).

Findings: only `hi` and `lo` matter; `frac` (0.2-0.5) changed nothing. With ffmpeg defaults (768/320) a keyframe refresh (GOP 1 s) changes
every block's noise, so 26 % of non-event frames trigger and only 61 % are skipped. `lo >= 1280` removes that (0.7 % false triggers); `hi` sets small-object sensitivity.
All recalls were 100 % (60/60 big, 12/12 small events), at every setting listed.

| preset (H,L,F) | skipped, static (max=0) | false trig. (max=0) | skipped, static, heartbeat 5 s (M=14) | heartbeat 10 s (M=29) | moving skipped | small-obj skipped / false |
|---|---|---|---|---|---|---|
| ffmpeg default 768,320,0.33 | 61.5 % | 26.1 % | - | - | 0.0 % | - |
| low 3072,2560,0.4 | 82.8 % | 0.7 % | 78.8 % | 81.0 % | 3.5 % | 82.8 % / 0.7 % |
| **medium 1536,1280,0.33** | 82.8 % | 0.7 % | **78.8 %** | **81.0 %** | **0.0 %** | 82.8 % / 0.7 % |
| high 1024,960,0.2 | 80.6 % | 3.3 % | 77.0 % | 78.9 % | 0.0 % | 77.2 % / 7.3 % |

**Acceptance gap**: medium misses ">= 80 % skipped" at the default heartbeat 5 s (78.8 %), because the injected events alone occupy ~17 % of samples
(3 s per 20 s + 1 s tail) and the heartbeat forces 1 frame per 15 (6.7 % of quiet time): the arithmetic ceiling is ~77-79 %. It passes at `heartbeat_s=10`
(81.0 %) or with event duty <= 14 %. Recall 100 % and moving <= 5 % pass. Low vs medium are indistinguishable on these (high-contrast) events; low differs only for faint changes (untested).
Values written to `services/frame-extractor/extractor/motion_presets.py`.
**`tests/test_motion.py` lines 92 and 117 still assert the old medium (768/320/0.33) and will fail** (not edited, outside my remit).

Verified: `fps=3,mpdecimate=...,scale -fps_mode vfr` works on Debian ffmpeg 7.1.5, offline and via RTSP (cam-035 over MediaMTX gave 78 frames/60 s, identical to the file run).

## D. Live gate (deployed extractor, registry accepts `motion`)
cam-031..034 registered temporarily with the standard plan, 180 s windows (Prometheus `vp_extractor_frames_total`), then deleted.
The deployed image still has the OLD presets (768/320/0.33), so this is the pre-calibration number:

| | emitted frames/s (4 cams) | per camera | detector calls (1 detect/frame) |
|---|---|---|---|
| motion off | 12.00 | 3.00 | 12.0/s |
| motion medium (old 768/320), heartbeat 5 s | 5.32 | 1.33 | 5.3/s (-56 %) |
| calibrated medium, offline prediction | ~2.5 (78.8 % skipped) | 0.64 | ~2.5/s (-79 %) |

Gotcha: `PUT /api/cameras/{id}` replaces the whole record; sending only `motion` nulls `plan_id`, the camera loses demand and drops to idle (0.2 fps).
Redeploy `frame-extractor` to pick up the new presets; the calibrated live number is then still to be measured.

## A. Sim cameras
cam-031..040 = profile S (clips S_1..S_10: 6x 960x540@15, 2x 640x360@10, 2x 1280x720@25; 60 s loops, started at `-ss 0` so events follow `assets/S-events.json`).
cam-001..030 and 041+ mapping unchanged. Image 233 -> 298 MB (+65 MB; the 10 S clips are 36 MB of the 66 MB in /sim/videos).
