import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { Input, Label } from '@/components/ui/form';
import { Slider } from '@/components/ui/slider';
import { MicrophonePicker } from '@/features/audio/MicrophonePicker';
import { ModelPicker } from '@/features/settings/ModelPicker';
import { useApp } from '@/lib/app-context';
import { clampOpacity, OVERLAY_OPACITY } from '@/lib/utils';
import { DEFAULT_SETTINGS, type DesktopSettings } from '@/platform';
import type { ProviderInfo, Providers } from '@/types/api';

export function SettingsPage() {
  const { api, platform } = useApp();
  const [settings, setSettings] = useState<DesktopSettings>(DEFAULT_SETTINGS);
  const [providers, setProviders] = useState<Providers | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    platform.getSettings().then(setSettings, () => undefined);
    api.providers().then(setProviders, (err: Error) => setError(err.message));
  }, [api, platform]);

  async function changeOpacity(value: number) {
    const overlayOpacity = clampOpacity(value);
    setSettings(await platform.setSettings({ overlayOpacity }));
    await platform.setOverlayOpacity(overlayOpacity);
  }

  async function toggleAutoStart(autoStartOnMeeting: boolean) {
    setSettings(await platform.setSettings({ autoStartOnMeeting }));
  }

  return (
    <main className="mx-auto grid max-w-2xl gap-6 p-6">
      <Link to="/" className="text-sm text-muted-foreground hover:underline">
        ← All sessions
      </Link>

      <Card className="grid gap-4">
        <CardTitle>Overlay</CardTitle>
        <div className="grid gap-2">
          <Label htmlFor="opacity">Opacity: {Math.round(settings.overlayOpacity * 100)}%</Label>
          <Slider
            id="opacity"
            aria-label="Overlay opacity"
            min={OVERLAY_OPACITY.min}
            max={OVERLAY_OPACITY.max}
            step={0.05}
            value={[settings.overlayOpacity]}
            onValueChange={([value]) => void changeOpacity(value ?? OVERLAY_OPACITY.default)}
          />
          <Button variant="ghost" size="sm" className="w-fit" onClick={() => void changeOpacity(OVERLAY_OPACITY.default)}>
            Reset to 85%
          </Button>
        </div>
      </Card>

      <Card className="grid gap-3">
        <CardTitle>Audio</CardTitle>
        <MicrophonePicker
          value={settings.micDeviceId}
          onChange={(micDeviceId) => void platform.setSettings({ micDeviceId }).then(setSettings)}
        />
        <CardDescription>
          The other side of the call is captured from your computer's sound output (Windows). Applies the next time a session starts.
        </CardDescription>
      </Card>

      <Card className="grid gap-3">
        <CardTitle>Meetings</CardTitle>
        <label className="flex items-start gap-3 text-sm">
          <input type="checkbox" checked={settings.autoStartOnMeeting} onChange={(e) => void toggleAutoStart(e.target.checked)} />
          <span>
            Start automatically when a meeting is detected
            <CardDescription>
              Off by default: InterviewCopilot asks first. Some places require everyone's consent before a call is transcribed.
            </CardDescription>
          </span>
        </label>
      </Card>

      <ModelPicker />

      <Card className="grid gap-4">
        <CardTitle>Providers</CardTitle>
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        {providers && (
          <>
            <ProviderList title="Speech-to-text" providers={providers.speechToText} />
            <ProviderList title="AI model" providers={providers.llm} />
          </>
        )}
      </Card>
    </main>
  );
}

function ProviderList({ title, providers }: { title: string; providers: ProviderInfo[] }) {
  return (
    <section className="grid gap-2">
      <h3 className="font-medium">{title}</h3>
      {providers.map((provider) => (
        <div key={provider.name} className="grid gap-2 rounded border border-border p-3">
          <div className="text-sm">
            {provider.displayName} {provider.isDefault && <span className="text-muted-foreground">(default)</span>}
          </div>
          {provider.requiresApiKey && <ApiKeyField provider={provider.name} />}
        </div>
      ))}
    </section>
  );
}

/** BYOK: the key goes to the OS keychain through the desktop app, never to a file or the backend DB. */
function ApiKeyField({ provider }: { provider: string }) {
  const { platform } = useApp();
  const [key, setKey] = useState('');
  const [status, setStatus] = useState<string | null>(null);

  async function save() {
    try {
      await platform.setApiKey(provider, key);
      setKey('');
      setStatus('Saved to your OS keychain.');
    } catch (err) {
      setStatus(err instanceof Error ? err.message : 'Could not save the key.');
    }
  }

  return (
    <div className="flex gap-2">
      <Input type="password" aria-label={`${provider} API key`} placeholder="Your API key" value={key} onChange={(e) => setKey(e.target.value)} />
      <Button size="sm" disabled={!key} onClick={() => void save()}>
        Save
      </Button>
      {status && <span className="self-center text-xs text-muted-foreground">{status}</span>}
    </div>
  );
}
