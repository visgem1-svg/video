import { describe, expect, it } from 'vitest'
import { findDuplicates, normalizeStreamUrl } from './url'
import { createMockBackend } from './mock/mockBackend'
import { MockWorld } from './mock/world'

describe('normalizeStreamUrl (9.4)', () => {
  it('lowercases scheme/host, drops default port, credentials and trailing slash', () => {
    const a = normalizeStreamUrl('rtsp://10.0.0.5:554/stream1')
    expect(a).toBe('rtsp://10.0.0.5/stream1')
    expect(normalizeStreamUrl('RTSP://admin:pw@10.0.0.5/stream1/')).toBe(a)
    expect(normalizeStreamUrl('rtsp://10.0.0.5:8554/stream1')).not.toBe(a)
    expect(normalizeStreamUrl('HTTPS://Cam.Example.com:443/live/index.m3u8')).toBe(
      'https://cam.example.com/live/index.m3u8',
    )
  })
  it('never treats publisher as a duplicate', () => {
    expect(normalizeStreamUrl('publisher')).toBeNull()
    const cams = [
      { camera_id: 'cam-001', stream_url: 'publisher' },
      { camera_id: 'cam-002', stream_url: 'publisher' },
    ]
    expect(findDuplicates('publisher', cams, 'cam-001')).toEqual([])
  })
  it('finds duplicates excluding self', () => {
    const cams = [
      { camera_id: 'cam-aaa', stream_url: 'rtsp://h:554/a' },
      { camera_id: 'cam-bbb', stream_url: 'RTSP://h/a/' },
      { camera_id: 'cam-ccc', stream_url: 'rtsp://h/other' },
    ]
    expect(findDuplicates('rtsp://h/a', cams, 'cam-aaa')).toEqual(['cam-bbb'])
  })
})

describe('mock people, duplicates and draft cache', () => {
  const world = new MockWorld({ seed: 3, now: () => 1_700_000_000_000 })
  const api = createMockBackend(world)

  it('flags the two seeded duplicate cameras', async () => {
    const cams = await api.listCameras()
    expect(cams.find((c) => c.camera_id === 'cam-016')?.duplicate_of).toEqual(['cam-017'])
    expect(cams.find((c) => c.camera_id === 'cam-017')?.duplicate_of).toEqual(['cam-016'])
    expect(cams.find((c) => c.camera_id === 'cam-001')?.duplicate_of).toEqual([])
  })

  it('enrolls a person, surfaces the server 400 for 0/2+ faces, renames and deletes', async () => {
    const before = (await api.listPersons()).length
    const file = (n: string) => new File(['x'], n, { type: 'image/jpeg' })
    await expect(api.enrollPerson('X', file('group.jpg'))).rejects.toThrow(/found 2/)
    await expect(api.enrollPerson('X', file('noface.jpg'))).rejects.toThrow(/no face/)
    const p = await api.enrollPerson('Rina Adler', file('rina.jpg'))
    expect((await api.listPersons()).length).toBe(before + 1)
    expect((await api.renamePerson(p.person_id, 'Rina A.')).name).toBe('Rina A.')
    expect((await api.addPersonPhoto(p.person_id, file('b.jpg'))).photos).toBe(2)
    await api.deletePerson(p.person_id)
    expect((await api.listPersons()).length).toBe(before)
  })

  it('filters recognitions by person_id', async () => {
    const all = await api.listRecognitions({ type: 'face', limit: 100 })
    const withPerson = all.find((r) => r.person_id)
    if (withPerson) {
      const only = await api.listRecognitions({ person_id: withPerson.person_id!, limit: 100 })
      expect(only.length).toBeGreaterThan(0)
      expect(only.every((r) => r.person_id === withPerson.person_id)).toBe(true)
    }
  })

  it(
    'draft cache: second identical query is cached, refresh bypasses',
    { timeout: 20000 },
    async () => {
      const req = { query: 'Find red buses!', camera_ids: ['cam-001'] }
      const first = await api.draftPlan(req)
      expect(first.cached).toBe(false)
      const second = await api.draftPlan({ ...req, query: '  find   RED buses ' })
      expect(second.cached).toBe(true)
      expect(second.cache_hits).toBeGreaterThanOrEqual(1)
      expect((await api.recentDrafts(10))[0]).toBe('find red buses')
      const refreshed = await api.draftPlan(req, undefined, { refresh: true })
      expect(refreshed.cached).toBe(false)
    },
  )
})
