import { lazy, Suspense, useEffect, useRef } from 'react'
import { NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Activity,
  Film,
  FlaskConical,
  LayoutGrid,
  Moon,
  ScanFace,
  Server,
  Sun,
  Users,
  Video,
  Workflow,
} from 'lucide-react'
import { Skeleton } from '@/components/ui/primitives'
import { Toaster } from '@/components/ui/toaster'
import { useHealthSeed } from '@/lib/api/queries'
import { useRuntime } from '@/lib/api/runtime'
import { useHubState } from '@/lib/realtime/hub'
import { seedHealth } from '@/lib/realtime/stores'
import { cn } from '@/lib/utils'
import { useTheme } from './theme'
import LiveWallPage from '@/features/live/LiveWallPage'

const FocusPage = lazy(() => import('@/features/live/FocusPage'))
const RecognitionsPage = lazy(() => import('@/features/recognitions/RecognitionsPage'))
const PeoplePage = lazy(() => import('@/features/people/PeoplePage'))
const PipelinesPage = lazy(() => import('@/features/pipelines/PipelinesPage'))
const SourcesPage = lazy(() => import('@/features/sources/SourcesPage'))
const HealthPage = lazy(() => import('@/features/health/HealthPage'))
const FramesPage = lazy(() => import('@/features/frames/FramesPage'))
const OperationsPage = lazy(() => import('@/features/operations/OperationsPage'))

const NAV = [
  { to: '/', label: 'Live wall', icon: LayoutGrid, end: true },
  { to: '/frames', label: 'Extracted frames', icon: Film },
  { to: '/recognitions', label: 'Recognitions', icon: ScanFace },
  { to: '/people', label: 'People', icon: Users },
  { to: '/pipelines', label: 'Pipelines', icon: Workflow },
  { to: '/sources', label: 'Sources', icon: Video },
  { to: '/operations', label: 'Operations', icon: Server },
  { to: '/health', label: 'Health', icon: Activity },
]

const HUB_LABEL: Record<string, string> = {
  connected: 'Live',
  connecting: 'Connecting',
  reconnecting: 'Reconnecting',
  disconnected: 'Offline',
  idle: 'Idle',
}

function RealtimeStatus() {
  const state = useHubState()
  const ok = state === 'connected'
  return (
    <div
      className="flex items-center gap-2 text-xs text-muted-foreground"
      title={`Realtime hub: ${state}`}
    >
      <span className={cn('size-2 rounded-full', ok ? 'live-dot bg-ok' : 'bg-warn')} aria-hidden />
      <span className="hidden lg:inline">Realtime: {HUB_LABEL[state] ?? state}</span>
      <span className="sr-only lg:hidden">Realtime {HUB_LABEL[state] ?? state}</span>
    </div>
  )
}

export default function App() {
  const { mode, reason } = useRuntime()
  const { theme, toggle } = useTheme()
  const seed = useHealthSeed()
  const qc = useQueryClient()
  const prevMode = useRef(mode)
  const { pathname } = useLocation()
  const wall = pathname === '/'
  useEffect(() => {
    // backend swapped (mock -> live): drop every cached query
    if (prevMode.current !== mode) {
      prevMode.current = mode
      void qc.resetQueries()
    }
  }, [mode, qc])
  useEffect(() => {
    if (seed.data) seedHealth(seed.data)
  }, [seed.data])

  return (
    <div className="flex h-full">
      <aside className="flex w-14 shrink-0 flex-col border-r border-sidebar-border bg-sidebar min-[1100px]:w-[232px]">
        <div className="flex h-14 items-center gap-2.5 px-3.5">
          <span className="grid size-7 place-items-center rounded-lg bg-primary text-primary-foreground">
            <Video className="size-4" />
          </span>
          <span className="hidden text-sm font-semibold tracking-tight min-[1100px]:inline">
            Vision Console
          </span>
        </div>
        <nav className="flex flex-col gap-0.5 p-2" aria-label="Main">
          {NAV.map(({ to, label, icon: Icon, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              title={label}
              className={({ isActive }) =>
                cn(
                  'relative flex h-9 items-center gap-3 rounded-lg px-2.5 text-[13px] text-muted-foreground transition-colors duration-[120ms] hover:bg-muted hover:text-foreground',
                  isActive && 'bg-accent font-medium text-accent-foreground hover:bg-accent',
                )
              }
            >
              {({ isActive }) => (
                <>
                  {isActive && (
                    <span className="absolute top-1.5 bottom-1.5 left-0 w-0.5 rounded-full bg-primary" />
                  )}
                  <Icon className="size-4 shrink-0" strokeWidth={1.75} aria-hidden />
                  <span className="hidden min-[1100px]:inline">{label}</span>
                </>
              )}
            </NavLink>
          ))}
        </nav>
        <div className="mt-auto flex flex-col gap-2 border-t border-sidebar-border p-3">
          {mode === 'mock' && (
            <span
              className="inline-flex items-center gap-1.5 rounded-full border border-warn/30 bg-warn-bg px-2 py-0.5 text-xs text-warn"
              title="Simulated data"
            >
              <FlaskConical className="size-3" aria-hidden />
              <span className="hidden min-[1100px]:inline">Mock data</span>
            </span>
          )}
          <div className="flex items-center justify-between">
            <RealtimeStatus />
            <button
              onClick={toggle}
              aria-label="Toggle theme"
              className="rounded-md p-1.5 text-muted-foreground hover:bg-muted hover:text-foreground"
            >
              {theme === 'dark' ? <Sun className="size-4" /> : <Moon className="size-4" />}
            </button>
          </div>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        {mode === 'mock' && (
          <div
            role="status"
            className="flex h-7 shrink-0 items-center gap-2 border-b border-warn/30 bg-warn-bg px-4 text-xs text-warn"
          >
            <FlaskConical className="size-3.5" aria-hidden />
            <b>Mock mode</b>
            {reason === 'unreachable'
              ? 'API gateway unreachable: showing simulated data, retrying every 10 s.'
              : 'Simulated data (VITE_MOCK=1).'}
          </div>
        )}
        <main className={cn('min-h-0 flex-1 overflow-auto', wall ? 'p-3' : 'p-4 min-[1024px]:p-6')}>
          <div
            key={pathname}
            className={cn('page-in flex flex-col', wall ? 'h-full' : 'min-h-full')}
          >
            <Suspense fallback={<Skeleton className="h-64 w-full" />}>
              <Routes>
                <Route path="/" element={<LiveWallPage />} />
                <Route path="/live/:cameraId" element={<FocusPage />} />
                <Route path="/recognitions" element={<RecognitionsPage />} />
                <Route path="/people" element={<PeoplePage />} />
                <Route path="/pipelines" element={<PipelinesPage />} />
                <Route path="/sources" element={<SourcesPage />} />
                <Route path="/health" element={<HealthPage />} />
                <Route path="/frames" element={<FramesPage />} />
                <Route path="/operations" element={<OperationsPage />} />
                <Route path="*" element={<LiveWallPage />} />
              </Routes>
            </Suspense>
          </div>
        </main>
      </div>
      <Toaster />
    </div>
  )
}
