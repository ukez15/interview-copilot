import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiClient, DEFAULT_API_BASE_URL, type SignInTokens } from './api';
import { detectPlatform, type Platform } from '@/platform';

interface AppContextValue {
  api: ApiClient;
  platform: Platform;
  signedIn: boolean;
  /** Stores the tokens with the platform (shared with the overlay window) and marks the user signed in. */
  completeSignIn: (tokens: SignInTokens) => Promise<void>;
  /** Clears the stored sign-in and returns to the sign-in page. */
  signOut: () => Promise<void>;
}

const AppContext = createContext<AppContextValue | null>(null);

export function AppProvider({
  children,
  api: providedApi,
  platform: providedPlatform,
}: {
  children: ReactNode;
  api?: ApiClient;
  platform?: Platform;
}) {
  const api = useMemo(() => providedApi ?? new ApiClient(import.meta.env.VITE_API_BASE_URL ?? DEFAULT_API_BASE_URL), [providedApi]);
  const platform = useMemo(() => providedPlatform ?? detectPlatform(), [providedPlatform]);
  const [signedIn, setSignedIn] = useState(false);

  useEffect(() => {
    // An expired access token is renewed once; if that fails the user is signed out (#8).
    api.setAuthHandlers({
      refresh: () => platform.refreshAuth(),
      signedOut: () => {
        void platform.setAuthTokens(null);
        setSignedIn(false);
      },
    });
    return () => api.setAuthHandlers(null);
  }, [api, platform]);

  useEffect(() => {
    void platform.getAuthToken().then((token) => {
      if (token) {
        api.setToken(token);
        setSignedIn(true);
      }
    });
  }, [api, platform]);

  const value = useMemo<AppContextValue>(
    () => ({
      api,
      platform,
      signedIn,
      completeSignIn: async (tokens) => {
        api.setToken(tokens.accessToken);
        await platform.setAuthTokens(tokens);
        setSignedIn(true);
      },
      signOut: async () => {
        api.setToken(null);
        await platform.setAuthTokens(null);
        setSignedIn(false);
      },
    }),
    [api, platform, signedIn],
  );
  return <AppContext.Provider value={value}>{children}</AppContext.Provider>;
}

export function useApp(): AppContextValue {
  const value = useContext(AppContext);
  if (!value) {
    throw new Error('useApp must be used inside <AppProvider>.');
  }
  return value;
}
