import { beforeEach, describe, expect, test } from 'bun:test'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { AwayResponder, type AwayDeps } from '../src/daemon/away'
import { AWAY_MAX_PER_HOUR, AWAY_MAX_PER_WINDOW } from '../src/shared/away'
import { parseConfig, type Config } from '../src/shared/config'
import type { IncomingMessage, PeerInfo, SentRecord } from '../src/shared/protocol'
import { ANNA, FakeTelegram, LEO, TEAM_QA } from './fake-telegram'

const BOT: PeerInfo = { id: 77, type: 'bot', name: 'Helper Bot', username: 'helper_bot' }
const TELEGRAM: PeerInfo = { id: 777000, type: 'user', name: 'Telegram', username: null }
const SUPPORT: PeerInfo = { id: 4242, type: 'user', name: 'Volunteer Support', username: null, isSupport: true }
const at = (day: number, h: number, m = 0) => new Date(2026, 10, day, h, m)

function cfg(
  over: {
    away?: object
    blocked?: number[]
    autoReply?: number[]
    requireApproval?: number[]
    autoSend?: number[]
    defaultPolicy?: 'approve' | 'block'
  } = {},
): Config {
  return parseConfig(
    JSON.stringify({
      footer: '\n\nF',
      defaultPolicy: over.defaultPolicy ?? 'approve',
      away: { enabled: true, from: '18:00', until: '09:00', message: '{name} is out 🔥', quietMinutes: 30, ...over.away },
      rules: {
        blocked: over.blocked ?? [],
        autoReply: over.autoReply ?? [],
        requireApproval: over.requireApproval ?? [],
        autoSend: over.autoSend ?? [],
      },
    }),
  )
}

/** A person nobody has saved: a stranger writing in. */
const stranger = (n: number): PeerInfo => ({ id: 5000 + n, type: 'user', name: `Stranger ${n}`, username: null })

let tg: FakeTelegram
let config: Config
let clock: number
let records: Omit<SentRecord, 'at'>[]
let hub: { handledLive: (id: number) => boolean; takeoverOf: (id: number) => string | null }
let pages: { senderKey: string }[]
let logs: string[]
/** Waits started by the responder, fired by hand. */
let timers: { fn: () => Promise<unknown>; ms: number; cancelled: boolean }[]

const make = (extra: Partial<AwayDeps> = {}) =>
  new AwayResponder({
    config: () => config,
    tg,
    hub,
    pages: () => pages,
    record: r => records.push(r),
    now: () => clock,
    log: m => logs.push(m),
    setTimer: (fn, ms) => {
      const t = { fn, ms, cancelled: false }
      timers.push(t)
      return { cancel: () => void (t.cancelled = true) }
    },
    ...extra,
  })

/** An incoming message dated `when` (defaults to the clock). */
function msg(chat: PeerInfo, when = new Date(clock), extra: Partial<IncomingMessage> = {}): IncomingMessage {
  return {
    id: Math.floor(Math.random() * 1e6),
    chatId: chat.id,
    chat,
    date: when.toISOString(),
    from: chat,
    outgoing: false,
    text: 'hi',
    media: null,
    replyToId: null,
    topicId: null,
    ...extra,
  }
}

beforeEach(() => {
  tg = new FakeTelegram().add(LEO, ANNA, TEAM_QA, BOT)
  config = cfg()
  clock = at(3, 20).getTime()
  records = []
  hub = { handledLive: () => false, takeoverOf: () => null }
  pages = []
  logs = []
  timers = []
})

/** One schedule, as the tray saves it, with a wait. */
const waiting = (over: object = {}) =>
  cfg({
    away: {
      schedules: [
        { id: 'day', name: 'Daytime', from: '08:00', until: '21:00', message: 'back soon', delaySeconds: 30, repeatMinutes: 60, ...over },
      ],
    },
  })

describe('AwayResponder', () => {
  test('replies once to a private message in the window', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    expect(tg.sent).toEqual([{ chatId: ANNA.id, text: 'Me is out 🔥\n\nF', opts: {} }])
    expect(records).toHaveLength(1)
    expect(records[0]).toMatchObject({ via: 'away', chatId: ANNA.id, sessionLabel: null, text: 'Me is out 🔥\n\nF' })
    expect(logs).toContain(`away reply (Away) to ${ANNA.id}`)
    clock = at(3, 21).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    expect(tg.sent).toHaveLength(1)
  })

  test('two quick messages get one reply', async () => {
    const r = make()
    const a = r.onIncoming(msg(ANNA), false)
    const b = r.onIncoming(msg(ANNA), false)
    await Promise.all([a, b])
    expect(tg.sent).toHaveLength(1)
  })

  const skipCases: [string, () => IncomingMessage][] = [
    ['outside the window', () => { clock = at(3, 17, 59).getTime(); return msg(ANNA) }],
    ['when disabled', () => { config = cfg({ away: { enabled: false } }); return msg(ANNA) }],
    ['to groups', () => msg(TEAM_QA, undefined, { from: ANNA })],
    ['to bots', () => msg(BOT)],
    ["to Telegram's service account (777000)", () => msg(TELEGRAM)],
    ['to Telegram support', () => msg(SUPPORT)],
    ['to self', () => msg({ ...ANNA, isSelf: true })],
    ['to outgoing messages', () => msg(ANNA, undefined, { outgoing: true })],
    ['to a blocked chat', () => { config = cfg({ blocked: [ANNA.id] }); return msg(ANNA) }],
    ['to a chat on away.skip, even on Always allow', () => { config = cfg({ autoSend: [ANNA.id], away: { skip: [ANNA.id] } }); return msg(ANNA) }],
    ['to an unlisted chat when defaultPolicy is block', () => { config = cfg({ defaultPolicy: 'block' }); return msg(ANNA) }],
  ]
  for (const [name, build] of skipCases) {
    test(`no reply ${name}`, async () => {
      const m = build()
      expect(await make().onIncoming(m, false)).toBe('skipped')
      expect(tg.sent).toEqual([])
    })
  }

  test('away.skip leaves everyone else alone', async () => {
    config = cfg({ away: { skip: [ANNA.id] } })
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    expect(await r.onIncoming(msg(LEO), false)).toBe('sent')
    expect(tg.sent).toHaveLength(1)
  })

  test('an Ask every time chat gets one too: the reply is your own text, there is nothing to approve', async () => {
    config = cfg({ requireApproval: [ANNA.id] })
    expect(await make().onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('defaultPolicy block still lets an Always allow chat get a reply', async () => {
    config = cfg({ defaultPolicy: 'block', autoSend: [ANNA.id] })
    expect(await make().onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('no reply once the hours are over, though the message came in before', async () => {
    const sentAt = at(4, 8, 58)
    clock = at(4, 9, 5).getTime() // caught up after a sleep or a reconnect
    expect(await make().onIncoming(msg(ANNA, sentAt), false)).toBe('skipped')
    clock = at(4, 8, 59).getTime()
    expect(await make().onIncoming(msg(ANNA, sentAt), false)).toBe('sent')
  })

  test(`at most ${AWAY_MAX_PER_HOUR} replies an hour, logged once`, async () => {
    const r = make()
    for (let i = 0; i < AWAY_MAX_PER_HOUR; i++) expect(await r.onIncoming(msg(stranger(i)), false)).toBe('sent')
    expect(await r.onIncoming(msg(stranger(100)), false)).toBe('skipped')
    expect(await r.onIncoming(msg(stranger(101)), false)).toBe('skipped')
    expect(tg.sent).toHaveLength(AWAY_MAX_PER_HOUR)
    expect(logs.filter(l => l.includes('limit'))).toHaveLength(1)
    // An hour on, there's room again.
    clock = at(3, 21, 1).getTime()
    expect(await r.onIncoming(msg(stranger(100)), false)).toBe('sent')
  })

  test(`at most ${AWAY_MAX_PER_WINDOW} replies in one window, however they spread over the hours`, async () => {
    clock = at(3, 18, 0).getTime()
    const r = make()
    let n = 0
    // Twelve an hour stays under the hourly limit, but the night adds up.
    for (let hour = 0; n < AWAY_MAX_PER_WINDOW; hour++) {
      clock = at(3, 18 + hour, 0).getTime()
      for (let i = 0; i < 12 && n < AWAY_MAX_PER_WINDOW; i++, n++) {
        clock += 60_000
        expect(await r.onIncoming(msg(stranger(n)), false)).toBe('sent')
      }
    }
    clock += 60 * 60_000
    expect(await r.onIncoming(msg(stranger(500)), false)).toBe('skipped')
    expect(logs).toContain(`away: reached the limit of ${AWAY_MAX_PER_WINDOW} replies in this Away window; holding off`)
    // The next window starts afresh.
    clock = at(4, 18, 5).getTime()
    expect(await r.onIncoming(msg(stranger(501)), false)).toBe('sent')
  })

  test('the window limit survives a restart', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'away-cap-'))
    try {
      const statePath = join(dir, 'away.json')
      clock = at(3, 18, 0).getTime()
      const first = make({ statePath })
      for (let i = 0; i < AWAY_MAX_PER_WINDOW; i++) {
        clock = at(3, 18, 0).getTime() + Math.floor(i / 10) * 3_600_000 + (i % 10) * 60_000
        expect(await first.onIncoming(msg(stranger(i)), false)).toBe('sent')
      }
      clock += 2 * 3_600_000
      expect(await make({ statePath }).onIncoming(msg(stranger(100)), false)).toBe('skipped')
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  test('never two replies to a chat within 30 minutes, even when the schedule repeats sooner', async () => {
    config = cfg({ away: { schedules: [{ id: 'a', name: 'Away', from: '18:00', until: '09:00', message: 'out', repeatMinutes: 5 }] } })
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    clock += 10 * 60_000
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    clock += 21 * 60_000
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('the hourly limit survives a restart', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'away-cap-'))
    try {
      const statePath = join(dir, 'away.json')
      const first = make({ statePath })
      for (let i = 0; i < AWAY_MAX_PER_HOUR; i++) await first.onIncoming(msg(stranger(i)), false)
      expect(await make({ statePath }).onIncoming(msg(stranger(100)), false)).toBe('skipped')
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  test('no reply to a message older than 10 minutes', async () => {
    clock = at(3, 20, 15).getTime()
    const r = make()
    expect(await r.onIncoming(msg(ANNA, at(3, 20, 4)), false)).toBe('skipped')
    expect(await r.onIncoming(msg(ANNA, at(3, 20, 6)), false)).toBe('sent')
  })

  test('no reply when you spoke recently', async () => {
    const r = make()
    r.noteOutgoing(ANNA.id, at(3, 19, 50).getTime())
    clock = at(3, 20, 10).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    clock = at(3, 20, 21).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('quietMinutes 0 ignores recent outgoing', async () => {
    config = cfg({ away: { quietMinutes: 0 } })
    const r = make()
    r.noteOutgoing(ANNA.id, clock - 60_000)
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('no reply while a Claude session is live on the chat', async () => {
    hub.takeoverOf = () => 's1'
    expect(await make().onIncoming(msg(ANNA), false)).toBe('skipped')
    hub.takeoverOf = () => null
    hub.handledLive = () => true
    config = cfg({ autoReply: [ANNA.id] })
    expect(await make().onIncoming(msg(ANNA), false)).toBe('skipped')
    // A live watch on a chat that is not on autoReply is never dispatched to by main.ts.
    config = cfg()
    expect(await make().onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('no reply to a message that paged', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), true)).toBe('skipped')
    pages = [{ senderKey: String(ANNA.id) }]
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    expect(tg.sent).toEqual([])
  })

  test('the away reply itself does not count as you speaking', async () => {
    // A full day of quiet: if the reply at 20:00 counted as you writing, 19:00 tomorrow would be silenced.
    config = cfg({ away: { quietMinutes: 1440 } })
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    clock = at(4, 19).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('editing the hours mid-night does not reply again', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    config = cfg({ away: { from: '17:00' } })
    clock = at(3, 21).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    expect(tg.sent).toHaveLength(1)
  })

  test("yesterday's answered set does not block tonight", async () => {
    const r = make()
    await r.onIncoming(msg(ANNA), false)
    clock = at(4, 20).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    expect(tg.sent).toHaveLength(2)
  })

  test('answered set survives a restart', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'away-'))
    try {
      const statePath = join(dir, 'away.json')
      await make({ statePath }).onIncoming(msg(ANNA), false)
      clock = at(3, 23).getTime()
      expect(await make({ statePath }).onIncoming(msg(ANNA), false)).toBe('skipped')
      expect(tg.sent).toHaveLength(1)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  test('an away.json from an earlier build still blocks that night', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'away-'))
    try {
      const statePath = join(dir, 'away.json')
      writeFileSync(statePath, JSON.stringify({ window: '2026-11-03T18:00', chats: [ANNA.id] }))
      const r = make({ statePath })
      expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
      expect(await r.onIncoming(msg(LEO), false)).toBe('sent')
      // Saved back in the new shape, ANNA kept.
      const saved = JSON.parse(readFileSync(statePath, 'utf8'))
      expect(Object.keys(saved.replied).map(Number).sort()).toEqual([ANNA.id, LEO.id].sort())
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  test('an unreadable away.json is ignored', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'away-'))
    try {
      const statePath = join(dir, 'away.json')
      for (const junk of ['not json', 'null', '[]', '{"replied": 5}', '{"window": 3, "chats": "x"}', '{"window": "soon", "chats": [1]}']) {
        writeFileSync(statePath, junk)
        tg.sent = []
        expect(await make({ statePath }).onIncoming(msg(ANNA), false)).toBe('sent')
      }
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  test('old entries are dropped from away.json', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'away-'))
    try {
      const statePath = join(dir, 'away.json')
      await make({ statePath }).onIncoming(msg(ANNA), false)
      clock = at(6, 20).getTime()
      await make({ statePath }).onIncoming(msg(LEO), false)
      expect(Object.keys(JSON.parse(readFileSync(statePath, 'utf8')).replied)).toEqual([String(LEO.id)])
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  test('a failed send is not remembered', async () => {
    const real = tg.sendText.bind(tg)
    let fail = true
    tg.sendText = async (...a: Parameters<typeof real>) => {
      if (fail) {
        fail = false
        throw new Error('flood')
      }
      return real(...a)
    }
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    expect(records).toEqual([])
    expect(logs.some(l => l.startsWith('away:'))).toBe(true)
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
  })

  test('turning it off stops replies at once', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    config = cfg({ away: { enabled: false } })
    expect(await r.onIncoming(msg(LEO), false)).toBe('skipped')
  })

  test('ui() and summary()', () => {
    const r = make()
    const ui = r.ui()
    expect(ui).toMatchObject({
      enabled: true,
      from: '18:00',
      until: '09:00',
      active: true,
      quietMinutes: 30,
      preview: 'Me is out 🔥\n\nF',
      current: 'away',
      currentUntil: at(4, 9).toISOString(),
      waiting: 0,
    })
    expect(ui.schedules).toEqual([
      {
        id: 'away',
        name: 'Away',
        enabled: true,
        from: '18:00',
        until: '09:00',
        days: [0, 1, 2, 3, 4, 5, 6],
        startDate: null,
        endDate: null,
        delaySeconds: 0,
        repeatMinutes: 0,
        message: '{name} is out 🔥',
        active: true,
        preview: 'Me is out 🔥\n\nF',
      },
    ])
    expect(ui.nextChange).toBe(at(4, 9).toISOString())
    expect(r.summary()).toBe('on, Away now (until 09:00, replies at once)')
    tg.phase = 'logged_out'
    expect(r.ui().preview).toBe('{name} is out 🔥\n\nF')
  })
})

describe('waiting for you', () => {
  beforeEach(() => {
    config = waiting()
    clock = at(3, 12).getTime()
  })

  /** Moves the clock past the wait and fires it. */
  async function fire(i = 0) {
    clock += timers[i]!.ms
    return timers[i]!.fn()
  }

  test("replies after the wait when you don't show up", async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    expect(timers).toHaveLength(1)
    expect(timers[0]!.ms).toBe(30_000)
    expect(tg.sent).toEqual([])
    expect(r.ui().waiting).toBe(1)
    expect(await fire()).toBe('sent')
    expect(tg.sent).toEqual([{ chatId: ANNA.id, text: 'back soon\n\nF', opts: {} }])
    expect(r.ui().waiting).toBe(0)
  })

  test('no reply when you read a chat or write during the wait', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    expect(await r.onIncoming(msg(LEO), false)).toBe('waiting')
    clock += 10_000
    r.noteRead()
    expect(timers.map(t => t.cancelled)).toEqual([true, true])
    expect(logs).toContain(`away: you're back, no away reply to ${ANNA.id}`)
    expect(await fire(0)).toBe('skipped')
    expect(tg.sent).toEqual([])
    expect(r.ui().waiting).toBe(0)
  })

  test('being around before the message came in does not stop it', async () => {
    const r = make()
    r.noteRead(clock - 5_000)
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    expect(timers[0]!.cancelled).toBe(false)
    expect(await fire()).toBe('sent')
  })

  test('sending a message yourself drops the replies waiting for older messages', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    clock += 10_000
    expect(await r.onIncoming(msg(LEO), false)).toBe('waiting')
    // You typed something on your phone between the two.
    r.noteSent(clock - 5_000)
    expect(timers.map(t => t.cancelled)).toEqual([true, false])
    expect(await fire(1)).toBe('sent')
    expect(tg.sent.map(s => s.chatId)).toEqual([LEO.id])
  })

  test("Telegram's clock running ahead of this PC doesn't hide that you read", async () => {
    const r = make()
    // The message is dated by Telegram 20 s ahead of this PC.
    expect(await r.onIncoming(msg(ANNA, new Date(clock + 20_000)), false)).toBe('waiting')
    clock += 10_000
    r.noteRead()
    expect(timers[0]!.cancelled).toBe(true)
    expect(await fire()).toBe('skipped')
    expect(tg.sent).toEqual([])
  })

  test("Telegram's clock running behind this PC doesn't drop a reply for a message after your read", async () => {
    const r = make()
    r.noteRead()
    clock += 2_000
    // Arrives after the read, but dated by Telegram 20 s behind this PC.
    expect(await r.onIncoming(msg(ANNA, new Date(clock - 20_000)), false)).toBe('waiting')
    expect(timers[0]!.cancelled).toBe(false)
    expect(await fire()).toBe('sent')
  })

  test('more messages during the wait share one reply', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    clock += 5_000
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    expect(timers).toHaveLength(1)
    clock -= 5_000
    await fire()
    expect(tg.sent).toHaveLength(1)
  })

  test('the rules are checked again when the wait is over', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    config = cfg({ away: { enabled: false } })
    expect(await fire()).toBe('skipped')
    expect(tg.sent).toEqual([])
  })

  test('a wait that ends late (the laptop slept) sends nothing', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    // The timer only fires after the laptop wakes, 40 minutes on: the message is old news by then.
    clock += 40 * 60_000
    expect(await timers[0]!.fn()).toBe('skipped')
    expect(tg.sent).toEqual([])
  })

  test('a wait that ends a little late still replies', async () => {
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    clock += 5 * 60_000
    expect(await timers[0]!.fn()).toBe('sent')
  })

  test('a chat can get another reply after the repeat minutes', async () => {
    const r = make()
    await r.onIncoming(msg(ANNA), false)
    await fire(0)
    clock += 30 * 60_000
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    clock += 31 * 60_000
    expect(await r.onIncoming(msg(ANNA), false)).toBe('waiting')
    expect(await fire(1)).toBe('sent')
    expect(tg.sent).toHaveLength(2)
  })

  test('the hand-over to a new schedule gets its own reply, 30 minutes after the last', async () => {
    config = cfg({
      away: {
        schedules: [
          { id: 'day', name: 'Daytime', from: '08:00', until: '21:00', message: 'back soon' },
          { id: 'night', name: 'Night', from: '21:00', until: '08:00', message: 'tomorrow' },
        ],
      },
    })
    clock = at(3, 20, 50).getTime()
    const r = make()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    // Not right after the Daytime one: two replies a few minutes apart read like spam.
    clock = at(3, 21, 5).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('skipped')
    clock = at(3, 21, 25).getTime()
    expect(await r.onIncoming(msg(ANNA), false)).toBe('sent')
    expect(tg.sent.map(s => s.text)).toEqual(['back soon\n\nF', 'tomorrow\n\nF'])
  })

  test('sends through the given sender', async () => {
    const sends: [number, string][] = []
    const r = make({
      send: async (chatId, text) => {
        sends.push([chatId, text])
        return { id: 9, chatId, date: new Date(clock).toISOString(), from: null, outgoing: true, text, media: null, replyToId: null, topicId: null }
      },
    })
    await r.onIncoming(msg(ANNA), false)
    await fire()
    expect(sends).toEqual([[ANNA.id, 'back soon\n\nF']])
    expect(tg.sent).toEqual([])
    expect(records[0]).toMatchObject({ messageId: 9, via: 'away' })
  })

  test('stop() drops what is waiting', async () => {
    const r = make()
    await r.onIncoming(msg(ANNA), false)
    r.stop()
    expect(timers[0]!.cancelled).toBe(true)
    expect(r.ui().waiting).toBe(0)
  })
})
