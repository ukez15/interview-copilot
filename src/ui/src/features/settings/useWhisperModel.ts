import { useCallback, useEffect, useState } from 'react';
import { useApp } from '@/lib/app-context';
import { ApiError } from '@/lib/api';
import type { WhisperModel } from '@/types/api';

/** The default model (ggml-small.en) is about 466 MB; the backend may not know the size up front. */
export const EXPECTED_MODEL_BYTES = 466 * 1024 * 1024;
export const WHISPER_POLL_MS = 1000;

export interface WhisperModelHandle {
  model: WhisperModel | null;
  error: string | null;
  download: () => Promise<void>;
}

/** Loads the speech model state and polls it every second while it downloads. Download starts only on request. */
export function useWhisperModel(): WhisperModelHandle {
  const { api } = useApp();
  const [model, setModel] = useState<WhisperModel | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setModel(await api.getWhisperModel());
      setError(null);
    } catch {
      setError("Couldn't check the speech model.");
    }
  }, [api]);

  useEffect(() => {
    api.getWhisperModel().then(setModel, () => setError("Couldn't check the speech model."));
  }, [api]);

  const downloading = model?.state === 'downloading';
  useEffect(() => {
    if (!downloading) {
      return;
    }
    const timer = setInterval(() => void refresh(), WHISPER_POLL_MS);
    return () => clearInterval(timer);
  }, [downloading, refresh]);

  const download = useCallback(async () => {
    try {
      setModel(await api.downloadWhisperModel());
      setError(null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        await refresh();
      } else {
        setError(err instanceof Error ? err.message : 'Could not start the download.');
      }
    }
  }, [api, refresh]);

  return { model, error, download };
}

export function formatMegabytes(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(0)} MB`;
}

/** Percent done, using the expected size when the backend doesn't know the total. */
export function downloadPercent(model: WhisperModel): number {
  const total = model.totalBytes && model.totalBytes > 0 ? model.totalBytes : EXPECTED_MODEL_BYTES;
  return Math.min(100, Math.round((model.bytesDownloaded / total) * 100));
}
