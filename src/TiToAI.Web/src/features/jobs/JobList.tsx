import { useState, useMemo } from "react";
import {
  Search,
  SearchX,
  Inbox,
  RefreshCw,
  AlertCircle,
  CheckCircle2,
  Sparkles,
  ShieldCheck,
  Clock,
  X,
  Loader2,
} from "lucide-react";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { cn } from "@/lib/utils";
import {
  type JobSummaryDto,
  type JobDetailDto,
  type QueueTabFilter,
  getTaskTypeInfo,
  formatReasonCode,
  type JobReconcileResponseData,
} from "./jobTypes";
import { JobItemCard } from "./JobItemCard";
import { JobDetailsDialog } from "./JobDetailsDialog";
import { JobReconcileDialog } from "./JobReconcileDialog";

export interface JobListProps {
  jobs: JobSummaryDto[];
  isLoading?: boolean;
  error?: string | null;
  onRefresh?: () => void;
  onNavigateToJob?: (job: JobSummaryDto | JobDetailDto) => void;
  onAction?: (
    actionType: "login" | "navigate_settings_accounts" | "resume" | "retry" | "cancel",
    job: JobSummaryDto | JobDetailDto
  ) => Promise<void> | void;
  onReconcileSuccess?: (job: JobSummaryDto | JobDetailDto, result: JobReconcileResponseData) => void;
  initialFilter?: QueueTabFilter;
  activeTab?: QueueTabFilter;
  onTabChange?: (tab: QueueTabFilter) => void;
  className?: string;
  actionLoadingJobId?: string | null;
}

export function JobList({
  jobs = [],
  isLoading = false,
  error = null,
  onRefresh,
  onNavigateToJob,
  onAction,
  onReconcileSuccess,
  initialFilter = "all",
  activeTab: controlledActiveTab,
  onTabChange,
  className,
  actionLoadingJobId,
}: JobListProps) {
  const [internalTab, setInternalTab] = useState<QueueTabFilter>(initialFilter);
  const activeTab = controlledActiveTab ?? internalTab;
  const handleTabChange = (tab: QueueTabFilter) => {
    setInternalTab(tab);
    onTabChange?.(tab);
  };
  const [searchQuery, setSearchQuery] = useState("");
  const [selectedJobForDetails, setSelectedJobForDetails] =
    useState<(JobSummaryDto | JobDetailDto) | null>(null);
  const [selectedJobForReconcile, setSelectedJobForReconcile] =
    useState<(JobSummaryDto | JobDetailDto) | null>(null);

  // Tính số lượng cho từng tab
  const tabCounts = useMemo(() => {
    let running = 0;
    let awaiting = 0;
    let failed = 0;
    let completed = 0;

    for (const job of jobs) {
      if (job.state === "Running" || job.state === "Reserved") {
        running++;
      } else if (job.state === "Queued" || job.state === "AwaitingUser") {
        awaiting++;
      } else if (job.state === "Failed") {
        failed++;
      } else if (job.state === "Succeeded" || job.state === "Canceled") {
        completed++;
      }
    }

    return {
      all: jobs.length,
      running,
      awaiting,
      failed,
      completed,
    };
  }, [jobs]);

  // Lọc tác vụ theo tab và từ khóa tìm kiếm
  const filteredJobs = useMemo(() => {
    let result = jobs;

    // Lọc theo Tab
    if (activeTab === "running") {
      result = result.filter(
        (j) => j.state === "Running" || j.state === "Reserved"
      );
    } else if (activeTab === "awaiting") {
      result = result.filter(
        (j) => j.state === "Queued" || j.state === "AwaitingUser"
      );
    } else if (activeTab === "failed") {
      result = result.filter((j) => j.state === "Failed");
    } else if (activeTab === "completed") {
      result = result.filter(
        (j) => j.state === "Succeeded" || j.state === "Canceled"
      );
    }

    // Lọc theo Search Query
    const query = searchQuery.trim().toLowerCase();
    if (query) {
      result = result.filter((j) => {
        const taskInfo = getTaskTypeInfo(j.taskType);
        const reasonDesc = formatReasonCode(j.reasonCode).toLowerCase();
        const idMatch = j.id.toLowerCase().includes(query);
        const taskTypeMatch =
          j.taskType.toLowerCase().includes(query) ||
          taskInfo.label.toLowerCase().includes(query);
        const projectMatch = j.projectId.toLowerCase().includes(query);
        const sceneMatch = j.sceneId ? j.sceneId.toLowerCase().includes(query) : false;
        const stepMatch = j.currentStep ? j.currentStep.toLowerCase().includes(query) : false;
        const reasonMatch =
          (j.reasonCode ? j.reasonCode.toLowerCase().includes(query) : false) ||
          reasonDesc.includes(query);

        return (
          idMatch ||
          taskTypeMatch ||
          projectMatch ||
          sceneMatch ||
          stepMatch ||
          reasonMatch
        );
      });
    }

    return result;
  }, [jobs, activeTab, searchQuery]);

  return (
    <div className={cn("flex flex-col h-full w-full gap-3", className)}>
      {/* Thanh công cụ: Tìm kiếm & Làm mới */}
      <div className="flex items-center gap-2">
        <div className="relative flex-1">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted-foreground pointer-events-none" />
          <Input
            placeholder="Tìm theo ID, loại tác vụ, phân cảnh, lỗi..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="h-8.5 pl-8 pr-8 text-xs bg-card"
          />
          {searchQuery && (
            <button
              type="button"
              onClick={() => setSearchQuery("")}
              className="absolute right-2.5 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground cursor-pointer"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          )}
        </div>

        {onRefresh && (
          <Button
            type="button"
            variant="outline"
            size="icon"
            onClick={onRefresh}
            disabled={isLoading}
            className="h-8.5 w-8.5 shrink-0 text-muted-foreground hover:text-foreground cursor-pointer"
            title="Làm mới hàng đợi"
          >
            <RefreshCw
              className={cn("h-3.5 w-3.5", isLoading && "animate-spin")}
            />
          </Button>
        )}
      </div>

      {/* Tabs phân loại trạng thái */}
      <Tabs
        value={activeTab}
        onValueChange={(val) => handleTabChange(val as QueueTabFilter)}
        className="w-full"
      >
        <TabsList className="grid w-full grid-cols-5 h-9 p-1 bg-muted/60">
          <TabsTrigger
            value="all"
            className="text-[11px] sm:text-xs px-1.5 py-1 gap-1 flex items-center justify-center truncate"
          >
            <span>Tất cả</span>
            <Badge
              variant="secondary"
              className="h-4 min-w-4 px-1 text-[10px] font-normal leading-none"
            >
              {tabCounts.all}
            </Badge>
          </TabsTrigger>

          <TabsTrigger
            value="running"
            className="text-[11px] sm:text-xs px-1.5 py-1 gap-1 flex items-center justify-center truncate"
          >
            <span>Đang chạy</span>
            {tabCounts.running > 0 && (
              <Badge
                variant="default"
                className="h-4 min-w-4 px-1 text-[10px] font-normal leading-none bg-primary text-primary-foreground"
              >
                {tabCounts.running}
              </Badge>
            )}
          </TabsTrigger>

          <TabsTrigger
            value="awaiting"
            className="text-[11px] sm:text-xs px-1.5 py-1 gap-1 flex items-center justify-center truncate"
          >
            <span>Chờ xử lý</span>
            {tabCounts.awaiting > 0 && (
              <Badge
                variant="warning"
                className="h-4 min-w-4 px-1 text-[10px] font-normal leading-none"
              >
                {tabCounts.awaiting}
              </Badge>
            )}
          </TabsTrigger>

          <TabsTrigger
            value="failed"
            className="text-[11px] sm:text-xs px-1.5 py-1 gap-1 flex items-center justify-center truncate"
          >
            <span>Thất bại</span>
            {tabCounts.failed > 0 && (
              <Badge
                variant="destructive"
                className="h-4 min-w-4 px-1 text-[10px] font-normal leading-none"
              >
                {tabCounts.failed}
              </Badge>
            )}
          </TabsTrigger>

          <TabsTrigger
            value="completed"
            className="text-[11px] sm:text-xs px-1.5 py-1 gap-1 flex items-center justify-center truncate"
          >
            <span>Hoàn tất</span>
            {tabCounts.completed > 0 && (
              <Badge
                variant="outline"
                className="h-4 min-w-4 px-1 text-[10px] font-normal leading-none"
              >
                {tabCounts.completed}
              </Badge>
            )}
          </TabsTrigger>
        </TabsList>
      </Tabs>

      {/* Thông báo lỗi nếu có */}
      {error && (
        <Alert variant="destructive" className="py-2.5 px-3 text-xs">
          <AlertCircle className="h-4 w-4" />
          <AlertTitle className="text-xs font-semibold">Lỗi tải danh sách</AlertTitle>
          <AlertDescription className="text-xs flex items-center justify-between gap-2 mt-1">
            <span>{error}</span>
            {onRefresh && (
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={onRefresh}
                className="h-6 px-2 text-[11px] shrink-0 cursor-pointer"
              >
                Thử lại
              </Button>
            )}
          </AlertDescription>
        </Alert>
      )}

      {/* Danh sách cuộn thẻ tác vụ */}
      <ScrollArea className="flex-1 w-full rounded-md border border-border/60 bg-muted/10 p-2.5">
        <div className="flex flex-col gap-2.5 pr-2">
          {/* Trạng thái đang tải lần đầu */}
          {isLoading && jobs.length === 0 && (
            <div className="flex flex-col items-center justify-center py-12 text-center text-muted-foreground gap-2.5">
              <Loader2 className="h-6 w-6 animate-spin text-primary" />
              <p className="text-xs">Đang đồng bộ hàng đợi tác vụ...</p>
            </div>
          )}

          {/* Trạng thái Empty State khi không có tác vụ nào */}
          {!isLoading && filteredJobs.length === 0 && (
            <div className="flex flex-col items-center justify-center py-12 px-4 text-center">
              {searchQuery ? (
                <>
                  <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground mb-3">
                    <SearchX className="h-6 w-6" />
                  </div>
                  <h4 className="text-sm font-semibold text-foreground">
                    Không tìm thấy tác vụ
                  </h4>
                  <p className="text-xs text-muted-foreground mt-1 max-w-xs">
                    Không có tác vụ nào khớp với từ khóa &ldquo;{searchQuery}&rdquo;. Vui lòng thử lại với từ khóa khác.
                  </p>
                  <Button
                    type="button"
                    variant="link"
                    size="sm"
                    onClick={() => setSearchQuery("")}
                    className="mt-2 text-xs text-primary cursor-pointer"
                  >
                    Xóa bộ lọc tìm kiếm
                  </Button>
                </>
              ) : activeTab === "running" ? (
                <>
                  <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground mb-3">
                    <CheckCircle2 className="h-6 w-6 text-success" />
                  </div>
                  <h4 className="text-sm font-semibold text-foreground">
                    Không có tác vụ đang chạy
                  </h4>
                  <p className="text-xs text-muted-foreground mt-1 max-w-xs">
                    Hàng đợi hiện đang rảnh rỗi. Các tác vụ mới được tạo sẽ xuất hiện ở đây.
                  </p>
                </>
              ) : activeTab === "awaiting" ? (
                <>
                  <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground mb-3">
                    <Sparkles className="h-6 w-6 text-warning" />
                  </div>
                  <h4 className="text-sm font-semibold text-foreground">
                    Không có tác vụ cần xử lý
                  </h4>
                  <p className="text-xs text-muted-foreground mt-1 max-w-xs">
                    Tất cả tài khoản và tác vụ đều đang ở trạng thái sẵn sàng hoặc tự động.
                  </p>
                </>
              ) : activeTab === "failed" ? (
                <>
                  <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground mb-3">
                    <ShieldCheck className="h-6 w-6 text-success" />
                  </div>
                  <h4 className="text-sm font-semibold text-foreground">
                    Không có tác vụ lỗi
                  </h4>
                  <p className="text-xs text-muted-foreground mt-1 max-w-xs">
                    Không có tác vụ nào gặp sự cố hoặc bị từ chối bởi nhà cung cấp AI.
                  </p>
                </>
              ) : activeTab === "completed" ? (
                <>
                  <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground mb-3">
                    <Clock className="h-6 w-6" />
                  </div>
                  <h4 className="text-sm font-semibold text-foreground">
                    Chưa có tác vụ hoàn tất
                  </h4>
                  <p className="text-xs text-muted-foreground mt-1 max-w-xs">
                    Các tác vụ hoàn thành thành công hoặc đã hủy sẽ được lưu giữ tại đây.
                  </p>
                </>
              ) : (
                <>
                  <div className="flex h-12 w-12 items-center justify-center rounded-full bg-muted text-muted-foreground mb-3">
                    <Inbox className="h-6 w-6" />
                  </div>
                  <h4 className="text-sm font-semibold text-foreground">
                    Hàng đợi trống
                  </h4>
                  <p className="text-xs text-muted-foreground mt-1 max-w-xs">
                    Chưa có tác vụ nào được gửi vào hàng đợi xử lý.
                  </p>
                </>
              )}
            </div>
          )}

          {/* Danh sách các thẻ Job */}
          {filteredJobs.map((job) => (
            <JobItemCard
              key={job.id}
              job={job}
              isActionLoading={actionLoadingJobId === job.id}
              onNavigateToJob={onNavigateToJob}
              onOpenDetails={(j) => setSelectedJobForDetails(j)}
              onOpenReconcile={(j) => setSelectedJobForReconcile(j)}
              onAction={onAction}
            />
          ))}
        </div>
      </ScrollArea>

      {/* Modal xem chi tiết tác vụ */}
      <JobDetailsDialog
        job={selectedJobForDetails}
        open={Boolean(selectedJobForDetails)}
        onOpenChange={(isOpen) => {
          if (!isOpen) setSelectedJobForDetails(null);
        }}
        onNavigateToJob={onNavigateToJob}
        onAction={onAction}
        onOpenReconcile={(j) => {
          setSelectedJobForDetails(null);
          setSelectedJobForReconcile(j);
        }}
        isActionLoading={
          selectedJobForDetails
            ? actionLoadingJobId === selectedJobForDetails.id
            : false
        }
      />

      {/* Modal đối soát an toàn tác vụ */}
      <JobReconcileDialog
        job={selectedJobForReconcile}
        open={Boolean(selectedJobForReconcile)}
        onOpenChange={(isOpen) => {
          if (!isOpen) setSelectedJobForReconcile(null);
        }}
        onReconciled={(res) => {
          if (selectedJobForReconcile) {
            onReconcileSuccess?.(selectedJobForReconcile, res);
          }
          onRefresh?.();
        }}
      />
    </div>
  );
}
