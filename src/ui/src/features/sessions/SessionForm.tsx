import { useRef, useState, type ClipboardEvent, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input, Label, Select, Textarea } from '@/components/ui/form';
import { JobDraftEditor } from '@/features/documents/JobDraftEditor';
import { UNREADABLE, normaliseTxtName, parseLink, type JobDraft } from '@/features/documents/job-link';
import { useApp } from '@/lib/app-context';
import {
  ANSWER_FRAMEWORKS,
  ANSWER_LENGTHS,
  MAX_SESSION_TITLE_LENGTH,
  QUESTION_TYPE_LABELS,
  QUESTION_TYPES,
  type CreateSessionInput,
  type QuestionType,
  type SessionMode,
} from '@/types/api';

const MODE_LABELS: Record<SessionMode, string> = {
  LiveInterview: 'Live interview',
  MockInterview: 'Mock interview practice',
  Conversation: 'Sales call / other conversation',
};

const FRAMEWORK_LABELS = { None: 'No framework', Star: 'STAR', Smart: 'SMART' } as const;

/** FR-01, FR-03..FR-06: everything set before a session starts. */
export function SessionForm({
  onSubmit,
  busy,
  variant = 'create',
  initial,
}: {
  /** `jobDescription` is set when a job link was pasted into the title (create variant) and its text is not blank. */
  onSubmit: (input: CreateSessionInput, jobDescription?: File) => void;
  busy?: boolean;
  /** "edit" (#10) hides title and mode, pre-fills from `initial` and shows "Save settings". */
  variant?: 'create' | 'edit';
  initial?: CreateSessionInput;
}) {
  const { api } = useApp();
  const editing = variant === 'edit';
  const [input, setInput] = useState<CreateSessionInput>(
    initial ?? {
      title: '',
      mode: 'LiveInterview',
      answerLength: 'Balanced',
      framework: 'Star',
      pinnedQuestionType: null,
      sessionPrompt: '',
    },
  );

  function update<K extends keyof CreateSessionInput>(key: K, value: CreateSessionInput[K]) {
    setInput((current) => ({ ...current, [key]: value }));
  }

  // A job description read from a pasted link, shown for editing under the title; saved as the session's first
  // document by the caller after the session exists.
  const [draft, setDraft] = useState<JobDraft | null>(null);
  const [fetching, setFetching] = useState(false);
  const [linkError, setLinkError] = useState<string | null>(null);
  // Only the latest fetch may change the form: a cancel or a newer paste makes earlier responses stale.
  const latestFetch = useRef(0);

  async function fetchJobDescription(url: URL) {
    const fetchId = ++latestFetch.current;
    setLinkError(null);
    setFetching(true);
    try {
      const preview = await api.previewJobPostingLink(url.href);
      if (fetchId !== latestFetch.current) {
        return;
      }
      update('title', preview.suggestedName.replace(/\.txt$/i, '').slice(0, MAX_SESSION_TITLE_LENGTH).trimEnd());
      setDraft({ name: preview.suggestedName, text: preview.text });
    } catch (err) {
      if (fetchId !== latestFetch.current) {
        return;
      }
      // Same editor, empty: the user pastes the text instead. The title keeps the pasted link.
      setLinkError(err instanceof Error && err.message ? err.message : UNREADABLE);
      setDraft({ name: `${url.hostname}.txt`, text: '' });
    } finally {
      if (fetchId === latestFetch.current) {
        setFetching(false);
      }
    }
  }

  function pasteIntoTitle(event: ClipboardEvent<HTMLInputElement>) {
    const pasted = event.clipboardData.getData('text').trim();
    const url = parseLink(pasted);
    const field = event.currentTarget;
    // Only a link that replaces the whole title is a "use this job" gesture; anywhere else it is a normal paste.
    if (!url || field.selectionStart !== 0 || field.selectionEnd !== field.value.length) {
      return;
    }
    event.preventDefault();
    update('title', pasted);
    void fetchJobDescription(url);
  }

  function cancelDraft() {
    latestFetch.current++;
    setFetching(false);
    setDraft(null);
    setLinkError(null);
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!editing && draft?.text.trim()) {
      const name = normaliseTxtName(draft.name) || 'Job description.txt';
      onSubmit(input, new File([draft.text], name, { type: 'text/plain' }));
      return;
    }
    onSubmit(input);
  }

  return (
    <form onSubmit={submit} className="grid gap-4">
      {!editing && (
        <>
          <div className="grid gap-1.5">
            <Label htmlFor="title">Title</Label>
            <Input
              id="title"
              placeholder="Acme — senior engineer onsite"
              value={input.title}
              onChange={(e) => update('title', e.target.value)}
              onPaste={pasteIntoTitle}
              required
            />
            {fetching && (
              <p role="status" className="text-sm text-muted-foreground">
                Getting job description…
              </p>
            )}
            {linkError && (
              <p role="alert" className="text-sm text-destructive">
                {linkError}
              </p>
            )}
            {draft && (
              <JobDraftEditor draft={draft} onChange={setDraft}>
                <Button type="button" variant="ghost" onClick={cancelDraft}>
                  Cancel
                </Button>
              </JobDraftEditor>
            )}
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor="mode">Mode</Label>
            <Select id="mode" value={input.mode} onChange={(e) => update('mode', e.target.value as SessionMode)}>
              {Object.entries(MODE_LABELS).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </Select>
          </div>
        </>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="grid gap-1.5">
          <Label htmlFor="length">Answer length</Label>
          <Select id="length" value={input.answerLength} onChange={(e) => update('answerLength', e.target.value as CreateSessionInput['answerLength'])}>
            {ANSWER_LENGTHS.map((length) => (
              <option key={length} value={length}>
                {length}
              </option>
            ))}
          </Select>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="framework">Answer framework</Label>
          <Select id="framework" value={input.framework} onChange={(e) => update('framework', e.target.value as CreateSessionInput['framework'])}>
            {ANSWER_FRAMEWORKS.map((framework) => (
              <option key={framework} value={framework}>
                {FRAMEWORK_LABELS[framework]}
              </option>
            ))}
          </Select>
        </div>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="pinned">Pin a question type (auto-detect overrides it when the conversation shifts)</Label>
        <Select
          id="pinned"
          value={input.pinnedQuestionType ?? ''}
          onChange={(e) => update('pinnedQuestionType', e.target.value === '' ? null : (e.target.value as QuestionType))}
        >
          <option value="">Auto-detect</option>
          {QUESTION_TYPES.map((type) => (
            <option key={type} value={type}>
              {QUESTION_TYPE_LABELS[type]}
            </option>
          ))}
        </Select>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="prompt">Session prompt</Label>
        <Textarea
          id="prompt"
          placeholder="Who you're talking to and how the AI should answer. E.g. 'Panel of two staff engineers; keep answers practical and mention my Kubernetes work.'"
          value={input.sessionPrompt}
          maxLength={4000}
          onChange={(e) => update('sessionPrompt', e.target.value)}
        />
      </div>

      <Button type="submit" disabled={busy || fetching}>
        {editing ? 'Save settings' : 'Create session'}
      </Button>
    </form>
  );
}
