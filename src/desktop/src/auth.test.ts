import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { refreshAccessToken, type RefreshTokenStore } from './auth-refresh';
import { AuthStore } from './auth-store';
import type { SecretCipher } from './key-store';

const tempDir = () => mkdtemp(path.join(tmpdir(), 'ic-auth-'));

function fakeCipher(available = true): SecretCipher {
  return {
    isEncryptionAvailable: () => available,
    encryptString: (text) => Buffer.from(`enc:${text}`),
    decryptString: (buffer) => buffer.toString().replace(/^enc:/, ''),
  };
}

describe('auth store', () => {
  it('encrypts the refresh token on disk and round-trips it', async () => {
    const dir = await tempDir();
    const store = AuthStore.inDirectory(fakeCipher(), dir);

    await store.set('refresh-value');

    expect(await readFile(path.join(dir, 'auth.json'), 'utf8')).not.toContain('refresh-value');
    expect(await store.get()).toBe('refresh-value');
    await store.clear();
    expect(await store.get()).toBeNull();
  });

  it('refuses to save without OS encryption, and rejects empty tokens', async () => {
    const store = AuthStore.inDirectory(fakeCipher(false), await tempDir());

    await expect(store.set('x')).rejects.toThrow('keychain');
    await expect(AuthStore.inDirectory(fakeCipher(), await tempDir()).set('')).rejects.toThrow('empty');
    expect(await store.get()).toBeNull();
  });

  it('treats a corrupt file as signed out', async () => {
    const dir = await tempDir();
    await writeFile(path.join(dir, 'auth.json'), 'not json');

    expect(await AuthStore.inDirectory(fakeCipher(), dir).get()).toBeNull();
  });
});

describe('refreshAccessToken', () => {
  function memoryStore(initial: string | null): RefreshTokenStore & { value: string | null } {
    const store = {
      value: initial,
      get: () => Promise.resolve(store.value),
      set: (token: string) => {
        store.value = token;
        return Promise.resolve();
      },
      clear: () => {
        store.value = null;
        return Promise.resolve();
      },
    };
    return store;
  }
  const reply = (status: number, body: unknown = {}) =>
    vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status })) as unknown as typeof fetch;

  it('returns the new access token and stores the new refresh token', async () => {
    const store = memoryStore('old');
    const fetchImpl = reply(200, { accessToken: 'a2', refreshToken: 'r2' });

    expect(await refreshAccessToken(store, 'http://b', fetchImpl)).toBe('a2');
    expect(store.value).toBe('r2');
    expect(fetchImpl).toHaveBeenCalledWith(
      'http://b/api/auth/refresh',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ refreshToken: 'old' }) }),
    );
  });

  it('does nothing without a stored token', async () => {
    const fetchImpl = reply(200);

    expect(await refreshAccessToken(memoryStore(null), 'http://b', fetchImpl)).toBeNull();
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('clears the stored token on 401', async () => {
    const store = memoryStore('old');

    expect(await refreshAccessToken(store, 'http://b', reply(401))).toBeNull();
    expect(store.value).toBeNull();
  });

  it('keeps the stored token on a network error, server error or odd response', async () => {
    const store = memoryStore('old');
    const offline = vi.fn().mockRejectedValue(new Error('offline')) as unknown as typeof fetch;

    expect(await refreshAccessToken(store, 'http://b', offline)).toBeNull();
    expect(await refreshAccessToken(store, 'http://b', reply(500))).toBeNull();
    expect(await refreshAccessToken(store, 'http://b', reply(200, { nope: 1 }))).toBeNull();
    expect(store.value).toBe('old');
  });
});
