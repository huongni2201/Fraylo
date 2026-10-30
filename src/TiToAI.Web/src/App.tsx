import { lazy, Suspense, useEffect, useRef, useState, useCallback, type ReactNode } from "react";
import { ThemeProvider, useTheme } from "./context/ThemeContext";
import { bridgeClient } from "./services/bridgeClient";
import {
  Video,
  Settings,
  HardDrive,
  SunMedium,
  Moon,
  Monitor,
  Check,
  ChevronRight,
  ListOrdered,
  Sparkles,
  Images,
  BarChart3,
  type LucideIcon,
} from "lucide-react";
import { BrandLogo } from "@/components/BrandLogo";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { QueueDrawer } from "./components/QueueDrawer";
import { jobApi } from "./features/jobs/jobApi";
import type { JobSummaryDto, JobDetailDto } from "./features/jobs/jobTypes";
import { ProjectListPage } from "./features/projects/ProjectListPage";
import { JobProgressPage } from "./features/jobs/JobProgressPage";
import { PublishingNavItem } from "./features/publishing/PublishingNavItem";
import { createPublishingApi } from "./features/publishing/publishingApi";
import { projectApi } from "./features/projects/projectApi";
import { cn } from "@/lib/utils";
import type { ThemePreference } from "./contracts/dtos";

export type ActiveRoute =
  | "projects"
  | "project-detail"
  | "prompt-templates"
  | "library"
  | "analytics"
  | "publishing"
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

function ThemeMenu() {
  const { preference, effectiveTheme, setThemePreference } = useTheme();
  const CurrentIcon = effectiveTheme === "dark" ? Moon : SunMedium;
  const currentLabel = THEME_OPTIONS.find((o) => o.value === preference)?.label ?? "Theo hệ thống";

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="size-9 rounded-lg text-muted-foreground hover:text-foreground"
          aria-label={`Giao diện: ${currentLabel}`}
          title={`Giao diện: ${currentLabel}`}
        >
          <CurrentIcon className="size-4" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-44">
        {THEME_OPTIONS.map((option) => (
          <DropdownMenuItem key={option.value} onSelect={() => setThemePreference(option.value)}>
            <option.icon className="size-4" aria-hidden="true" />
            <span>{option.label}</span>
            {preference === option.value && <Check className="ml-auto size-4 text-primary-text" aria-hidden="true" />}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function AppContent() {
  const [activeRoute, setActiveRoute] = useState<ActiveRoute>("projects");
  // Đã mở một lần thì giữ mounted (ẩn bằng class) để bảo toàn bản nháp, như trước khi lazy-load.
  const [loadedRoutes, setLoadedRoutes] = useState<ReadonlySet<ActiveRoute>>(() => new Set());
  useEffect(() => {
    setLoadedRoutes(prev => (prev.has(activeRoute) ? prev : new Set(prev).add(activeRoute)));
  }, [activeRoute]);
  const isLoaded = (route: ActiveRoute) => activeRoute === route || loadedRoutes.has(route);
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
  const [isQueueOpen, setIsQueueOpen] = useState(false);
  const [publishTarget, setPublishTarget] = useState<{ projectId: string; revision: number } | null>(null);
  const [accountsNavigationSignal, setAccountsNavigationSignal] = useState(0);

  // References cho việc phục hồi focus sau khi đóng drawer.
  const settingsBtnRef = useRef<HTMLButtonElement>(null);
  const queueBtnRef = useRef<HTMLButtonElement>(null);

  // Quản lý số lượng tác vụ đang hoạt động thực tế trên Topbar
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
    setIsQueueOpen(false);
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

  const handleOpenQueue = () => {
    setIsQueueOpen(true);
  };

  const handleNavigateToJob = useCallback(
    (job: JobSummaryDto | JobDetailDto) => {
      setIsQueueOpen(false);
      if (job.projectId) {
        setSelectedProjectId(job.projectId);
        setActiveRoute("project-detail");
      }
    },
    [],
  );

  const handleNavigateToSettingsAccounts = useCallback(() => {
    setIsQueueOpen(false);
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

  const isModalActive = isQueueOpen;

  const getBreadcrumbTitle = () => {
    switch (activeRoute) {
      case "projects":
      case "project-detail":
        return "Dự án";
      case "prompt-templates":
        return "Prompt";
      case "library":
        return "Tài nguyên";
      case "analytics":
        return "TikTok Analytics";
      case "publishing":
        return "Đăng video";
      case "job-progress":
        return "Tiến trình";
      case "settings":
        return "Cài đặt";
      default:
        return "TiToAI";
    }
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
        inert={isModalActive}
        aria-hidden={isModalActive ? true : undefined}
      >
        <div className="flex h-14 shrink-0 items-center gap-3 border-b border-sidebar-border px-4">
          <BrandLogo size={24} className="shrink-0 text-primary" />
          <div className="flex min-w-0 flex-col">
            <span className="text-base font-bold leading-none tracking-tight text-foreground">TiToAI</span>
            <span className="mt-1 text-[11px] font-medium leading-none text-muted-foreground">Desktop Studio</span>
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

          <NavGroup label="Đăng & phân tích">
            <NavItem
              active={isAnalyticsActive}
              icon={BarChart3}
              label="TikTok Analytics"
              onClick={() => handleNavigate("analytics")}
            />
            <PublishingNavItem active={activeRoute === "publishing"} onClick={() => handleNavigate("publishing")} />
          </NavGroup>

          <NavGroup label="Hệ thống">
            <NavItem
              active={isJobProgressActive}
              icon={ListOrdered}
              label="Tiến trình"
              onClick={() => handleNavigate("job-progress")}
            />
            <button
              ref={settingsBtnRef}
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
          <div className="py-0.5 text-center text-[11px] font-medium text-muted-foreground/70">TiToAI v0.1.0</div>
        </div>
      </aside>

      {/* 2. KHU VỰC CHÍNH (TOPBAR + NỘI DUNG) */}
      <div
        className="flex min-w-0 flex-1 flex-col overflow-hidden bg-background"
        inert={isModalActive}
        aria-hidden={isModalActive ? true : undefined}
      >
        <header className="flex h-14 shrink-0 items-center justify-between gap-4 border-b border-border bg-background/80 px-6 backdrop-blur supports-[backdrop-filter]:bg-background/70">
          <nav aria-label="Breadcrumb" className="flex min-w-0 items-center gap-1.5 text-sm">
            <span className="text-muted-foreground">TiToAI</span>
            <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/60" aria-hidden="true" />
            <span className="truncate font-medium text-foreground" aria-current="page">
              {getBreadcrumbTitle()}
            </span>
          </nav>

          <div className="flex shrink-0 items-center gap-1">
            <ThemeMenu />

            {/* Nút mở hàng đợi tác vụ kèm badge */}
            <Button
              ref={queueBtnRef}
              type="button"
              variant="ghost"
              size="icon"
              className="queue-btn relative size-9 rounded-lg text-muted-foreground hover:text-foreground"
              onClick={handleOpenQueue}
              title="Hàng đợi tác vụ"
              aria-label={activeJobCount > 0 ? `Mở hàng đợi tác vụ, ${activeJobCount} tác vụ đang chạy` : "Mở hàng đợi tác vụ"}
              aria-haspopup="dialog"
              aria-expanded={isQueueOpen}
            >
              <ListOrdered className="size-4" />
              {activeJobCount > 0 && (
                <span
                  className="absolute -right-1 -top-1 flex size-4 items-center justify-center rounded-full bg-primary text-[10px] font-bold leading-none text-primary-foreground tabular-nums animate-in zoom-in-50"
                  aria-hidden="true"
                >
                  {activeJobCount > 99 ? "99+" : activeJobCount}
                </span>
              )}
            </Button>
          </div>
        </header>

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

      {/* QUEUE DRAWER */}
      <QueueDrawer
        isOpen={isQueueOpen}
        onClose={() => setIsQueueOpen(false)}
        triggerRef={queueBtnRef}
        onNavigateToJob={handleNavigateToJob}
        onNavigateToSettingsAccounts={handleNavigateToSettingsAccounts}
      />
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
