import { describe, expect, it } from 'vitest';
import { CARD } from '@/test/fixtures';
import { findPreparedAnswer, normalizeQuestion } from './snapshot';

describe('findPreparedAnswer', () => {
  const answers = { 'Wie ist die Lage in der Halle gerade?': { toolCalls: [], card: CARD } };

  it('findet die Antwort unabhängig von Leerraum, Satzzeichen am Ende und Schreibweise', () => {
    expect(findPreparedAnswer(answers, '  wie ist die LAGE in der  Halle gerade ')).not.toBeNull();
    expect(findPreparedAnswer(answers, 'Wie ist die Lage in der Halle gerade?!')).not.toBeNull();
  });

  it('liefert für freie Fragen nichts (sie gehen in die Warteschlange)', () => {
    expect(findPreparedAnswer(answers, 'Wie wird das Wetter?')).toBeNull();
  });

  it('normalisiert wie der Server', () => {
    expect(normalizeQuestion('A  b?')).toBe('a b');
  });
});
