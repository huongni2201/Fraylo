import { lazy, Suspense, useEffect, useState, useCallback, type ReactNode } from "react";
import { ThemeProvider, useTheme } from "./context/ThemeContext";
import { bridgeClient } from "./services/bridgeClient";
import {
  Video,
  Settings,
  HardDrive,
  SunMedium,
  Moon,
  Monitor,
  ListOrdered,
  Sparkles,
  Images,
  BarChart3,
  FilePenLine,
  History,
  type LucideIcon,
} from "lucide-react";
import { BrandLogo } from "@/components/BrandLogo";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { jobApi } from "./features/jobs/jobApi";
import { ProjectListPage } from "./features/projects/ProjectListPage";
import { JobProgressPage } from "./features/jobs/JobProgressPage";
import { PublishingNavItem } from "./features/publishing/PublishingNavItem";
import { createPublishingApi } from "./features/publishing/publishingApi";
import { projectApi } from "./features/projects/projectApi";
import { cn } from "@/lib/utils";
import type { FacebookView } from "./features/facebook/FacebookPage";
import type { ThemePreference } from "./contracts/dtos";

export type ActiveRoute =
  | "projects"
  | "project-detail"
  | "prompt-templates"
  | "library"
  | "analytics"
  | "publishing"
  | "facebook-compose"
  | "facebook-queue"
  | "facebook-history"
  | "job-progress"
  | "settings";

const publishingApi = createPublishingApi((command, payload, options) =>
  bridgeClient.invoke(command, payload, options));

// Các màn hình chỉ tải khi mở lần đầu, giảm JS khởi động của trang Dự án.
const ProjectPage = lazy(() => import("./features/projects/ProjectPage").then(m => ({ default: m.ProjectPage })));
const PromptTemplateManager = lazy(() => import("./features/prompts/PromptTemplateManager").then(m => ({ default: m.PromptTemplateManager })));
const LibraryPage = lazy(() => import("./features/library/LibraryPage").then(m => ({ default: m.LibraryPage })));
const SettingsPage = lazy(() => import("./features/settings/SettingsPage").then(m => ({ default: m.SettingsPage })));
const AnalyticsPage = lazy(() => import("./features/analytics/AnalyticsPage").then(m => ({ default: m.AnalyticsPage })));
const PublishingPage = lazy(() => import("./features/publishing/PublishingPage").then(m => ({ default: m.PublishingPage })));

const FacebookPage = lazy(() => import("./features/facebook/FacebookPage").then(m => ({ default: m.FacebookPage })));

const THEME_OPTIONS: { value: ThemePreference; label: string; icon: LucideIcon }[] = [
  { value: "system", label: "Theo hệ thống", icon: Monitor },
  { value: "light", label: "Sáng", icon: SunMedium },
  { value: "dark", label: "Tối", icon: Moon },
];

/** Mục điều hướng: viền trái và nền nhạt cho mục đang mở, vùng bấm đủ lớn để dùng bằng chuột và bàn phím. */
const navItemClass = (active: boolean) =>
  cn(
    "nav-item relative flex h-10 w-full items-center gap-3 rounded-lg px-3 text-left text-sm transition-colors cursor-pointer",
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-sidebar",
    active
      ? "bg-primary/15 font-medium text-primary-text before:absolute before:inset-y-2 before:left-0 before:w-[3px] before:rounded-full before:bg-primary"
      : "text-sidebar-foreground hover:bg-muted hover:text-foreground",
  );

function NavItem({
  active,
  icon: Icon,
  label,
  onClick,
}: {
  active: boolean;
  icon: LucideIcon;
  label: string;
  onClick: () => void;
}) {
  return (
    <button type="button" className={navItemClass(active)} onClick={onClick} aria-current={active ? "page" : undefined}>
      <Icon className="size-4 shrink-0" aria-hidden="true" />
      <span className="truncate">{label}</span>
    </button>
  );
}

function NavGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <p className="px-3 pb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">{label}</p>
      {children}
    </div>
  );
}

function ThemeSwitch() {
  const { preference, setThemePreference } = useTheme();
  return (
    <div role="group" aria-label="Giao diện" className="grid grid-cols-3 gap-1 rounded-lg border border-sidebar-border bg-card/60 p-1">
      {THEME_OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          title={option.label}
          aria-label={option.label}
          aria-pressed={preference === option.value}
          onClick={() => setThemePreference(option.value)}
          className={cn(
            "flex h-8 cursor-pointer items-center justify-center rounded-md transition-colors",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            preference === option.value
              ? "bg-primary/15 text-primary-text"
              : "text-muted-foreground hover:bg-muted hover:text-foreground",
          )}
        >
          <option.icon className="size-4" aria-hidden="true" />
        </button>
      ))}
    </div>
  );
}

function AppContent() {
  const [activeRoute, setActiveRoute] = useState<ActiveRoute>("projects");
  const [platform, setPlatform] = useState<"tiktok" | "facebook">(() => {
    try { return localStorage.getItem("fraylo.platform") === "facebook" ? "facebook" : "tiktok"; } catch { return "tiktok"; }
  });
  const [lastTikTokRoute, setLastTikTokRoute] = useState<ActiveRoute>("publishing");
  const [lastFacebookRoute, setLastFacebookRoute] = useState<ActiveRoute>("facebook-compose");
  const [facebookPendingCount, setFacebookPendingCount] = useState(0);
  useEffect(() => {
    if (activeRoute === "publishing" || activeRoute === "analytics") {
      setLastTikTokRoute(activeRoute); setPlatform("tiktok");
    } else if (activeRoute.startsWith("facebook-")) {
      setLastFacebookRoute(activeRoute); setPlatform("facebook");
    }
  }, [activeRoute]);
  useEffect(() => { try { localStorage.setItem("fraylo.platform", platform); } catch { /* Lựa chọn chỉ lưu trong phiên khi storage không dùng được. */ } }, [platform]);
  // Đã mở một lần thì giữ mounted (ẩn bằng class) để bảo toàn bản nháp, như trước khi lazy-load.
  const [loadedRoutes, setLoadedRoutes] = useState<ReadonlySet<ActiveRoute>>(() => new Set());
  useEffect(() => {
    setLoadedRoutes(prev => (prev.has(activeRoute) ? prev : new Set(prev).add(activeRoute)));
  }, [activeRoute]);
  const isLoaded = (route: ActiveRoute) => activeRoute === route || loadedRoutes.has(route);
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
  const [publishTarget, setPublishTarget] = useState<{ projectId: string; revision: number } | null>(null);
  const [accountsNavigationSignal, setAccountsNavigationSignal] = useState(0);

  // Số tác vụ đang hoạt động, hiện trên mục Tiến trình
  const [activeJobCount, setActiveJobCount] = useState<number>(0);

  const fetchActiveJobCount = useCallback(async () => {
    try {
      const activeJobs = await jobApi.listActive();
      const count = activeJobs.filter(
        (j) =>
          j.state === "Running" ||
          j.state === "Queued" ||
          j.state === "Reserved" ||
          j.state === "AwaitingUser",
      ).length;
      setActiveJobCount(count);
    } catch {
      // Nuốt lỗi kết nối nền
    }
  }, []);

  useEffect(() => {
    fetchActiveJobCount();
    const unsubState = jobApi.onStateChanged(() => {
      fetchActiveJobCount();
    });
    return () => {
      unsubState();
    };
  }, [fetchActiveJobCount]);

  const [isSettingsDirty, setIsSettingsDirty] = useState<boolean>(false);
  const [pendingRoute, setPendingRoute] = useState<ActiveRoute | null>(null);

  const handleOpenSettings = () => {
    setActiveRoute("settings");
  };

  const handleNavigate = (targetRoute: ActiveRoute) => {
    if (activeRoute === targetRoute) return;
    if (activeRoute === "settings" && isSettingsDirty) {
      setPendingRoute(targetRoute);
      return;
    }
    if (targetRoute === "settings") {
      handleOpenSettings();
    } else {
      setActiveRoute(targetRoute);
    }
  };

  const handleConfirmDiscardSettings = () => {
    setIsSettingsDirty(false);
    if (pendingRoute) {
      setActiveRoute(pendingRoute);
      setPendingRoute(null);
    }
  };

  const handleCancelDiscardSettings = () => {
    setPendingRoute(null);
  };

  const handleNavigateToSettingsAccounts = useCallback(() => {
    setAccountsNavigationSignal(value => value + 1);
    handleNavigate("settings");
  }, [handleNavigate]);

  const handleOpenPublishing = (projectId: string) => {
    setPublishTarget(current => ({ projectId, revision: (current?.revision ?? 0) + 1 }));
    handleNavigate("publishing");
  };

  const handleReviewPublishedVideo = async (projectId: string) => {
    const project = (await projectApi.listAll()).find(item => item.projectId === projectId);
    if (!project) throw new Error("Dự án không còn tồn tại. Hãy tải lại danh sách video.");
    setSelectedProjectId(projectId);
    handleNavigate("project-detail");
  };

  const isProjectsActive = activeRoute === "projects" || activeRoute === "project-detail";
  const isPromptTemplatesActive = activeRoute === "prompt-templates";
  const isLibraryActive = activeRoute === "library";
  const isAnalyticsActive = activeRoute === "analytics";
  const isSettingsActive = activeRoute === "settings";
  const isJobProgressActive = activeRoute === "job-progress";

  return (
    <div className="flex h-screen w-screen overflow-hidden bg-background text-foreground antialiased font-sans select-none">
      {/* 1. SIDEBAR TRÁI (khóa tương tác khi hàng đợi đang mở) */}
      <aside
        className="flex h-screen w-60 min-w-60 shrink-0 flex-col border-r border-sidebar-border bg-sidebar text-sidebar-foreground select-none"
        aria-label="Điều hướng chính"
      >
        <div className="flex h-14 shrink-0 items-center gap-3 border-b border-sidebar-border px-4">
          <BrandLogo size={30} className="shrink-0" />
          <div className="flex min-w-0 flex-col">
            <span className="text-xl font-bold leading-none tracking-tight text-foreground">fraylo</span>
            <span className="mt-1 text-[11px] font-medium leading-none text-muted-foreground">Ideas into motion.</span>
          </div>
        </div>

        <nav className="sidebar-nav flex flex-1 flex-col gap-5 overflow-y-auto px-3 py-4" aria-label="Menu chức năng">
          <NavGroup label="Sản xuất">
            <NavItem active={isProjectsActive} icon={Video} label="Dự án" onClick={() => handleNavigate("projects")} />
            <NavItem
              active={isPromptTemplatesActive}
              icon={Sparkles}
              label="Prompt"
              onClick={() => handleNavigate("prompt-templates")}
            />
            <NavItem active={isLibraryActive} icon={Images} label="Tài nguyên" onClick={() => handleNavigate("library")} />
          </NavGroup>

          <NavGroup label="Nền tảng">
            <div role="group" aria-label="Chọn nền tảng" className="mb-2 grid grid-cols-2 gap-1 rounded-lg border border-sidebar-border p-1">
              {(["tiktok", "facebook"] as const).map(value => <button key={value} type="button" aria-pressed={platform === value}
                className={cn("h-9 rounded-md text-xs font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", platform === value ? "bg-primary/15 text-primary-text" : "text-muted-foreground hover:bg-muted")}
                onClick={() => { setPlatform(value); handleNavigate(value === "tiktok" ? lastTikTokRoute : lastFacebookRoute); }}>{value === "tiktok" ? "TikTok" : "Facebook"}</button>)}
            </div>
            <div className={platform === "tiktok" ? "contents" : "hidden"}>
              <NavItem active={isAnalyticsActive} icon={BarChart3} label="TikTok Analytics" onClick={() => handleNavigate("analytics")} />
              <PublishingNavItem active={activeRoute === "publishing"} onClick={() => handleNavigate("publishing")} />
            </div>
            <div className={platform === "facebook" ? "contents" : "hidden"}>
              <NavItem active={activeRoute === "facebook-compose"} icon={FilePenLine} label="Soạn bài" onClick={() => handleNavigate("facebook-compose")} />
              <button type="button" className={navItemClass(activeRoute === "facebook-queue")} onClick={() => handleNavigate("facebook-queue")} aria-current={activeRoute === "facebook-queue" ? "page" : undefined}>
                <ListOrdered className="size-4 shrink-0" aria-hidden="true" /><span className="truncate">Hàng đợi</span>
                {facebookPendingCount > 0 && <span className="ml-auto rounded-full bg-primary px-2 py-0.5 text-[11px] text-primary-foreground" aria-label={facebookPendingCount + " tác vụ Facebook đang chờ"}>{facebookPendingCount > 99 ? "99+" : facebookPendingCount}</span>}
              </button>
              <NavItem active={activeRoute === "facebook-history"} icon={History} label="Lịch sử" onClick={() => handleNavigate("facebook-history")} />
            </div>
          </NavGroup>

          <NavGroup label="Hệ thống">
            <button
              type="button"
              className={navItemClass(isJobProgressActive)}
              onClick={() => handleNavigate("job-progress")}
              aria-current={isJobProgressActive ? "page" : undefined}
            >
              <ListOrdered className="size-4 shrink-0" aria-hidden="true" />
              <span className="truncate">Tiến trình</span>
              {activeJobCount > 0 && (
                <span
                  className="ml-auto flex h-5 min-w-5 items-center justify-center rounded-full bg-primary px-1.5 text-[11px] font-bold leading-none text-primary-foreground tabular-nums"
                  aria-label={`${activeJobCount} tác vụ đang chạy`}
                >
                  {activeJobCount > 99 ? "99+" : activeJobCount}
                </span>
              )}
            </button>
            <button
              type="button"
              className={navItemClass(isSettingsActive)}
              onClick={() => handleNavigate("settings")}
              aria-current={isSettingsActive ? "page" : undefined}
            >
              <Settings className="size-4 shrink-0" aria-hidden="true" />
              <span className="truncate">Cài đặt</span>
            </button>
          </NavGroup>
        </nav>

        <div className="mt-auto flex shrink-0 flex-col gap-2 border-t border-sidebar-border p-3">
          <div className="flex items-center gap-2.5 rounded-lg border border-sidebar-border bg-card/60 p-2.5">
            <HardDrive className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <div className="flex min-w-0 flex-col">
              <span className="text-[11px] font-medium leading-tight text-muted-foreground">Dữ liệu</span>
              <span className="truncate text-xs font-semibold leading-tight text-foreground">Lưu trên máy này</span>
            </div>
          </div>
          <ThemeSwitch />
          <div className="py-0.5 text-center text-[11px] font-medium text-muted-foreground/70">Fraylo</div>
        </div>
      </aside>

      {/* 2. KHU VỰC CHÍNH (TOPBAR + NỘI DUNG) */}
      <div
        className="flex min-w-0 flex-1 flex-col overflow-hidden bg-background"
      >
        {/* VÙNG NỘI DUNG CHÍNH (BẢO TOÀN DRAFT KHI ĐỔI VIEW/ROUTE) */}
        <main className="flex-1 overflow-y-auto">
          <div className="mx-auto w-full max-w-[1400px] px-6 py-6 lg:px-8 lg:py-8">
            {/* CÀI ĐẶT */}
            <div className={activeRoute === "settings" ? "block" : "hidden"}>
              {isLoaded("settings") && (
                <Suspense fallback={null}>
                  <SettingsPage
                    onDirtyChange={setIsSettingsDirty}
                    accountsNavigationSignal={accountsNavigationSignal}
                  />
                </Suspense>
              )}
            </div>

            {/* DỰ ÁN & PHÂN CẢNH */}
            <div className={isProjectsActive ? "block" : "hidden"}>
              {selectedProjectId ? (
                <Suspense fallback={null}>
                  <ProjectPage
                    projectId={selectedProjectId}
                    onBack={() => setSelectedProjectId(null)}
                    onOpenPublishing={handleOpenPublishing}
                  />
                </Suspense>
              ) : (
                <ProjectListPage
                  onSelectProject={(projectId) => {
                    setSelectedProjectId(projectId);
                  }}
                />
              )}
            </div>

            <div className={isPromptTemplatesActive ? "block" : "hidden"}>
              <div className="flex flex-col gap-6">
                <PageHeader title="Prompt" description="Quản lý các mẫu lời dẫn AI dùng cho video." />
                {isLoaded("prompt-templates") && (
                  <Suspense fallback={null}>
                    <PromptTemplateManager />
                  </Suspense>
                )}
              </div>
            </div>

            <div className={isLibraryActive ? "block" : "hidden"}>
              <div className="flex flex-col gap-6">
                <PageHeader title="Tài nguyên" description="Quản lý hình ảnh, âm thanh và video dùng trong các dự án." />
                {isLoaded("library") && (
                  <Suspense fallback={null}>
                    <LibraryPage />
                  </Suspense>
                )}
              </div>
            </div>

            <div className={isAnalyticsActive ? "block" : "hidden"}>
              {isLoaded("analytics") && (
                <Suspense fallback={null}>
                  <AnalyticsPage active={isAnalyticsActive} />
                </Suspense>
              )}
            </div>

            <div className={activeRoute === "publishing" ? "block" : "hidden"}>
              {isLoaded("publishing") && (
                <Suspense fallback={null}>
                  <PublishingPage
                    active={activeRoute === "publishing"}
                    api={publishingApi}
                    initialProjectId={publishTarget?.projectId}
                    selectionRevision={publishTarget?.revision}
                    onOpenAccounts={handleNavigateToSettingsAccounts}
                    onReviewVideo={handleReviewPublishedVideo}
                  />
                </Suspense>
              )}
            </div>

            <div className={activeRoute.startsWith("facebook-") ? "block" : "hidden"}>
              {(["facebook-compose", "facebook-queue", "facebook-history"] as ActiveRoute[]).some(isLoaded) && <Suspense fallback={null}>
                <FacebookPage active={activeRoute.startsWith("facebook-")} view={(activeRoute.startsWith("facebook-") ? activeRoute.slice(9) : lastFacebookRoute.slice(9)) as FacebookView}
                  onNavigate={view => handleNavigate(("facebook-" + view) as ActiveRoute)} onOpenAccounts={handleNavigateToSettingsAccounts} onPendingCount={setFacebookPendingCount} />
              </Suspense>}
            </div>

            <div className={isJobProgressActive ? "block" : "hidden"}>
              <JobProgressPage active={isJobProgressActive} />
            </div>
          </div>
        </main>
      </div>

      {/* XÁC NHẬN BỎ THAY ĐỔI CHƯA LƯU CỦA CÀI ĐẶT */}
      {pendingRoute && (
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="app-dirty-confirm-title"
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-xs animate-in fade-in-0"
        >
          <div
            role="alertdialog"
            aria-labelledby="app-dirty-confirm-title"
            aria-describedby="app-dirty-confirm-desc"
            className="flex w-full max-w-md flex-col gap-4 rounded-xl border border-border bg-card p-6 shadow-xl animate-in zoom-in-95"
          >
            <div className="flex flex-col gap-1.5">
              <h3 id="app-dirty-confirm-title" className="text-base font-semibold text-foreground">
                Cài đặt chưa được lưu
              </h3>
              <p id="app-dirty-confirm-desc" className="text-sm leading-relaxed text-muted-foreground">
                Bạn đang có thay đổi chưa lưu trong Cài đặt. Bỏ thay đổi để chuyển trang?
              </p>
            </div>
            <div className="flex items-center justify-end gap-2 pt-2">
              <Button type="button" variant="outline" size="sm" onClick={handleCancelDiscardSettings}>
                Ở lại
              </Button>
              <Button type="button" variant="destructive" size="sm" onClick={handleConfirmDiscardSettings}>
                Bỏ thay đổi
              </Button>
            </div>
          </div>
        </div>
      )}

    </div>
  );
}

export default function App() {
  return (
    <ThemeProvider>
      <AppContent />
    </ThemeProvider>
  );
}
