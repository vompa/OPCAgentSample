// Parser für Server-Sent Events (text/event-stream), ohne Browser-APIs; dient Live-Strom und Fragen.

export interface SseMessage {
  event: string;
  data: string;
  id: string;
  /** Wartezeit in Millisekunden aus einer `retry:`-Zeile (sonst null). */
  retry: number | null;
}

/** Nimmt beliebig zerstückelte Textblöcke entgegen und liefert vollständige Ereignisse. */
export class SseParser {
  private buffer = '';

  /** Fügt Text an und gibt alle dadurch vollständig gewordenen Ereignisse zurück. */
  push(chunk: string): SseMessage[] {
    this.buffer += chunk.replace(/\r\n?/g, '\n');
    const messages: SseMessage[] = [];
    let separator = this.buffer.indexOf('\n\n');
    while (separator >= 0) {
      const message = parseBlock(this.buffer.slice(0, separator));
      this.buffer = this.buffer.slice(separator + 2);
      if (message) messages.push(message);
      separator = this.buffer.indexOf('\n\n');
    }
    return messages;
  }
}

function parseBlock(block: string): SseMessage | null {
  let event = 'message';
  let id = '';
  let retry: number | null = null;
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (line === '' || line.startsWith(':')) continue; // Kommentar (z. B. heartbeat)
    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
    if (field === 'event') event = value;
    else if (field === 'data') data.push(value);
    else if (field === 'id') id = value;
    else if (field === 'retry' && /^\d+$/.test(value)) retry = Number(value);
  }
  if (data.length === 0 && retry === null) return null;
  return { event, data: data.join('\n'), id, retry };
}
