import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";
import { ErrorCodes, type ErrorDto } from "../contracts/bridge";
import type {
  EffectiveTheme,
  ThemePreference,
  ThemePreferenceDto,
} from "../contracts/dtos";
import { bridgeClient } from "../services/bridgeClient";

export type ThemePersistenceWarning = "read" | "write";

export type ThemeContextType = {
  preference: ThemePreference;
  effectiveTheme: EffectiveTheme;
  persistenceWarning: ThemePersistenceWarning | null;
  setThemePreference: (pref: ThemePreference) => void;
  retryPersistence: () => void;
  toggleTheme: () => void;
};

const ThemeContext = createContext<ThemeContextType | null>(null);

function getSystemTheme(): EffectiveTheme {
  if (typeof window !== "undefined" && window.matchMedia) {
    return window.matchMedia("(prefers-color-scheme: dark)").matches
      ? "dark"
      : "light";
  }
  return "dark";
}

function isThemeSnapshot(value: unknown): value is ThemePreferenceDto {
  if (typeof value !== "object" || value === null) return false;

  const snapshot = value as Record<string, unknown>;
  return (
    (snapshot.preference === "system" ||
      snapshot.preference === "light" ||
      snapshot.preference === "dark") &&
    (snapshot.effectiveTheme === "light" || snapshot.effectiveTheme === "dark")
  );
}

function getError(error: unknown): Partial<ErrorDto> {
  return typeof error === "object" && error !== null
    ? (error as Partial<ErrorDto>)
    : {};
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreference] = useState<ThemePreference>("system");
  const [effectiveTheme, setEffectiveTheme] = useState<EffectiveTheme>(getSystemTheme);
  const [isReady, setIsReady] = useState(false);
  const [persistenceWarning, setPersistenceWarning] =
    useState<ThemePersistenceWarning | null>(null);

  const applySnapshot = useCallback((snapshot: ThemePreferenceDto) => {
    setPreference(snapshot.preference);
    setEffectiveTheme(snapshot.effectiveTheme);
    setPersistenceWarning(null);

    const root = document.documentElement;
    root.setAttribute("data-theme", snapshot.effectiveTheme);
    root.style.colorScheme = snapshot.effectiveTheme;
    root.setAttribute("data-theme-ready", "true");
    setIsReady(true);
  }, []);

  const applyReadFailure = useCallback((error: unknown) => {
    const errorDto = getError(error);
    const fallback = isThemeSnapshot(errorDto.details)
      ? errorDto.details
      : { preference: "system" as const, effectiveTheme: getSystemTheme() };

    setPreference(fallback.preference);
    setEffectiveTheme(fallback.effectiveTheme);
    setPersistenceWarning(
      errorDto.code === ErrorCodes.BRIDGE_UNAVAILABLE ? null : "read",
    );

    const root = document.documentElement;
    root.setAttribute("data-theme", fallback.effectiveTheme);
    root.style.colorScheme = fallback.effectiveTheme;
    root.setAttribute("data-theme-ready", "true");
    setIsReady(true);
  }, []);

  const readPreference = useCallback(async () => {
    const response = await bridgeClient.invoke<unknown>("theme.get_preference", {});
    if (!isThemeSnapshot(response)) {
      throw {
        code: ErrorCodes.INTERNAL_ERROR,
        message: "Phản hồi theme.get_preference không hợp lệ.",
        retryable: false,
        correlationId: "",
        details: null,
      } satisfies ErrorDto;
    }
    return response;
  }, []);

  useEffect(() => {
    let active = true;
    const unsubscribe = bridgeClient.subscribeEvent<ThemePreferenceDto>(
      "theme.changed",
      (event) => {
        if (active && isThemeSnapshot(event.payload)) {
          applySnapshot(event.payload);
        }
      },
    );

    void readPreference()
      .then((snapshot) => {
        if (active) applySnapshot(snapshot);
      })
      .catch((error: unknown) => {
        if (active) applyReadFailure(error);
      });

    return () => {
      active = false;
      unsubscribe();
    };
  }, [applyReadFailure, applySnapshot, readPreference]);

  const setThemePreference = useCallback(
    (pref: ThemePreference) => {
      const optimisticSnapshot: ThemePreferenceDto = {
        preference: pref,
        effectiveTheme: pref === "system" ? getSystemTheme() : pref,
      };
      applySnapshot(optimisticSnapshot);

      void bridgeClient
        .invoke<unknown>("theme.set_preference", { preference: pref })
        .then((response) => {
          if (isThemeSnapshot(response)) applySnapshot(response);
        })
        .catch((error: unknown) => {
          const details = getError(error).details;
          if (isThemeSnapshot(details)) {
            setPreference(details.preference);
            setEffectiveTheme(details.effectiveTheme);
            const root = document.documentElement;
            root.setAttribute("data-theme", details.effectiveTheme);
            root.style.colorScheme = details.effectiveTheme;
          }
          setPersistenceWarning("write");
        });
    },
    [applySnapshot],
  );

  const retryPersistence = useCallback(() => {
    if (persistenceWarning === "read") {
      void readPreference()
        .then(applySnapshot)
        .catch(applyReadFailure);
      return;
    }

    setThemePreference(preference);
  }, [
    applyReadFailure,
    applySnapshot,
    persistenceWarning,
    preference,
    readPreference,
    setThemePreference,
  ]);

  const toggleTheme = useCallback(() => {
    const nextPreference =
      preference === "system" ? "light" : preference === "light" ? "dark" : "system";
    setThemePreference(nextPreference);
  }, [preference, setThemePreference]);

  if (!isReady) return null;

  return (
    <ThemeContext.Provider
      value={{
        preference,
        effectiveTheme,
        persistenceWarning,
        setThemePreference,
        retryPersistence,
        toggleTheme,
      }}
    >
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme(): ThemeContextType {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error("useTheme phải được sử dụng bên trong ThemeProvider");
  }
  return context;
}
