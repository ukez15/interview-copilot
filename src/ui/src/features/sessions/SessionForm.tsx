import { useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input, Label, Select, Textarea } from '@/components/ui/form';
import {
  ANSWER_FRAMEWORKS,
  ANSWER_LENGTHS,
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
  onSubmit: (input: CreateSessionInput) => void;
  busy?: boolean;
  /** "edit" (#10) hides title and mode, pre-fills from `initial` and shows "Save settings". */
  variant?: 'create' | 'edit';
  initial?: CreateSessionInput;
}) {
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

  function submit(event: FormEvent) {
    event.preventDefault();
    onSubmit(input);
  }

  return (
    <form onSubmit={submit} className="grid gap-4">
      {!editing && (
        <>
          <div className="grid gap-1.5">
            <Label htmlFor="title">Title</Label>
            <Input id="title" placeholder="Acme — senior engineer onsite" value={input.title} onChange={(e) => update('title', e.target.value)} required />
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

      <Button type="submit" disabled={busy}>
        {editing ? 'Save settings' : 'Create session'}
      </Button>
    </form>
  );
}
