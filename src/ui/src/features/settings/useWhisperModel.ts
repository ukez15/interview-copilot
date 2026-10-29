import { useCallback, useEffect, useState } from 'react';
import { useApp } from '@/lib/app-context';
import { ApiError } from '@/lib/api';
import type { WhisperModel } from '@/types/api';

/** Used for progress when neither the backend's total nor an approximate size is known (small.en is about 466 MB). */
export const EXPECTED_MODEL_BYTES = 466 * 1024 * 1024;
export const WHISPER_POLL_MS = 1000;

/** How each offered speech model is named in Settings (#85); others show their file name. */
export const SPEECH_MODEL_LABELS: Record<string, string> = {
  'ggml-small.en.bin': 'Standard (small.en, faster)',
  'ggml-large-v3-turbo-q5_0.bin': 'More accurate (slower)',
};

export function speechModelLabel(model: string): string {
  return SPEECH_MODEL_LABELS[model] ?? model;
}

export interface SpeechModelsHandle {
  models: WhisperModel[] | null;
  /** The model sessions use: the chosen one, else the default (the first offered). */
  active: string | null;
  error: string | null;
  download: (model: string) => Promise<void>;
  choose: (model: string) => Promise<void>;
}

/**
 * Loads the offered speech models and which one is in use, and polls every second while one downloads (#85).
 * Downloads start only on request.
 */
export function useSpeechModels(): SpeechModelsHandle {
  const { api } = useApp();
  const [models, setModels] = useState<WhisperModel[] | null>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setModels(await api.speechModels());
      setError(null);
    } catch {
      setError("Couldn't check the speech model.");
    }
  }, [api]);

  useEffect(() => {
    api.speechModels().then(setModels, () => setError("Couldn't check the speech model."));
    api.getPreferences().then(
      (prefs) => setChosen(prefs.speechModel ?? null),
      () => undefined, // without preferences the default is in use
    );
  }, [api]);

  const downloading = models?.some((m) => m.state === 'downloading') ?? false;
  useEffect(() => {
    if (!downloading) {
      return;
    }
    const timer = setInterval(() => void refresh(), WHISPER_POLL_MS);
    return () => clearInterval(timer);
  }, [downloading, refresh]);

  const download = useCallback(
    async (model: string) => {
      try {
        const started = await api.downloadSpeechModel(model);
        setModels((previous) => previous?.map((m) => (m.model === started.model ? started : m)) ?? [started]);
        setError(null);
      } catch (err) {
        if (err instanceof ApiError && err.status === 409) {
          await refresh();
        } else {
          setError(err instanceof Error ? err.message : 'Could not start the download.');
        }
      }
    },
    [api, refresh],
  );

  const choose = useCallback(
    async (model: string) => {
      try {
        const prefs = await api.setSpeechModel(model);
        setChosen(prefs.speechModel ?? null);
        setError(null);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Couldn't switch the speech model.");
      }
    },
    [api],
  );

  const active = chosen ?? models?.[0]?.model ?? null;
  return { models, active, error, download, choose };
}

export interface WhisperModelHandle {
  model: WhisperModel | null;
  error: string | null;
  download: () => Promise<void>;
}

/** The speech model sessions use, for the sessions-page banner. */
export function useWhisperModel(): WhisperModelHandle {
  const { models, active, error, download } = useSpeechModels();
  const model = models?.find((m) => m.model === active) ?? null;
  const downloadActive = useCallback(async () => {
    if (active) {
      await download(active);
    }
  }, [active, download]);
  return { model, error, download: downloadActive };
}

export function formatMegabytes(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(0)} MB`;
}

/** Percent done, using the approximate (or expected) size when the backend doesn't know the total. */
export function downloadPercent(model: WhisperModel): number {
  const total = model.totalBytes && model.totalBytes > 0 ? model.totalBytes : (model.approximateBytes ?? EXPECTED_MODEL_BYTES);
  return Math.min(100, Math.round((model.bytesDownloaded / total) * 100));
}

/** "about 466 MB", or the default model's size when the backend doesn't say. */
export function approximateSize(model: WhisperModel): string {
  return `about ${formatMegabytes(model.approximateBytes ?? EXPECTED_MODEL_BYTES)}`;
}
