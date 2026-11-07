import React, { useState, useRef, useCallback, useEffect } from "react";
import { Check, Info, Settings } from "lucide-react";
import { useTheme } from "../../context/ThemeContext";
import { AccountSettingsPanel } from "./AccountSettingsPanel";
import { StorageSettingsPanel } from "./StorageSettingsPanel";
import { AiProviderSettingsPanel } from "./AiProviderSettingsPanel";
import { cn } from "../../lib/utils";
import { PageHeader } from "../../components/PageHeader";
import { updatesApi, type UpdateInfo } from "./updatesApi";

export type SettingsTab = "accounts" | "storage" | "ai" | "appearance";

export interface SettingsPageProps {
  initialTab?: SettingsTab;
  onDirtyChange?: (isDirty: boolean) => void;
  resetSignal?: number;
  accountsNavigationSignal?: number;
  aiNavigationSignal?: number;
}

// shadcn UI compatible primitives local to Settings to avoid test-loader dual React declaration quirks
function Button({
  className,
  variant = "default",
  size = "default",
  ...props
}: React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "default" | "outline" | "secondary" | "destructive" | "ghost";
  size?: "default" | "sm" | "lg" | "icon";
}) {
  const variantClasses = {
    default: "bg-primary text-primary-foreground shadow-sm hover:bg-primary/90",
    outline: "border border-input bg-background shadow-xs hover:bg-accent hover:text-accent-foreground",
    secondary: "bg-secondary text-secondary-foreground shadow-xs hover:bg-secondary/80",
    destructive: "bg-destructive text-destructive-foreground shadow-xs hover:bg-destructive/90",
    ghost: "hover:bg-accent hover:text-accent-foreground",
  };
  const sizeClasses = {
    default: "h-9 px-4 py-2",
    sm: "h-8 rounded-md px-3 text-xs",
    lg: "h-10 rounded-md px-8",
    icon: "h-9 w-9",
  };
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ring-offset-background disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0",
        variantClasses[variant],
        sizeClasses[size],
        className
      )}
      {...props}
    />
  );
}

function Card({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn("rounded-xl border border-border bg-card text-card-foreground shadow-sm", className)}
      {...props}
    />
  );
}

function CardHeader({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("flex flex-col space-y-1.5 p-6", className)} {...props} />;
}

function CardTitle({ className, ...props }: React.HTMLAttributes<HTMLHeadingElement>) {
  return <h3 className={cn("font-semibold leading-none tracking-tight text-lg", className)} {...props} />;
}

function CardDescription({ className, ...props }: React.HTMLAttributes<HTMLParagraphElement>) {
  return <p className={cn("text-sm text-muted-foreground", className)} {...props} />;
}

function CardContent({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("p-6 pt-0", className)} {...props} />;
}

function Alert({
  className,
  variant = "default",
  ...props
}: React.HTMLAttributes<HTMLDivElement> & {
  variant?: "default" | "destructive" | "warning" | "info" | "success";
}) {
  const variantClasses = {
    default: "bg-background text-foreground border-border",
    destructive: "border-destructive/50 text-destructive dark:border-destructive [&>svg]:text-destructive",
    warning: "border-warning/50 text-warning dark:text-warning [&>svg]:text-warning",
    info: "border-info/50 text-info dark:text-info [&>svg]:text-info",
    success: "border-success/50 text-success dark:text-success [&>svg]:text-success",
  };
  return (
    <div
      role="alert"
      className={cn(
        "relative w-full rounded-lg border px-4 py-3 text-sm [&>svg+div]:translate-y-[-3px] [&>svg]:absolute [&>svg]:left-4 [&>svg]:top-4 [&>svg]:text-foreground [&>svg~*]:pl-7",
        variantClasses[variant],
        className
      )}
      {...props}
    />
  );
}

function AlertTitle({ className, ...props }: React.HTMLAttributes<HTMLHeadingElement>) {
  return <h5 className={cn("mb-1 font-medium leading-none tracking-tight", className)} {...props} />;
}

function AlertDescription({ className, ...props }: React.HTMLAttributes<HTMLParagraphElement>) {
  return <div className={cn("text-sm [&_p]:leading-relaxed", className)} {...props} />;
}

function AppUpdateCard() {
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError((e as { message?: string })?.message ?? "Không thể cập nhật.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Phiên bản & cập nhật</CardTitle>
        <CardDescription>
          Kiểm tra bản mới trên GitHub Releases. Dữ liệu được sao lưu tự động trước khi cập nhật.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {info && (
          <p className="text-sm">
            Hiện tại <b>{info.currentVersion}</b>
            {info.hasUpdate ? <> — có bản mới <b>{info.latestVersion}</b></> : " — đang là bản mới nhất."}
          </p>
        )}
        {info?.hasUpdate && info.releaseNotes.length > 0 && (
          <ul className="list-disc pl-5 text-sm text-muted-foreground">
            {info.releaseNotes.map((note) => <li key={note}>{note}</li>)}
          </ul>
        )}
        {error && <Alert variant="destructive">{error}</Alert>}
        <div className="flex gap-2">
          <Button variant="outline" disabled={busy} onClick={() => run(async () => setInfo(await updatesApi.check()))}>
            Kiểm tra cập nhật
          </Button>
          {info?.hasUpdate && (
            <Button disabled={busy} onClick={() => run(() => updatesApi.apply())}>
              {busy ? "Đang tải..." : `Cập nhật lên ${info.latestVersion}`}
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

export function SettingsPage({ initialTab = "accounts", onDirtyChange, resetSignal, accountsNavigationSignal, aiNavigationSignal }: SettingsPageProps) {
  const [activeTab, setActiveTab] = useState<SettingsTab>(initialTab);
  const [isStorageDirty, setIsStorageDirty] = useState<boolean>(false);
  const [isAiDirty, setIsAiDirty] = useState<boolean>(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const isPageDirty = isStorageDirty || isAiDirty;

  useEffect(() => {
    if (accountsNavigationSignal) setActiveTab("accounts");
  }, [accountsNavigationSignal]);

  useEffect(() => {
    if (aiNavigationSignal) setActiveTab("ai");
  }, [aiNavigationSignal]);

  useEffect(() => {
    onDirtyChange?.(isPageDirty);
  }, [isPageDirty, onDirtyChange]);

  const {
    preference,
    persistenceWarning,
    retryPersistence,
    setThemePreference,
  } = useTheme();

  const tabs: { key: SettingsTab; label: string; indexStr: string }[] = [
    { key: "accounts", label: "Tài khoản", indexStr: "1. Tài khoản" },
    { key: "storage", label: "Lưu trữ", indexStr: "2. Lưu trữ" },
    { key: "ai", label: "AI & Prompt", indexStr: "3. AI & Prompt" },
    { key: "appearance", label: "Giao diện", indexStr: "4. Giao diện" },
  ];

  // Điều hướng bằng phím mũi tên giữa các Tab theo chuẩn WAI-ARIA
  const handleTabListKeyDown = useCallback(
    (e: React.KeyboardEvent, currentTab: SettingsTab) => {
      const tabKeys: SettingsTab[] = ["accounts", "storage", "ai", "appearance"];
      const currentIndex = tabKeys.indexOf(currentTab);
      let nextIndex = -1;

      if (e.key === "ArrowRight" || e.key === "ArrowDown") {
        e.preventDefault();
        nextIndex = (currentIndex + 1) % tabKeys.length;
      } else if (e.key === "ArrowLeft" || e.key === "ArrowUp") {
        e.preventDefault();
        nextIndex = (currentIndex - 1 + tabKeys.length) % tabKeys.length;
      } else if (e.key === "Home") {
        e.preventDefault();
        nextIndex = 0;
      } else if (e.key === "End") {
        e.preventDefault();
        nextIndex = tabKeys.length - 1;
      }

      if (nextIndex !== -1) {
        const nextTab = tabKeys[nextIndex];
        setActiveTab(nextTab);
        const tabBtn = containerRef.current?.querySelector<HTMLButtonElement>(
          `#tab-${nextTab}`
        );
        tabBtn?.focus();
      }
    },
    []
  );

  // Điều hướng bằng phím mũi tên trong nhóm Radio Theme
  const handleThemeKeyDown = useCallback(
    (e: React.KeyboardEvent, currentTheme: "system" | "light" | "dark") => {
      const themes: Array<"system" | "light" | "dark"> = [
        "system",
        "light",
        "dark",
      ];
      const currentIndex = themes.indexOf(currentTheme);
      let nextIndex = -1;

      if (e.key === "ArrowRight" || e.key === "ArrowDown") {
        e.preventDefault();
        nextIndex = (currentIndex + 1) % themes.length;
      } else if (e.key === "ArrowLeft" || e.key === "ArrowUp") {
        e.preventDefault();
        nextIndex = (currentIndex - 1 + themes.length) % themes.length;
      }

      if (nextIndex !== -1) {
        const nextTheme = themes[nextIndex];
        setThemePreference(nextTheme);
        const card = containerRef.current?.querySelector<HTMLButtonElement>(
          `#theme-option-${nextTheme}`
        );
        card?.focus();
      }
    },
    [setThemePreference]
  );

  return (
    <div className="flex flex-col gap-6 max-w-6xl mx-auto w-full" ref={containerRef}>
      <PageHeader
        title={
          <span className="flex items-center gap-2.5">
            <Settings className="h-6 w-6 text-primary" aria-hidden="true" />
            Cài đặt hệ thống
          </span>
        }
        description="Quản lý tài khoản AI & mạng xã hội, đường dẫn lưu trữ, dịch vụ AI và giao diện ứng dụng"
        className="border-b border-border/50 pb-4"
      />

      {/* 4 TAB NGANG NẰM TRONG APP SHELL (WAI-ARIA TABLIST) */}
      <div
        className="inline-flex h-11 items-center justify-start rounded-lg bg-muted/60 p-1 text-muted-foreground w-full sm:w-auto border border-border/60 gap-1 overflow-x-auto"
        role="tablist"
        aria-label="Các nhóm cài đặt Fraylo"
      >
        {tabs.map((tab) => {
          const isSelected = activeTab === tab.key;
          return (
            <button
              key={tab.key}
              type="button"
              role="tab"
              id={`tab-${tab.key}`}
              aria-controls={`panel-${tab.key}`}
              aria-selected={isSelected}
              tabIndex={isSelected ? 0 : -1}
              className={cn(
                "inline-flex items-center justify-center whitespace-nowrap rounded-md px-4 py-2 text-sm font-medium transition-[color,background-color,border-color,box-shadow,opacity,transform] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50",
                isSelected
                  ? "bg-background text-foreground shadow-xs font-semibold"
                  : "text-muted-foreground hover:bg-background/50 hover:text-foreground"
              )}
              onClick={() => setActiveTab(tab.key)}
              onKeyDown={(e) => handleTabListKeyDown(e, tab.key)}
            >
              {tab.indexStr}
            </button>
          );
        })}
      </div>

      {/* BODY NỘI DUNG TỪNG TAB */}
      <div className="mt-1">
        {/* 1. TAB TÀI KHOẢN */}
        <div
          role="tabpanel"
          id="panel-accounts"
          aria-labelledby="tab-accounts"
          tabIndex={0}
          className={cn("focus-visible:outline-none", activeTab !== "accounts" && "hidden")}
          hidden={activeTab !== "accounts"}
        >
          <AccountSettingsPanel />
        </div>

        {/* 2. TAB LƯU TRỮ */}
        <div
          role="tabpanel"
          id="panel-storage"
          aria-labelledby="tab-storage"
          tabIndex={0}
          className={cn("focus-visible:outline-none", activeTab !== "storage" && "hidden")}
          hidden={activeTab !== "storage"}
        >
          <StorageSettingsPanel
            onDirtyChange={setIsStorageDirty}
            resetSignal={resetSignal}
          />
        </div>

        {/* 3. TAB AI & PROMPT */}
        <div
          role="tabpanel"
          id="panel-ai"
          aria-labelledby="tab-ai"
          tabIndex={0}
          aria-label="Nhóm cài đặt AI & Prompt"
          className={cn("space-y-6 focus-visible:outline-none", activeTab !== "ai" && "hidden")}
          hidden={activeTab !== "ai"}
        >
          <AiProviderSettingsPanel
            onDirtyChange={setIsAiDirty}
            resetSignal={resetSignal}
            onNavigateToAccounts={() => setActiveTab("accounts")}
          />
        </div>

        {/* 4. TAB GIAO DIỆN */}
        <div
          role="tabpanel"
          id="panel-appearance"
          aria-labelledby="tab-appearance"
          tabIndex={0}
          className={cn("space-y-6 focus-visible:outline-none", activeTab !== "appearance" && "hidden")}
          hidden={activeTab !== "appearance"}
        >
          <Card>
            <CardHeader>
              <CardTitle>Giao diện hiển thị</CardTitle>
              <CardDescription>
                Áp dụng ngay qua giao diện, hỗ trợ Theo hệ thống (System), Sáng (Light) và Tối (Dark).
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <div
                className="grid grid-cols-1 md:grid-cols-3 gap-4"
                role="radiogroup"
                aria-label="Tùy chọn giao diện ứng dụng"
              >
                <button
                  type="button"
                  role="radio"
                  id="theme-option-system"
                  aria-checked={preference === "system"}
                  tabIndex={preference === "system" ? 0 : -1}
                  className={cn(
                    "flex flex-col text-left rounded-xl border-2 p-4 transition-[color,background-color,border-color,box-shadow,opacity,transform] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                    preference === "system"
                      ? "border-primary bg-primary/5 shadow-xs"
                      : "border-border hover:border-muted-foreground/30 bg-card"
                  )}
                  onClick={() => setThemePreference("system")}
                  onKeyDown={(e) => handleThemeKeyDown(e, "system")}
                >
                  <div className="flex items-center justify-between w-full mb-1.5">
                    <span className="font-semibold text-sm text-foreground">Theo hệ thống (System)</span>
                    {preference === "system" && <Check className="h-4 w-4 text-primary" aria-hidden="true" />}
                  </div>
                  <p className="text-xs text-muted-foreground">Tự động đồng bộ theo cấu hình sáng/tối của Windows.</p>
                </button>

                <button
                  type="button"
                  role="radio"
                  id="theme-option-light"
                  aria-checked={preference === "light"}
                  tabIndex={preference === "light" ? 0 : -1}
                  className={cn(
                    "flex flex-col text-left rounded-xl border-2 p-4 transition-[color,background-color,border-color,box-shadow,opacity,transform] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                    preference === "light"
                      ? "border-primary bg-primary/5 shadow-xs"
                      : "border-border hover:border-muted-foreground/30 bg-card"
                  )}
                  onClick={() => setThemePreference("light")}
                  onKeyDown={(e) => handleThemeKeyDown(e, "light")}
                >
                  <div className="flex items-center justify-between w-full mb-1.5">
                    <span className="font-semibold text-sm text-foreground">Giao diện Sáng (Light)</span>
                    {preference === "light" && <Check className="h-4 w-4 text-primary" aria-hidden="true" />}
                  </div>
                  <p className="text-xs text-muted-foreground">Tông màu sáng, nền sạch, độ tương phản cao.</p>
                </button>

                <button
                  type="button"
                  role="radio"
                  id="theme-option-dark"
                  aria-checked={preference === "dark"}
                  tabIndex={preference === "dark" ? 0 : -1}
                  className={cn(
                    "flex flex-col text-left rounded-xl border-2 p-4 transition-[color,background-color,border-color,box-shadow,opacity,transform] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                    preference === "dark"
                      ? "border-primary bg-primary/5 shadow-xs"
                      : "border-border hover:border-muted-foreground/30 bg-card"
                  )}
                  onClick={() => setThemePreference("dark")}
                  onKeyDown={(e) => handleThemeKeyDown(e, "dark")}
                >
                  <div className="flex items-center justify-between w-full mb-1.5">
                    <span className="font-semibold text-sm text-foreground">Giao diện Tối (Dark)</span>
                    {preference === "dark" && <Check className="h-4 w-4 text-primary" aria-hidden="true" />}
                  </div>
                  <p className="text-xs text-muted-foreground">Nền tối ấm nhiều lớp, accent cam-đỏ; dịu mắt khi làm việc lâu.</p>
                </button>
              </div>

              {persistenceWarning && (
                <Alert variant="warning" role="alert" aria-live="polite">
                  <Info className="h-4 w-4" aria-hidden="true" />
                  <AlertTitle>Giao diện chưa được lưu.</AlertTitle>
                  <AlertDescription className="flex flex-wrap items-center justify-between gap-2 mt-1">
                    <span>
                      {persistenceWarning === "read"
                        ? "Ứng dụng đang dùng Theo hệ thống; cấu hình đã lưu chưa bị ghi đè."
                        : "Giao diện đang có hiệu lực trong phiên này nhưng chưa lưu được."}
                    </span>
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      onClick={retryPersistence}
                    >
                      Thử lại
                    </Button>
                  </AlertDescription>
                </Alert>
              )}

              <Alert variant="info">
                <Info className="h-4 w-4" aria-hidden="true" />
                <AlertTitle>Giao diện:</AlertTitle>
                <AlertDescription>
                  Thay đổi có hiệu lực ngay lập tức và được tự động lưu bền vững.
                </AlertDescription>
              </Alert>
            </CardContent>
          </Card>

          <AppUpdateCard />
        </div>
      </div>
    </div>
  );
}
