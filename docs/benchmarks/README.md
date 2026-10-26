# Latency benchmark (NFR-05)

NFR-05 says the copilot must start answering within **2-5 seconds** on the reference hardware (16 GB RAM,
no GPU). This benchmark measures it through the real HTTP API and writes `docs/benchmarks/latency.md`.

It is not part of CI: CI runners are not the reference hardware. Run it by hand on the reference machine.

## What it does

1. Registers a throwaway account (`benchmark-<UTC yyyyMMddHHmmss>`, random password generated at run time),
   signs in, creates a Live Interview session (Balanced length, no framework) and starts it.
2. Sends one warm-up question (not counted), so the model is loaded before timing starts.
3. Sends the 20 questions in `questions.json` one after another, at least two for each of the 8 question types.
4. For each question it measures, from sending the request:
   - **TTFT**: time until the first `delta` event (the first answer token);
   - **Total**: time until the `done` event.
   A question that errors or takes longer than 60 s is recorded as failed and left out of the statistics.
5. Ends the session and writes the report.

## Prerequisites

- [Ollama](https://ollama.com) running, with the model pulled, for example `ollama pull llama3.2:3b`.
- The backend running on the same machine: `pnpm dev:backend` (listens on `http://127.0.0.1:5480`).
- Close heavy applications first, so the numbers reflect the app and not your other work.

## Run it

From the repository root:

```bash
dotnet run --project src/backend/tools/InterviewCopilot.Benchmark -c Release -- --model llama3.2:3b
```

Options (all optional):

| Option | Default | Meaning |
|--------|---------|---------|
| `--base-url` | `http://127.0.0.1:5480` | Where the backend is listening. |
| `--questions` | `questions.json` next to the executable | A different question set (same JSON shape). |
| `--output` | `docs/benchmarks/latency.md` | Report path, relative to the current directory. Overwritten each run. |
| `--model` | `llama3.2:3b` | A label written into the report. The backend uses its own configured model; this does not switch it. |

Exit code: `0` once the measurement ran, **even if the verdict is FAIL**. Non-zero only when the backend cannot
be reached or sign-in fails (`Start the backend first: pnpm dev:backend`), or the arguments are invalid.

## Reading the report

- **Header**: date (UTC), model label, CPU, logical cores, RAM, OS. Check that it matches the reference hardware
  before committing the file.
- **Results table**: one row per question with TTFT and total time in milliseconds, and `ok` or `failed: <reason>`.
- **Summary**: P50 and P95 of TTFT and of total time, over successful runs only, using the nearest-rank method
  (the value at rank `ceil(p/100 * n)` of the sorted samples).
- **Verdict**: `PASS` when the **P95 TTFT is 5000 ms or less**. With no successful run the verdict is `FAIL`.
  Look at the number of failed questions too, because they do not count towards the percentiles.

The first run after a reboot can be slower while Ollama loads the model; the warm-up question absorbs most of that.
