import { describe, expect, it } from 'vitest';
import { SseParser } from './sse';

describe('SseParser', () => {
  it('liefert vollständige Ereignisse mit Name, Daten und Id', () => {
    const parser = new SseParser();
    const messages = parser.push('event: feed\nid: 17-0\ndata: {"a":1}\n\n');
    expect(messages).toEqual([{ event: 'feed', data: '{"a":1}', id: '17-0', retry: null }]);
  });

  it('setzt zerstückelte Blöcke zusammen und kennt CRLF', () => {
    const parser = new SseParser();
    expect(parser.push('event: clock\r\ndata: {"x"')).toEqual([]);
    const messages = parser.push(':1}\r\n\r\nevent: alarm\ndata: {}\n\n');
    expect(messages.map((m) => [m.event, m.data])).toEqual([
      ['clock', '{"x":1}'],
      ['alarm', '{}'],
    ]);
  });

  it('ignoriert Kommentare (heartbeat) und liest retry', () => {
    const parser = new SseParser();
    expect(parser.push(': heartbeat\n\n')).toEqual([]);
    const [message] = parser.push('retry: 3000\n\n');
    expect(message?.retry).toBe(3000);
  });

  it('verbindet mehrere data-Zeilen mit Zeilenumbruch', () => {
    const [message] = new SseParser().push('data: a\ndata: b\n\n');
    expect(message?.data).toBe('a\nb');
  });
});
