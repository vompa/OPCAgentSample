// Ersatz für die API-Schicht in Store- und Komponententests (`vi.mock('@/api/client', ...)`).
import { vi } from 'vitest';

export const apiMock = {
  baseUrl: '',
  me: vi.fn(),
  overview: vi.fn(),
  favorites: vi.fn(),
  saveFavorites: vi.fn(),
  resetFavorites: vi.fn(),
  inbox: vi.fn(),
  takeInboxItem: vi.fn(),
  feed: vi.fn(),
  latestHistory: vi.fn(),
  sessions: vi.fn(),
  session: vi.fn(),
  newSession: vi.fn(),
  recentQuestions: vi.fn(),
  health: vi.fn(),
  demoSession: vi.fn(),
  demoLogin: vi.fn(),
  demoLogout: vi.fn(),
  reportsSummary: vi.fn(),
  reportsMachines: vi.fn(),
  ask: vi.fn(),
};

export function resetApiMock(): void {
  for (const value of Object.values(apiMock)) if (typeof value === 'function') value.mockReset();
}
