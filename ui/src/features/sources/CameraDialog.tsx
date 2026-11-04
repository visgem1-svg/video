import { cloneElement, isValidElement, useId, useMemo, useState, type ReactElement } from 'react'
import { findDuplicates } from '@/lib/url'
import { Loader2 } from 'lucide-react'
import { Dropdown } from '@/components/ui/dropdown'
import { toast } from '@/lib/toast'
import { Dialog, DialogContent, Switch } from '@/components/ui/dialog'
import { Button, Input, Label, Segmented } from '@/components/ui/primitives'
import { ApiError } from '@/lib/api/types'
import { useSaveCamera } from '@/lib/api/queries'
import { DEFAULT_MOTION, SENSITIVITIES, motionOf } from '@/lib/motion'
import {
  toCameraInput,
  type CameraConfig,
  type CameraInput,
  type MotionConfig,
  type Plan,
} from '@/lib/types'
import { mapServerErrors, parseTags, validateCamera, type CameraErrors } from './validation'

const EMPTY: CameraInput = {
  camera_id: '',
  name: '',
  stream_url: 'rtsp://',
  analysis_fps: 3,
  enabled: true,
  plan_id: null,
  tags: [],
}

export function CameraDialog({
  open,
  onOpenChange,
  camera,
  allCameras,
  plans,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  camera: CameraConfig | null
  allCameras: CameraConfig[]
  plans: Plan[]
}) {
  // Remount form state whenever the dialog opens for a different camera.
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {open && (
        <DialogContent
          className="w-[min(640px,calc(100vw-2rem))]"
          title={camera ? `Edit ${camera.name}` : 'Add camera'}
          description="Changes are published to the registry (camera.config)."
        >
          <CameraForm
            key={camera?.camera_id ?? 'new'}
            camera={camera}
            allCameras={allCameras}
            plans={plans}
            onDone={() => onOpenChange(false)}
          />
        </DialogContent>
      )}
    </Dialog>
  )
}

function CameraForm({
  camera,
  allCameras,
  plans,
  onDone,
}: {
  camera: CameraConfig | null
  allCameras: CameraConfig[]
  plans: Plan[]
  onDone: () => void
}) {
  const editing = !!camera
  const [form, setForm] = useState<CameraInput>(camera ? toCameraInput(camera) : EMPTY)
  const [tags, setTags] = useState((camera?.tags ?? []).join(', '))
  const [analysisUrl, setAnalysisUrl] = useState(camera?.analysis_stream_url ?? '')
  const [motion, setMotion] = useState<MotionConfig>(camera ? motionOf(camera) : DEFAULT_MOTION)
  const [errors, setErrors] = useState<CameraErrors>({})
  const [serverNote, setServerNote] = useState<string>()
  const save = useSaveCamera()
  const duplicates = useMemo(
    () => findDuplicates(form.stream_url, allCameras, camera?.camera_id ?? form.camera_id),
    [form.stream_url, allCameras, camera, form.camera_id],
  )
  const set = <K extends keyof CameraInput>(k: K, v: CameraInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  const submit = (ev: React.FormEvent) => {
    ev.preventDefault()
    const payload: CameraInput = {
      camera_id: form.camera_id.trim(),
      name: form.name.trim(),
      stream_url: form.stream_url.trim(),
      analysis_fps: Number(form.analysis_fps),
      enabled: form.enabled,
      plan_id: form.plan_id || null,
      tags: parseTags(tags),
      // Optional 9.9 fields are only sent when set (or when clearing a stored value), so a
      // backend without them never sees them.
      ...(analysisUrl.trim()
        ? { analysis_stream_url: analysisUrl.trim() }
        : camera?.analysis_stream_url
          ? { analysis_stream_url: null }
          : {}),
      ...(motion.enabled || camera?.motion
        ? { motion: { ...motion, heartbeat_s: Number(motion.heartbeat_s) } }
        : {}),
    }
    const errs = validateCamera(
      payload,
      allCameras.map((c) => c.camera_id),
      editing,
    )
    setErrors(errs)
    setServerNote(undefined)
    if (Object.keys(errs).length) return
    save.mutate(
      { id: camera?.camera_id, camera: payload },
      {
        onError: (err) => {
          if (!(err instanceof ApiError) || (err.status !== 400 && err.status !== 422)) return
          const { errors: fieldErrors, rest } = mapServerErrors(err.body)
          setErrors(fieldErrors)
          setServerNote(
            rest ?? (Object.keys(fieldErrors).length ? 'Fix the marked fields.' : undefined),
          )
        },
        onSuccess: () => {
          toast.success(editing ? 'Camera updated' : 'Camera added', payload.name)
          onDone()
        },
      },
    )
  }

  return (
    <form onSubmit={submit} className="space-y-3" noValidate>
      <Field
        label="Camera id"
        error={errors.camera_id}
        hint="3-40 chars: a-z, 0-9, - (regex ^[a-z0-9][a-z0-9-]{2,39}$). Also the stream path name; cannot be changed later."
      >
        <Input
          value={form.camera_id}
          disabled={editing}
          onChange={(e) => set('camera_id', e.target.value)}
          aria-invalid={!!errors.camera_id}
          placeholder="cam-021"
        />
      </Field>
      <Field label="Name" error={errors.name}>
        <Input
          value={form.name}
          onChange={(e) => set('name', e.target.value)}
          aria-invalid={!!errors.name}
          placeholder="Gate 3"
        />
      </Field>
      <Field
        label="Stream URL (camera)"
        error={errors.stream_url}
        hint={
          'rtsp / rtsps / rtmp / srt, or http(s) .m3u8. Use "publisher" if the stream is pushed into MediaMTX.'
        }
      >
        <Input
          value={form.stream_url}
          onChange={(e) => set('stream_url', e.target.value)}
          aria-invalid={!!errors.stream_url}
          placeholder="rtsp://user:pass@192.168.1.20:554/stream1"
        />
      </Field>
      <Field
        label="Analysis stream (substream) URL (optional)"
        error={errors.analysis_stream_url}
        hint="Use the camera's low-resolution stream for analysis — much cheaper to decode. The main stream is only used for live viewing."
      >
        <Input
          value={analysisUrl}
          onChange={(e) => setAnalysisUrl(e.target.value)}
          aria-invalid={!!errors.analysis_stream_url}
          placeholder="rtsp://user:pass@192.168.1.20:554/stream2"
        />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Analysis fps" error={errors.analysis_fps}>
          <Input
            type="number"
            step="0.5"
            min="0.1"
            max="30"
            value={form.analysis_fps}
            onChange={(e) => set('analysis_fps', e.target.valueAsNumber)}
            aria-invalid={!!errors.analysis_fps}
          />
        </Field>
        <Field label="Plan">
          <Dropdown
            ariaLabel="Plan"
            className="w-full"
            value={form.plan_id ?? ''}
            onChange={(v) => set('plan_id', v || null)}
            options={[
              { value: '', label: 'Default (detect_objects)' },
              ...plans.map((p) => ({ value: p.plan_id, label: p.name })),
            ]}
          />
        </Field>
      </div>
      <Field label="Tags (comma separated)" error={errors.tags}>
        <Input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="gate, yard" />
      </Field>
      <fieldset className="space-y-3 rounded-lg border p-3">
        <legend className="px-1 text-xs font-medium text-muted-foreground">Motion detection</legend>
        <label className="flex items-start gap-2 text-sm">
          <Switch
            checked={motion.enabled}
            onCheckedChange={(v) => setMotion((m) => ({ ...m, enabled: v }))}
            aria-label="Enable motion detection"
            className="mt-0.5"
          />
          <span>
            Skip unchanged frames
            <span className="block text-[11px] text-muted-foreground">
              Frames where nothing moves are not decoded further or sent for analysis.
            </span>
          </span>
        </label>
        <fieldset disabled={!motion.enabled} className="space-y-3 border-0 p-0 disabled:opacity-50">
          <div className="space-y-1">
            <Label>Sensitivity</Label>
            <div>
              <Segmented
                value={motion.sensitivity}
                onChange={(v) => setMotion((m) => ({ ...m, sensitivity: v }))}
                options={SENSITIVITIES.map(({ value, label }) => ({ value, label }))}
              />
            </div>
            {errors.motion_sensitivity ? (
              <p className="text-xs text-destructive">{errors.motion_sensitivity}</p>
            ) : (
              <p className="text-[11px] text-muted-foreground">
                {SENSITIVITIES.find((o) => o.value === motion.sensitivity)?.hint}
              </p>
            )}
          </div>
          <Field
            label="Heartbeat (seconds)"
            error={errors.motion_heartbeat_s}
            hint="A frame is still sent at least this often when nothing moves. 1-60, default 5."
          >
            <Input
              type="number"
              step="1"
              min="1"
              max="60"
              className="block w-28"
              value={Number.isNaN(motion.heartbeat_s) ? '' : motion.heartbeat_s}
              onChange={(e) => setMotion((m) => ({ ...m, heartbeat_s: e.target.valueAsNumber }))}
              aria-invalid={!!errors.motion_heartbeat_s}
            />
          </Field>
        </fieldset>
      </fieldset>
      <label className="flex items-center gap-2 text-sm">
        <Switch
          checked={form.enabled}
          onCheckedChange={(v) => set('enabled', v)}
          aria-label="Enabled"
        />{' '}
        Enabled
      </label>
      {duplicates.length > 0 && (
        <p
          role="status"
          className="rounded-md border border-warning/40 bg-warning/10 p-2 text-xs text-warning"
        >
          This stream URL is already used by {duplicates.join(', ')}. You can still save, but both
          cameras will pull the same stream.
        </p>
      )}
      {save.isError && (
        <p role="alert" className="text-sm text-destructive">
          {serverNote ?? String(save.error)}
        </p>
      )}
      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={save.isPending}>
          {save.isPending && <Loader2 className="animate-spin" />} {editing ? 'Save' : 'Add camera'}
        </Button>
      </div>
    </form>
  )
}

function Field({
  label,
  error,
  hint,
  children,
}: {
  label: string
  error?: string
  hint?: string
  children: React.ReactNode
}) {
  const id = useId()
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      {isValidElement(children)
        ? cloneElement(children as ReactElement<{ id?: string }>, { id })
        : children}
      {error ? (
        <p className="text-xs text-destructive">{error}</p>
      ) : (
        hint && <p className="text-[11px] text-muted-foreground">{hint}</p>
      )}
    </div>
  )
}
