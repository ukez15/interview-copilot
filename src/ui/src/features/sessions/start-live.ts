import type { ApiClient } from '@/lib/api';
import type { Platform } from '@/platform';
import type { Session } from '@/types/api';

/** Providers that take a BYOK key (mirrors the backend's ProviderNames.KeyedProviders). */
const KEYED_PROVIDERS = new Set(['deepgram', 'anthropic', 'openai', 'google']);

/**
 * Takes a session live and starts audio capture. Shared by the session page and the meeting prompt
 * (#16). Between going live and capturing, the keys of the chosen cloud providers are handed to the
 * backend (#30), which keeps them in memory for this session only.
 */
export async function startLiveSession(api: ApiClient, platform: Platform, sessionId: string): Promise<Session> {
  const started = await api.startSession(sessionId);
  await handOffKeys(api, platform, sessionId);
  await platform.startCapture(sessionId);
  return started;
}

/** Best effort: a failed hand-off must never block capture, so every error is swallowed here. */
async function handOffKeys(api: ApiClient, platform: Platform, sessionId: string): Promise<void> {
  let providers: string[];
  try {
    const prefs = await api.getPreferences();
    providers = [
      ...new Set([prefs.llmProvider ?? prefs.defaultLlmProvider, prefs.speechToTextProvider ?? prefs.defaultSpeechToTextProvider]),
    ].filter((name) => KEYED_PROVIDERS.has(name));
  } catch {
    return;
  }

  for (const provider of providers) {
    try {
      const key = await platform.getApiKey(provider);
      if (key) {
        await api.setSessionKey(sessionId, provider, key);
      }
    } catch {
      // The answer will say "Add your key in Settings" if the key really is missing.
    }
  }
}
