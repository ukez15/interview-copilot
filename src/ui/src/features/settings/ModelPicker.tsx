import { useEffect, useState } from 'react';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { Label } from '@/components/ui/form';
import { Select } from '@/components/ui/select';
import { useApp } from '@/lib/app-context';
import type { OllamaModel } from '@/types/api';

const MB = 1024 * 1024;
const GB = 1024 * MB;

export function formatModelSize(bytes: number): string {
  return bytes >= GB ? `${(bytes / GB).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / MB))} MB`;
}

/** Lets the user pick any model already pulled into Ollama; the choice is stored on the account (#31). */
export function ModelPicker() {
  const { api } = useApp();
  const [models, setModels] = useState<OllamaModel[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [defaultModel, setDefaultModel] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    const message = (err: unknown) => (err instanceof Error ? err.message : 'Could not load the model list.');
    api.getPreferences().then(
      (prefs) => {
        setSelected(prefs.llmModel);
        setDefaultModel(prefs.defaultLlmModel ?? null);
      },
      (err: unknown) => setError(message(err)),
    );
    api.ollamaModels().then(setModels, (err: unknown) => setError(message(err)));
  }, [api]);

  async function change(value: string) {
    const llmModel = value === '' ? null : value;
    setStatus(null);
    try {
      const saved = await api.setLlmModel(llmModel);
      setSelected(saved.llmModel);
      setError(null);
      setStatus('Saved.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the model.');
    }
  }

  const missing = selected !== null && !models.some((m) => m.name === selected);

  return (
    <Card className="grid gap-3">
      <CardTitle>Ollama model</CardTitle>
      <div className="grid gap-2">
        <Label htmlFor="llm-model">AI model</Label>
        <Select
          id="llm-model"
          value={selected ?? ''}
          onValueChange={(v) => void change(v)}
          options={[
            { value: '', label: defaultModel ? `Default (${defaultModel})` : 'Default model' },
            ...(missing ? [{ value: selected, label: `${selected} (not installed)` }] : []),
            ...models.map((model) => ({ value: model.name, label: `${model.name} (${formatModelSize(model.sizeBytes)})` })),
          ]}
        />
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {status && <p className="text-xs text-muted-foreground">{status}</p>}
      <CardDescription>
        Used for manual and automatic answers. Models you have already pulled with Ollama appear here.
      </CardDescription>
    </Card>
  );
}
