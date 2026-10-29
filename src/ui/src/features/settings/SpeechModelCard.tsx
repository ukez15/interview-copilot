import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import type { WhisperModel } from '@/types/api';
import { approximateSize, downloadPercent, formatMegabytes, speechModelLabel, useSpeechModels } from './useWhisperModel';

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

/** One offered model: its size and state, with Download / Retry / Use this (#85). */
function SpeechModelRow({
  model,
  inUse,
  onDownload,
  onChoose,
}: {
  model: WhisperModel;
  inUse: boolean;
  onDownload: () => void;
  onChoose: () => void;
}) {
  const label = speechModelLabel(model.model);
  return (
    <li aria-label={label} className="grid gap-2 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-sm font-medium">{label}</span>
        <span className="text-xs text-muted-foreground">{approximateSize(model)}</span>
      </div>
      {model.state === 'ready' &&
        (inUse ? (
          <CardDescription>In use. Ready. Speech is transcribed on this computer.</CardDescription>
        ) : (
          <Button variant="outline" size="sm" className="w-fit" onClick={onChoose}>
            Use this
          </Button>
        ))}
      {model.state === 'missing' && (
        <>
          <CardDescription>
            {inUse ? "Not downloaded yet. It is needed to transcribe the call, and is downloaded once." : 'Not downloaded.'}
          </CardDescription>
          <Button size="sm" className="w-fit" onClick={onDownload}>
            Download
          </Button>
        </>
      )}
      {model.state === 'downloading' && <DownloadProgress model={model} />}
      {model.state === 'failed' && (
        <>
          <p role="alert" className="text-sm text-destructive">
            {model.message ?? 'The download failed.'}
          </p>
          <Button size="sm" className="w-fit" onClick={onDownload}>
            Retry
          </Button>
        </>
      )}
    </li>
  );
}

/**
 * Settings card: the local speech models (#19, #85). Each shows its size; a missing one downloads only on click,
 * and a ready one can be put in use. The larger model is more accurate but slower on the CPU.
 */
export function SpeechModelCard() {
  const { models, active, error, download, choose } = useSpeechModels();

  return (
    <Card className="grid gap-3">
      <CardTitle>Speech model</CardTitle>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {models && (
        <ul className="grid gap-2">
          {models.map((model) => (
            <SpeechModelRow
              key={model.model}
              model={model}
              inUse={model.model === active}
              onDownload={() => void download(model.model)}
              onChoose={() => void choose(model.model)}
            />
          ))}
        </ul>
      )}
    </Card>
  );
}
