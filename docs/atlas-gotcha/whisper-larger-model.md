# A larger speech model in Settings — ATLAS + GOTCHA brief

Status: **awaiting the owner's OK.** No code until then. Issue #85.
Permanent rules: `GOTCHA-PROJECT.md` and `CLAUDE.md`.

## User story

As a user whose answers transcribe poorly, I want to choose a more accurate local speech model in
Settings, so practice and live calls hear me better without paying for a cloud service.

- GIVEN Settings, WHEN I choose the larger model, THEN I see its size and that it's slower, and it
  downloads only after I confirm.
- GIVEN it's downloaded and chosen, WHEN I start a session, THEN transcription uses it.
- GIVEN I never choose it, WHEN I use the app, THEN nothing changes: small.en, no extra download.

---

## ATLAS

### [A] Architect
- **Purpose:** better free, offline transcription for users who can spare the CPU.
- **Why not just swap the default:** a larger model is slower on a CPU and competes with Ollama. That
  competition is what made practice questions take 90 s. small.en stays the default.
- **Constraints:** zero cost and local. Downloads are user-started (today's Settings card already works
  this way for small.en). Whisper.net's downloader already fetches ggml models, including quantized ones,
  from the public Hugging Face location. Clean Architecture: the choice is a preference, and the Infrastructure
  adapter loads the file.
- **Out of scope:** GPU support, cloud speech-to-text (Deepgram stays as it is), and changing how speech is cut.

### [T] Trace
1. Settings → Speech model shows the current model and the larger option with size and speed notes.
2. The user picks it → confirm → download with progress (the existing card's flow).
3. Once ready, it becomes the active model and is saved as the user's preference. Sessions started after
   that use it; a session already live keeps its model.
4. Switching back to small.en needs no download if its file is still there.

### [L] Link
| From | To | Method | Contract | Failure mode |
|---|---|---|---|---|
| UI | API | Speech model status and download (existing), plus the chosen model (question 3) | adds a model name | download error shown on the card, as today |
| WhisperModelManager | Whisper.net downloader | `GetGgmlModelAsync(type, quantization)` | new types in `WhisperModelCatalog` | network error → retry from the card |
| Whisper provider | model file | loads the chosen file | — | file missing → "Download the speech model in Settings" (as today) |

### [A] Assemble
Tests first → catalog entry and preference → manager and provider pick the chosen file → Settings card → checks → review → PR.

### [S] Stress-test
- **Disk:** the larger file sits next to small.en (about 0.5 GB more), shown on the card.
- **Speed:** measured on the owner's machine with the test installer before it's recommended in the UI.
- **Failure:** a half-finished download never becomes active; small.en keeps working.

---

## GOTCHA

=== GOALS ===
- A user can choose and download a larger local model; small.en stays the default.
- Coverage of at least 75% per package; CI green.

=== ORCHESTRATION ===
1. Failing tests. 2. Catalog and preference. 3. Manager and provider. 4. Settings card. 5. Checks → review → PR.

=== TOOLS ===
Whisper.net (existing, its ggml downloader), .NET 10, React 19.3.1, Vitest 5.0.5. No new packages.

=== CONTEXT ===
Owner's machine: CPU only; Ollama `llama3.2:3b` reads about 41 tokens/s. Backend tests run in CI only.

=== HEURISTICS ===
DO: download only on the user's click (yes/no). Keep small.en the default (yes/no).
DON'T: delete a model file without asking (yes/no). Switch a live session's model mid-call (yes/no).

=== ARGS ===
- Model file names and sizes per question 1; quantization Q5_0.

---

## Open questions for the owner

1. **Which larger model?** **Recommended:** `large-v3-turbo`, quantized (Q5_0, about 550 MB). It's
   close to the full large model's accuracy, with a much smaller download. Or `medium.en` quantized
   (Q5_0, about 540 MB): English-only and a bit lighter on the CPU. Both are slower than small.en
   (466 MB) on a CPU. I'd measure on your machine before writing speed notes into the UI.
2. **Where in Settings?** **Recommended:** on the existing "Speech model" card, as a choice between
   "Standard (small.en, faster)" and "More accurate (slower)", with the size shown.
3. **Per user or per machine?** **Recommended:** per user, saved with the other preferences (like the chat
   model), so the backend picks the file for each session.
4. **Old model file:** **Recommended:** keep small.en on disk, so switching back is instant; no automatic
   delete.
