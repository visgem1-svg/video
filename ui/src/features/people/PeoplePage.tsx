import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Camera,
  ImagePlus,
  Upload,
  Loader2,
  Pencil,
  Search,
  Trash2,
  UserPlus,
  Users,
} from 'lucide-react'
import { Dialog, DialogClose, DialogContent } from '@/components/ui/dialog'
import {
  Badge,
  Button,
  EmptyState,
  Input,
  Label,
  PageHeader,
  Skeleton,
} from '@/components/ui/primitives'
import {
  useAddPersonPhoto,
  useCameras,
  useDeletePerson,
  useEnrollPerson,
  usePersons,
  useRecognitionHistory,
  useRenamePerson,
} from '@/lib/api/queries'
import { toast } from '@/lib/toast'
import type { Person } from '@/lib/types'
import { formatAge, formatDateTime } from '@/lib/utils'
import { RecognitionCard } from '@/features/recognitions/RecognitionsPage'

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e))

/** Image picker with preview. */
function ImagePicker({
  file,
  onChange,
}: {
  file: File | null
  onChange: (f: File | null) => void
}) {
  const [preview, setPreview] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (!file) return setPreview(null)
    const url = URL.createObjectURL(file)
    setPreview(url)
    return () => URL.revokeObjectURL(url)
  }, [file])
  return (
    <div className="space-y-2">
      <input
        ref={input}
        type="file"
        accept="image/jpeg,image/png,image/webp"
        className="sr-only"
        aria-label="Photo"
        onChange={(e) => onChange(e.target.files?.[0] ?? null)}
      />
      <button
        type="button"
        onClick={() => input.current?.click()}
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.preventDefault()
          const f = e.dataTransfer.files?.[0]
          if (f && f.type.startsWith('image/')) onChange(f)
        }}
        className="grid aspect-[4/3] w-full place-items-center overflow-hidden rounded-md border border-dashed text-sm text-muted-foreground hover:bg-accent/50"
      >
        {preview ? (
          <img src={preview} alt="Preview" className="size-full object-contain" />
        ) : (
          <span className="flex flex-col items-center gap-1">
            <Upload className="size-6 text-faint" aria-hidden />
            Drop a clear, front-facing photo or click to browse
            <span className="text-xs text-faint">JPEG, PNG or WebP · exactly one face</span>
          </span>
        )}
      </button>
      {file && <div className="truncate text-xs text-muted-foreground">{file.name}</div>}
    </div>
  )
}

function EnrollDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {open && (
        <DialogContent
          title="Enroll a person"
          description="The photo must contain exactly one face."
        >
          <EnrollForm onDone={() => onOpenChange(false)} />
        </DialogContent>
      )}
    </Dialog>
  )
}
function EnrollForm({ onDone }: { onDone: () => void }) {
  const [name, setName] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const enroll = useEnrollPerson()
  return (
    <form
      className="space-y-3"
      onSubmit={(e) => {
        e.preventDefault()
        if (name.trim() && file)
          enroll.mutate(
            { name: name.trim(), image: file },
            {
              onSuccess: () => {
                toast.success(`${name.trim()} enrolled`)
                onDone()
              },
            },
          )
      }}
    >
      <div className="space-y-1">
        <Label>Name</Label>
        <Input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Dana Levi"
          maxLength={80}
        />
      </div>
      <ImagePicker file={file} onChange={setFile} />
      {enroll.isError && (
        <p
          role="alert"
          className="rounded-md border border-destructive/40 bg-destructive/10 p-2 text-sm"
        >
          {errText(enroll.error)}
        </p>
      )}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={!name.trim() || !file || enroll.isPending}>
          {enroll.isPending && <Loader2 className="animate-spin" />} Enroll
        </Button>
      </div>
    </form>
  )
}

function LastSeen({ personId }: { personId: string }) {
  const { data } = useRecognitionHistory({ person_id: personId, limit: 1 })
  const { data: cameras } = useCameras()
  const last = data?.[0]
  if (!last) return <span className="text-muted-foreground">Not seen yet</span>
  const cam = cameras?.find((c) => c.camera_id === last.camera_id)?.name ?? last.camera_id
  return (
    <span title={formatDateTime(last.ts)}>
      {formatAge(Date.now() - last.ts)} ago · {cam}
    </span>
  )
}

function PersonCard({ person, onOpen }: { person: Person; onOpen: () => void }) {
  return (
    <button
      onClick={onOpen}
      className="overflow-hidden rounded-lg border bg-card text-left transition-shadow hover:ring-2 hover:ring-primary/50"
    >
      <div className="aspect-square bg-muted">
        <img
          src={person.photo_url}
          alt={person.name}
          loading="lazy"
          className="size-full object-cover"
        />
      </div>
      <div className="space-y-0.5 p-3">
        <div className="truncate font-semibold">{person.name}</div>
        <div className="text-xs text-muted-foreground">
          {person.photos} photo{person.photos === 1 ? '' : 's'} · enrolled{' '}
          {new Date(person.created_at).toLocaleDateString()}
        </div>
        <div className="truncate text-xs">
          <LastSeen personId={person.person_id} />
        </div>
      </div>
    </button>
  )
}

function PersonDrawer({ person, onClose }: { person: Person; onClose: () => void }) {
  const [name, setName] = useState(person.name)
  const [confirm, setConfirm] = useState(false)
  const rename = useRenamePerson()
  const addPhoto = useAddPersonPhoto()
  const del = useDeletePerson()
  const { data: sightings, isLoading } = useRecognitionHistory({
    person_id: person.person_id,
    limit: 12,
  })
  const { data: cameras } = useCameras()
  const names = useMemo(() => new Map((cameras ?? []).map((c) => [c.camera_id, c.name])), [cameras])
  const fileInput = useRef<HTMLInputElement>(null)

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title={person.name}
        description={`${person.photos} photo(s) · enrolled ${formatDateTime(person.created_at)}`}
        className="top-0 right-0 left-auto h-full max-h-none w-[min(520px,100vw)] translate-x-0 translate-y-0 rounded-none"
      >
        <div className="space-y-5">
          <div className="flex gap-4">
            <img
              src={person.photo_url}
              alt={person.name}
              className="size-28 rounded-lg border object-cover"
            />
            <div className="min-w-0 flex-1 space-y-2">
              <Label>Name</Label>
              <div className="flex gap-2">
                <Input
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  maxLength={80}
                  aria-label="Person name"
                />
                <Button
                  variant="outline"
                  disabled={!name.trim() || name.trim() === person.name || rename.isPending}
                  onClick={() => rename.mutate({ id: person.person_id, name: name.trim() })}
                >
                  <Pencil /> Save
                </Button>
              </div>
              {rename.isError && (
                <p className="text-xs text-destructive">{errText(rename.error)}</p>
              )}
              <div className="flex flex-wrap gap-2">
                <input
                  ref={fileInput}
                  type="file"
                  accept="image/jpeg,image/png,image/webp"
                  className="sr-only"
                  aria-label="Add photo file"
                  onChange={(e) => {
                    const f = e.target.files?.[0]
                    e.target.value = ''
                    if (f) addPhoto.mutate({ id: person.person_id, image: f })
                  }}
                />
                <Button
                  variant="outline"
                  size="sm"
                  disabled={addPhoto.isPending}
                  onClick={() => fileInput.current?.click()}
                >
                  {addPhoto.isPending ? <Loader2 className="animate-spin" /> : <ImagePlus />} Add
                  photo
                </Button>
                <Button variant="outline" size="sm" onClick={() => setConfirm(true)}>
                  <Trash2 className="text-destructive" /> Delete
                </Button>
              </div>
              {addPhoto.isError && (
                <p role="alert" className="text-xs text-destructive">
                  {errText(addPhoto.error)}
                </p>
              )}
            </div>
          </div>

          <div>
            <h3 className="mb-2 flex items-center gap-2 text-sm font-semibold">
              <Camera className="size-4" /> Recent sightings
            </h3>
            {isLoading ? (
              <Skeleton className="h-40" />
            ) : !sightings?.length ? (
              <EmptyState title="No sightings yet" className="py-6" />
            ) : (
              <div className="grid grid-cols-2 gap-3">
                {sightings.map((r) => (
                  <RecognitionCard
                    key={r.recognition_id}
                    rec={r}
                    cameraName={names.get(r.camera_id)}
                  />
                ))}
              </div>
            )}
          </div>
        </div>

        <Dialog open={confirm} onOpenChange={setConfirm}>
          <DialogContent
            title={`Delete ${person.name}?`}
            description="The person and all their photos are removed from the face gallery."
          >
            <div className="flex justify-end gap-2">
              <DialogClose asChild>
                <Button variant="ghost">Cancel</Button>
              </DialogClose>
              <Button
                variant="destructive"
                disabled={del.isPending}
                onClick={() =>
                  del.mutate(person.person_id, {
                    onSuccess: () => {
                      toast.success(`${person.name} deleted`)
                      onClose()
                    },
                  })
                }
              >
                Delete
              </Button>
            </div>
          </DialogContent>
        </Dialog>
      </DialogContent>
    </Dialog>
  )
}

export default function PeoplePage() {
  const { data: persons, isLoading, error } = usePersons()
  const [q, setQ] = useState('')
  const [enrollOpen, setEnrollOpen] = useState(false)
  const [openId, setOpenId] = useState<string | null>(null)
  const filtered = useMemo(() => {
    const n = q.trim().toLowerCase()
    return (persons ?? []).filter((p) => !n || p.name.toLowerCase().includes(n))
  }, [persons, q])
  const open = persons?.find((p) => p.person_id === openId)

  return (
    <div>
      <PageHeader
        title="People"
        description="Known faces used by identify_faces."
        actions={
          <>
            <div className="relative">
              <Search className="absolute top-2.5 left-2.5 size-4 text-muted-foreground" />
              <Input
                className="w-52 pl-8"
                placeholder="Search name"
                value={q}
                onChange={(e) => setQ(e.target.value)}
                aria-label="Search people"
              />
            </div>
            <Badge>{persons?.length ?? 0} people</Badge>
            <Button onClick={() => setEnrollOpen(true)}>
              <UserPlus /> Enroll person
            </Button>
          </>
        }
      />
      {error && (
        <div className="mb-3 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm">
          Could not load people: {errText(error)}
        </div>
      )}
      {isLoading ? (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-3">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="aspect-[3/4]" />
          ))}
        </div>
      ) : filtered.length === 0 ? (
        <EmptyState
          icon={<Users className="size-8" />}
          title="No people enrolled"
          hint="Enroll a person with a clear photo of their face."
        />
      ) : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-3">
          {filtered.map((p) => (
            <PersonCard key={p.person_id} person={p} onOpen={() => setOpenId(p.person_id)} />
          ))}
        </div>
      )}
      <EnrollDialog open={enrollOpen} onOpenChange={setEnrollOpen} />
      {open && <PersonDrawer key={open.person_id} person={open} onClose={() => setOpenId(null)} />}
    </div>
  )
}
