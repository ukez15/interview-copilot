# Brief: detect "in a call", not "meeting app running". ATLAS + GOTCHA, compact

Status: **approved by owner** (2026-10-26).

Owner decisions (2026-10-26):
- **D1:** Windows uses the registry mic-in-use signal AND the process must be running.
- **D2:** if the registry can't be read, or the app has no entry, **no prompt** (no process-only fallback).
- **D3:** browser meetings are out of scope.
- **D4:** macOS keeps the process-based check; follow-up issue #59 tracks options 5/6.
- **D5:** prompt again for each call.
- **D6:** ships as a PR after the beta.

Found in the #36 beta smoke test: `watchForMeetings` (`src/desktop/src/main.ts`) asks "meeting
detected, start InterviewCopilot?" whenever Zoom, Teams, Slack, Discord and similar are *running*.
People who keep Teams or Slack open all day get the prompt on every app launch with no call in progress.
README "Known limitations (beta)" lists this.

## Options

| # | Signal | Windows | macOS | Cost | Verdict |
|---|--------|---------|-------|------|---------|
| 1 | **Meeting app is using the microphone** (CapabilityAccessManager ConsentStore) | Registry, per app, no admin, no native code | n/a | `reg query` every 5 s | **Recommended for Windows** |
| 2 | WASAPI capture sessions (`IAudioSessionManager2`, per-process capture sessions) | Accurate, live | n/a | Native addon or helper exe to build, sign and ship | Same answer as #1, much more work |
| 3 | Window titles ("Zoom Meeting", Teams call window) | `Get-Process` MainWindowTitle | Needs Screen Recording / Accessibility permission | Cheap | Fragile: titles change per app version and language; Teams/Slack titles don't reliably mark a call |
| 4 | Keep process check, just show the prompt less | — | — | Trivial | Doesn't fix the false positive |
| 5 | macOS per-process input (`kAudioProcessPropertyIsRunningInput`, macOS 14.2+) | n/a | Accurate, per app | Small Swift helper; untested without a Mac | Future, see macOS below |
| 6 | macOS "default input device is running somewhere" (`kAudioDevicePropertyDeviceIsRunningSomewhere`) AND meeting app running | n/a | Any app using the mic | Small Swift helper | Fallback for macOS < 14.2 |

### What option 1 reads (checked on this PC)

`HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone`

- Store apps are subkeys named by package family, e.g. `MSTeams_8wekyb3d8bbwe` (new Teams).
- Desktop apps are under `NonPackaged`, keyed by exe path with `#` for `\`, e.g.
  `C:#Users#…#AppData#Roaming#Zoom#bin#Zoom.exe`.
- Each has `LastUsedTimeStart` / `LastUsedTimeStop` (FILETIME). While the app holds the mic,
  `LastUsedTimeStop` is `0`. This is the same data behind the Windows "app is using your microphone"
  indicator, and it's readable by the user without admin.

Known caveats:
- **Mute.** Zoom and Teams normally keep the mic open while muted, so a muted call still counts. An app
  that fully closes the mic on mute would read as "call ended" until unmuted. To be confirmed in a real call.
- **Stale entry.** If an app crashes while holding the mic, `Stop` can stay `0`. Mitigation: also require
  the process to be running (keep today's `tasklist` check as the AND).
- **Our own capture** writes an entry too (InterviewCopilot's exe). It isn't in `MEETING_APPS`, so it's
  ignored, but tests cover that.
- **Browser meetings** (Meet, Teams web) show up as `chrome.exe` / `msedge.exe`. Treating "browser uses
  the mic" as a meeting would prompt for any site using the mic. Proposed out of scope (owner decision D3).
- Group policy can hide mic access per app; then nothing is recorded and we fall back (decision D2).

## A: Architect

- **Goal:** on Windows, prompt only when a known meeting app is actually holding the microphone, and
  prompt again for the next call after the mic is released (today: once per app launch).
- **Keep:** NFR-12 unchanged. `decide()` / `meetingEventFor()` stay the gate: no capture without the
  user's click unless auto-start is on. Only the *input* to `decide()` changes.
- **Constraints:** zero cost, no admin, no new native dependency, no extra permission prompt.
- **Out of scope:** browser-based meetings, a macOS change in this PR (see macOS), window titles,
  native WASAPI code.

## T: Trace (Windows)

1. Every 5 s (`MEETING_POLL_MS`, unchanged) `watchForMeetings` calls a new `detectActiveMeeting()`.
2. It runs `reg query <ConsentStore\microphone> /s` (`execFile`, `windowsHide`) and the existing `tasklist`.
3. Pure parser: registry output → list of `{ key, start, stop }`; "in use" = `stop === 0` and `start > 0`.
4. Map in-use keys to apps: NonPackaged → exe basename through `normalizeProcessName` + `MEETING_APPS`;
   packaged → a new `MEETING_PACKAGES` table (`MSTeams_8wekyb3d8bbwe` → Microsoft Teams, plus Zoom /
   Slack / WhatsApp store packages if the owner wants them).
5. Result is an app name only if it's in use **and** its process is running; otherwise `null`.
6. `decide(lastMeeting, current, autoStart)` as today. Mic released → `current` becomes `null`, so the
   next call prompts again.

## L: Link

| From | To | Contract | Failure |
|------|----|----------|---------|
| main.ts | `reg.exe` | text output of `reg query … /s` | non-zero exit / parse error → fallback (D2) |
| main.ts | `tasklist` | unchanged | unchanged (`.catch(() => [])`) |
| detection | `decide()` | `string \| null` app name | unchanged |

## A: Assemble (order)

1. Tests first in `desktop.test.ts` (or a new `meeting-detection.test.ts`): registry parser with real
   captured output (packaged + NonPackaged, in-use and released, `#` paths, Unicode exe names),
   app mapping, AND with process list, stale-entry case, own-exe ignored, fallback path.
2. `meeting-detection.ts`: `parseConsentStore`, `MEETING_PACKAGES`, `detectActiveMeeting(platform)`.
3. `main.ts`: swap `detectMeetingApp(await listProcessNames())` for `detectActiveMeeting()`.
4. README: replace the known-limitation line (Windows fixed; macOS still process-based if D4 = later).
5. Manual check on Windows: Teams open, no call → no prompt; join a call → prompt; leave and rejoin →
   prompt again; muted call stays detected.
6. `pnpm --filter @interview-copilot/desktop test:coverage` ≥ 75%, lint, typecheck. "What we adjusted" review.

## S: Stress-test

- **Load:** one `reg query` + one `tasklist` per 5 s, both ~tens of ms. No change in practice.
- **Wrong positive:** stale `Stop = 0` → guarded by the process AND.
- **Wrong negative:** app closes mic on mute, or policy hides usage → handled by D2.
- **Privacy:** reads only timestamps already on the user's machine; nothing leaves it.
- **Security:** fixed args to `reg.exe`, no shell, no user input in the command.

## macOS (note, not in this PR unless D4 says so)

There's no registry equivalent. Options 5 and 6 need a small native helper (Swift CLI bundled with the
app, or a Node addon), and nobody can test on a real Mac yet (the beta's macOS build is untested). Proposal:
keep the process-based check on macOS for now and track a follow-up issue for option 5 with option 6 as
the fallback for macOS < 14.2.

## Decisions for the owner

- **D1 Approach (Windows):** option 1, registry mic-in-use AND process running. *(recommended)*
- **D2 Fallback when the registry can't be read or the app has no entry:** (a) no prompt at all
  *(recommended: a missed prompt is cheaper than a nagging one; the user can still start manually)*,
  or (b) fall back to today's process-only check.
- **D3 Browser meetings:** out of scope *(recommended)*, or treat Chrome/Edge/Firefox mic use as a meeting.
- **D4 macOS:** keep process check and open a follow-up issue *(recommended)*, or build option 5/6 now untested.
- **D5 Re-prompt per call:** with this change a new prompt comes for each call (mic released → next call).
  OK? *(recommended yes)*
- **D6 Where it lands:** a branch `feat/meeting-in-call-detection` off `release/36-beta` (created locally),
  as a PR after the beta, or folded into the beta. Also: open a GitHub issue for it?

## GOTCHA

- **Goals:** prompt only during an actual call on Windows; NFR-12 untouched; README limitation updated.
- **Orchestration:** tests → `meeting-detection.ts` → `main.ts` → README → manual check → review.
- **Tools:** `reg.exe query`, `tasklist`, vitest + coverage-v8, pnpm.
- **Context:** `src/desktop/src/meeting-detection.ts`, `main.ts` `watchForMeetings`, FR-16 / NFR-12 in
  `interview-copilot.md`, README "Known limitations (beta)".
- **Heuristics:** pure parsers, side effects only in thin `exec` wrappers; no new native deps; never
  start capture without a click unless auto-start; pnpm only; no push without the owner's OK.
- **Args:** D1–D6 above.

## What we adjusted (after the build)

- **Goals:** met on Windows. Teams or Zoom open with no call → no prompt; holding the mic → prompt, and
  again on the next call. Checked against this PC's real registry (no call → `null`). **Still open:** the
  manual in-call check from Assemble step 5 (join a call, leave and rejoin, stay muted), which needs a
  real Teams/Zoom call.
- **Orchestration:** as planned, tests first. `listProcessNames` was folded into `currentMeeting`, which
  takes an injectable `run` so the Windows/macOS/failure paths are unit-tested without spawning processes.
- **Tools:** `reg query` and `tasklist` run in parallel. No new dependency.
- **Context:** `MEETING_PACKAGES` only has `MSTeams_8wekyb3d8bbwe` (new Teams), the one Store package
  verified on a real machine. Store builds of Zoom, Slack or Discord aren't mapped. Their desktop
  installers (NonPackaged) are, through `MEETING_APPS`. Add family names when seen on a real install.
- **Heuristics:** D2 applies to any read failure on both platforms: an error gives `null` (no prompt).
  On macOS this replaces the old `.catch(() => [])`, which had the same effect.
- **Args:** D1–D6 applied; macOS follow-up is #59. Desktop coverage 97.6% statements, 93.2% branches.
