/**
 * DTOs cho các lệnh Gate G1 theo contracts.md v1.0.4
 */

export type ThemePreference = "system" | "light" | "dark";
export type EffectiveTheme = "light" | "dark";

export type ThemePreferenceDto = {
  preference: ThemePreference;
  effectiveTheme: EffectiveTheme;
};

export type ThemeSetPreferencePayload = {
  preference: ThemePreference;
};

export type SettingsDto = {
  documentsPath: string;
  imagesPath: string;
  videosPath: string;
  documentsRootId: string;
  imagesRootId: string;
  videosRootId: string;
  imageProvider: string;
  scriptProvider: string;
  videoPromptProvider: string;
  themePreference: ThemePreference;
  effectiveTheme: EffectiveTheme;
};

export type SaveSettingsRequestPayload = {
  documentsPath: string;
  imagesPath: string;
  videosPath: string;
  imageProvider: string;
  scriptProvider: string;
  videoPromptProvider: string;
};

export type BrowseFolderRequestPayload = {
  target: "documents" | "images" | "videos";
  initialPath: string | null;
};

export type BrowseFolderResponseData = {
  selectedPath: string | null;
};

export type ValidateRootsRequestPayload = {
  documentsPath: string;
  imagesPath: string;
  videosPath: string;
};

export type ValidateRootsResponseData = {
  isValid: boolean;
  fieldErrors: Record<string, string[]>;
};
