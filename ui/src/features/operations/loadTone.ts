/** Utilisation bar colour: green below 85%, amber to 100%, red when full. */
export function utilTone(pct: number): string {
  return pct >= 100 ? 'bg-err' : pct >= 85 ? 'bg-warn' : 'bg-ok'
}

/** 9.8 weighted load: whole numbers plain, otherwise one decimal (idle cameras weigh 0.1) */
export const fmtLoad = (x: number): string => (Number.isInteger(x) ? String(x) : x.toFixed(1))
