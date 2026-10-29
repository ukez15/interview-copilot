import { useEffect, type RefObject } from 'react';

/** Within this many pixels of the end counts as "at the bottom", so a nearly-scrolled-down view keeps following. */
export const AUTO_SCROLL_STICK_PX = 24;

/**
 * `follow`: scroll to new text unless the user has scrolled up to read (mock interviews).
 * `always`: scroll to new text every time (live calls).
 */
export type AutoScrollMode = 'follow' | 'always';

/** Keeps the overlay's scroll container on its newest text as content is added (#74). */
export function useAutoScroll(ref: RefObject<HTMLElement | null>, mode: AutoScrollMode): void {
  useEffect(() => {
    const element = ref.current;
    if (!element) {
      return;
    }

    let pinned = true;
    const onScroll = () => {
      pinned = element.scrollHeight - element.scrollTop - element.clientHeight <= AUTO_SCROLL_STICK_PX;
    };
    const observer = new MutationObserver(() => {
      if (mode === 'always' || pinned) {
        element.scrollTop = element.scrollHeight;
      }
    });

    element.addEventListener('scroll', onScroll);
    observer.observe(element, { childList: true, subtree: true, characterData: true });
    return () => {
      observer.disconnect();
      element.removeEventListener('scroll', onScroll);
    };
  }, [ref, mode]);
}
