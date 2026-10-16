import { Button } from '@/components/ui/button';
import { DownloadProgress } from '@/features/settings/SpeechModelCard';
import { useWhisperModel } from '@/features/settings/useWhisperModel';

/** Shown on the sessions page while the speech model isn't ready; the download starts only on click. */
export function SpeechModelBanner() {
  const { model, download } = useWhisperModel();

  if (!model || model.state === 'ready') {
    return null;
  }

  return (
    <div role="status" aria-label="Speech model" className="grid gap-2 rounded-[var(--radius-lg)] border border-border bg-muted p-3 text-sm">
      {model.state === 'downloading' ? (
        <>
          <span>Downloading the speech model…</span>
          <DownloadProgress model={model} />
        </>
      ) : (
        <>
          <span>
            {model.state === 'failed'
              ? (model.message ?? 'The speech model download failed.')
              : 'Download the speech model (about 466 MB) so InterviewCopilot can transcribe calls.'}
          </span>
          <Button size="sm" className="w-fit" onClick={() => void download()}>
            {model.state === 'failed' ? 'Retry' : 'Download'}
          </Button>
        </>
      )}
    </div>
  );
}
