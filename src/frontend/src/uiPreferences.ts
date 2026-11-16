/**
 * Non-sensitive, browser-local interface settings only.
 * Never persist access tokens, tenant IDs, perizia data or document metadata.
 * Values are allowlisted and storage access is best-effort (private mode,
 * disabled cookies and enterprise browser policies must not break the UI).
 */
export type UiLanguage = "it" | "en";
export type UiTheme = "light" | "dark";

export const UI_LANGUAGE_KEY = "peritial.ui.language";
export const UI_THEME_KEY = "peritial.ui.theme";

function read(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Persistence is optional; retain the active in-memory preference.
  }
}

export function preferredLanguage(): UiLanguage {
  return read(UI_LANGUAGE_KEY) === "en" ? "en" : "it";
}

export function preferredTheme(): UiTheme {
  return read(UI_THEME_KEY) === "dark" ? "dark" : "light";
}

export function saveLanguage(language: UiLanguage): void {
  write(UI_LANGUAGE_KEY, language);
}

export function saveTheme(theme: UiTheme): void {
  write(UI_THEME_KEY, theme);
}
