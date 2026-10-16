import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import type { WhisperModel } from '@/types/api';
import { downloadPercent, formatMegabytes, useWhisperModel } from './useWhisperModel';

export function DownloadProgress({ model }: { model: WhisperModel }) {
  const percent = downloadPercent(model);
  return (
    <div className="grid gap-1">
      <div
        role="progressbar"
        aria-label="Speech model download"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        className="h-2 w-full overflow-hidden rounded bg-muted"
      >
        <div className="h-full bg-primary" style={{ width: `${percent}%` }} />
      </div>
      <span className="text-xs text-muted-foreground">
        {formatMegabytes(model.bytesDownloaded)} downloaded ({percent}%)
      </span>
    </div>
  );
}

/** Settings card: whether the local speech model is present, with a Download / Retry button and progress. */
export function SpeechModelCard() {
  const { model, error, download } = useWhisperModel();

  return (
    <Card className="grid gap-3">
      <CardTitle>Speech model</CardTitle>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {model?.state === 'ready' && <CardDescription>Ready. Speech is transcribed on this computer.</CardDescription>}
      {model?.state === 'missing' && (
        <>
          <CardDescription>
            The speech model (about 466 MB) isn't downloaded yet. It is needed to transcribe the call, and is downloaded once.
          </CardDescription>
          <Button className="w-fit" onClick={() => void download()}>
            Download
          </Button>
        </>
      )}
      {model?.state === 'downloading' && <DownloadProgress model={model} />}
      {model?.state === 'failed' && (
        <>
          <p role="alert" className="text-sm text-destructive">
            {model.message ?? 'The download failed.'}
          </p>
          <Button className="w-fit" onClick={() => void download()}>
            Retry
          </Button>
        </>
      )}
    </Card>
  );
}
