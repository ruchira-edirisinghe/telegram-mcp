import { describe, expect, test } from 'bun:test'
import { parseConfig, type Config } from '../src/shared/config'
import { decideSend } from '../src/shared/policy'
import { listContacts, pageTarget, renameContact, setAutoReply, setPageFrom, setSendMode } from '../src/shared/contacts'

function fresh(): Config {
  return parseConfig(
    JSON.stringify({
      chats: {
        leo: { id: 10, username: 'leo_p', name: 'Leo Perera', type: 'user', aliases: ['lp'] },
        leonardo: { id: 11, username: 'leonardo_s', name: 'Leonardo Silva' },
        anna: { id: 12, username: 'anna_k', name: 'Anna K' },
        qa: { id: -1001234567890, name: 'Team QA', type: 'group', topicId: 42 },
        'qa-all': { id: -1001234567890, name: 'Team QA' },
        allhands: { id: -1005550001111, name: 'Company All-Hands' },
        mine: { id: 1, name: 'Me' },
      },
      rules: {
        blocked: [5550001111],
        requireApproval: ['qa'],
        autoSend: ['leo', '@anna_k', 'me', 'allhands'],
        autoReply: ['lp', 'qa-all'],
      },
    }),
  )
}

const byNick = (c: Config, meId?: number) => Object.fromEntries(listContacts(c, meId).map(x => [x.nickname, x]))

describe('listContacts', () => {
  test('every saved chat, sorted, with the rule that decides sending there', () => {
    const c = fresh()
    const list = listContacts(c, 1)
    expect(list.map(x => x.nickname)).toEqual(['allhands', 'anna', 'leo', 'leonardo', 'mine', 'qa', 'qa-all'])
    const by = byNick(c, 1)
    expect(by.leo).toMatchObject({ chatId: 10, username: 'leo_p', type: 'user', sendMode: 'autoSend', autoReply: true, aliases: ['lp'] })
    expect(by.anna).toMatchObject({ sendMode: 'autoSend', autoReply: false }) // listed by @username
    expect(by.leonardo).toMatchObject({ sendMode: 'none', autoReply: false, policy: 'needs approval (default)' })
    expect(by.allhands).toMatchObject({ sendMode: 'blocked' }) // blocked by raw id beats autoSend
    expect(by.mine).toMatchObject({ sendMode: 'autoSend' }) // "me"
  })

  test('two nicknames for one chat share its rules and name each other', () => {
    const by = byNick(fresh())
    expect(by.qa).toMatchObject({ sendMode: 'requireApproval', autoReply: true, sameChat: ['qa-all'] })
    expect(by['qa-all']).toMatchObject({ sendMode: 'requireApproval', autoReply: true, sameChat: ['qa'] })
  })
})

describe('setSendMode', () => {
  test('moves the chat to exactly one list, removing every other entry that means it', () => {
    const c = fresh()
    setSendMode(c, 'leo', 'requireApproval')
    expect(c.rules.autoSend).toEqual(['@anna_k', 'me', 'allhands'])
    expect(c.rules.requireApproval).toEqual(['qa', 'leo'])
    expect(decideSend(c, { id: 10, username: 'leo_p' }, { isWatching: false }).action).toBe('approve')

    setSendMode(c, 'anna', 'blocked') // was listed by @username
    expect(c.rules.autoSend).toEqual(['me', 'allhands'])
    expect(c.rules.blocked).toEqual([5550001111, 'anna'])
    expect(decideSend(c, { id: 12, username: 'anna_k' }, { isWatching: false }).action).toBe('block')
  })

  test('unblocking a chat blocked by raw id clears that id too', () => {
    const c = fresh()
    setSendMode(c, 'allhands', 'autoSend')
    expect(c.rules.blocked).toEqual([])
    expect(byNick(c).allhands!.sendMode).toBe('autoSend')
    expect(decideSend(c, { id: -1005550001111 }, { isWatching: false }).action).toBe('send')
  })

  test('another nickname for the same chat leaves the old list too', () => {
    const c = fresh()
    setSendMode(c, 'qa-all', 'autoSend')
    expect(c.rules.requireApproval).toEqual([])
    expect(byNick(c).qa!.sendMode).toBe('autoSend')
  })

  test('"me" is cleared only for your own chat', () => {
    const c = fresh()
    setSendMode(c, 'leonardo', 'blocked')
    expect(c.rules.autoSend).toContain('me')
    setSendMode(c, 'mine', 'none', 1)
    expect(c.rules.autoSend).toEqual(['leo', '@anna_k', 'allhands'])
    expect(byNick(c, 1).mine!.sendMode).toBe('none')
  })

  test('none takes the chat off every sending list and leaves autoReply alone', () => {
    const c = fresh()
    setSendMode(c, 'leo', 'none')
    expect(c.rules.autoSend).toEqual(['@anna_k', 'me', 'allhands'])
    expect(c.rules.autoReply).toEqual(['lp', 'qa-all'])
    expect(byNick(c).leo!.sendMode).toBe('none')
  })

  test('the current mode is a no-op', () => {
    const c = fresh()
    const before = JSON.stringify(c.rules)
    expect(setSendMode(c, 'qa', 'requireApproval')).toBe(false)
    expect(JSON.stringify(c.rules)).toBe(before)
  })

  test('unknown nicknames are refused', () => {
    expect(() => setSendMode(fresh(), 'nobody', 'autoSend')).toThrow('No saved chat called "nobody"')
  })
})

describe('setAutoReply', () => {
  test('off removes every entry that means the chat (alias, other nickname)', () => {
    const c = fresh()
    setAutoReply(c, 'leo', false)
    expect(c.rules.autoReply).toEqual(['qa-all'])
    setAutoReply(c, 'qa', false)
    expect(c.rules.autoReply).toEqual([])
  })

  test('on adds the nickname once; already on is a no-op', () => {
    const c = fresh()
    expect(setAutoReply(c, 'anna', true)).toBe(true)
    expect(c.rules.autoReply).toEqual(['lp', 'qa-all', 'anna'])
    expect(setAutoReply(c, 'anna', true)).toBe(false)
    expect(setAutoReply(c, 'leo', true)).toBe(false) // listed through its alias
    expect(c.rules.autoReply).toEqual(['lp', 'qa-all', 'anna'])
  })
})

describe('renameContact', () => {
  test('renames in place and rewrites the rules that used the old name', () => {
    const c = fresh()
    expect(renameContact(c, 'qa', 'QA  Review')).toBe('qa review')
    expect(Object.keys(c.chats)).toEqual(['leo', 'leonardo', 'anna', 'qa review', 'qa-all', 'allhands', 'mine'])
    expect(c.rules.requireApproval).toEqual(['qa review'])
    expect(c.chats['qa review']).toMatchObject({ id: -1001234567890, topicId: 42 })
    expect(byNick(c)['qa review']!.sendMode).toBe('requireApproval')
  })

  test('an alias of the same chat can become its nickname', () => {
    const c = fresh()
    expect(renameContact(c, 'leo', 'LP')).toBe('lp')
    expect(c.chats.lp!.aliases).toEqual([])
    expect(c.rules.autoSend).toContain('lp')
    expect(byNick(c).lp).toMatchObject({ sendMode: 'autoSend', autoReply: true })
  })

  test('a case-only change is allowed; the same name is a no-op', () => {
    const c = parseConfig(JSON.stringify({ chats: { Leo: { id: 10 } }, rules: { autoSend: ['Leo'] } }))
    expect(renameContact(c, 'Leo', 'leo')).toBe('leo')
    expect(c.rules.autoSend).toEqual(['leo'])
    expect(renameContact(c, 'leo', ' leo ')).toBe('leo')
  })

  test('names that already mean something else are refused', () => {
    expect(() => renameContact(fresh(), 'anna', 'leonardo')).toThrow('already means leonardo')
    expect(() => renameContact(fresh(), 'anna', 'lp')).toThrow('already means leo')
    expect(() => renameContact(fresh(), 'anna', 'Leo Perera')).toThrow('already means leo')
    expect(() => renameContact(fresh(), 'qa', 'qa-all')).toThrow('already a nickname for this chat')
    expect(() => renameContact(fresh(), 'anna', 'saved messages')).toThrow()
    expect(() => renameContact(fresh(), 'anna', '@anna')).toThrow('cannot start with @')
    expect(() => renameContact(fresh(), 'anna', '   ')).toThrow('cannot be empty')
  })

  test('a name the rules mention but that matches nothing is refused (it would join those lists)', () => {
    const c = parseConfig(JSON.stringify({ chats: { anna: { id: 12 } }, rules: { autoSend: ['bob'] } }))
    expect(() => renameContact(c, 'anna', 'bob')).toThrow('autoSend')
    expect(c.chats.anna).toBeDefined()
  })

  test('away.skip follows a rename, and a name only away.skip mentions is refused too', () => {
    const c = parseConfig(JSON.stringify({ chats: { anna: { id: 12 }, leo: { id: 10 } }, away: { skip: ['anna', 'bob', 99] } }))
    expect(renameContact(c, 'anna', 'annie')).toBe('annie')
    expect(c.away.skip).toEqual(['annie', 'bob', 99])
    expect(() => renameContact(c, 'leo', 'bob')).toThrow('away.skip')
  })
})

describe('setPageFrom', () => {
  test('a person goes on paging.pageFrom by user id, and comes off again', () => {
    const c = fresh()
    expect(setPageFrom(c, 'leo', true, 1)).toBe(true)
    expect(c.paging.pageFrom).toEqual([10])
    expect(byNick(c, 1).leo).toMatchObject({ pageable: true, pageFrom: true })
    expect(byNick(c, 1).leonardo).toMatchObject({ pageable: true, pageFrom: false }) // no type saved, but a user id
    expect(setPageFrom(c, 'leo', true, 1)).toBe(false)
    expect(setPageFrom(c, 'leo', false, 1)).toBe(true)
    expect(c.paging.pageFrom).toEqual([])
    expect(setPageFrom(c, 'leo', false, 1)).toBe(false)
  })

  test('groups, channels and you are not people who page you', () => {
    const c = fresh()
    expect(() => setPageFrom(c, 'qa', true, 1)).toThrow('Only a person can page you, and "qa" is a group.')
    expect(() => setPageFrom(c, 'allhands', true, 1)).toThrow('"allhands" is a group or channel.')
    expect(() => setPageFrom(c, 'mine', true, 1)).toThrow('"mine" is you.')
    expect(() => setPageFrom(c, 'nobody', true, 1)).toThrow('No saved chat called "nobody".')
    const by = byNick(c, 1)
    expect([by.qa!.pageable, by.allhands!.pageable, by.mine!.pageable]).toEqual([false, false, false])
    expect(c.paging.pageFrom).toEqual([])
  })

  test('an id put on pageFrom by hand shows up, and can be taken off', () => {
    const c = parseConfig(JSON.stringify({ chats: { anna: { id: 12 } }, paging: { pageFrom: [12, 99] } }))
    expect(byNick(c).anna).toMatchObject({ pageFrom: true })
    expect(setPageFrom(c, 'anna', false)).toBe(true)
    expect(c.paging.pageFrom).toEqual([99])
  })
})

describe('pageTarget', () => {
  test('a person by nickname; anything else is refused', () => {
    const c = fresh()
    expect(pageTarget(c, 'leonardo', 1)).toMatchObject({ nickname: 'leonardo', entry: { id: 11 } })
    expect(() => pageTarget(c, 'qa', 1)).toThrow('Only a person can be paged, and "qa" is a group.')
    expect(() => pageTarget(c, 'mine', 1)).toThrow('"mine" is you.')
  })
})
