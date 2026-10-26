import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { chooseOption, openSelect } from '@/test/select';
import { Label } from './form';
import { Select } from './select';

const options = [
  { value: '', label: 'System default' },
  { value: 'a', label: 'Alpha' },
  { value: 'b', label: 'Beta' },
];

function Harness({ initial = '', onValueChange = () => {}, disabled }: { initial?: string; onValueChange?: (v: string) => void; disabled?: boolean }) {
  const [value, setValue] = useState(initial);
  return (
    <>
      <Label htmlFor="pick">Pick one</Label>
      <Select
        id="pick"
        value={value}
        disabled={disabled}
        options={options}
        onValueChange={(v) => {
          setValue(v);
          onValueChange(v);
        }}
      />
    </>
  );
}

describe('Select', () => {
  it('is named by its label and shows the current option text', () => {
    render(<Harness initial="a" />);

    expect(screen.getByLabelText('Pick one')).toHaveTextContent('Alpha');
    expect(screen.getByRole('combobox', { name: 'Pick one' })).toBeInTheDocument();
  });

  it('opens, lists every option and reports the chosen value', async () => {
    const onValueChange = vi.fn();
    render(<Harness initial="a" onValueChange={onValueChange} />);

    const list = await openSelect(userEvent, 'Pick one');
    expect(list.getAllByRole('option').map((o) => o.textContent)).toEqual(['System default', 'Alpha', 'Beta']);
    await userEvent.click(list.getByRole('option', { name: 'Beta' }));

    expect(onValueChange).toHaveBeenCalledWith('b');
    expect(screen.getByLabelText('Pick one')).toHaveTextContent('Beta');
  });

  it('maps the empty value to an option and back, without exposing the sentinel', async () => {
    const onValueChange = vi.fn();
    render(<Harness initial="" onValueChange={onValueChange} />);

    expect(screen.getByLabelText('Pick one')).toHaveTextContent('System default');
    await chooseOption(userEvent, 'Pick one', 'Alpha');
    await chooseOption(userEvent, 'Pick one', 'System default');

    expect(onValueChange.mock.calls).toEqual([['a'], ['']]);
  });

  it('cannot be opened when disabled', async () => {
    render(<Harness disabled />);

    const trigger = screen.getByRole('combobox', { name: 'Pick one' });
    expect(trigger).toBeDisabled();
    await userEvent.click(trigger);
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });
});
