import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { Input, Label } from '@/components/ui/form';
import { Select } from '@/components/ui/select';
import { useApp } from '@/lib/app-context';
import type { ProviderInfo } from '@/types/api';

/**
 * Speech-to-text provider choice (#20). Shown only when the backend offers more than one provider, which
 * happens once Deepgram is enabled in its configuration. The Deepgram key lives in the OS keychain (desktop
 * bridge); this card shows whether one is saved but never the key itself.
 */
export function SpeechToTextCard({ providers }: { providers: ProviderInfo[] }) {
  const { api } = useApp();
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    api.getPreferences().then(
      (prefs) => setSelected(prefs.speechToTextProvider ?? null),
      (err: unknown) => setError(err instanceof Error ? err.message : 'Could not load your choice.'),
    );
  }, [api]);

  if (providers.length < 2) return null;

  const defaultProvider = providers.find((p) => p.isDefault);
  const effective = providers.find((p) => p.name === (selected ?? defaultProvider?.name));

  async function change(value: string) {
    setStatus(null);
    try {
      const saved = await api.setSpeechToTextProvider(value === '' ? null : value);
      setSelected(saved.speechToTextProvider ?? null);
      setError(null);
      setStatus('Saved.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the choice.');
    }
  }

  return (
    <Card className="grid gap-3">
      <CardTitle>Speech-to-text</CardTitle>
      <div className="grid gap-2">
        <Label htmlFor="stt-provider">Speech-to-text provider</Label>
        <Select
          id="stt-provider"
          value={selected ?? ''}
          onValueChange={(v) => void change(v)}
          options={[
            { value: '', label: defaultProvider ? `Default (${defaultProvider.displayName})` : 'Default' },
            ...providers.map((provider) => ({ value: provider.name, label: provider.displayName })),
          ]}
        />
      </div>
      {effective?.requiresApiKey && <SpeechKeyField provider={effective.name} />}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {status && <p className="text-xs text-muted-foreground">{status}</p>}
      <CardDescription>
        Whisper runs on this computer. A cloud provider sends the conversation audio to that service and needs your own key.
      </CardDescription>
    </Card>
  );
}

/** BYOK: the key goes to the OS keychain through the desktop app, never to a file or the backend DB. */
function SpeechKeyField({ provider }: { provider: string }) {
  const { platform } = useApp();
  const [key, setKey] = useState('');
  const [hasKey, setHasKey] = useState<boolean | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const [version, setVersion] = useState(0);

  useEffect(() => {
    let current = true;
    platform.getApiKey(provider).then(
      (saved) => current && setHasKey(saved !== null),
      () => current && setHasKey(false),
    );
    return () => {
      current = false;
    };
  }, [platform, provider, version]);

  async function save() {
    try {
      await platform.setApiKey(provider, key);
      setKey('');
      setMessage(null);
      setVersion((v) => v + 1);
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Could not save the key.');
    }
  }

  async function remove() {
    try {
      await platform.deleteApiKey(provider);
      setMessage(null);
      setVersion((v) => v + 1);
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Could not remove the key.');
    }
  }

  return (
    <div className="grid gap-2">
      <Label htmlFor={`${provider}-key`}>{`${provider} API key`}</Label>
      <div className="flex gap-2">
        <Input
          id={`${provider}-key`}
          type="password"
          autoComplete="off"
          placeholder="Your API key"
          value={key}
          onChange={(e) => setKey(e.target.value)}
        />
        <Button size="sm" disabled={!key} onClick={() => void save()}>
          Save key
        </Button>
        {hasKey && (
          <Button size="sm" variant="ghost" onClick={() => void remove()}>
            Remove key
          </Button>
        )}
      </div>
      {hasKey !== null && (
        <p className="text-xs text-muted-foreground">{hasKey ? 'A key is saved in your OS keychain.' : 'No key saved yet.'}</p>
      )}
      {message && (
        <p role="alert" className="text-sm text-destructive">
          {message}
        </p>
      )}
    </div>
  );
}
