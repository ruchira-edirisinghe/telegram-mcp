import { z } from 'zod'

/**
 * Away auto-reply: the schema, the schedule maths and the message rendering, shared by the
 * daemon, the CLI and the tray. Imports only zod: config.ts imports this file, and
 * policy.ts imports config.ts, so anything more would make a cycle.
 *
 * Away is a list of schedules. Each has its hours, the days of the week (and optionally the dates)
 * it runs on, its own message, and how long to wait for you before replying: a daytime schedule
 * can wait 30 seconds and say nothing if you read or answer meanwhile, a night one can reply at once.
 */

export const AWAY_DAY_MESSAGE =
  "Hi! Thanks for contacting me 🙏 I'm unavailable right now and will reply as soon as I'm back at my seat."
export const AWAY_NIGHT_MESSAGE =
  "Hi! Thanks for your message. I'm unavailable for the rest of the day and will get back to you when I'm back at work tomorrow 🌙"
/** Kept for the CLI's and older callers' sake: the night schedule's message. */
export const AWAY_DEFAULT_MESSAGE = AWAY_NIGHT_MESSAGE
/** The message an earlier build sent when none was set: what an older config without one keeps sending. */
export const AWAY_LEGACY_MESSAGE =
  "{name} is off the grid for the night and will get back to you tomorrow. If something's on fire, call them 🔥"
export const AWAY_MESSAGE_MAX = 1000
export const AWAY_NAME_MAX = 40
/** Only messages at most this old (past the schedule's wait) get a reply. */
export const AWAY_MAX_AGE_MS = 600_000
/**
 * At most this many chats get an away reply in any hour, whoever writes. A wave of DMs from strangers
 * would otherwise get a reply each from your own account, the pattern Telegram's anti-spam flags.
 */
export const AWAY_MAX_PER_HOUR = 20
/**
 * And at most this many in one window (a night, a day), so the hourly limit can't add up to hundreds
 * over a long night. An earlier build allowed 20 a night.
 */
export const AWAY_MAX_PER_WINDOW = 30
/** A chat never gets two away replies this close together, whatever the schedules: not at 20:59 and again at 21:01. */
export const AWAY_MIN_GAP_MINUTES = 30
/** The longest a schedule may wait for you before replying. */
export const AWAY_DELAY_MAX = 600
export const AWAY_REPEAT_MAX = 1440
export const ALL_DAYS = [0, 1, 2, 3, 4, 5, 6] as const

const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/
const DATE_RE = /^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/
const ID_RE = /^[a-z0-9][a-z0-9-]{0,39}$/

const timeField = (name: string) =>
  z.string({ error: `${name} must be a time like 18:00` }).regex(TIME_RE, `${name} must be a time like 18:00`)
const dateField = (name: string) =>
  z
    .string({ error: `${name} must be a date like 2026-12-24` })
    .regex(DATE_RE, `${name} must be a date like 2026-12-24`)
    .nullable()
    .default(null)
const wholeNumber = (name: string, max: number) => {
  const msg = `${name} must be a whole number from 0 to ${max}`
  return z.number({ error: msg }).int(msg).min(0, msg).max(max, msg)
}

const messageField = z
  .string({ error: "The message can't be empty." })
  .refine(m => m.trim() !== '', "The message can't be empty.")
  .refine(m => m.length <= AWAY_MESSAGE_MAX, `The message is too long (${AWAY_MESSAGE_MAX} characters at most).`)

export const AwayScheduleSchema = z
  .looseObject({
    id: z.string({ error: 'A schedule needs an id' }).regex(ID_RE, 'A schedule id is lowercase letters, digits and dashes'),
    name: z
      .string({ error: "A schedule's name can't be empty." })
      .trim()
      .min(1, "A schedule's name can't be empty.")
      .max(AWAY_NAME_MAX, `A schedule's name is ${AWAY_NAME_MAX} characters at most.`),
    enabled: z.boolean({ error: 'enabled must be true or false' }).default(true),
    from: timeField('from'),
    until: timeField('until'),
    /** Days of the week it starts on, 0 = Sunday … 6 = Saturday. An overnight window belongs to the day it starts. */
    days: z
      .array(z.number().int().min(0).max(6), { error: 'days must be a list of weekdays, 0 (Sunday) to 6 (Saturday)' })
      .min(1, 'Pick at least one day.')
      .default([...ALL_DAYS])
      .transform(d => [...new Set(d)].sort((a, b) => a - b)),
    /** First and last date it may start on (inclusive, this PC's calendar); null = no limit. */
    startDate: dateField('startDate'),
    endDate: dateField('endDate'),
    /** Seconds to wait for you before replying; if you read a chat or send anything meanwhile, no reply. 0 = at once. */
    delaySeconds: wholeNumber('delaySeconds', AWAY_DELAY_MAX).default(0),
    /** 0: one reply per chat per window. Otherwise a chat can get another after this many minutes (never sooner than AWAY_MIN_GAP_MINUTES). */
    repeatMinutes: wholeNumber('repeatMinutes', AWAY_REPEAT_MAX).default(0),
    message: messageField,
  })
  .refine(s => s.from !== s.until, { message: "From and until can't be the same time." })
  .refine(s => !s.startDate || !s.endDate || s.startDate <= s.endDate, { message: 'The first date must be on or before the last.' })
export type AwaySchedule = z.infer<typeof AwayScheduleSchema>

export const DEFAULT_SCHEDULES: AwaySchedule[] = [
  {
    id: 'day',
    name: 'Daytime',
    enabled: true,
    from: '08:00',
    until: '21:00',
    days: [...ALL_DAYS],
    startDate: null,
    endDate: null,
    delaySeconds: 30,
    repeatMinutes: 60,
    message: AWAY_DAY_MESSAGE,
  },
  {
    id: 'night',
    name: 'Night',
    enabled: true,
    from: '21:00',
    until: '08:00',
    days: [...ALL_DAYS],
    startDate: null,
    endDate: null,
    delaySeconds: 0,
    repeatMinutes: 0,
    message: AWAY_NIGHT_MESSAGE,
  },
]

/** The keys an earlier build kept at the top of `away`, for a single window. */
const LEGACY_KEYS = ['from', 'until', 'message'] as const

/**
 * An `away` written by an earlier build (`{ enabled, from, until, message, quietMinutes }`, any of them)
 * becomes one schedule with the same hours and message, with that build's defaults for what's missing
 * (18:00 to 09:00), so it keeps replying when it did: `{ enabled: true }` alone must not start replying
 * all day. Only no `away` at all (or an empty one) gets the day and night defaults. Schedules missing
 * an id get one.
 */
function migrate(raw: unknown): unknown {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return raw
  const a = { ...(raw as Record<string, unknown>) }
  if (!('schedules' in a)) {
    const legacy = Object.keys(a).length > 0
    a.schedules = legacy
      ? [
          {
            id: 'away',
            name: 'Away',
            from: a.from ?? '18:00',
            until: a.until ?? '09:00',
            message: a.message ?? AWAY_LEGACY_MESSAGE,
          },
        ]
      : structuredClone(DEFAULT_SCHEDULES)
  }
  for (const k of LEGACY_KEYS) delete a[k]
  if (Array.isArray(a.schedules)) {
    const used = new Set(a.schedules.flatMap(s => (s && typeof s === 'object' && typeof s.id === 'string' ? [s.id] : [])))
    a.schedules = a.schedules.map(s => {
      if (!s || typeof s !== 'object' || typeof (s as { id?: unknown }).id === 'string') return s
      let n = 1
      while (used.has(`s${n}`)) n++
      used.add(`s${n}`)
      return { ...s, id: `s${n}` }
    })
  }
  return a
}

const QUIET_MSG = 'quietMinutes must be a whole number from 0 to 1440'

export const AwaySchema = z.preprocess(
  migrate,
  z
    .looseObject({
      /** The master switch: no schedule replies while this is off. */
      enabled: z.boolean({ error: 'enabled must be true or false' }).default(false),
      /** Minutes after anything sent from your account in a chat (you or Claude; not the Page button) before an away reply is allowed; 0 turns that off. */
      quietMinutes: z.number({ error: QUIET_MSG }).int(QUIET_MSG).min(0, QUIET_MSG).max(1440, QUIET_MSG).default(30),
      schedules: z.array(AwayScheduleSchema, { error: 'schedules must be a list' }).max(12, '12 schedules at most.'),
      /**
       * Chats that never get an away reply (a nickname, @username or numeric id), such as a manager who
       * shouldn't hear from a bot. Everything else about them is unchanged: sending, watching, auto-reply.
       */
      skip: z.array(z.union([z.string().min(1), z.number().int()]), { error: 'skip must be a list of chats' }).default([]),
    })
    .refine(a => new Set(a.schedules.map(s => s.id)).size === a.schedules.length, {
      message: 'Two schedules have the same id.',
    }),
)
export type AwayConfig = z.infer<typeof AwaySchema>

/**
 * What the tray, the menu bar and the CLI may change. `from`, `until` and `message` are the single
 * window an earlier build had: they change the schedule named by `schedule` (an id or a name), or
 * else the one the menu bar shows them from, primarySchedule (the menu bar app still sends them).
 */
export type AwayPatch = Partial<Pick<AwayConfig, 'enabled' | 'quietMinutes' | 'skip'>> & {
  schedules?: unknown[]
  schedule?: string
  from?: string
  until?: string
  message?: string
}
const PATCH_KEYS = ['enabled', 'quietMinutes', 'skip', 'schedules', 'schedule', 'from', 'until', 'message']

/**
 * The one schedule an older app or the CLI shows as "the" away window: the one that would reply now
 * (even with away off), else the first switched on, else the first. Edits through the old single-window
 * fields go to this same schedule, so editing the message at night changes the Night text.
 */
export function primarySchedule(away: Pick<AwayConfig, 'schedules'>, now: Date): AwaySchedule | undefined {
  return awayWindowAt({ enabled: true, schedules: away.schedules }, now)?.schedule ?? away.schedules.find(s => s.enabled) ?? away.schedules[0]
}

/** A schedule by id or name (any case), for `tgmcp away night message …`. */
export function findSchedule(away: Pick<AwayConfig, 'schedules'>, ref: string): AwaySchedule | undefined {
  const r = ref.trim().toLowerCase()
  return away.schedules.find(s => s.id === r) ?? away.schedules.find(s => s.name.toLowerCase() === r)
}

export function applyAwayPatch(
  current: AwayConfig,
  patch: unknown,
  now: Date = new Date(),
): { ok: true; away: AwayConfig } | { ok: false; error: string } {
  if (typeof patch !== 'object' || patch === null || Array.isArray(patch) || Object.keys(patch).length === 0) {
    return { ok: false, error: 'nothing to change: send enabled, quietMinutes, skip or schedules' }
  }
  const unknown = Object.keys(patch).filter(k => !PATCH_KEYS.includes(k))
  if (unknown.length > 0) return { ok: false, error: `unknown setting: ${unknown.join(', ')}` }
  const p = patch as Record<string, unknown>
  const next: Record<string, unknown> = { ...current, schedules: p.schedules ?? current.schedules.map(s => ({ ...s })) }
  if ('enabled' in p) next.enabled = p.enabled
  if ('quietMinutes' in p) next.quietMinutes = p.quietMinutes
  // The whole list, replacing what is saved (the tray's per-chat switches send it back with one chat added or removed).
  if ('skip' in p) next.skip = p.skip
  const legacy = LEGACY_KEYS.filter(k => k in p)
  if ('schedule' in p && (typeof p.schedule !== 'string' || legacy.length === 0)) {
    return { ok: false, error: 'schedule names the schedule that from, until or message change' }
  }
  if (legacy.length > 0) {
    const list = next.schedules as Record<string, unknown>[]
    if (!Array.isArray(list)) return { ok: false, error: 'schedules must be a list' }
    let target: AwaySchedule | undefined
    if (typeof p.schedule === 'string') {
      target = findSchedule(current, p.schedule)
      if (!target) {
        const names = current.schedules.map(s => s.name).join(', ')
        return { ok: false, error: `No schedule called "${p.schedule}"${names ? ` (there are: ${names})` : ''}.` }
      }
    } else {
      target = primarySchedule(current, now)
    }
    const i = Math.max(0, target ? list.findIndex(s => s?.id === target.id) : 0)
    const changed = { ...(list[i] ?? { id: 'away', name: 'Away', from: '18:00', until: '09:00', message: AWAY_NIGHT_MESSAGE }) }
    for (const k of legacy) changed[k] = p[k]
    next.schedules = list.length === 0 ? [changed] : list.map((s, j) => (j === i ? changed : s))
  }
  const parsed = AwaySchema.safeParse(next)
  if (!parsed.success) {
    const issue = parsed.error.issues[0]
    return { ok: false, error: issue ? scheduleError(issue, next.schedules) : 'invalid away settings' }
  }
  return { ok: true, away: parsed.data }
}

/** "Daytime: from must be a time like 18:00", so the tray can tell which schedule a mistake is in. */
function scheduleError(issue: z.core.$ZodIssue, schedules: unknown): string {
  const [key, index] = issue.path
  if (key !== 'schedules' || typeof index !== 'number' || !Array.isArray(schedules)) return issue.message
  const name = (schedules[index] as { name?: unknown } | undefined)?.name
  return typeof name === 'string' && name.trim() ? `${name.trim()}: ${issue.message}` : issue.message
}

// ---- the windows -----------------------------------------------------------------

const pad = (n: number) => String(n).padStart(2, '0')

/** Local date at "HH:MM" on the day `offset` days from `base`'s day. Local constructors keep DST right. */
function localAt(base: Date, offset: number, hhmm: string): Date {
  const [h, m] = hhmm.split(':').map(Number) as [number, number]
  return new Date(base.getFullYear(), base.getMonth(), base.getDate() + offset, h, m)
}

/** "2026-11-03", this PC's calendar. */
export const ymd = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
const stamp = (d: Date) => `${ymd(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}`

/** Does the schedule run on the day `start` falls on (its weekday, and inside its dates)? */
export function runsOn(s: Pick<AwaySchedule, 'days' | 'startDate' | 'endDate'>, start: Date): boolean {
  const day = ymd(start)
  return s.days.includes(start.getDay()) && (!s.startDate || day >= s.startDate) && (!s.endDate || day <= s.endDate)
}

export interface AwayWindow {
  /** `<schedule id>@<local start>`, unique per schedule and night. */
  id: string
  start: Date
  end: Date
  schedule: AwaySchedule
}

type WindowShape = Pick<AwaySchedule, 'from' | 'until'> & Partial<Pick<AwaySchedule, 'days' | 'startDate' | 'endDate'>>

/** The window of this one schedule containing `now` (start included, end excluded), or null. Ignores `enabled`. */
export function scheduleWindowAt(s: WindowShape, now: Date): { id: string; start: Date; end: Date } | null {
  const crosses = s.from > s.until
  const rule = { days: s.days ?? [...ALL_DAYS], startDate: s.startDate ?? null, endDate: s.endDate ?? null }
  for (const offset of [0, -1]) {
    const start = localAt(now, offset, s.from)
    const end = localAt(now, offset + (crosses ? 1 : 0), s.until)
    if (start <= now && now < end && runsOn(rule, start)) return { id: stamp(start), start, end }
  }
  return null
}

/** Dated schedules beat weekly ones, then the shorter window wins, then the one higher in the list. */
function moreSpecific(a: AwayWindow, b: AwayWindow): boolean {
  const da = a.schedule.startDate || a.schedule.endDate ? 1 : 0
  const db = b.schedule.startDate || b.schedule.endDate ? 1 : 0
  if (da !== db) return da > db
  return a.end.getTime() - a.start.getTime() < b.end.getTime() - b.start.getTime()
}

/** The schedule replying at `now`, or null: away off, or no switched-on schedule covers it. */
export function awayWindowAt(away: Pick<AwayConfig, 'enabled' | 'schedules'>, now: Date): AwayWindow | null {
  if (!away.enabled) return null
  let best: AwayWindow | null = null
  for (const schedule of away.schedules) {
    if (!schedule.enabled) continue
    const w = scheduleWindowAt(schedule, now)
    if (!w) continue
    const win = { ...w, id: `${schedule.id}@${w.id}`, schedule }
    if (!best || moreSpecific(win, best)) best = win
  }
  return best
}

/** How far ahead nextAwayChange looks: a schedule dated for a trip months away still says when it starts. */
const LOOKAHEAD_DAYS = 400

/** When the replying schedule changes next (turns on, off or hands over): null if away is off or nothing is coming within a year or so. */
export function nextAwayChange(away: AwayConfig, now: Date): Date | null {
  if (!away.enabled) return null
  const current = awayWindowAt(away, now)?.schedule.id ?? null
  const times = new Set<number>()
  for (const s of away.schedules) {
    if (!s.enabled) continue
    const crosses = s.from > s.until
    for (let offset = -1; offset <= LOOKAHEAD_DAYS; offset++) {
      const start = localAt(now, offset, s.from)
      // Only the days it runs: a schedule dated in the past adds nothing to check.
      if (!runsOn(s, start)) continue
      times.add(start.getTime())
      times.add(localAt(now, offset + (crosses ? 1 : 0), s.until).getTime())
    }
  }
  const sorted = [...times].filter(t => t > now.getTime()).sort((a, b) => a - b)
  for (const t of sorted) {
    if ((awayWindowAt(away, new Date(t))?.schedule.id ?? null) !== current) return new Date(t)
  }
  return null
}

// ---- the message -----------------------------------------------------------------

export function firstName(me: { name: string } | null | undefined): string | null {
  return me?.name.trim().split(/\s+/)[0] || null
}

/** {name} becomes the first name when there is one. The footer is the caller's job (withFooter in policy.ts). */
export function renderAwayMessage(message: string, name: string | null): string {
  return name === null ? message : message.replaceAll('{name}', name)
}

// ---- words -----------------------------------------------------------------------

const DAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/** "every day", "weekdays", "weekends", "Mon, Wed, Fri". */
export function daysText(days: readonly number[]): string {
  const set = new Set(days)
  if (set.size === 7) return 'every day'
  if (set.size === 5 && [1, 2, 3, 4, 5].every(d => set.has(d))) return 'weekdays'
  if (set.size === 2 && set.has(0) && set.has(6)) return 'weekends'
  return [1, 2, 3, 4, 5, 6, 0].filter(d => set.has(d)).map(d => DAY_SHORT[d]).join(', ')
}

export function delayText(seconds: number): string {
  if (seconds === 0) return 'replies at once'
  return seconds < 60 || seconds % 60 !== 0 ? `waits ${seconds} s for you` : `waits ${seconds / 60} min for you`
}

/** "Daytime 08:00–21:00, every day, waits 30 s for you". */
export function scheduleSummary(s: AwaySchedule): string {
  const dates =
    s.startDate && s.endDate ? `, ${s.startDate} to ${s.endDate}` : s.startDate ? `, from ${s.startDate}` : s.endDate ? `, until ${s.endDate}` : ''
  return `${s.name} ${s.from}–${s.until}, ${daysText(s.days)}${dates}, ${delayText(s.delaySeconds)}${s.enabled ? '' : ' (switched off)'}`
}

export function awaySummary(away: AwayConfig, now: Date): string {
  if (!away.enabled) return 'off'
  if (!away.schedules.some(s => s.enabled)) return 'on, but no schedule is switched on'
  const w = awayWindowAt(away, now)
  if (w) return `on, ${w.schedule.name} now (until ${w.schedule.until}, ${delayText(w.schedule.delaySeconds)})`
  const next = nextAwayChange(away, now)
  const nextName = next ? awayWindowAt(away, next)?.schedule.name : undefined
  if (!next || !nextName) return 'on, nothing scheduled'
  const time = `${pad(next.getHours())}:${pad(next.getMinutes())}`
  const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1)
  const when = ymd(next) === ymd(now) || (ymd(next) === ymd(tomorrow) && next.getTime() - now.getTime() < 86_400_000) ? time : `${ymd(next)} ${time}`
  return `on, ${nextName} starts at ${when}`
}

// ---- CLI ---------------------------------------------------------------------------

const USAGE =
  'usage: away [on | off | quiet <minutes> | [<schedule>] hours HH:MM HH:MM | [<schedule>] message <text>]\n' +
  '  hours and message change the named schedule (e.g. away night message …), or else the one replying now'

export function parseAwayArgs(
  args: string[],
): { kind: 'show' } | { kind: 'patch'; patch: AwayPatch } | { kind: 'error'; usage: string } {
  if (args.length >= 2 && (args[1] === 'hours' || args[1] === 'message') && !['on', 'off', 'quiet', 'hours', 'message'].includes(args[0]!)) {
    const r = parseAwayArgs(args.slice(1))
    return r.kind === 'patch' ? { kind: 'patch', patch: { schedule: args[0]!, ...r.patch } } : r
  }
  const [cmd, ...rest] = args
  if (cmd === undefined) return { kind: 'show' }
  if (cmd === 'on' && rest.length === 0) return { kind: 'patch', patch: { enabled: true } }
  if (cmd === 'off' && rest.length === 0) return { kind: 'patch', patch: { enabled: false } }
  if (cmd === 'hours' && rest.length === 2) return { kind: 'patch', patch: { from: rest[0]!, until: rest[1]! } }
  if (cmd === 'message' && rest.length > 0) return { kind: 'patch', patch: { message: rest.join(' ') } }
  if (cmd === 'quiet' && rest.length === 1 && /^\d+$/.test(rest[0]!)) {
    return { kind: 'patch', patch: { quietMinutes: Number(rest[0]) } }
  }
  return { kind: 'error', usage: USAGE }
}
