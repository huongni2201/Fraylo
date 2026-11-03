import React, { useState, useEffect, useCallback, useMemo, useRef } from "react";
import {
  aiSettingsApi,
  type SettingsDto,
  type ProviderListResponse,
  type ProviderCapabilityDto,
  type ProviderAccountSummaryDto,
  type TaskCapabilityDto,
} from "./aiSettingsApi";
import {
  Check,
  CheckCircle2,
  AlertTriangle,
  AlertCircle,
  Loader2,
  Film,
  ExternalLink,
  RotateCcw,
  Save,
  Cpu,
  Sparkles,
} from "lucide-react";
import { cn } from "../../lib/utils";

export interface AiProviderSettingsPanelProps {
  onDirtyChange?: (isDirty: boolean) => void;
  resetSignal?: number;
  onNavigateToAccounts?: () => void;
}

// shadcn UI-compatible primitives local to Settings to avoid test-loader dual React declaration quirks
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
        "inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ring-offset-background disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0",
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

function Label({ className, ...props }: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return (
    <label
      className={cn(
        "text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70",
        className
      )}
      {...props}
    />
  );
}

function Badge({
  className,
  variant = "default",
  ...props
}: React.HTMLAttributes<HTMLDivElement> & {
  variant?: "default" | "secondary" | "destructive" | "outline" | "warning" | "success";
}) {
  const variantClasses = {
    default: "border-transparent bg-primary text-primary-foreground",
    secondary: "border-transparent bg-secondary text-secondary-foreground",
    destructive: "border-transparent bg-destructive text-destructive-foreground",
    outline: "text-foreground border-border",
    warning: "border-warning/30 bg-warning/10 text-warning dark:text-warning",
    success: "border-success/30 bg-success/10 text-success dark:text-success",
  };
  return (
    <div
      className={cn(
        "inline-flex items-center rounded-md border px-2.5 py-0.5 text-xs font-semibold transition-colors focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2",
        variantClasses[variant],
        className
      )}
      {...props}
    />
  );
}

export function AiProviderSettingsPanel({
  onDirtyChange,
  resetSignal,
  onNavigateToAccounts,
}: AiProviderSettingsPanelProps) {
  // Bản cấu hình đã lưu trên Host
  const [savedSettings, setSavedSettings] = useState<SettingsDto | null>(null);
  const [providersData, setProvidersData] = useState<ProviderListResponse | null>(null);

  // Form draft state
  const [scriptProvider, setScriptProvider] = useState<string>("");
  const [imageProvider, setImageProvider] = useState<string>("");
  const [videoPromptProvider, setVideoPromptProvider] = useState<string>("");
  const [maxOpenBrowsers, setMaxOpenBrowsers] = useState<number>(4);
  const [maxConcurrentJobs, setMaxConcurrentJobs] = useState<number>(8);
  const [visibleBrowsers, setVisibleBrowsers] = useState<string[]>([]);

  // UI state
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [isSaving, setIsSaving] = useState<boolean>(false);
  const [saveSuccessMessage, setSaveSuccessMessage] = useState<string | null>(null);
  const [generalError, setGeneralError] = useState<string | null>(null);
  const [isConcurrencyConflict, setIsConcurrencyConflict] = useState<boolean>(false);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  // Nạp cài đặt và danh sách provider từ Host
  const loadData = useCallback(async () => {
    setIsLoading(true);
    setGeneralError(null);
    setIsConcurrencyConflict(false);
    try {
      const [settingsRes, providersRes] = await Promise.all([
        aiSettingsApi.getSettings(),
        aiSettingsApi.listProviders().catch((err) => {
          console.warn("[AiProviderSettings] Không thể nạp provider.list:", err);
          return null;
        }),
      ]);

      setSavedSettings(settingsRes);
      setScriptProvider(settingsRes.scriptProvider ?? "");
      setImageProvider(settingsRes.imageProvider ?? "");
      setVideoPromptProvider(settingsRes.videoPromptProvider ?? "");
      setMaxOpenBrowsers(settingsRes.maxOpenBrowsers ?? 4);
      setMaxConcurrentJobs(settingsRes.maxConcurrentJobs ?? 8);
      setVisibleBrowsers(settingsRes.visibleBrowserProviders ?? []);

      if (providersRes) {
        setProvidersData(providersRes);
      }
      setFieldErrors({});
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      setGeneralError(`Không thể nạp cấu hình cài đặt AI: ${message}`);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  // Kiểm tra Dirty State: so sánh draft với savedSettings
  const isDirty = useMemo(() => {
    if (!savedSettings) return false;
    return (
      (scriptProvider || null) !== (savedSettings.scriptProvider || null) ||
      (imageProvider || null) !== (savedSettings.imageProvider || null) ||
      (videoPromptProvider || null) !== (savedSettings.videoPromptProvider || null) ||
      maxOpenBrowsers !== (savedSettings.maxOpenBrowsers ?? 4) ||
      maxConcurrentJobs !== (savedSettings.maxConcurrentJobs ?? 8) ||
      [...visibleBrowsers].sort().join(",") !== [...(savedSettings.visibleBrowserProviders ?? [])].sort().join(",")
    );
  }, [
    savedSettings,
    scriptProvider,
    imageProvider,
    videoPromptProvider,
    maxOpenBrowsers,
    maxConcurrentJobs,
    visibleBrowsers,
  ]);

  // Đồng bộ dirty state với component cha
  useEffect(() => {
    onDirtyChange?.(isDirty);
  }, [isDirty, onDirtyChange]);

  // Hoàn tác về bản đã lưu gần nhất (Hủy thay đổi)
  const handleDiscard = useCallback(() => {
    if (savedSettings) {
      setScriptProvider(savedSettings.scriptProvider ?? "");
      setImageProvider(savedSettings.imageProvider ?? "");
      setVideoPromptProvider(savedSettings.videoPromptProvider ?? "");
      setMaxOpenBrowsers(savedSettings.maxOpenBrowsers ?? 4);
      setMaxConcurrentJobs(savedSettings.maxConcurrentJobs ?? 8);
      setVisibleBrowsers(savedSettings.visibleBrowserProviders ?? []);
    }
    setFieldErrors({});
    setGeneralError(null);
    setIsConcurrencyConflict(false);
    setSaveSuccessMessage(null);
  }, [savedSettings]);

  // Xử lý resetSignal từ parent (Discard toàn trang)
  const prevResetSignalRef = useRef<number | undefined>(resetSignal);
  useEffect(() => {
    if (resetSignal !== undefined && resetSignal !== prevResetSignalRef.current) {
      prevResetSignalRef.current = resetSignal;
      handleDiscard();
    }
  }, [resetSignal, handleDiscard]);

  // Tìm thông tin Capability & Accounts của từng provider
  const getProviderInfo = useCallback(
    (providerKey: string, taskType: string) => {
      if (!providerKey || !providersData) return null;

      const providerCap = providersData.providers?.find(
        (p: ProviderCapabilityDto) => p.providerKey === providerKey
      );

      const accounts =
        providersData.accounts?.filter(
          (a: ProviderAccountSummaryDto) => a.providerKey === providerKey
        ) ?? [];

      const tasks =
        providersData.tasks?.filter(
          (t: TaskCapabilityDto) => t.providerKey === providerKey
        ) ?? [];

      const selectedTask = tasks.find((t) => t.taskType === taskType);
      const isTrial = selectedTask?.verificationStatus === "TRIAL" &&
        selectedTask.automationSupported;
      const isUnverified = !isTrial &&
        (selectedTask?.verificationStatus !== "VERIFIED" || !selectedTask.automationSupported);

      const connectedAccountsCount = accounts.filter(
        (a) => a.enabled !== false
      ).length;

      return {
        providerCap,
        accounts,
        connectedAccountsCount,
        isUnverified,
        isTrial,
      };
    },
    [providersData]
  );

  // Xác thực và Lưu cài đặt
  const handleSave = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    if (isSaving || isLoading) return;

    // Validate giới hạn ngân sách (Budgets)
    const errs: Record<string, string[]> = {};
    if (
      isNaN(maxOpenBrowsers) ||
      maxOpenBrowsers < 1 ||
      maxOpenBrowsers > 16 ||
      !Number.isInteger(maxOpenBrowsers)
    ) {
      errs.maxOpenBrowsers = [
        "Số trình duyệt mở tối đa phải là số nguyên từ 1 đến 16 (mặc định: 4).",
      ];
    }
    if (
      isNaN(maxConcurrentJobs) ||
      maxConcurrentJobs < 1 ||
      maxConcurrentJobs > 64 ||
      !Number.isInteger(maxConcurrentJobs)
    ) {
      errs.maxConcurrentJobs = [
        "Số tác vụ chạy đồng thời tối đa phải là số nguyên từ 1 đến 64 (mặc định: 8).",
      ];
    }

    if (Object.keys(errs).length > 0) {
      setFieldErrors(errs);
      return;
    }

    setIsSaving(true);
    setGeneralError(null);
    setIsConcurrencyConflict(false);
    setSaveSuccessMessage(null);
    setFieldErrors({});

    try {
      const res = await aiSettingsApi.saveSettings({
        expectedRevision: savedSettings?.revision ?? 0,
        scriptProvider: scriptProvider || null,
        imageProvider: imageProvider || null,
        videoPromptProvider: videoPromptProvider || null,
        maxOpenBrowsers,
        maxConcurrentJobs,
        visibleBrowserProviders: visibleBrowsers,
        // Bảo tồn nguyên vẹn đường dẫn thư mục lưu trữ đã lưu
        documentsPath: savedSettings?.documentsPath,
        imagesPath: savedSettings?.imagesPath,
        videosPath: savedSettings?.videosPath,
      });

      // Thành công: cập nhật state đã lưu
      setSavedSettings(res);
      setScriptProvider(res.scriptProvider ?? "");
      setImageProvider(res.imageProvider ?? "");
      setVideoPromptProvider(res.videoPromptProvider ?? "");
      setMaxOpenBrowsers(res.maxOpenBrowsers ?? 4);
      setMaxConcurrentJobs(res.maxConcurrentJobs ?? 8);
      setVisibleBrowsers(res.visibleBrowserProviders ?? []);
      setSaveSuccessMessage("Đã lưu thành công cấu hình dịch vụ AI và hạn mức!");
      setFieldErrors({});
    } catch (err: any) {
      const code = err?.code;
      if (code === "CONCURRENCY_CONFLICT" || code === "VERSION_MISMATCH") {
        setIsConcurrencyConflict(true);
        setGeneralError(
          "Cài đặt đã bị thay đổi ở một phiên khác. Bạn có thể tải lại để lấy mới nhất."
        );
        // BẢO TOÀN NGUYÊN VẸN FORM DRAFT
      } else {
        const fe = err?.fieldErrors || err?.details?.fieldErrors;
        if (fe && typeof fe === "object") {
          setFieldErrors(fe);
        } else {
          const message =
            err?.message || (err instanceof Error ? err.message : String(err));
          setGeneralError(message || "Không thể lưu cài đặt. Vui lòng thử lại.");
        }
        // BẢO TOÀN NGUYÊN VẸN FORM DRAFT
      }
    } finally {
      setIsSaving(false);
    }
  };

  // Render thông tin trạng thái provider bên dưới dropdown
  const renderProviderStatus = (selectedKey: string, taskType: string) => {
    if (!selectedKey) return null;
    const info = getProviderInfo(selectedKey, taskType);

    return (
      <div className="mt-2 space-y-1.5 pt-1 border-t border-border/40 text-xs">
        <div className="flex flex-wrap items-center gap-2">
          {info?.isTrial ? null : info?.isUnverified ? (
            <Badge
              variant="warning"
              className="gap-1 py-0.5"
              data-testid="verification-badge"
            >
              <AlertTriangle className="h-3 w-3" />
              Chưa xác minh — Tự động hóa chưa sẵn sàng
            </Badge>
          ) : (
            <Badge
              variant="success"
              className="gap-1 py-0.5"
              data-testid="verification-badge"
            >
              <CheckCircle2 className="h-3 w-3" />
              Đã xác minh (Tự động hóa hoàn toàn)
            </Badge>
          )}

          <span
            className="text-muted-foreground font-medium"
            data-testid="account-count-status"
          >
            {info && info.connectedAccountsCount > 0
              ? `${info.connectedAccountsCount} tài khoản đã kết nối`
              : "Chưa có tài khoản nào được kết nối"}
          </span>
        </div>

        <div>
          <button
            type="button"
            onClick={onNavigateToAccounts}
            className="inline-flex items-center gap-1 font-medium text-primary hover:underline hover:text-primary/90 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring rounded-xs"
          >
            Quản lý tài khoản tại Cài đặt → Tài khoản
            <ExternalLink className="h-3 w-3" />
          </button>
        </div>
      </div>
    );
  };

  return (
    <div className="space-y-6">
      {/* Tiêu đề & Giới thiệu */}
      <div className="space-y-1">
        <h3 className="text-lg font-bold text-foreground">
          Cài đặt Dịch vụ AI & Hạn mức
        </h3>
        <p className="text-sm text-muted-foreground">
          Chỉ định nhà cung cấp AI cho từng bước kịch bản, ảnh và prompt video; thiết lập hạn mức phiên trình duyệt và tác vụ đồng thời toàn hệ thống.
        </p>
      </div>

      {isLoading ? (
        <div className="flex flex-col items-center justify-center p-8 text-muted-foreground gap-2">
          <Loader2 className="h-6 w-6 animate-spin text-primary" />
          <p className="text-sm">Đang nạp thông tin cài đặt AI và danh mục provider...</p>
        </div>
      ) : (
        <form onSubmit={handleSave} className="space-y-6" noValidate>
          {/* Thông báo Thành công */}
          {saveSuccessMessage && (
            <Alert variant="success" className="animate-in fade-in-50">
              <Check className="h-4 w-4" />
              <AlertTitle>Thành công</AlertTitle>
              <AlertDescription>{saveSuccessMessage}</AlertDescription>
            </Alert>
          )}

          {/* Cảnh báo Xung đột phiên bản (Concurrency Conflict) */}
          {isConcurrencyConflict && (
            <Alert variant="warning" className="animate-in fade-in-50">
              <AlertTriangle className="h-4 w-4" />
              <AlertTitle>Xung đột phiên bản cài đặt</AlertTitle>
              <AlertDescription className="space-y-2">
                <p>{generalError}</p>
                <div className="pt-1">
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={loadData}
                    className="gap-1.5 border-warning/40 text-warning dark:text-warning hover:bg-warning/10"
                  >
                    <RotateCcw className="h-3.5 w-3.5" />
                    Tải lại cấu hình mới nhất
                  </Button>
                </div>
              </AlertDescription>
            </Alert>
          )}

          {/* Thông báo Lỗi chung */}
          {!isConcurrencyConflict && generalError && (
            <Alert variant="destructive" className="animate-in fade-in-50">
              <AlertCircle className="h-4 w-4" />
              <AlertTitle>Lỗi</AlertTitle>
              <AlertDescription>{generalError}</AlertDescription>
            </Alert>
          )}

          {/* KHỐI 1: 3 DROPDOWNS DỊCH VỤ AI ĐỘC LẬP */}
          <Card>
            <CardHeader className="pb-4">
              <div className="flex items-center gap-2">
                <Sparkles className="h-5 w-5 text-primary" />
                <CardTitle>Nhà cung cấp Dịch vụ AI</CardTitle>
              </div>
              <CardDescription>
                Lựa chọn độc lập dịch vụ xử lý cho từng loại tác vụ trong luồng sản xuất.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-5">
              {/* Dropdown 1: Script Provider */}
              <div className="space-y-2">
                <Label htmlFor="script-provider" className="font-medium text-foreground">
                  Tác vụ Kịch bản & Đề xuất (Script)
                </Label>
                <select
                  id="script-provider"
                  value={scriptProvider}
                  onChange={(e) => {
                    setScriptProvider(e.target.value);
                    setSaveSuccessMessage(null);
                  }}
                  aria-describedby="script-provider-desc"
                  className={cn(
                    "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                    fieldErrors.scriptProvider && "border-destructive focus-visible:ring-destructive"
                  )}
                >
                  <option value="">Chưa chọn (Mặc định)</option>
                  <option value="gemini-web">Gemini Web</option>
                  <option value="chatgpt-web">ChatGPT Web</option>
                </select>
                <p id="script-provider-desc" className="text-xs text-muted-foreground">
                  Dùng cho phân tích/đề xuất ý tưởng (SuggestIdeas) và phát triển kịch bản chi tiết.
                </p>
                {fieldErrors.scriptProvider && (
                  <p className="text-xs text-destructive">{fieldErrors.scriptProvider[0]}</p>
                )}
                {renderProviderStatus(scriptProvider, "Script")}
              </div>

              {/* Dropdown 2: Image Provider */}
              <div className="space-y-2">
                <Label htmlFor="image-provider" className="font-medium text-foreground">
                  Tác vụ Tạo ảnh (Image)
                </Label>
                <select
                  id="image-provider"
                  value={imageProvider}
                  onChange={(e) => {
                    setImageProvider(e.target.value);
                    setSaveSuccessMessage(null);
                  }}
                  aria-describedby="image-provider-desc"
                  className={cn(
                    "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                    fieldErrors.imageProvider && "border-destructive focus-visible:ring-destructive"
                  )}
                >
                  <option value="">Chưa chọn (Mặc định)</option>
                  <option value="gemini-web">Gemini Web</option>
                  <option value="chatgpt-web">ChatGPT Web</option>
                </select>
                <p id="image-provider-desc" className="text-xs text-muted-foreground">
                  Tạo hình ảnh minh họa cảnh quay theo mô tả phân cảnh kịch bản.
                </p>
                {fieldErrors.imageProvider && (
                  <p className="text-xs text-destructive">{fieldErrors.imageProvider[0]}</p>
                )}
                {renderProviderStatus(imageProvider, "Image")}
              </div>

              {/* Dropdown 3: Video Prompt Provider */}
              <div className="space-y-2">
                <Label htmlFor="video-prompt-provider" className="font-medium text-foreground">
                  Tác vụ Prompt Video (Video Prompt)
                </Label>
                <select
                  id="video-prompt-provider"
                  value={videoPromptProvider}
                  onChange={(e) => {
                    setVideoPromptProvider(e.target.value);
                    setSaveSuccessMessage(null);
                  }}
                  aria-describedby="video-prompt-provider-desc"
                  className={cn(
                    "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                    fieldErrors.videoPromptProvider && "border-destructive focus-visible:ring-destructive"
                  )}
                >
                  <option value="">Chưa chọn (Mặc định)</option>
                  <option value="gemini-web">Gemini Web</option>
                  <option value="chatgpt-web">ChatGPT Web</option>
                </select>
                <p id="video-prompt-provider-desc" className="text-xs text-muted-foreground">
                  Tạo câu lệnh prompt video tiếng Anh cho từng cảnh (chuyển động, góc quay, ánh sáng).
                </p>
                {fieldErrors.videoPromptProvider && (
                  <p className="text-xs text-destructive">{fieldErrors.videoPromptProvider[0]}</p>
                )}
                {renderProviderStatus(videoPromptProvider, "VideoPrompt")}
              </div>
            </CardContent>
          </Card>

          {/* KHỐI 2: THẺ THÔNG TIN VIDEO RENDER (CỐ ĐỊNH MUSEAI) */}
          <Card className="border-border/60 bg-muted/30">
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <CardTitle className="text-base font-semibold flex items-center gap-2 text-foreground">
                  <Film className="h-4 w-4 text-primary" />
                  Tác vụ Tạo Video (Video Render)
                </CardTitle>
                <Badge variant="outline" className="text-xs bg-primary/10 text-primary border-primary/20">
                  Cố định qua MuseAI
                </Badge>
              </div>
              <CardDescription className="text-xs text-muted-foreground mt-1">
                Tác vụ Tạo Video (Video Render) cố định chạy qua hệ thống tài khoản MuseAI toàn cục, không chọn trong dropdown này.
              </CardDescription>
            </CardHeader>
            <CardContent className="pt-0 text-xs text-muted-foreground space-y-1">
              <p>
                Hệ thống tự động điều phối hàng đợi Video Render tới tài khoản MuseAI đang rảnh trong danh sách tài khoản toàn ứng dụng.
              </p>
              <button
                type="button"
                onClick={onNavigateToAccounts}
                className="inline-flex items-center gap-1 font-medium text-primary hover:underline hover:text-primary/90 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring rounded-xs"
              >
                Quản lý hoặc đăng nhập tài khoản MuseAI tại Cài đặt → Tài khoản
                <ExternalLink className="h-3 w-3" />
              </button>
            </CardContent>
          </Card>

          {/* KHỐI: HIỆN / ẨN BROWSER THEO TỪNG AI */}
          <Card>
            <CardHeader className="pb-4">
              <CardTitle>Hiển thị browser khi chạy tác vụ</CardTitle>
              <CardDescription>
                Bật để mở cửa sổ browser của AI đó khi chạy tác vụ (tiện theo dõi/gỡ lỗi); tắt để chạy ẩn. Áp dụng cho lần mở browser tiếp theo.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {[
                ["chatgpt-web", "ChatGPT Web"],
                ["gemini-web", "Gemini Web"],
                ["museai", "MuseAI (tạo video)"],
              ].map(([key, name]) => (
                <label key={key} className="flex items-center gap-3 text-sm font-medium text-foreground">
                  <input
                    type="checkbox"
                    checked={visibleBrowsers.includes(key)}
                    onChange={(e) => {
                      setVisibleBrowsers((prev) =>
                        e.target.checked ? [...prev, key] : prev.filter((k) => k !== key)
                      );
                      setSaveSuccessMessage(null);
                    }}
                  />
                  Hiện browser {name}
                </label>
              ))}
            </CardContent>
          </Card>

          {/* KHỐI 3: HẠN MỨC ĐỒNG THỜI TOÀN HỆ THỐNG (BUDGETS) */}
          <Card>
            <CardHeader className="pb-4">
              <div className="flex items-center gap-2">
                <Cpu className="h-5 w-5 text-primary" />
                <CardTitle>Hạn mức đồng thời toàn ứng dụng (Budgets)</CardTitle>
              </div>
              <CardDescription>
                Cấu hình giới hạn an toàn tài nguyên phần cứng máy tính và hàng đợi thực thi.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid grid-cols-1 md:grid-cols-2 gap-6">
              {/* Max Open Browsers */}
              <div className="space-y-2">
                <Label htmlFor="max-open-browsers" className="font-medium text-foreground">
                  Số trình duyệt mở tối đa (Max Open Browsers)
                </Label>
                <input
                  id="max-open-browsers"
                  type="number"
                  min={1}
                  max={16}
                  step={1}
                  value={maxOpenBrowsers}
                  onChange={(e) => {
                    setMaxOpenBrowsers(parseInt(e.target.value, 10));
                    setSaveSuccessMessage(null);
                    setFieldErrors((prev) => {
                      const next = { ...prev };
                      delete next.maxOpenBrowsers;
                      return next;
                    });
                  }}
                  aria-describedby="max-open-browsers-desc"
                  aria-invalid={!!fieldErrors.maxOpenBrowsers}
                  className={cn(
                    "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                    fieldErrors.maxOpenBrowsers && "border-destructive focus-visible:ring-destructive"
                  )}
                />
                <p id="max-open-browsers-desc" className="text-xs text-muted-foreground">
                  Tổng số phiên trình duyệt (ẩn và hiển thị) được phép mở đồng thời trên toàn ứng dụng (1 - 16, mặc định: 4).
                </p>
                {fieldErrors.maxOpenBrowsers && (
                  <p className="text-xs text-destructive" role="alert">
                    {fieldErrors.maxOpenBrowsers[0]}
                  </p>
                )}
              </div>

              {/* Max Concurrent Jobs */}
              <div className="space-y-2">
                <Label htmlFor="max-concurrent-jobs" className="font-medium text-foreground">
                  Số tác vụ chạy đồng thời tối đa toàn app (Max Concurrent Jobs)
                </Label>
                <input
                  id="max-concurrent-jobs"
                  type="number"
                  min={1}
                  max={64}
                  step={1}
                  value={maxConcurrentJobs}
                  onChange={(e) => {
                    setMaxConcurrentJobs(parseInt(e.target.value, 10));
                    setSaveSuccessMessage(null);
                    setFieldErrors((prev) => {
                      const next = { ...prev };
                      delete next.maxConcurrentJobs;
                      return next;
                    });
                  }}
                  aria-describedby="max-concurrent-jobs-desc"
                  aria-invalid={!!fieldErrors.maxConcurrentJobs}
                  className={cn(
                    "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                    fieldErrors.maxConcurrentJobs && "border-destructive focus-visible:ring-destructive"
                  )}
                />
                <p id="max-concurrent-jobs-desc" className="text-xs text-muted-foreground">
                  Tổng số tác vụ hàng đợi được xử lý song song trên toàn hệ thống (1 - 64, mặc định: 8).
                </p>
                {fieldErrors.maxConcurrentJobs && (
                  <p className="text-xs text-destructive" role="alert">
                    {fieldErrors.maxConcurrentJobs[0]}
                  </p>
                )}
              </div>
            </CardContent>
          </Card>

          {/* KHỐI ACTION BUTTONS (LƯU & HỦY) */}
          <div className="flex items-center justify-end gap-3 pt-4 border-t border-border">
            <Button
              type="button"
              variant="outline"
              onClick={handleDiscard}
              disabled={!isDirty || isSaving}
              className="gap-1.5"
            >
              <RotateCcw className="h-4 w-4" />
              Hủy / Khôi phục
            </Button>

            <Button
              type="submit"
              disabled={!isDirty || isSaving || isLoading}
              className="gap-1.5 min-w-[120px]"
            >
              {isSaving ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Đang lưu...
                </>
              ) : (
                <>
                  <Save className="h-4 w-4" />
                  Lưu cài đặt
                </>
              )}
            </Button>
          </div>
        </form>
      )}
    </div>
  );
}
