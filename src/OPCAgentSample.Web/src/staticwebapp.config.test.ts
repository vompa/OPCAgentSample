import { describe, expect, it } from 'vitest';
import rawConfig from '../public/staticwebapp.config.json?raw';

const config = JSON.parse(rawConfig) as { navigationFallback: { exclude: string[] } };

describe('staticwebapp.config.json', () => {
  it('liefert für API-, MCP- und Service-Worker-Pfade kein index.html als Fallback', () => {
    const { exclude } = config.navigationFallback;
    expect(exclude).toEqual(expect.arrayContaining(['/api/*', '/mcp*', '/sw.js']));
  });
});
