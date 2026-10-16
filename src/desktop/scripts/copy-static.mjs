// Copies non-TypeScript assets for the capture window next to its compiled scripts.
import { copyFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const target = path.join(root, 'dist-electron', 'renderer', 'capture');
await mkdir(target, { recursive: true });
await copyFile(path.join(root, 'static', 'capture.html'), path.join(target, 'capture.html'));
