import { act, render, screen } from '@testing-library/react';
import { useRef } from 'react';
import { describe, expect, it } from 'vitest';
import { AUTO_SCROLL_STICK_PX, useAutoScroll, type AutoScrollMode } from './useAutoScroll';

const VIEW_HEIGHT = 100;
const LINE_HEIGHT = 50;

function Box({ mode, lines }: { mode: AutoScrollMode; lines: string[] }) {
  const ref = useRef<HTMLDivElement>(null);
  useAutoScroll(ref, mode);
  return (
    <div ref={ref} data-testid="box">
      {lines.map((line) => (
        <p key={line}>{line}</p>
      ))}
    </div>
  );
}

/** jsdom has no layout: give the box a height that grows with its lines and a clamped scrollTop. */
function fakeLayout(box: HTMLElement) {
  let top = 0;
  const contentHeight = () => box.children.length * LINE_HEIGHT;
  Object.defineProperty(box, 'clientHeight', { configurable: true, get: () => VIEW_HEIGHT });
  Object.defineProperty(box, 'scrollHeight', { configurable: true, get: contentHeight });
  Object.defineProperty(box, 'scrollTop', {
    configurable: true,
    get: () => top,
    set: (value: number) => {
      top = Math.max(0, Math.min(value, contentHeight() - VIEW_HEIGHT));
      box.dispatchEvent(new Event('scroll'));
    },
  });
}

function userScrollsTo(box: HTMLElement, top: number) {
  act(() => {
    box.scrollTop = top;
  });
}

async function mount(mode: AutoScrollMode) {
  const lines = ['one', 'two', 'three'];
  const view = render(<Box mode={mode} lines={lines} />);
  const box = screen.getByTestId('box');
  fakeLayout(box);
  const add = async (line: string) => {
    lines.push(line);
    await act(async () => {
      view.rerender(<Box mode={mode} lines={[...lines]} />);
      await Promise.resolve();
    });
  };
  const bottom = () => box.scrollHeight - box.clientHeight;
  return { box, add, bottom };
}

describe('useAutoScroll', () => {
  it('follows new text to the bottom', async () => {
    const { box, add, bottom } = await mount('follow');

    await add('four');

    expect(box.scrollTop).toBe(bottom());
  });

  it('in follow mode, stays put while the user has scrolled up, then resumes at the bottom', async () => {
    const { box, add, bottom } = await mount('follow');
    await add('four');

    userScrollsTo(box, 0);
    await add('five');
    expect(box.scrollTop).toBe(0);

    userScrollsTo(box, bottom() - AUTO_SCROLL_STICK_PX);
    await add('six');
    expect(box.scrollTop).toBe(bottom());
  });

  it('in always mode, jumps to the newest text even after the user scrolled up', async () => {
    const { box, add, bottom } = await mount('always');

    userScrollsTo(box, 0);
    await add('four');

    expect(box.scrollTop).toBe(bottom());
  });
});
