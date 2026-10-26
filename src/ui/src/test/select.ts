import { screen, within } from '@testing-library/react';
import type userEvent from '@testing-library/user-event';

type Clicker = Pick<typeof userEvent, 'click' | 'keyboard'>;

/** Opens the dropdown named `name` and returns its option list. Close it with `closeSelect`. */
export async function openSelect(user: Clicker, name: string | RegExp) {
  await user.click(await screen.findByRole('combobox', { name }));
  return within(await screen.findByRole('listbox'));
}

export async function closeSelect(user: Clicker) {
  await user.keyboard('{Escape}');
}

/** Opens the dropdown named `name` and clicks the option with text `optionText`. */
export async function chooseOption(user: Clicker, name: string | RegExp, optionText: string | RegExp) {
  const list = await openSelect(user, name);
  await user.click(await list.findByRole('option', { name: optionText }));
}
