import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import './index.css'
import App from './app/App'
import { ThemeProvider } from './app/theme'
import { initRuntime } from './lib/api/runtime'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 5_000 } },
})

const root = createRoot(document.getElementById('root')!)
root.render(
  <div className="grid h-full place-items-center text-sm text-muted-foreground">
    Connecting to gateway…
  </div>,
)

void initRuntime().then(() => {
  root.render(
    <StrictMode>
      <ThemeProvider>
        <QueryClientProvider client={queryClient}>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </QueryClientProvider>
      </ThemeProvider>
    </StrictMode>,
  )
})
