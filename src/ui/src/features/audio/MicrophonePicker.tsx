import { useEffect, useState } from 'react';
import { Label, Select } from '@/components/ui/form';

export interface Microphone {
  deviceId: string;
  label: string;
}

/** Lists audio inputs with the standard Web API (works in Electron and in a browser). */
export async function listMicrophones(devices: Pick<MediaDevices, 'enumerateDevices'> | undefined = navigator.mediaDevices): Promise<Microphone[]> {
  if (!devices) {
    return [];
  }
  const all = await devices.enumerateDevices();
  return all
    .filter((d) => d.kind === 'audioinput' && d.deviceId !== 'default' && d.deviceId !== 'communications' && d.deviceId !== '')
    .map((d, index) => ({ deviceId: d.deviceId, label: d.label || `Microphone ${index + 1}` }));
}

/** Settings → Audio: choose which microphone is captured (null = OS default). */
export function MicrophonePicker({
  value,
  onChange,
  load = listMicrophones,
}: {
  value: string | null;
  onChange: (deviceId: string | null) => void;
  load?: () => Promise<Microphone[]>;
}) {
  const [microphones, setMicrophones] = useState<Microphone[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    load().then(setMicrophones, () => setError("Couldn't list microphones."));
  }, [load]);

  const missing = value !== null && microphones.length > 0 && !microphones.some((m) => m.deviceId === value);
  return (
    <div className="grid gap-2">
      <Label htmlFor="microphone">Microphone</Label>
      <Select id="microphone" value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : e.target.value)}>
        <option value="">System default</option>
        {microphones.map((mic) => (
          <option key={mic.deviceId} value={mic.deviceId}>
            {mic.label}
          </option>
        ))}
        {missing && <option value={value}>Saved microphone (not connected)</option>}
      </Select>
      {error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  );
}
