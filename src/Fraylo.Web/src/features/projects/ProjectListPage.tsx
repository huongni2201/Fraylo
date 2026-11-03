import { useState, useEffect, useMemo, useCallback, useRef } from "react";
import { projectApi, projectGroupApi, STATUS_LABEL, type ProjectGroupDto, type ProjectSummaryDto } from "./projectApi";
import { ProjectForm } from "./ProjectForm";
import { ProjectGroupCreateDialog } from "./ProjectGroupCreateDialog";
import { BulkVideoCreateDialog } from "./BulkVideoCreateDialog";
import { ProjectGroupSettings } from "./ProjectGroupSettings";
import type { ErrorDto } from "../../contracts/bridge";
import {
  Card,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogCancel,
} from "@/components/ui/alert-dialog";
import {
  ArrowLeft,
  ArrowRight,
  ChevronRight,
  Plus,
  Search,
  RefreshCw,
  Loader2,
  AlertCircle,
  MoreHorizontal,
  Pencil,
  Trash2,
  Info,
  Film,
  Inbox,
  Layers,
  Rocket,
} from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { cn } from "@/lib/utils";

export interface ProjectListPageProps {
  onSelectProject: (projectId: string) => void;
}

type StatusFilter = "all" | "Draft" | "InProgress" | "Completed";

/** null: danh sách nhóm; UNGROUPED: dự án chưa thuộc nhóm; còn lại là groupId. */
const UNGROUPED = "__ungrouped__";
type GroupScope = string | null;

interface GroupStats {
  total: number;
  inProgress: number;
  completed: number;
}

type GroupTab = "projects" | "settings";

const STATUS_TABS: { value: StatusFilter; label: string }[] = [
  { value: "all", label: "Tất cả" },
  { value: "InProgress", label: "Đang sản xuất" },
  { value: "Completed", label: "Hoàn thành" },
  { value: "Draft", label: "Nháp" },
];

/** 12 thẻ = 2 hàng ở mọi breakpoint (2, 3, 4, 6 cột). */
const PAGE_SIZE = 12;

const segmentTabClass =
  "inline-flex items-center justify-center whitespace-nowrap rounded-md px-3 py-1 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";

const STATUS_VARIANT: Record<string, "secondary" | "info" | "success"> = {
  Draft: "secondary",
  InProgress: "info",
  Completed: "success",
};

function countByStatus(list: ProjectSummaryDto[]): GroupStats {
  return {
    total: list.length,
    inProgress: list.filter((p) => p.status === "InProgress").length,
    completed: list.filter((p) => p.status === "Completed").length,
  };
}

interface GroupCardProps {
  name: string;
  description: string;
  stats: GroupStats;
  ungrouped?: boolean;
  onOpen: () => void;
}

function GroupCard({ name, description, stats, ungrouped = false, onOpen }: GroupCardProps) {
  const percent = stats.total ? Math.round((stats.completed / stats.total) * 100) : 0;
  return (
    <button
      type="button"
      onClick={onOpen}
      className={cn(
        "group flex flex-col gap-4 rounded-xl border bg-card p-5 text-left shadow-xs transition-colors hover:border-primary/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
        ungrouped ? "border-dashed bg-transparent" : "border-border",
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <div
          className={cn(
            "flex size-10 shrink-0 items-center justify-center rounded-lg",
            ungrouped ? "bg-muted text-muted-foreground" : "bg-primary/15 text-primary-text",
          )}
        >
          {ungrouped ? <Inbox className="size-5" /> : <Layers className="size-5" />}
        </div>
        <ChevronRight className="size-4 text-muted-foreground transition-transform group-hover:translate-x-0.5 group-hover:text-foreground" />
      </div>

      <div className="min-w-0 space-y-1">
        <h3 className="truncate text-base font-semibold text-foreground">{name}</h3>
        <p className="line-clamp-2 min-h-[2.5rem] text-xs leading-relaxed text-muted-foreground">
          {description}
        </p>
      </div>

      <div className="mt-auto space-y-2">
        <p className="text-xs text-muted-foreground">
          <span className="font-semibold text-foreground">{stats.total}</span> dự án
          <span aria-hidden="true"> · </span>
          <span className="font-semibold text-foreground">{stats.inProgress}</span> đang sản xuất
        </p>
        <div
          className="h-1.5 overflow-hidden rounded-full bg-muted"
          role="progressbar"
          aria-label={`Tiến độ hoàn thành của ${name}`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent}
        >
          <div className="h-full rounded-full bg-success transition-[width] duration-300" style={{ width: `${percent}%` }} />
        </div>
      </div>
    </button>
  );
}

interface ProjectCardProps {
  project: ProjectSummaryDto;
  onOpen: () => void;
  onEdit: () => void;
  onDelete: () => void;
}

function ProjectCard({ project, onOpen, onEdit, onDelete }: ProjectCardProps) {
  const [previewFailed, setPreviewFailed] = useState(false);
  const previewSrc = project.previewVideoAssetId && !previewFailed
    ? `https://media.Fraylo.local/media/${encodeURIComponent(project.previewVideoAssetId)}#t=0.1`
    : null;

  return (
    <article className="group relative flex flex-col overflow-hidden rounded-xl border border-border bg-card shadow-xs transition-colors hover:border-primary/60 focus-within:ring-2 focus-within:ring-ring">
      <button type="button" onClick={onOpen} className="relative block aspect-[9/16] w-full bg-neutral-900 text-left focus-visible:outline-none" aria-label={`Mở dự án ${project.name}`}>
        {previewSrc ? (
          <video
            src={previewSrc}
            muted
            playsInline
            preload="metadata"
            onError={() => setPreviewFailed(true)}
            className="absolute inset-0 size-full object-cover"
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-neutral-400">
            <Film className="size-8" aria-hidden="true" />
            <span className="text-xs">{project.status === "InProgress" ? "Đang sản xuất" : "Chưa có video"}</span>
          </div>
        )}
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent p-3 pt-10 text-white">
          <h3 className="line-clamp-2 text-sm font-semibold leading-snug">{project.name}</h3>
          <p className="mt-0.5 truncate text-[11px] opacity-80">
            {project.groupName ? `${project.groupName} · ` : ""}{new Date(project.updatedAtUtc).toLocaleDateString("vi-VN")}
          </p>
        </div>
      </button>

      <div className="absolute left-2 top-2">
        <Badge variant={STATUS_VARIANT[project.status] ?? "secondary"} className="gap-1.5 shadow-xs">
          <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
          {STATUS_LABEL[project.status] ?? project.status}
        </Badge>
      </div>

      <div className="absolute right-1.5 top-1.5">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="secondary" size="icon" className="size-8 bg-background/80 backdrop-blur-xs">
              <MoreHorizontal className="size-4" />
              <span className="sr-only">Tùy chọn dự án {project.name}</span>
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onClick={onOpen}>
              <ArrowRight className="mr-2 size-4" /> Mở dự án
            </DropdownMenuItem>
            <DropdownMenuItem onClick={onEdit}>
              <Pencil className="mr-2 size-4" /> Sửa thông tin
            </DropdownMenuItem>
            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={onDelete}>
              <Trash2 className="mr-2 size-4" /> Xóa dự án
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </article>
  );
}

export function ProjectListPage({
  onSelectProject,
}: ProjectListPageProps) {
  const [projects, setProjects] = useState<ProjectSummaryDto[]>([]);
  const [groups, setGroups] = useState<ProjectGroupDto[]>([]);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // Phạm vi đang xem (cấp 1: danh sách nhóm, cấp 2: dự án trong một nhóm) và bộ lọc trong phạm vi đó.
  const [scope, setScope] = useState<GroupScope>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");
  const [page, setPage] = useState(1);
  const [groupTab, setGroupTab] = useState<GroupTab>("projects");
  const [isCreateGroupOpen, setIsCreateGroupOpen] = useState(false);
  const [isBulkOpen, setIsBulkOpen] = useState(false);

  // Modals & Form
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [formMode, setFormMode] = useState<"create" | "edit">("create");
  const [editingProject, setEditingProject] = useState<ProjectSummaryDto | null>(null);

  // Modal Xóa
  const [deletingProject, setDeletingProject] = useState<ProjectSummaryDto | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchProjects = useCallback(async () => {
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    const controller = new AbortController();
    abortControllerRef.current = controller;

    setIsLoading(true);
    setErrorMessage(null);

    try {
      const [list, groupList] = await Promise.all([projectApi.listAll(), projectGroupApi.listGroups()]);
      if (controller.signal.aborted) return;
      setProjects(list);
      setGroups(groupList);
      // Nhóm vừa bị xóa thì quay về danh sách nhóm thay vì hiển thị một trang rỗng.
      setScope((prev) =>
        prev === null || prev === UNGROUPED || groupList.some((group) => group.groupId === prev) ? prev : null,
      );
    } catch (err: unknown) {
      if (controller.signal.aborted) return;
      const errorDto = err as ErrorDto;
      console.error("[ProjectListPage] Lỗi tải dự án:", errorDto);
      setErrorMessage(errorDto?.message || "Không thể tải danh sách dự án.");
    } finally {
      if (!controller.signal.aborted) {
        setIsLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    fetchProjects();
    return () => {
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
    };
  }, [fetchProjects]);

  const openScope = (next: GroupScope) => {
    setScope(next);
    setGroupTab("projects");
    setStatusFilter("all");
    setSearchQuery("");
    setPage(1);
  };

  /** Chỉ tải lại nhóm (không bật trạng thái đang tải), để form cấu hình nhóm không bị dựng lại. */
  const reloadGroups = useCallback(async () => {
    setGroups(await projectGroupApi.listGroups());
  }, []);

  const handleGroupCreated = async (group: ProjectGroupDto) => {
    setIsCreateGroupOpen(false);
    await reloadGroups();
    openScope(group.groupId);
  };

  const handleGroupDeleted = () => {
    openScope(null);
    void fetchProjects();
  };

  const groupStats = useMemo(() => {
    const map = new Map<string, GroupStats>();
    for (const group of groups) {
      map.set(group.groupId, countByStatus(projects.filter((p) => p.groupId === group.groupId)));
    }
    return map;
  }, [projects, groups]);

  const ungroupedProjects = useMemo(() => projects.filter((p) => !p.groupId), [projects]);

  const scopedProjects = useMemo(() => {
    if (scope === null) return [];
    if (scope === UNGROUPED) return ungroupedProjects;
    return projects.filter((p) => p.groupId === scope);
  }, [projects, scope, ungroupedProjects]);

  const visibleProjects = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    return scopedProjects.filter((prj) => {
      if (statusFilter !== "all" && prj.status !== statusFilter) return false;
      if (!query) return true;
      return prj.name.toLowerCase().includes(query) || (prj.description || "").toLowerCase().includes(query);
    });
  }, [scopedProjects, statusFilter, searchQuery]);

  // Trang hiện tại được kẹp lại khi danh sách co (ví dụ sau khi xóa dự án ở trang cuối).
  const totalPages = Math.max(1, Math.ceil(visibleProjects.length / PAGE_SIZE));
  const currentPage = Math.min(page, totalPages);
  const pagedProjects = useMemo(
    () => visibleProjects.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE),
    [visibleProjects, currentPage],
  );

  const activeGroup = scope !== null && scope !== UNGROUPED ? groups.find((g) => g.groupId === scope) ?? null : null;
  // Tạo video từ trong nhóm sẽ gán sẵn nhóm đó.
  const createGroupId = scope !== null && scope !== UNGROUPED ? scope : null;

  const openCreate = () => {
    setEditingProject(null);
    setFormMode("create");
    setIsFormOpen(true);
  };

  const handleDelete = async () => {
    if (!deletingProject || isDeleting) return;
    setIsDeleting(true);
    setDeleteError(null);

    try {
      await projectApi.deleteProject(deletingProject.projectId);
      setProjects((prev) => prev.filter((p) => p.projectId !== deletingProject.projectId));
      setDeletingProject(null);
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[ProjectListPage] Lỗi xóa dự án:", errorDto);
      if (errorDto?.code === "CONCURRENCY_CONFLICT") {
        setDeleteError("Không thể xóa dự án vì có tác vụ đang chạy trong dự án này.");
      } else {
        setDeleteError(errorDto?.message || "Đã xảy ra lỗi khi xóa dự án.");
      }
    } finally {
      setIsDeleting(false);
    }
  };

  const title =
    scope === null ? "Dự án video" : scope === UNGROUPED ? "Chưa thuộc nhóm" : activeGroup?.name ?? "Nhóm dự án";
  const subtitle =
    scope === null
      ? "Chọn một nhóm để xem các video TikTok bên trong."
      : scope === UNGROUPED
        ? "Các dự án chưa được gán vào nhóm nào."
        : activeGroup?.description || "Nhóm chưa có mô tả.";

  const renderOverview = () => {
    const totals = countByStatus(projects);
    const groupCount = groups.length;

    if (groupCount === 0 && projects.length === 0) {
      return (
        <Card className="flex flex-col items-center justify-center p-12 text-center">
          <div className="mb-4 rounded-full bg-primary/10 p-4 text-primary-text">
            <Film className="size-10" />
          </div>
          <h3 className="mb-1 text-lg font-semibold text-foreground">Chưa có nhóm hay dự án nào</h3>
          <p className="mb-4 max-w-md text-sm text-muted-foreground">
            Tạo nhóm để gom các video cùng chủ đề, hoặc tạo video đầu tiên để bắt đầu.
          </p>
          <div className="flex items-center gap-2">
            <Button variant="outline" onClick={() => setIsCreateGroupOpen(true)}>
              <Layers className="mr-2 size-4" /> Tạo nhóm
            </Button>
            <Button onClick={openCreate}>
              <Plus className="mr-2 size-4" /> Tạo video
            </Button>
          </div>
        </Card>
      );
    }

    const summary = [
      { label: "Nhóm", value: groupCount },
      { label: "Dự án", value: totals.total },
      { label: "Đang sản xuất", value: totals.inProgress },
      { label: "Hoàn thành", value: totals.completed },
    ];

    return (
      <div className="space-y-6">
        <dl className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          {summary.map((item) => (
            <div key={item.label} className="rounded-xl border border-border bg-card px-4 py-3">
              <dt className="text-xs font-medium text-muted-foreground">{item.label}</dt>
              <dd className="mt-1 text-2xl font-bold tabular-nums text-foreground">{item.value}</dd>
            </div>
          ))}
        </dl>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {groups.map((group) => (
            <GroupCard
              key={group.groupId}
              name={group.name}
              description={group.description || "Chưa có mô tả nhóm."}
              stats={groupStats.get(group.groupId) ?? { total: 0, inProgress: 0, completed: 0 }}
              onOpen={() => openScope(group.groupId)}
            />
          ))}
          {ungroupedProjects.length > 0 && (
            <GroupCard
              name="Chưa thuộc nhóm"
              description="Các dự án chưa được gán vào nhóm nào."
              stats={countByStatus(ungroupedProjects)}
              ungrouped
              onOpen={() => openScope(UNGROUPED)}
            />
          )}
        </div>
      </div>
    );
  };

  const renderGroupProjects = () => {
    if (scopedProjects.length === 0) {
      return (
        <Card className="flex flex-col items-center justify-center p-12 text-center">
          <div className="mb-4 rounded-full bg-primary/10 p-4 text-primary-text">
            <Film className="size-10" />
          </div>
          <h3 className="mb-1 text-lg font-semibold text-foreground">Chưa có dự án nào trong mục này</h3>
          <p className="mb-4 max-w-md text-sm text-muted-foreground">
            Tạo video đầu tiên để chuẩn bị đầu vào và tạo video TikTok.
          </p>
          <Button onClick={openCreate}>
            <Plus className="mr-2 size-4" /> Tạo video
          </Button>
        </Card>
      );
    }

    return (
      <div className="space-y-4">
        <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
          <div
            className="inline-flex h-9 w-fit items-center overflow-x-auto rounded-lg bg-muted p-1 text-muted-foreground"
            role="tablist"
            aria-label="Lọc theo trạng thái dự án"
          >
            {STATUS_TABS.map((tab) => {
              const count =
                tab.value === "all"
                  ? scopedProjects.length
                  : scopedProjects.filter((p) => p.status === tab.value).length;
              const active = statusFilter === tab.value;
              return (
                <button
                  key={tab.value}
                  type="button"
                  role="tab"
                  aria-selected={active}
                  className={cn(segmentTabClass, active ? "bg-background text-foreground shadow-xs" : "text-muted-foreground hover:text-foreground")}
                  onClick={() => {
                    setStatusFilter(tab.value);
                    setPage(1);
                  }}
                >
                  {tab.label} ({count})
                </button>
              );
            })}
          </div>

          <div className="flex items-center gap-2">
            <div className="relative w-full md:w-72">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                type="search"
                aria-label="Tìm dự án theo tên hoặc mô tả"
                placeholder="Tìm dự án theo tên hoặc mô tả…"
                className="h-9 pl-9"
                value={searchQuery}
                onChange={(e) => {
                  setSearchQuery(e.target.value);
                  setPage(1);
                }}
              />
            </div>

            <Button
              type="button"
              variant="outline"
              size="icon"
              className="size-9 shrink-0"
              onClick={fetchProjects}
              title="Tải lại danh sách"
              aria-label="Tải lại danh sách"
              disabled={isLoading}
            >
              <RefreshCw className={cn("size-4", isLoading && "animate-spin")} />
            </Button>
          </div>
        </div>

        {visibleProjects.length === 0 ? (
          <Card className="flex flex-col items-center justify-center p-12 text-center">
            <h3 className="mb-1 text-lg font-semibold text-foreground">Không có dự án phù hợp</h3>
            <p className="mb-4 text-sm text-muted-foreground">Không tìm thấy dự án nào khớp với điều kiện lọc hiện tại.</p>
            <Button
              variant="outline"
              onClick={() => {
                setStatusFilter("all");
                setSearchQuery("");
              }}
            >
              Xóa bộ lọc
            </Button>
          </Card>
        ) : (
          <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-6">
              {pagedProjects.map((prj) => (
                <ProjectCard
                  key={prj.projectId}
                  project={prj}
                  onOpen={() => onSelectProject(prj.projectId)}
                  onEdit={() => {
                    setEditingProject(prj);
                    setFormMode("edit");
                    setIsFormOpen(true);
                  }}
                  onDelete={() => {
                    setDeletingProject(prj);
                    setDeleteError(null);
                  }}
                />
              ))}
            </div>

            {totalPages > 1 && (
              <nav
                aria-label="Phân trang dự án"
                className="flex flex-col items-center justify-between gap-3 rounded-xl border border-border bg-card px-4 py-3 text-xs text-muted-foreground sm:flex-row"
              >
                <span>
                  Hiển thị <strong className="text-foreground">{(currentPage - 1) * PAGE_SIZE + 1}–{Math.min(currentPage * PAGE_SIZE, visibleProjects.length)}</strong> / <strong className="text-foreground">{visibleProjects.length}</strong> dự án
                  <span aria-hidden="true"> · </span>
                  Trang {currentPage} / {totalPages}
                </span>
                <div className="flex items-center gap-2">
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    disabled={currentPage <= 1}
                    onClick={() => setPage(currentPage - 1)}
                    className="h-auto gap-1 px-2.5 py-1"
                  >
                    <ArrowLeft className="size-3.5" /> Trước
                  </Button>
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    disabled={currentPage >= totalPages}
                    onClick={() => setPage(currentPage + 1)}
                    className="h-auto gap-1 px-2.5 py-1"
                  >
                    Sau <ArrowRight className="size-3.5" />
                  </Button>
                </div>
              </nav>
            )}
          </div>
        )}
      </div>
    );
  };

  const renderGroupTabs = () => {
    const tabs: { value: GroupTab; label: string }[] = [
      { value: "projects", label: `Dự án (${scopedProjects.length})` },
      { value: "settings", label: "Cấu hình nhóm" },
    ];
    return (
      <div
        className="inline-flex h-9 w-fit items-center rounded-lg bg-muted p-1 text-muted-foreground"
        role="tablist"
        aria-label="Nội dung của nhóm"
      >
        {tabs.map((tab) => {
          const active = groupTab === tab.value;
          return (
            <button
              key={tab.value}
              type="button"
              role="tab"
              aria-selected={active}
              className={cn(segmentTabClass, active ? "bg-background text-foreground shadow-xs" : "text-muted-foreground hover:text-foreground")}
              onClick={() => setGroupTab(tab.value)}
            >
              {tab.label}
            </button>
          );
        })}
      </div>
    );
  };

  const renderBody = () => {
    if (isLoading) {
      return (
        <Card className="flex flex-col items-center justify-center p-12 text-center">
          <Loader2 className="mb-3 size-8 animate-spin text-primary" />
          <p className="text-sm text-muted-foreground">Đang tải danh sách Dự án từ Host…</p>
        </Card>
      );
    }
    if (errorMessage) {
      return (
        <Card className="border-destructive/50 bg-destructive/5 p-8 text-center">
          <AlertCircle className="mx-auto mb-3 size-10 text-destructive" />
          <h3 className="mb-1 text-lg font-semibold text-foreground">Không thể tải danh sách Dự án</h3>
          <p className="mb-4 text-sm text-muted-foreground">{errorMessage}</p>
          <Button variant="outline" onClick={fetchProjects}>
            <RefreshCw className="mr-2 size-4" /> Thử lại
          </Button>
        </Card>
      );
    }
    if (scope === null) return renderOverview();
    if (activeGroup && groupTab === "settings") {
      return (
        <ProjectGroupSettings
          key={activeGroup.groupId}
          group={activeGroup}
          onSaved={reloadGroups}
          onDeleted={handleGroupDeleted}
        />
      );
    }
    return renderGroupProjects();
  };

  return (
    <div className="space-y-6 pb-10">
      {/* Header: tiêu đề theo phạm vi đang xem + hành động chung */}
      <PageHeader
        title={title}
        description={subtitle}
        leading={
          scope !== null && (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="-ml-2 h-7 gap-1 px-2 text-muted-foreground hover:text-foreground"
              onClick={() => openScope(null)}
            >
              <ArrowLeft className="size-3.5" /> Tất cả nhóm
            </Button>
          )
        }
        actions={
          scope === null ? (
            <>
              <Button type="button" variant="outline" onClick={() => setIsCreateGroupOpen(true)} className="gap-1.5">
                <Layers className="size-4" /> Tạo nhóm
              </Button>
              <Button type="button" variant="outline" onClick={openCreate} className="gap-1.5">
                <Plus className="size-4" /> Tạo 1 video
              </Button>
              <Button type="button" onClick={() => setIsBulkOpen(true)} className="gap-1.5">
                <Rocket className="size-4" /> Tạo hàng loạt
              </Button>
            </>
          ) : (
            <>
              <Button type="button" variant="outline" onClick={openCreate} className="gap-1.5">
                <Plus className="size-4" /> Tạo 1 video
              </Button>
              <Button type="button" onClick={() => setIsBulkOpen(true)} className="gap-1.5">
                <Rocket className="size-4" /> Tạo hàng loạt
              </Button>
            </>
          )
        }
      />

      {activeGroup && renderGroupTabs()}
      {renderBody()}

      {/* Modal Form Tạo/Sửa */}
      <ProjectForm
        isOpen={isFormOpen}
        mode={formMode}
        project={editingProject}
        defaultGroupId={createGroupId}
        onClose={() => setIsFormOpen(false)}
        onSuccess={() => {
          setIsFormOpen(false);
          fetchProjects();
        }}
      />

      <BulkVideoCreateDialog
        isOpen={isBulkOpen}
        groups={groups}
        defaultGroupId={activeGroup?.groupId ?? null}
        onClose={() => setIsBulkOpen(false)}
        onCreated={fetchProjects}
      />

      <ProjectGroupCreateDialog
        isOpen={isCreateGroupOpen}
        onClose={() => setIsCreateGroupOpen(false)}
        onCreated={handleGroupCreated}
      />

      {/* Modal xác nhận xóa */}
      <AlertDialog
        open={!!deletingProject}
        onOpenChange={(open) => {
          if (!open && !isDeleting) {
            setDeletingProject(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle className="text-destructive">
              Xác nhận xóa Dự Án
            </AlertDialogTitle>
            <AlertDialogDescription className="space-y-3 text-sm">
              {deleteError && (
                <Alert variant="destructive">
                  <AlertCircle className="h-4 w-4" />
                  <AlertDescription>{deleteError}</AlertDescription>
                </Alert>
              )}
              <span className="block text-foreground">
                Bạn có chắc chắn muốn xóa dự án <strong>{deletingProject?.name}</strong>?
              </span>
              <Alert variant="default" className="bg-muted/40">
                <Info className="h-4 w-4 text-muted-foreground" />
                <AlertDescription className="text-xs text-muted-foreground">
                  Toàn bộ các cảnh và tài nguyên phân cảnh trong dự án này sẽ bị xóa.
                </AlertDescription>
              </Alert>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel
              disabled={isDeleting}
              onClick={() => setDeletingProject(null)}
            >
              Hủy
            </AlertDialogCancel>
            <Button
              variant="destructive"
              disabled={isDeleting}
              onClick={handleDelete}
            >
              {isDeleting ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" /> Đang xóa…
                </>
              ) : (
                "Xác nhận xóa"
              )}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
