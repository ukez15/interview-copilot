import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { fakeApi, json, renderWithApp } from '@/test/helpers';
import { formatModelSize, ModelPicker } from './ModelPicker';
import { chooseOption, closeSelect, openSelect } from '@/test/select';

const models = [
  { name: 'llama3.2:3b', sizeBytes: 2_019_393_189, modifiedAt: '2026-09-01T10:00:00Z' },
  { name: 'tiny:1b', sizeBytes: 300_000, modifiedAt: '2026-09-02T10:00:00Z' },
];

function setup(saved: string | null, putResponse?: Response) {
  return fakeApi((url, init) => {
    if (url.endsWith('/api/providers/ollama/models')) return json(models);
    if (init.method === 'PUT') return putResponse ?? json({ llmModel: JSON.parse(String(init.body)).llmModel });
    return json({ llmModel: saved, defaultLlmModel: 'llama3.2:3b' });
  });
}

describe('ModelPicker', () => {
  it('names the configured default model', async () => {
    const { api } = setup(null);
    renderWithApp(<ModelPicker />, { api });

    const models = await openSelect(userEvent, 'AI model');
    expect(await models.findByRole('option', { name: 'Default (llama3.2:3b)' })).toBeInTheDocument();
  });

  it('lists installed models with sizes and selects the saved one', async () => {
    const { api } = setup('llama3.2:3b');
    renderWithApp(<ModelPicker />, { api });

    const models = await openSelect(userEvent, 'AI model');
    expect(await models.findByRole('option', { name: 'llama3.2:3b (1.9 GB)' })).toBeInTheDocument();
    expect(models.getByRole('option', { name: 'tiny:1b (1 MB)' })).toBeInTheDocument();
    await closeSelect(userEvent);
    await waitFor(() => expect(screen.getByLabelText('AI model')).toHaveTextContent('llama3.2:3b (1.9 GB)'));
  });

  it('saves the choice on change and back to default with null', async () => {
    const { api, fetchImpl } = setup(null);
    renderWithApp(<ModelPicker />, { api });

    await chooseOption(userEvent, 'AI model', /tiny:1b/);
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    await chooseOption(userEvent, 'AI model', /^Default/);

    await waitFor(() => {
      const bodies = fetchImpl.mock.calls.filter((c) => c[1]?.method === 'PUT').map((c) => JSON.parse(String(c[1]!.body)));
      expect(bodies).toEqual([{ llmModel: 'tiny:1b' }, { llmModel: null }]);
    });
  });

  it('keeps a saved model that is no longer installed visible', async () => {
    const { api } = setup('gone:7b');
    renderWithApp(<ModelPicker />, { api });

    await waitFor(() => expect(screen.getByLabelText('AI model')).toHaveTextContent('gone:7b (not installed)'));
    const models = await openSelect(userEvent, 'AI model');
    expect(await models.findByRole('option', { name: 'gone:7b (not installed)' })).toBeInTheDocument();
  });

  it('shows the actionable message when Ollama is unreachable', async () => {
    const { api } = fakeApi((url) =>
      url.endsWith('/ollama/models')
        ? json({ detail: "Can't reach Ollama. Make sure it's installed and running." }, 503)
        : json({ llmModel: null }),
    );
    renderWithApp(<ModelPicker />, { api });

    expect(await screen.findByRole('alert')).toHaveTextContent("Can't reach Ollama. Make sure it's installed and running.");
  });

  it('shows an error when saving fails', async () => {
    const { api } = setup(null, json({ detail: 'Model names can be at most 200 characters.' }, 400));
    renderWithApp(<ModelPicker />, { api });

    await chooseOption(userEvent, 'AI model', /tiny:1b/);

    expect(await screen.findByRole('alert')).toHaveTextContent('at most 200');
  });
});

describe('formatModelSize', () => {
  it('uses GB for large and MB for small models', () => {
    expect(formatModelSize(4_683_087_332)).toBe('4.4 GB');
    expect(formatModelSize(50 * 1024 * 1024)).toBe('50 MB');
  });
});
