import type { Recognition } from '../types'

/** SVG data-URI stand-in for a MinIO snapshot (mock mode only). */
export function mockSnapshot(r: Pick<Recognition, 'type' | 'value' | 'camera_id'>): string {
  const body =
    r.type === 'plate'
      ? `<rect x="90" y="150" width="220" height="60" rx="6" fill="#fde047" stroke="#111" stroke-width="3"/><text x="200" y="193" font-family="monospace" font-size="38" font-weight="700" text-anchor="middle" fill="#111">${esc(r.value)}</text>`
      : `<circle cx="200" cy="130" r="48" fill="#475569"/><rect x="140" y="185" width="120" height="70" rx="30" fill="#475569"/><text x="200" y="280" font-family="sans-serif" font-size="22" text-anchor="middle" fill="#e2e8f0">${esc(r.value)}</text>`
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300" viewBox="0 0 400 300"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0f172a"/><stop offset="1" stop-color="#1e293b"/></linearGradient></defs><rect width="400" height="300" fill="url(#g)"/>${body}<text x="10" y="20" font-family="monospace" font-size="13" fill="#94a3b8">${esc(r.camera_id)}</text></svg>`
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`
}
const esc = (s: string) =>
  s.replace(/[<>&"]/g, (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' })[c]!)

/** Face-thumbnail stand-in for GET /api/persons/{id}/photo (mock mode only). */
export function mockAvatar(name: string): string {
  const hue = [...name].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 7)
  const initials = name
    .split(/\s+/)
    .map((w) => w[0] ?? '')
    .join('')
    .slice(0, 2)
    .toUpperCase()
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="240" height="240" viewBox="0 0 240 240"><rect width="240" height="240" fill="hsl(${hue} 35% 28%)"/><circle cx="120" cy="96" r="46" fill="hsl(${hue} 30% 62%)"/><rect x="50" y="150" width="140" height="110" rx="55" fill="hsl(${hue} 30% 62%)"/><text x="120" y="112" font-family="sans-serif" font-size="40" font-weight="700" text-anchor="middle" fill="hsl(${hue} 35% 20%)">${esc(initials)}</text></svg>`
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`
}
