import { memo, useEffect, useRef } from 'react'
import { hub } from '@/lib/realtime/hub'
import {
  buildDrawItems,
  colorFor,
  fadeAlpha,
  isDashed,
  mergeDetection,
  pillText,
  showLabel,
  type DrawItem,
} from '@/lib/overlay'
import type { Detection } from '@/lib/types'

/**
 * Canvas overlay. Holds the last detection until the next one arrives, dims it with age and
 * hides it after 5 s. Detections never touch React state: the hub handler stores the latest
 * message and (re)starts a requestAnimationFrame loop that idles once the boxes are hidden.
 */
export const DetectionOverlay = memo(function DetectionOverlay({
  cameraId,
  active,
  labels = true,
}: {
  cameraId: string
  active: boolean
  labels?: boolean
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!active || !canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    let cssW = 0
    let cssH = 0
    let dpr = 1
    let det: Detection | null = null
    let items: DrawItem[] = []
    let arrived = 0
    let raf = 0
    let lastDraw = 0
    let dirty = true

    const resize = () => {
      dpr = window.devicePixelRatio || 1
      cssW = canvas.clientWidth
      cssH = canvas.clientHeight
      canvas.width = Math.max(1, Math.round(cssW * dpr))
      canvas.height = Math.max(1, Math.round(cssH * dpr))
      if (det) items = buildDrawItems(det, cssW, cssH)
      dirty = true
      if (!raf && det) raf = requestAnimationFrame(frame)
    }

    const frame = () => {
      raf = 0
      const nowMs = performance.now()
      // held boxes only dim slowly: redraw at ~12 fps unless something new arrived
      if (!dirty && nowMs - lastDraw < 80) {
        raf = requestAnimationFrame(frame)
        return
      }
      dirty = false
      lastDraw = nowMs
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      ctx.clearRect(0, 0, cssW, cssH)
      if (!det) return
      const alpha = fadeAlpha(nowMs - arrived)
      if (alpha <= 0) {
        det = null
        items = []
        return
      }
      ctx.globalAlpha = alpha
      ctx.font = '600 11px ui-sans-serif, system-ui, sans-serif'
      ctx.textBaseline = 'top'
      for (const it of items) {
        const c = colorFor(it)
        const { x, y, w, h } = it.rect
        ctx.setLineDash(isDashed(it) ? [6, 3] : [])
        // dark outer stroke keeps the box readable on snow and at night
        ctx.strokeStyle = 'rgba(0,0,0,.55)'
        ctx.lineWidth = 4
        ctx.strokeRect(x, y, w, h)
        ctx.strokeStyle = c
        ctx.lineWidth = 2
        ctx.strokeRect(x, y, w, h)
        if (labels && showLabel(it, cssW)) {
          const text = pillText(it)
          const tw = ctx.measureText(text).width + 10
          const ly = y >= 18 ? y - 18 : y
          ctx.setLineDash([])
          ctx.fillStyle = c
          ctx.beginPath()
          ctx.roundRect(x - 1, ly, tw, 17, 4)
          ctx.fill()
          ctx.fillStyle = '#0B0D12'
          ctx.fillText(text, x + 4, ly + 3)
        }
      }
      ctx.globalAlpha = 1
      raf = requestAnimationFrame(frame)
    }

    const ro = new ResizeObserver(resize)
    ro.observe(canvas)
    resize()

    const off = hub.onDetections(cameraId, (d) => {
      det = mergeDetection(det, d)
      arrived = performance.now()
      dirty = true
      items = buildDrawItems(det, cssW, cssH)
      if (!raf) raf = requestAnimationFrame(frame)
    })

    return () => {
      off()
      ro.disconnect()
      cancelAnimationFrame(raf)
      ctx.clearRect(0, 0, canvas.width, canvas.height)
    }
  }, [cameraId, active, labels])

  return <canvas ref={canvasRef} className="pointer-events-none absolute inset-0 size-full" />
})
