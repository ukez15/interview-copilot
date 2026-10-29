import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createWebPlatform, type CaptureStatus } from '@/platform';
import { fakeApi, json, renderWithApp, sampleSession } from '@/test/helpers';
import { SessionPage } from '@/features/sessions/SessionPage';
import { CAPTURE_POLL_MS, CaptureIndicator } from './CaptureIndicator';
import { MIC_PAUSED, MicStatus } from './MicStatus';
import { listMicrophones, MicrophonePicker } from './MicrophonePicker';
import { chooseOption } from '@/test/select';

afterEach(() => vi.useRealTimers());

const live: CaptureStatus = { state: 'live', interviewer: true, mic: false, message: 'Microphone unavailable.' };

describe('CaptureIndicator', () => {
  it('polls capture status while active and shows each track', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const platform = createWebPlatform();
    const getStatus = vi
      .spyOn(platform, 'getCaptureStatus')
      .mockResolvedValueOnce({ state: 'connecting', interviewer: false, mic: false, message: null })
      .mockResolvedValue(live);
    const { api } = fakeApi(() => json({}));
    renderWithApp(<CaptureIndicator active />, { api, platform });

    expect(await screen.findByText('Connecting…')).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(CAPTURE_POLL_MS));

    expect(await screen.findByText('Listening')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Interviewer ✓ · Mic ✗');
    expect(screen.getByText('Microphone unavailable.')).toBeInTheDocument();
    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it('renders nothing and does not poll when inactive', () => {
    const platform = createWebPlatform();
    const getStatus = vi.spyOn(platform, 'getCaptureStatus');
    const { api } = fakeApi(() => json({}));
    const { container } = renderWithApp(<CaptureIndicator active={false} />, { api, platform });

    expect(container).toBeEmptyDOMElement();
    expect(getStatus).not.toHaveBeenCalled();
  });
});

describe('MicStatus', () => {
  function mount(status: CaptureStatus, active = true, paused = false) {
    const platform = createWebPlatform();
    const getStatus = vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue(status);
    const { api } = fakeApi(() => json({}));
    const view = renderWithApp(<MicStatus active={active} paused={paused} />, { api, platform });
    return { ...view, getStatus };
  }

  it('says the mic is paused while practice is not taking an answer (#78)', async () => {
    mount({ state: 'live', interviewer: false, mic: true, message: null }, true, true);

    const status = await screen.findByRole('status');
    expect(status).toHaveTextContent(MIC_PAUSED);
    expect(status).not.toHaveTextContent('Listening');
  });

  it('still reports a mic problem while paused', async () => {
    mount({ state: 'live', interviewer: false, mic: false, message: 'Microphone unavailable.' }, true, true);

    expect(await screen.findByRole('status')).toHaveTextContent('Mic ✗ Microphone unavailable.');
  });

  it('shows the mic is live while capture is listening to it', async () => {
    mount({ state: 'live', interviewer: false, mic: true, message: null });

    expect(await screen.findByRole('status')).toHaveTextContent('Mic ✓ Listening');
  });

  it('shows why the mic is not heard instead of listening silently', async () => {
    mount({ state: 'live', interviewer: false, mic: false, message: 'Microphone unavailable.' });

    expect(await screen.findByRole('status')).toHaveTextContent('Mic ✗ Microphone unavailable.');
  });

  it('falls back to the capture state when there is no message', async () => {
    mount({ state: 'error', interviewer: false, mic: false, message: null });

    expect(await screen.findByRole('status')).toHaveTextContent('Mic ✗ Capture problem');
  });

  it('does not flag the mic while capture is still connecting', async () => {
    mount({ state: 'connecting', interviewer: false, mic: false, message: null });

    const status = await screen.findByRole('status');
    expect(status).toHaveTextContent('Connecting…');
    expect(status).not.toHaveTextContent('✗');
  });

  it('renders nothing and does not poll when inactive', () => {
    const { container, getStatus } = mount({ state: 'live', interviewer: false, mic: true, message: null }, false);

    expect(container).toBeEmptyDOMElement();
    expect(getStatus).not.toHaveBeenCalled();
  });
});

describe('MicrophonePicker', () => {
  it('lists real input devices only, labelling unnamed ones', async () => {
    const enumerateDevices = vi.fn().mockResolvedValue([
      { kind: 'audioinput', deviceId: 'default', label: 'Default' },
      { kind: 'audioinput', deviceId: 'communications', label: 'Comms' },
      { kind: 'audioinput', deviceId: 'usb', label: 'USB Headset' },
      { kind: 'audioinput', deviceId: 'int', label: '' },
      { kind: 'videoinput', deviceId: 'cam', label: 'Camera' },
    ]);

    expect(await listMicrophones({ enumerateDevices })).toEqual([
      { deviceId: 'usb', label: 'USB Headset' },
      { deviceId: 'int', label: 'Microphone 2' },
    ]);
    expect(await listMicrophones(undefined)).toEqual([]);
  });

  it('chooses a device or the system default, and flags a saved device that is missing', async () => {
    const onChange = vi.fn();
    const load = () => Promise.resolve([{ deviceId: 'usb', label: 'USB Headset' }]);
    const { rerender } = render(<MicrophonePicker value={null} onChange={onChange} load={load} />);

    await chooseOption(userEvent, 'Microphone', 'USB Headset');
    expect(onChange).toHaveBeenLastCalledWith('usb');

    rerender(<MicrophonePicker value="usb" onChange={onChange} load={load} />);
    await chooseOption(userEvent, 'Microphone', 'System default');
    expect(onChange).toHaveBeenLastCalledWith(null);

    rerender(<MicrophonePicker value="gone" onChange={onChange} load={load} />);
    expect(await screen.findByText('Saved microphone (not connected)')).toBeInTheDocument();
  });

  it('shows an error when devices cannot be listed', async () => {
    render(<MicrophonePicker value={null} onChange={vi.fn()} load={() => Promise.reject(new Error('denied'))} />);

    expect(await screen.findByText("Couldn't list microphones.")).toBeInTheDocument();
  });
});

describe('SessionPage capture', () => {
  it('starts capture with the session and stops it before ending', async () => {
    const { api } = fakeApi((url) => {
      if (url.endsWith('/start')) return json({ ...sampleSession, status: 'Live' });
      if (url.endsWith('/end')) return json({ ...sampleSession, status: 'Ended' });
      return json(sampleSession);
    });
    const platform = createWebPlatform();
    const order: string[] = [];
    vi.spyOn(platform, 'startCapture').mockImplementation((id) => {
      order.push(`start:${id}`);
      return Promise.resolve(live);
    });
    vi.spyOn(platform, 'stopCapture').mockImplementation(() => {
      order.push('stop');
      return Promise.resolve({ ...live, state: 'stopped' });
    });
    vi.spyOn(platform, 'hideOverlay').mockImplementation(() => {
      order.push('hide');
      return Promise.resolve();
    });
    vi.spyOn(platform, 'getCaptureStatus').mockResolvedValue(live);
    renderWithApp(<SessionPage />, { api, platform, route: '/sessions/s1', path: '/sessions/:sessionId' });

    await userEvent.click(await screen.findByRole('button', { name: 'Start session' }));
    expect(await screen.findByText('Listening')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'End session' }));

    await waitFor(() => expect(order).toEqual(['start:s1', 'stop', 'hide']));
  });

  it('web platform explains capture needs the desktop app', async () => {
    const platform = createWebPlatform();

    expect((await platform.startCapture('s1')).message).toContain('desktop app');
    expect((await platform.stopCapture()).state).toBe('idle');
  });
});
