# Copilot latency benchmark (NFR-05)

Time to first answer token (TTFT) and total time for each question, measured through the real HTTP API.

- Date (UTC): 2026-10-26 14:54:55
- Model: llama3.2:3b
- CPU: Intel(R) Core(TM) Ultra 9 288V
- Logical cores: 8
- RAM: 31.6 GiB
- OS: Microsoft Windows 10.0.26300
- Questions: 20 of 20 succeeded

> **Not the reference machine.** NFR-05's reference is 16 GB RAM with no GPU. This run used 32 GB RAM and a
> faster laptop CPU; Ollama ran the model on the CPU only (`ollama ps`: 100% CPU), with no GPU offload. A
> re-run on 16 GB reference hardware is still owed before #24 closes.

## Results

| # | Type | TTFT (ms) | Total (ms) | Status |
|---|------|-----------|------------|--------|
| 1 | Behavioral | 834 | 9329 | ok |
| 2 | Behavioral | 884 | 6453 | ok |
| 3 | Behavioral | 421 | 6209 | ok |
| 4 | HowDoYou | 637 | 6456 | ok |
| 5 | HowDoYou | 264 | 5570 | ok |
| 6 | Situational | 750 | 8471 | ok |
| 7 | Situational | 378 | 8527 | ok |
| 8 | TechnicalKnowledge | 1119 | 14218 | ok |
| 9 | TechnicalKnowledge | 625 | 9874 | ok |
| 10 | TechnicalKnowledge | 464 | 14521 | ok |
| 11 | SystemDesign | 770 | 8814 | ok |
| 12 | SystemDesign | 365 | 16602 | ok |
| 13 | Coding | 808 | 12568 | ok |
| 14 | Coding | 418 | 9634 | ok |
| 15 | Coding | 557 | 6566 | ok |
| 16 | TellMeAboutYourself | 116 | 6642 | ok |
| 17 | TellMeAboutYourself | 338 | 7872 | ok |
| 18 | Experience | 378 | 6959 | ok |
| 19 | Experience | 589 | 8598 | ok |
| 20 | Experience | 436 | 7919 | ok |

## Summary (successful runs, nearest-rank percentiles)

| Metric | P50 (ms) | P95 (ms) |
|--------|----------|----------|
| Time to first token | 464 | 884 |
| Total time | 8471 | 14521 |

NFR-05 verdict: **PASS** (P95 TTFT 884 ms against the 5000 ms limit).
