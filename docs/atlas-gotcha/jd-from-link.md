# Brief: job description from a link. ATLAS + GOTCHA, compact

Status: **approved by owner** (2026-10-26): all recommendations, proposed labels and Args accepted.

Owner decisions (2026-10-26):
- Q1 session document; Q2 backend; Q3 JSON-LD then SmartReader, no Ollama pass; Q4 paste-instead box
  on failure; Q5 editable preview, nothing saved before the click; Q6 `<Job title> - <Company>.txt`,
  editable; Q7 labels as proposed; Q8 direct connection, system proxy ignored.

Build details settled while building (within the approved design; listed for review):
- The preview editor and the paste-instead box are the same editor: on a failed fetch it opens with
  empty text and the link's host as the name. Field labels "Document name" and "Job description",
  plus a "Cancel" button.
- The fetcher owns its own `HttpClient` (not `IHttpClientFactory`, whose logging writes request URLs),
  and its requests are filtered out of OpenTelemetry HTTP tracing so the URL never reaches a trace.
- Failure messages from the backend end with "Paste the text instead." where pasting is the way out.

Owner's request: "if user paste the job link that has the jd, the app should be able to get the jd and
map it to the jd box. that will inform how the app will help answers questions".

## How a JD reaches the copilot today

- There is **no dedicated "JD box"**. A JD is one of the session's documents (up to 10, FR-02),
  added through the file input in `DocumentsPanel` ("Resume, job description, sales script, product docs").
- `POST /api/sessions/{id}/documents` → `DocumentService.AttachAsync` → an `IDocumentTextExtractor`
  (txt/md, PDF, docx) → `Session.AttachDocument(fileName, text)` → SQLite.
- Live answers: `DocumentContextSelector` picks 800-character chunks by keyword overlap, inside a
  6,000-character budget. Practice: `PracticePromptBuilder` asks the selector for
  "job description requirements responsibilities skills".
- The only free-text box is the **Session prompt** (FR-06, max 4,000 characters). It's meant for
  "who you're talking to and how to answer". Most JDs would exceed 4,000 characters.

**Recommendation:** treat "the JD box" as a session document. A link-fetched JD then grounds live
answers and practice questions with no change to prompt building. (Question 1.)

## A: Architect

- **Purpose:** the user pastes a job-posting link. The app fetches the page, pulls out the job
  description, and adds it to the session as a document so answers are grounded in it.
- **Users:** candidates preparing a session (Draft state; also allowed later, like uploads today).
- **Constraints:**
  - Zero cost: no paid scraping or rendering APIs; open-source packages only.
  - The fetch of the user-pasted URL is the **only** outbound call, and only on the user's click.
  - Page contents are personal data: never logged. URLs are not logged either (they can carry tokens).
  - SSRF guard: http/https only; reject loopback, private, link-local, CGNAT, multicast and
    unspecified addresses, re-checked on every redirect and pinned at connect time (no DNS rebinding).
  - Size and time limits on the fetch (Args).
  - UI stays Electron-free (Mode C rule).
- **Tech decisions already made:** .NET 10 Clean Architecture backend, React/Vite/TS/shadcn UI,
  SQLite, typed `HttpClient`s in Infrastructure, xunit v3 + FluentAssertions 7, Vitest.
- **Out of scope:**
  - Rendering JavaScript (no headless browser, no hidden Electron window).
  - Signing in to job boards or reusing the user's browser cookies.
  - A new dedicated JD field or schema change (unless the owner picks that in question 1).
  - Changing how `DocumentContextSelector` ranks chunks.
  - Fetching from the browser extension.
  - Re-fetching or tracking changes to a posting.

## Open questions for the owner

1. **"JD box".** There isn't one. Store the fetched JD as a **session document** (recommended), or
   add a dedicated JD field to the session (schema change + migration + prompt changes)?
2. **Where the fetch happens.** **Backend** (recommended: testable with a fake handler, keeps the UI
   Electron-free for Mode C, sits next to the existing extractors) or the **desktop** main process via
   Electron `net` (Electron only, not available to a future hosted mode)?
3. **How to extract the text.** Recommended order, all local and free:
   a. **schema.org `JobPosting` JSON-LD** (title, company, description). Common on applicant-tracking
      pages such as Greenhouse, Lever, Ashby and SmartRecruiters; the cleanest source when present.
   b. Else **readable-content extraction** (SmartReader, a .NET port of Mozilla Readability).
   c. Else fail with "paste the text instead".
   A **local Ollama clean-up pass** is not recommended for v1: on the reference laptop (16 GB, no GPU)
   it adds tens of seconds and can drop or invent requirements. Agree, or include it as an option?
4. **Login-walled and JavaScript-rendered sites** (LinkedIn, Indeed, Workday). Without a browser they
   often return a sign-in wall, a bot check (403/999) or an empty shell. Recommended: detect "no
   job description found" and show a clear message with a **paste-the-text box** in the same panel, so
   the user can still add the JD without making a file. OK? And should that paste box also be usable
   on its own (no link)?
5. **Edit before saving.** Recommended: show the extracted text in an editable preview; nothing is
   saved until the user clicks save. Or save straight away?
6. **Document name.** Proposal: `<Job title> - <Company>.txt` from JSON-LD, else the page `<title>`,
   else the link's host. Do you want a different pattern (for example a "JD - " prefix) and should the
   name be editable in the preview?
7. **UI labels.** Placement proposal: inside `DocumentsPanel`, under the file input. Please give or
   approve: the URL input label, the fetch button text, the save button text, and the
   paste-instead message. Placeholder suggestions: "Job posting link", "Get job description",
   "Add to documents", "We couldn't read a job description from that page. Paste the text instead."
8. **Proxy.** Pinning the connection to a checked IP doesn't work through an HTTP proxy. Recommended:
   ignore any system proxy for this fetch (direct connection only). OK?

## T: Trace (assuming the recommendations)

1. User pastes a link and clicks the fetch button. UI validates it looks like an http(s) URL.
2. UI → `POST /api/sessions/{id}/documents/from-link/preview` with `{ url }` (bearer auth, owner check).
3. `JobPostingImportService` (Application) loads the session (ownership; 10-document limit checked
   early so the user isn't told to edit text they can't save).
4. `IWebPageFetcher` (Application interface, Infrastructure implementation):
   parse URL → scheme check → resolve DNS → reject blocked addresses → connect to the checked IP only →
   GET with timeout and size cap → follow up to N redirects manually, re-checking each →
   accept only `text/html` / `application/xhtml+xml` → decode by declared charset.
   Failure points: invalid URL, blocked address, timeout, too large, non-HTML, HTTP error.
5. `IJobPostingExtractor` (Infrastructure): JSON-LD `JobPosting` → else readable content → normalise to
   plain text (HTML in `description` converted to text with line breaks and bullets), trim, cap length.
   Failure point: nothing usable found.
6. API returns `{ suggestedName, text, source: "JsonLd" | "Readable" }`. Nothing is stored.
7. UI shows the editable preview (name + text). User edits, clicks save.
8. UI saves through the **existing** upload endpoint as a `.txt` file built in the browser
   (`new File([text], name)`), so storage, limits and extraction reuse today's path. No new save API.
9. Document appears in the list; `DocumentContextSelector` and `PracticePromptBuilder` use it as today.

Errors at 4–5 surface as `DomainException`-style 400s with a user-safe message (no page content,
no URL echoed into logs) and the UI offers the paste box.

## L: Link

| From | To | Method | Contract | Failure mode |
|---|---|---|---|---|
| UI `DocumentsPanel` | API | `POST …/documents/from-link/preview` | `{url}` → `{suggestedName,text,source}` | 400 with message → show message + paste box |
| API endpoint | `JobPostingImportService` | in-process | `PreviewAsync(ownerId, sessionId, url, ct)` | owner/limit errors as today |
| Service | `IWebPageFetcher` | in-process | `FetchAsync(Uri, ct)` → `FetchedPage(finalUri, html)` | `DomainException` subclasses per failure |
| Fetcher | job site | HTTPS/HTTP GET | user-pasted URL only | timeout, size cap, blocked IP, non-HTML |
| Service | `IJobPostingExtractor` | in-process | `Extract(html, finalUri)` → result or null | null → "paste instead" |
| UI | API | existing `POST …/documents` | multipart `.txt` | existing errors (limit, empty) |

## A: Assemble

1. **Contracts:** Application interfaces + DTOs + Args constants; UI types and `api.previewJobPosting`.
2. **Tests first:** SSRF address classifier, fetcher (fake `HttpMessageHandler`, redirects, limits),
   extractor (synthetic HTML fixtures written for the tests, not copied from real job sites),
   service, endpoint integration test with the fetcher faked, UI panel tests.
3. **Core:** fetcher, extractor, service, endpoint, DI registration (own typed client, no resilience
   retries on user URLs).
4. **UI:** link input, preview/edit, save via existing upload, paste-instead box.
5. **Gates:** `dotnet build`, `dotnet format --verify-no-changes`, `dotnet test -c Release` with
   coverage; `pnpm lint`, `pnpm typecheck`, `pnpm test` in `src/ui`. Then "What we adjusted".

## S: Stress-test (acceptance criteria)

- `file:`, `ftp:`, `javascript:` URLs → rejected before any network call.
- `http://127.0.0.1`, `http://localhost:5480`, `http://[::1]`, `http://10.x`, `172.16–31.x`,
  `192.168.x`, `169.254.169.254`, `100.64.x`, `0.0.0.0`, IPv4-mapped IPv6 of those → rejected.
- Public host that redirects to a private address → rejected at the redirect.
- Hostname that resolves to a private address → rejected (checked after DNS, pinned at connect).
- Server that never responds → fails after the timeout. Body larger than the cap → fails, stops reading.
- `Content-Type: application/pdf` or `image/*` → "not a web page" message.
- Page with JobPosting JSON-LD → title, company and description extracted; HTML in description becomes
  readable text.
- Page without JSON-LD but with an article-like body → readable text extracted.
- Login wall / empty JS shell → "paste the text instead".
- No log line contains page HTML, extracted text or the URL.
- Session already has 10 documents → told before fetching.

---

## GOTCHA

=== GOALS ===
- A user can paste a job-posting link and, after a preview they can edit, add the job description to
  the session as a document in one click.
- Pages with JobPosting JSON-LD or a readable main body produce clean plain text.
- Pages the app can't read produce a clear message and a way to paste the text.
- No request ever reaches a local or private address; no page content or URL is logged.
- Coverage stays ≥ 75% in every package; all repo gates pass.

=== ORCHESTRATION ===
1. Application: `IWebPageFetcher`, `IJobPostingExtractor`, `JobPostingImportService`, DTOs, Args constants.
2. Tests for 1 and for the Infrastructure pieces (red).
3. Infrastructure: `BlockedAddress` classifier, `SafeWebPageFetcher` (SocketsHttpHandler with
   `ConnectCallback`, manual redirects), `JobPostingExtractor` (JSON-LD, then SmartReader). DI.
4. Api: preview endpoint + integration test with fakes.
5. UI: types + api method + tests (red), then `DocumentsPanel` link/preview/paste UI.
6. Gates, then "What we adjusted" against every layer.

=== TOOLS ===
- .NET 10, ASP.NET Core minimal APIs, `System.Net.Http.SocketsHttpHandler`, `System.Text.Json`.
- **AngleSharp 1.8.6** (MIT): HTML parsing, JSON-LD script lookup, HTML-to-text.
- **SmartReader 0.11.1** (Apache-2.0): readable-content fallback.
- xunit.v3 4.0.2, FluentAssertions 7.2.2, Microsoft.AspNetCore.Mvc.Testing 10.0.13.
- UI: React, shadcn components already in `src/ui`, Vitest + Testing Library. pnpm only.

=== CONTEXT ===
- `GOTCHA-PROJECT.md` applies in full (zero cost, 127.0.0.1, no content logging, tests first, no
  `electron` in `src/ui`, exact versions only).
- Documents are capped at 10 per session (`SessionLimits.MaxDocumentsPerSession`) and stored as text.
- Another session is shipping the beta (#36): this work does not touch `README.md` or `release.yml`.
- Windows: `dotnet test -c Release`; no code written via PowerShell strings; no `dotnet format` fix
  mode on code that doesn't compile.

=== HEURISTICS ===
DO:
- Validate the URL and every resolved address before connecting, and connect only to the checked address.
- Re-validate on every redirect; follow redirects manually.
- Stream the body and stop at the size cap.
- Keep fetching and parsing behind Application interfaces with fakes in tests.
- Use synthetic HTML fixtures in tests.
- Return user-safe error messages; offer paste-instead on any extraction failure.
DON'T:
- Don't log page HTML, extracted text or the URL.
- Don't send cookies, auth headers or the user's identity with the fetch.
- Don't retry user URLs automatically or use the shared resilience pipeline for them.
- Don't save anything until the user clicks save.
- Don't call Ollama or any LLM in this flow (unless the owner chooses it in question 3).
- Don't add a headless browser or any paid service.

=== ARGS === (proposed; owner to confirm or change)
- Endpoint: `POST /api/sessions/{sessionId}/documents/from-link/preview`
- Request timeout: 15 s total (including redirects)
- Max response body: 3 MB
- Max redirects: 5
- Max extracted text kept: 50,000 characters
- Allowed schemes: `http`, `https`; allowed content types: `text/html`, `application/xhtml+xml`
- User-Agent: `InterviewCopilot/<version> (+https://github.com/ukez15/interview-copilot)`
- Accept-Language: none sent (site default)
- Blocked ranges: 0.0.0.0/8, 10/8, 100.64/10, 127/8, 169.254/16, 172.16/12, 192.0.0/24, 192.168/16,
  198.18/15, 224/4, 240/4, ::/128, ::1/128, fc00::/7, fe80::/10, ff00::/8, IPv4-mapped forms
- Saved file extension: `.txt`

---

## What we adjusted (post-build review, 2026-10-26)

Gates, run in the worktree: `dotnet build -c Release` clean; `dotnet format --verify-no-changes` clean;
`dotnet test -c Release` 744/744 (arch, unit, integration); line coverage Domain 100%, Api 99.5%,
Application 98.2%, Infrastructure 98.5%, ServiceDefaults 94.4%. `pnpm lint` and `pnpm typecheck` clean;
`pnpm test:coverage` 189/189, 98.7% lines. No vulnerable packages; AngleSharp (MIT) and SmartReader
(Apache-2.0) pass the copyleft deny-list.

- **Goals:** met. Link → editable preview → "Add to documents"; failures open the same editor empty
  for pasting; nothing is stored before the click (integration test checks no document after preview).
- **Orchestration:** contracts and tests first, then Infrastructure, Api, UI. Backend and UI were
  built in parallel against the contract fixed in this brief.
- **Tools:** AngleSharp 1.8.6 and SmartReader 0.11.1 as listed. SmartReader is used through
  `new Reader(uri, html)`; its `ParseArticle(string, string)` overload downloads the page, so it is not used.
- **Context:** no `README.md` or `release.yml` changes; no `electron` in `src/ui`; Release builds only.
- **Heuristics:** addresses checked at connect time and on every redirect; no proxy, cookies or auth;
  no logging in the fetcher; its requests skip OpenTelemetry tracing. HTTP *metrics* still record the
  host and port (not the path), and only leave the machine when an OTLP endpoint is configured (dev).
- **Args:** all used as constants on `JobPostingImportService` (15 s, 3 MB, 5 redirects, 50,000 chars).

Adjustments and decisions made during the build (for the owner's review):
- Extra blocked IPv6 forms beyond the Args list: all of `::/96`; NAT64 `64:ff9b::/96` and 6to4
  `2002::/16` are checked through their embedded IPv4 address. Unknown address families are blocked.
- A response with no Content-Type is rejected as "not a web page". Charset order: header, then
  `<meta charset>` in the first 2 KB, then UTF-8.
- Extra messages: "That link redirected too many times. Paste the text instead." and, from the UI when
  an upload fails with no message, "Couldn't add the job description."
- JSON-LD: `name` is used when `title` is missing; a list of hiring organisations uses the first.
- Readable fallback converts the article HTML with the same HTML-to-text code, so lists keep "- " bullets.
- Name fallback order ends with "Job description.txt" when even the host is unusable.
- UI: the link input is `type="url"` with placeholder `https://`; on failure the editor's name is the
  link's hostname + ".txt" (e.g. `www.jobs.test.txt`).
- `BlockedAddressException` is public (Sonar S3871 forbids internal exceptions); it is the only new
  public Infrastructure type.
- Three UI files were written with CRLF; normalised to LF to match `.gitattributes`.

Not covered by automated tests: a real fetch against live job sites (tests use synthetic HTML), and the
OpenTelemetry filter lambda itself.

---

## Change 1: paste a job link into the session Title (owner request, 2026-10-26)

Owner's words: "when i paste jd link into title, it should be able to pick the jd from that link".
In the manual test the owner looked for the link box in the create-session form, not in the
Documents panel (which only exists after a session is created).

Owner decisions (2026-10-26, all recommendations accepted):
- **Trigger: on paste.** Pasting an http(s) link into the create form's Title (empty or fully selected)
  fetches the JD at once. Typing a normal title never triggers it.
- **Title becomes `<Job title> - <Company>`** from the posting (the suggested name without `.txt`),
  falling back to the page title, then the site name; still editable.
- **Editable preview** under the Title (same editor as the Documents panel). On Create the session is
  made and the JD is saved as its first document. Nothing is stored before Create.
- **Failure: paste box.** Show the reason and the empty editor; the Title keeps what was pasted.

Design within those decisions:
- New endpoint, not tied to a session (there is none yet):
  `POST /api/documents/from-link/preview`, body `{ url }`, same response and errors as the session one.
  The session-scoped endpoint stays (it also checks the 10-document limit).
- `SessionForm` gains the paste behaviour in its `create` variant only, and `onSubmit(input, jobDescription?: File)`.
  `SessionsPage` uploads the file after creating the session; if that upload fails it stays on the
  page with the error and the new session in the list. `StartFromMeetingPage` uploads it before
  starting; an upload failure there doesn't block the live start (the meeting is already on).
- The editor is shared between the Documents panel and the form. In the form it has "Cancel" (drop the
  JD) but no save button; a status line "Getting job description…" shows while fetching, and Create
  is disabled meanwhile.

Also in this change: dropdown lists matched the app theme (owner report: "plain white", unreadable).
`index.css` sets `color-scheme: light dark` and gives `select option` the theme background and text.

Also from the manual test (owner, 2026-10-26):
- Real job links work, both in the Title and in the Documents panel.
- The Documents file picker is now an outline button labelled "Choose files" (the native input is
  visually hidden but keeps keyboard focus and the "Add documents" accessible name), so it reads as clickable.
- **Owner decision: keep the "Job posting link" box in the Documents panel.** Title paste only exists
  while creating a session; the panel box is the way to add a JD from a link to an existing session.
