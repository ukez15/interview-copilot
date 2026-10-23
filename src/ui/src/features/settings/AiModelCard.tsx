import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { Input, Label, Select } from '@/components/ui/form';
import { useApp } from '@/lib/app-context';
import type { CloudModel, Preferences, ProviderInfo } from '@/types/api';
import { ModelPicker } from './ModelPicker';

/**
 * Settings → AI model (#30): pick the chat provider. Ollama keeps its installed-model picker; the cloud
 * providers get a key field (OS keychain via the desktop app), a model list loaded from the provider, and Save.
 */
export function AiModelCard() {
  const { api } = useApp();
  const [providers, setProviders] = useState<ProviderInfo[]>([]);
  const [prefs, setPrefs] = useState<Preferences | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api.providers(), api.getPreferences()]).then(
      ([all, saved]) => {
        setProviders(all.llm);
        setPrefs(saved);
        setSelected(saved.llmProvider ?? saved.defaultLlmProvider);
      },
      (err: unknown) => setError(err instanceof Error ? err.message : 'Could not load the AI providers.'),
    );
  }, [api]);

  async function change(name: string) {
    const provider = providers.find((p) => p.name === name);
    if (!provider) {
      return;
    }
    setError(null);
    if (provider.isLocal) {
      // Going back to a local provider needs no key or model list, so it is saved right away.
      try {
        setPrefs(await api.setLlmChoice(provider.name, null));
        setSelected(provider.name);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Could not save the provider.');
      }
      return;
    }
    setSelected(provider.name);
  }

  const current = providers.find((p) => p.name === selected);
  const cloud = current && !current.isLocal ? current : null;

  return (
    <>
      <Card className="grid gap-3">
        <CardTitle>AI provider</CardTitle>
        <div className="grid gap-2">
          <Label htmlFor="llm-provider">Provider</Label>
          <Select id="llm-provider" value={selected ?? ''} disabled={providers.length === 0} onChange={(e) => void change(e.target.value)}>
            {providers.map((provider) => (
              <option key={provider.name} value={provider.name}>
                {provider.displayName}
              </option>
            ))}
          </Select>
        </div>
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        {cloud && prefs ? (
          <CloudModelSettings key={cloud.name} provider={cloud} prefs={prefs} onSaved={setPrefs} />
        ) : (
          <CardDescription>Runs on this computer and costs nothing. Nothing leaves your machine.</CardDescription>
        )}
      </Card>
      {current?.isLocal && <ModelPicker />}
    </>
  );
}

interface CloudModelSettingsProps {
  provider: ProviderInfo;
  prefs: Preferences;
  onSaved: (prefs: Preferences) => void;
}

function CloudModelSettings({ provider, prefs, onSaved }: CloudModelSettingsProps) {
  const { api, platform } = useApp();
  const desktop = platform.kind === 'desktop';
  const name = provider.displayName.replace(/ \(.*\)$/, '');
  const [hasKey, setHasKey] = useState(false);
  const [keyInput, setKeyInput] = useState('');
  const [models, setModels] = useState<CloudModel[]>([]);
  const [model, setModel] = useState(prefs.llmProvider === provider.name ? (prefs.llmModel ?? '') : '');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    platform.getApiKey(provider.name).then(
      (key) => setHasKey(Boolean(key)),
      () => setHasKey(false),
    );
  }, [platform, provider.name]);

  function fail(err: unknown, fallback: string) {
    setStatus(null);
    setError(err instanceof Error ? err.message : fallback);
  }

  async function saveKey() {
    try {
      await platform.setApiKey(provider.name, keyInput);
      setKeyInput('');
      setHasKey(true);
      setError(null);
      setStatus('Key saved to your OS keychain.');
    } catch (err) {
      fail(err, 'Could not save the key.');
    }
  }

  async function deleteKey() {
    try {
      await platform.deleteApiKey(provider.name);
      setHasKey(false);
      setModels([]);
      setError(null);
      setStatus('Key deleted.');
    } catch (err) {
      fail(err, 'Could not delete the key.');
    }
  }

  async function loadModels() {
    setLoading(true);
    setStatus(null);
    try {
      const key = await platform.getApiKey(provider.name);
      if (!key) {
        setHasKey(false);
        setError(`Add your ${name} API key first.`);
        return;
      }
      setModels(await api.cloudModels(provider.name, key));
      setError(null);
    } catch (err) {
      fail(err, 'Could not load the models.');
    } finally {
      setLoading(false);
    }
  }

  async function saveChoice() {
    try {
      onSaved(await api.setLlmChoice(provider.name, model));
      setError(null);
      setStatus('Saved.');
    } catch (err) {
      fail(err, 'Could not save the model.');
    }
  }

  const listed = models.some((m) => m.id === model);

  return (
    <div className="grid gap-3">
      {!desktop && (
        <p className="text-sm text-muted-foreground">Cloud API keys are stored in your OS keychain, so they need the desktop app.</p>
      )}
      <div className="grid gap-2">
        <Label htmlFor="cloud-api-key">API key</Label>
        <div className="flex gap-2">
          <Input
            id="cloud-api-key"
            type="password"
            autoComplete="off"
            placeholder={hasKey ? 'A key is saved. Paste a new one to replace it' : 'Your API key'}
            value={keyInput}
            disabled={!desktop}
            onChange={(e) => setKeyInput(e.target.value)}
          />
          <Button size="sm" disabled={!desktop || !keyInput} onClick={() => void saveKey()}>
            Save key
          </Button>
          <Button size="sm" variant="ghost" disabled={!hasKey} onClick={() => void deleteKey()}>
            Delete key
          </Button>
        </div>
        <p className="text-xs text-muted-foreground">{hasKey ? 'A key is saved on this computer.' : 'No key saved.'}</p>
      </div>
      <div className="grid gap-2">
        <Label htmlFor="cloud-model">Model</Label>
        <div className="flex gap-2">
          <Select id="cloud-model" value={model} onChange={(e) => setModel(e.target.value)}>
            <option value="">Choose a model</option>
            {model !== '' && !listed && <option value={model}>{model}</option>}
            {models.map((m) => (
              <option key={m.id} value={m.id}>
                {m.displayName}
              </option>
            ))}
          </Select>
          <Button size="sm" variant="ghost" disabled={!hasKey || loading} onClick={() => void loadModels()}>
            Load models
          </Button>
          <Button size="sm" disabled={model === ''} onClick={() => void saveChoice()}>
            Save
          </Button>
        </div>
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {status && <p className="text-xs text-muted-foreground">{status}</p>}
      <CardDescription>
        Your questions and documents are sent to {name} while this is selected. The key stays in your keychain
        and is shared with the backend in memory only, while a session is live.
      </CardDescription>
    </div>
  );
}
