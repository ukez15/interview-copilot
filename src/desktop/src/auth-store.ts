import { promises as fs } from 'node:fs';
import path from 'node:path';
import type { SecretCipher } from './key-store';

/**
 * Keeps the sign-in across restarts (#8). Only the refresh token is stored, encrypted with the OS
 * keychain via safeStorage; the password is never stored. Refuses to save if encryption is unavailable.
 */
export class AuthStore {
  constructor(
    private readonly cipher: SecretCipher,
    private readonly filePath: string,
  ) {}

  static inDirectory(cipher: SecretCipher, directory: string): AuthStore {
    return new AuthStore(cipher, path.join(directory, 'auth.json'));
  }

  async get(): Promise<string | null> {
    if (!this.cipher.isEncryptionAvailable()) {
      return null;
    }
    try {
      const parsed = JSON.parse(await fs.readFile(this.filePath, 'utf8')) as { refreshToken?: unknown };
      return typeof parsed.refreshToken === 'string' && parsed.refreshToken !== ''
        ? this.cipher.decryptString(Buffer.from(parsed.refreshToken, 'base64'))
        : null;
    } catch {
      return null;
    }
  }

  async set(refreshToken: string): Promise<void> {
    if (!this.cipher.isEncryptionAvailable()) {
      throw new Error("Your OS keychain isn't available, so the sign-in wasn't saved.");
    }
    if (typeof refreshToken !== 'string' || refreshToken === '') {
      throw new Error('The refresh token is empty.');
    }
    await fs.mkdir(path.dirname(this.filePath), { recursive: true });
    const payload = { refreshToken: this.cipher.encryptString(refreshToken).toString('base64') };
    await fs.writeFile(this.filePath, JSON.stringify(payload), { encoding: 'utf8', mode: 0o600 });
  }

  async clear(): Promise<void> {
    await fs.rm(this.filePath, { force: true });
  }
}
