import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { HIDDEN_PAUSE_MS, LiveActivity } from './activity';

function makeDocument() {
  const target = new EventTarget() as EventTarget & { hidden: boolean };
  target.hidden = false;
  return target;
}

describe('LiveActivity', () => {
  let doc: ReturnType<typeof makeDocument>;
  let live: { pause: Mock<() => void>; resume: Mock<() => void> };
  let activity: LiveActivity;

  const hide = () => {
    doc.hidden = true;
    doc.dispatchEvent(new Event('visibilitychange'));
  };
  const show = () => {
    doc.hidden = false;
    doc.dispatchEvent(new Event('visibilitychange'));
  };

  beforeEach(() => {
    vi.useFakeTimers();
    doc = makeDocument();
    live = { pause: vi.fn<() => void>(), resume: vi.fn<() => void>() };
    activity = new LiveActivity(live, doc as unknown as Document);
    activity.attach();
  });

  afterEach(() => {
    activity.detach();
    vi.useRealTimers();
  });

  it('pausiert erst, wenn der Tab länger als 60 Sekunden verborgen war', () => {
    hide();
    vi.advanceTimersByTime(HIDDEN_PAUSE_MS - 1);
    expect(live.pause).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(live.pause).toHaveBeenCalledTimes(1);
  });

  it('pausiert nicht, wenn der Tab vorher wieder sichtbar wird, und verbindet beim Sichtbarwerden', () => {
    hide();
    vi.advanceTimersByTime(HIDDEN_PAUSE_MS - 1000);
    show();
    vi.advanceTimersByTime(HIDDEN_PAUSE_MS * 2);

    expect(live.pause).not.toHaveBeenCalled();
    expect(live.resume).toHaveBeenCalledTimes(1);
  });

  it('verbindet bei pointerdown und keydown neu', () => {
    doc.dispatchEvent(new Event('pointerdown'));
    doc.dispatchEvent(new Event('keydown'));

    expect(live.resume).toHaveBeenCalledTimes(2);
  });

  it('zählt die verborgene Zeit ab dem Anhängen, wenn der Tab schon verborgen startet', () => {
    activity.detach();
    doc.hidden = true;
    activity = new LiveActivity(live, doc as unknown as Document);
    activity.attach();

    vi.advanceTimersByTime(HIDDEN_PAUSE_MS);

    expect(live.pause).toHaveBeenCalledTimes(1);
  });

  it('reagiert nach detach() auf nichts mehr', () => {
    activity.detach();
    hide();
    vi.advanceTimersByTime(HIDDEN_PAUSE_MS * 2);
    doc.dispatchEvent(new Event('pointerdown'));

    expect(live.pause).not.toHaveBeenCalled();
    expect(live.resume).not.toHaveBeenCalled();
  });
});
