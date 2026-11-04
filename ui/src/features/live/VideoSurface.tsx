import { memo, useRef } from 'react'
import { Loader2, VideoOff } from 'lucide-react'
import { useRuntime } from '@/lib/api/runtime'
import { useWhep } from '@/lib/whep/useWhep'
import { hashHue } from '@/lib/utils'

/** Synthetic "camera" used in mock mode (no MediaMTX): cheap CSS-only animated scene. */
function MockScene({ cameraId, label }: { cameraId: string; label: string }) {
  const hue = hashHue(cameraId)
  return (
    <div
      className="absolute inset-0 overflow-hidden"
      style={{
        background: `linear-gradient(135deg, hsl(${hue} 30% 14%), hsl(${(hue + 40) % 360} 35% 22%) 60%, hsl(${hue} 25% 10%))`,
      }}
    >
      <div className="absolute inset-x-0 top-0 h-1/4 animate-[scan_6s_linear_infinite] bg-gradient-to-b from-transparent via-white/5 to-transparent" />
      <div className="absolute bottom-2 left-2 font-mono text-[10px] text-white/25">
        {label} · simulated
      </div>
    </div>
  )
}

export const VideoSurface = memo(function VideoSurface({
  cameraId,
  label,
  active,
}: {
  cameraId: string
  label: string
  active: boolean
}) {
  const { mode } = useRuntime()
  const mock = mode === 'mock'
  const ref = useRef<HTMLVideoElement>(null)
  const status = useWhep(cameraId, active && !mock, ref)

  if (mock) return <MockScene cameraId={cameraId} label={label} />
  return (
    <>
      <video
        ref={ref}
        className="absolute inset-0 size-full bg-[var(--video-bg)] object-contain"
        muted
        playsInline
        autoPlay
      />
      {status !== 'playing' && (
        <div className="absolute inset-0 grid place-items-center bg-[var(--video-bg)]/80 text-[13px] text-white/70">
          <div className="flex flex-col items-center gap-1.5">
            {status === 'connecting' ? (
              <Loader2 className="size-5 animate-spin text-white/60" aria-hidden />
            ) : (
              <VideoOff className="size-5 text-white/50" aria-hidden />
            )}
            {status === 'connecting'
              ? 'Connecting'
              : status === 'failed'
                ? 'No signal, retrying'
                : 'Paused'}
          </div>
        </div>
      )}
    </>
  )
})
