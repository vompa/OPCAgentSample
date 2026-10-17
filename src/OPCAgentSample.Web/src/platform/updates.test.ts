import { describe, expect, it, vi } from 'vitest';
import { reloadOnServiceWorkerUpdate } from './updates';

function fakeContainer(controller: unknown) {
  let listener: () => void = () => {};
  return {
    container: {
      controller,
      addEventListener: (_type: 'controllerchange', fn: () => void) => {
        listener = fn;
      },
    },
    fire: () => listener(),
  };
}

describe('reloadOnServiceWorkerUpdate', () => {
  it('lädt einmal neu, wenn ein bestehender Worker abgelöst wird', () => {
    const { container, fire } = fakeContainer({});
    const reload = vi.fn();
    reloadOnServiceWorkerUpdate(container, reload);
    fire();
    fire();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('lädt nicht neu bei der Erstinstallation (kein Controller)', () => {
    const { container, fire } = fakeContainer(null);
    const reload = vi.fn();
    reloadOnServiceWorkerUpdate(container, reload);
    fire();
    expect(reload).not.toHaveBeenCalled();
  });

  it('tut nichts ohne Service-Worker-Unterstützung', () => {
    expect(() => reloadOnServiceWorkerUpdate(undefined, vi.fn())).not.toThrow();
  });
});
