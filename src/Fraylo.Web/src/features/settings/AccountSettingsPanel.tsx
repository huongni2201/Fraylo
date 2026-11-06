import React, { useState, useEffect, useCallback, useMemo } from "react";
import {
  accountSettingsApi,
  type ProviderAccountDto,
  type CreateProviderAccountPayload,
} from "./accountSettingsApi";
import { RefreshCw, Plus, Check, AlertCircle, X, ShieldCheck, ShieldAlert, Laptop, Trash2, Pencil, Film, Sparkles } from "lucide-react";
import { IconFacebook, IconTikTok } from "../../components/Icons";
import { cn } from "../../lib/utils";
import { browserManagerApi } from "../browsers/browserManagerApi";

const hasVerifiedSession = (account: ProviderAccountDto) =>
  !!account.verifiedIdentity && account.sessionStatus?.toLowerCase() === "connected";

const PROVIDER_NAMES: Record<string, string> = {
  "chatgpt-web": "ChatGPT Web",
  "chatgpt": "ChatGPT Web",
  "gemini-web": "Gemini Web",
  "gemini": "Gemini Web",
  "museai": "MuseAI Video",
  "museai-video": "MuseAI Video",
  "tiktok": "TikTok",
  "TikTok": "TikTok",
  "facebook": "Facebook",
};

export type ProviderTabKey = "all" | "chatgpt" | "gemini" | "museai" | "tiktok" | "facebook";

export interface ProviderTabItem {
  key: ProviderTabKey;
  label: string;
  defaultProviderKey: string;
}

export const PROVIDER_TABS: ProviderTabItem[] = [
  { key: "all", label: "Tất cả", defaultProviderKey: "chatgpt-web" },
  { key: "chatgpt", label: "ChatGPT", defaultProviderKey: "chatgpt-web" },
  { key: "gemini", label: "Gemini", defaultProviderKey: "gemini-web" },
  { key: "museai", label: "MuseAI", defaultProviderKey: "museai" },
  { key: "tiktok", label: "TikTok", defaultProviderKey: "tiktok" },
  { key: "facebook", label: "Facebook", defaultProviderKey: "facebook" },
];

// shadcn UI-compatible primitives
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

function Badge({
  className,
  variant = "default",
  ...props
}: React.HTMLAttributes<HTMLDivElement> & {
  variant?: "default" | "secondary" | "destructive" | "outline" | "success" | "warning" | "info";
}) {
  const variantClasses = {
    default: "border-transparent bg-primary text-primary-foreground shadow-xs hover:bg-primary/80",
    secondary: "border-transparent bg-secondary text-secondary-foreground hover:bg-secondary/80",
    destructive: "border-transparent bg-destructive text-destructive-foreground shadow-xs hover:bg-destructive/80",
    outline: "text-foreground border-border",
    success: "border-success/30 bg-success/15 text-success dark:text-success",
    warning: "border-warning/30 bg-warning/15 text-warning dark:text-warning",
    info: "border-info/30 bg-info/15 text-info dark:text-info",
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

export function AccountSettingsPanel() {
  const [accounts, setAccounts] = useState<ProviderAccountDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successNotice, setSuccessNotice] = useState<string | null>(null);

  // Sub-tabs phân loại Provider: Tất cả, ChatGPT, Gemini, MuseAI, TikTok
  const [activeProviderTab, setActiveProviderTab] = useState<ProviderTabKey>("all");
  const [statusFilter, setStatusFilter] = useState<string>("all");

  // Create form modal / toggle
  const [isCreating, setIsCreating] = useState(false);
  const [newProviderKey, setNewProviderKey] = useState("chatgpt-web");
  const [newDisplayName, setNewDisplayName] = useState("");
  const [newMaxTabs, setNewMaxTabs] = useState(2);
  const [isSubmittingCreate, setIsSubmittingCreate] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // Edit form modal / inline state
  const [editingAccountId, setEditingAccountId] = useState<string | null>(null);
  const [editDisplayName, setEditDisplayName] = useState("");
  const [editMaxTabs, setEditMaxTabs] = useState(2);
  const [isSubmittingEdit, setIsSubmittingEdit] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);

  // Probe session loading state per account ID
  const [probingAccountId, setProbingAccountId] = useState<string | null>(null);
  const [togglingAccountId, setTogglingAccountId] = useState<string | null>(null);

  const fetchAccounts = useCallback(async () => {
    setIsLoading(true);
    setError(null);
    try {
      const list = await accountSettingsApi.listAccounts();
      setAccounts(list.filter((a) => a.lifecycle === "Active"));
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Không thể tải danh sách tài khoản từ Host.";
      setError(msg);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchAccounts();
  }, [fetchAccounts]);

  useEffect(() => browserManagerApi.onProfileStateChanged(({ payload }) => {
    if (!payload?.profileId || !payload.sessionStatus) return;
    setAccounts((previous) => previous.map((account) =>
      account.profileId === payload.profileId
        ? { ...account, sessionStatus: payload.sessionStatus }
        : account
    ));
  }), []);

  // Đếm số lượng tài khoản theo từng tab
  const tabCounts = useMemo(() => {
    const counts: Record<ProviderTabKey, number> = {
      all: accounts.length,
      chatgpt: 0,
      gemini: 0,
      museai: 0,
      tiktok: 0,
      facebook: 0,
    };
    for (const acc of accounts) {
      const p = acc.providerKey?.toLowerCase() || "";
      if (p.includes("chatgpt") || p.includes("openai")) {
        counts.chatgpt++;
      } else if (p.includes("gemini") || p.includes("google")) {
        counts.gemini++;
      } else if (p.includes("muse")) {
        counts.museai++;
      } else if (p.includes("tiktok")) {
        counts.tiktok++;
      } else if (p.includes("facebook")) {
        counts.facebook++;
      }
    }
    return counts;
  }, [accounts]);

  // Lọc tài khoản theo sub-tab Provider và trạng thái
  const filteredAccounts = useMemo(() => {
    return accounts.filter((acc) => {
      const p = acc.providerKey?.toLowerCase() || "";
      if (activeProviderTab === "chatgpt") {
        if (!p.includes("chatgpt") && !p.includes("openai")) return false;
      } else if (activeProviderTab === "gemini") {
        if (!p.includes("gemini") && !p.includes("google")) return false;
      } else if (activeProviderTab === "museai") {
        if (!p.includes("muse")) return false;
      } else if (activeProviderTab === "tiktok") {
        if (!p.includes("tiktok")) return false;
      } else if (activeProviderTab === "facebook") {
        if (!p.includes("facebook")) return false;
      }

      if (statusFilter === "enabled" && !acc.enabled) return false;
      if (statusFilter === "disabled" && acc.enabled) return false;
      if (statusFilter === "verified" && !hasVerifiedSession(acc)) return false;
      if (statusFilter === "unverified" && hasVerifiedSession(acc)) return false;
      return true;
    });
  }, [accounts, activeProviderTab, statusFilter]);

  const handleOpenCreate = () => {
    const selectedTab = PROVIDER_TABS.find((t) => t.key === activeProviderTab);
    if (selectedTab && selectedTab.key !== "all") {
      setNewProviderKey(selectedTab.defaultProviderKey);
    } else {
      setNewProviderKey("chatgpt-web");
    }
    setIsCreating(true);
    setCreateError(null);
  };

  // Toggle Bật/Tắt tài khoản
  const handleToggleEnable = async (acc: ProviderAccountDto) => {
    if (!acc.enabled && !acc.verifiedIdentity) {
      setError(
        `Không thể kích hoạt tài khoản "${acc.displayName}" khi chưa xác minh danh tính qua session probe. Vui lòng bấm "Đăng nhập trình duyệt" để đăng nhập và bấm "Kiểm tra phiên".`
      );
      return;
    }

    setTogglingAccountId(acc.id);
    setError(null);
    setSuccessNotice(null);
    try {
      const updated = await accountSettingsApi.enableAccount(acc.id, !acc.enabled);
      setAccounts((prev) => prev.map((a) => (a.id === acc.id ? updated : a)));
      setSuccessNotice(
        `Đã ${updated.enabled ? "kích hoạt" : "vô hiệu hóa"} tài khoản "${updated.displayName}".`
      );
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Không thể thay đổi trạng thái tài khoản.";
      setError(msg);
    } finally {
      setTogglingAccountId(null);
    }
  };

  // Kiểm tra phiên đăng nhập (Probe Session)
  const handleProbeSession = async (acc: ProviderAccountDto) => {
    setProbingAccountId(acc.id);
    setError(null);
    setSuccessNotice(null);
    try {
      const res = await accountSettingsApi.probeSession(acc.id);
      await fetchAccounts();
      if (res.isVerified && res.accountIdentity) {
        setSuccessNotice(
          `Phiên đăng nhập hợp lệ: Danh tính "${res.accountIdentity}" (Trạng thái: ${res.sessionStatus}). Bạn đã có thể bật hoạt động cho tài khoản này.`
        );
      } else {
        const detail = res.errorMessage || `Phiên chưa xác minh hoặc hết hạn (Trạng thái: ${res.sessionStatus}).`;
        setError(
          `${detail} Vui lòng bấm "Đăng nhập trình duyệt" để hoàn tất đăng nhập trước khi kiểm tra lại.`
        );
      }
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Lỗi kiểm tra phiên đăng nhập.";
      setError(msg);
    } finally {
      setProbingAccountId(null);
    }
  };

  // Mở trình duyệt để đăng nhập thủ công
  const handleLaunchLogin = async (acc: ProviderAccountDto) => {
    setError(null);
    setSuccessNotice(null);
    try {
      await accountSettingsApi.launchLogin(acc.profileId, acc.providerKey);
      setSuccessNotice(
        `Đã mở cửa sổ trình duyệt cho "${acc.displayName}". Sau khi đăng nhập xong, hãy bấm "Kiểm tra phiên".`
      );
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Không thể mở trình duyệt đăng nhập.";
      setError(msg);
    }
  };

  // Xoá tài khoản AI: đóng trình duyệt, xoá dữ liệu đăng nhập và gỡ tài khoản khỏi hệ thống
  const handleDeleteAccount = async (acc: ProviderAccountDto) => {
    if (
      !window.confirm(
        `Xoá tài khoản "${acc.displayName}"? Trình duyệt và toàn bộ dữ liệu đăng nhập của tài khoản này sẽ bị xoá vĩnh viễn, không thể khôi phục.`
      )
    ) {
      return;
    }
    setTogglingAccountId(acc.id);
    setError(null);
    setSuccessNotice(null);
    try {
      await accountSettingsApi.deleteAccount(acc.id);
      setAccounts((prev) => prev.filter((a) => a.id !== acc.id));
      setSuccessNotice(`Đã xoá tài khoản "${acc.displayName}" và trình duyệt của tài khoản.`);
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Không thể xoá tài khoản.";
      setError(msg);
    } finally {
      setTogglingAccountId(null);
    }
  };

  // Bắt đầu chỉnh sửa tài khoản
  const handleStartEdit = (acc: ProviderAccountDto) => {
    setEditingAccountId(acc.id);
    setEditDisplayName(acc.displayName);
    setEditMaxTabs(acc.maxTabs);
    setEditError(null);
  };

  const handleCancelEdit = () => {
    setEditingAccountId(null);
    setEditDisplayName("");
    setEditError(null);
  };

  // Lưu chỉnh sửa tài khoản
  const handleSaveEdit = async (acc: ProviderAccountDto) => {
    if (!editDisplayName.trim()) {
      setEditError("Vui lòng nhập tên gợi nhớ cho tài khoản.");
      return;
    }
    const n = Math.max(1, Number(editMaxTabs) || 1);
    setIsSubmittingEdit(true);
    setEditError(null);
    setError(null);
    setSuccessNotice(null);
    try {
      const updated = await accountSettingsApi.updateAccount({
        id: acc.id,
        displayName: editDisplayName.trim(),
        enabled: acc.enabled,
        maxTabs: n,
        maxConcurrentJobs: n,
        perTaskLimitsJson: acc.perTaskLimitsJson,
        expectedRevision: acc.revision,
      });
      setAccounts((prev) => prev.map((a) => (a.id === acc.id ? updated : a)));
      setEditingAccountId(null);
      setSuccessNotice(`Đã cập nhật tài khoản "${updated.displayName}" với giới hạn ${n} tác vụ đồng thời.`);
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Không thể cập nhật tài khoản.";
      setEditError(msg);
    } finally {
      setIsSubmittingEdit(false);
    }
  };

  // Tạo tài khoản mới
  const handleCreateAccount = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newDisplayName.trim()) {
      setCreateError("Vui lòng nhập tên gợi nhớ cho tài khoản.");
      return;
    }
    const n = Math.max(1, Number(newMaxTabs) || 1);
    setIsSubmittingCreate(true);
    setCreateError(null);
    try {
      const payload: CreateProviderAccountPayload = {
        providerKey: newProviderKey,
        displayName: newDisplayName.trim(),
        maxConcurrentJobs: n,
        maxTabs: n,
      };
      const created = await accountSettingsApi.createAccount(payload);
      setAccounts((prev) => [...prev, created]);
      setIsCreating(false);
      setNewDisplayName("");
      setSuccessNotice(
        `Đã tạo tài khoản "${created.displayName}". Vui lòng đăng nhập và kiểm tra phiên trước khi kích hoạt.`
      );
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "message" in err
          ? String((err as { message: string }).message)
          : "Không thể tạo tài khoản mới.";
      setCreateError(msg);
    } finally {
      setIsSubmittingCreate(false);
    }
  };

  return (
    <div className="flex flex-col gap-6">
      {/* Header section */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h3 className="text-lg font-bold text-foreground">
            Quản lý Tài khoản Dịch vụ AI
          </h3>
          <p className="text-sm text-muted-foreground mt-0.5">
            Hồ sơ tài khoản AI dùng chung toàn ứng dụng. Tác vụ được cấp phát theo năng lực và Provider tương ứng.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => void fetchAccounts()}
            disabled={isLoading}
            aria-label="Tải lại danh sách tài khoản"
          >
            <RefreshCw className={cn("h-4 w-4", isLoading && "animate-spin")} /> Tải lại
          </Button>
          <Button
            type="button"
            variant="default"
            size="sm"
            onClick={handleOpenCreate}
            aria-label="Thêm tài khoản AI mới"
          >
            <Plus className="h-4 w-4" /> Thêm tài khoản AI
          </Button>
        </div>
      </div>

      {/* Thông báo thành công & lỗi chung */}
      {successNotice && (
        <Alert variant="success" role="status">
          <Check className="h-4 w-4" />
          <div className="text-sm font-medium">{successNotice}</div>
        </Alert>
      )}
      {error && (
        <Alert variant="destructive" role="alert">
          <AlertCircle className="h-4 w-4" />
          <div className="text-sm font-medium">{error}</div>
        </Alert>
      )}

      {/* Form tạo tài khoản mới */}
      {isCreating && (
        <div className="rounded-xl border border-primary/50 bg-card/60 backdrop-blur-xs p-5 shadow-sm flex flex-col gap-4">
          <div className="flex items-center justify-between pb-2 border-b border-border/50">
            <h4 className="text-base font-semibold text-foreground flex items-center gap-2">
              <Plus className="h-4 w-4 text-primary" /> Thêm tài khoản AI mới vào Pool hệ thống
            </h4>
            <button
              type="button"
              className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground transition-colors"
              onClick={() => setIsCreating(false)}
              aria-label="Đóng form tạo mới"
            >
              <X className="h-4 w-4" />
            </button>
          </div>

          {createError && (
            <Alert variant="destructive" role="alert">
              <AlertCircle className="h-4 w-4" />
              <div className="text-sm font-medium">{createError}</div>
            </Alert>
          )}

          <form onSubmit={handleCreateAccount} className="flex flex-col gap-4">
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
              <div className="space-y-1.5">
                <label htmlFor="new-acc-provider" className="text-xs font-semibold text-foreground">
                  Dịch vụ AI (Provider)
                </label>
                <select
                  id="new-acc-provider"
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  value={newProviderKey}
                  onChange={(e) => setNewProviderKey(e.target.value)}
                  disabled={isSubmittingCreate}
                >
                  <option value="chatgpt-web">ChatGPT Web (OpenAI)</option>
                  <option value="gemini-web">Gemini Web (Google)</option>
                  <option value="museai">MuseAI Video</option>
                  <option value="tiktok">TikTok Channel</option>
                  <option value="facebook">Facebook</option>
                </select>
              </div>

              <div className="space-y-1.5">
                <label htmlFor="new-acc-name" className="text-xs font-semibold text-foreground">
                  Tên gợi nhớ (Display Name)
                </label>
                <input
                  id="new-acc-name"
                  type="text"
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  placeholder="Ví dụ: ChatGPT Team 01"
                  value={newDisplayName}
                  onChange={(e) => setNewDisplayName(e.target.value)}
                  disabled={isSubmittingCreate}
                  required
                />
              </div>

              <div className="space-y-1.5">
                <label htmlFor="new-acc-max-tabs" className="text-xs font-semibold text-foreground">
                  Số tác vụ chạy đồng thời
                </label>
                <input
                  id="new-acc-max-tabs"
                  type="number"
                  min={1}
                  max={10}
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  value={newMaxTabs}
                  onChange={(e) => setNewMaxTabs(Math.max(1, parseInt(e.target.value, 10) || 1))}
                  disabled={isSubmittingCreate}
                />
                <p className="text-[11px] text-muted-foreground">
                  Mỗi tác vụ sử dụng một tab riêng. Các tác vụ vượt giới hạn sẽ chờ trong hàng đợi.
                </p>
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => setIsCreating(false)}
                disabled={isSubmittingCreate}
              >
                Hủy
              </Button>
              <Button
                type="submit"
                variant="default"
                size="sm"
                disabled={isSubmittingCreate || !newDisplayName.trim()}
              >
                {isSubmittingCreate ? "Đang lưu..." : "Tạo tài khoản"}
              </Button>
            </div>
          </form>
        </div>
      )}

      {/* Thanh tab nhỏ phân loại theo Provider & Bộ lọc trạng thái */}
      <div className="flex flex-wrap items-center justify-between gap-3 p-3 rounded-lg bg-card border border-border shadow-2xs">
        {/* Nhóm tab nhỏ phân loại: Tất cả, ChatGPT, Gemini, MuseAI, TikTok */}
        <div
          role="tablist"
          aria-label="Phân loại tài khoản theo Provider"
          className="inline-flex h-9 items-center justify-start rounded-lg bg-muted/60 p-1 text-muted-foreground border border-border/60 gap-1 overflow-x-auto"
        >
          {PROVIDER_TABS.map((tab) => {
            const isSelected = activeProviderTab === tab.key;
            const count = tabCounts[tab.key];
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                id={`account-tab-${tab.key}`}
                aria-selected={isSelected}
                tabIndex={isSelected ? 0 : -1}
                className={cn(
                  "inline-flex items-center gap-1.5 whitespace-nowrap rounded-md px-3 py-1 text-xs font-medium transition-[color,background-color,border-color,box-shadow,opacity,transform] cursor-pointer",
                  isSelected
                    ? "bg-background text-foreground shadow-xs font-semibold"
                    : "text-muted-foreground hover:bg-background/50 hover:text-foreground"
                )}
                onClick={() => setActiveProviderTab(tab.key)}
              >
                {tab.key === "tiktok" && <IconTikTok size={13} className="shrink-0" />}
                {tab.key === "facebook" && <IconFacebook size={13} className="shrink-0" />}
                {tab.key === "museai" && <Film className="h-3 w-3 shrink-0" />}
                {(tab.key === "chatgpt" || tab.key === "gemini") && <Sparkles className="h-3 w-3 shrink-0" />}
                <span>{tab.label}</span>
                <span
                  className={cn(
                    "rounded-full px-1.5 py-0.2 text-[10px] font-semibold leading-none",
                    isSelected
                      ? "bg-primary/20 text-primary"
                      : "bg-muted-foreground/15 text-muted-foreground"
                  )}
                >
                  {count}
                </span>
              </button>
            );
          })}
        </div>

        {/* Bộ lọc trạng thái và thống kê số lượng */}
        <div className="flex items-center gap-3 ml-auto">
          <div className="flex items-center gap-2">
            <span className="text-xs font-medium text-muted-foreground">Trạng thái:</span>
            <select
              className="flex h-8 rounded-md border border-input bg-background px-2.5 py-1 text-xs shadow-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
              aria-label="Lọc theo trạng thái tài khoản"
            >
              <option value="all">Tất cả trạng thái</option>
              <option value="enabled">Đang hoạt động</option>
              <option value="disabled">Đang tắt</option>
              <option value="verified">Đã xác minh</option>
              <option value="unverified">Chưa xác minh</option>
            </select>
          </div>

          <div className="text-xs text-muted-foreground">
            Hiển thị <strong className="text-foreground">{filteredAccounts.length}</strong> / {accounts.length} tài khoản
          </div>
        </div>
      </div>

      {/* Danh sách các tài khoản AI */}
      {isLoading ? (
        <div className="flex flex-col items-center justify-center p-8 text-muted-foreground gap-2">
          <RefreshCw className="h-6 w-6 animate-spin text-primary" />
          <p className="text-sm">Đang tải danh sách tài khoản AI...</p>
        </div>
      ) : filteredAccounts.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border/80 p-8 text-center bg-card/40 flex flex-col items-center justify-center gap-3">
          <h4 className="font-semibold text-base text-foreground">
            {activeProviderTab === "all"
              ? "Không tìm thấy tài khoản nào"
              : `Chưa có tài khoản nào thuộc nhóm ${PROVIDER_TABS.find((t) => t.key === activeProviderTab)?.label || ""}`}
          </h4>
          <p className="text-sm text-muted-foreground max-w-md">
            {accounts.length === 0
              ? "Hệ thống chưa có tài khoản nào. Nhấn nút bên dưới để thêm tài khoản đầu tiên vào hệ thống."
              : activeProviderTab === "all"
              ? "Không có tài khoản nào khớp với bộ lọc hiện tại."
              : `Bạn chưa thêm tài khoản ${PROVIDER_TABS.find((t) => t.key === activeProviderTab)?.label || ""} nào vào hệ thống.`}
          </p>
          <Button
            type="button"
            variant="default"
            size="sm"
            onClick={handleOpenCreate}
            className="mt-2"
          >
            <Plus className="h-4 w-4" />{" "}
            {activeProviderTab !== "all"
              ? `Thêm tài khoản ${PROVIDER_TABS.find((t) => t.key === activeProviderTab)?.label}`
              : "Thêm tài khoản AI"}
          </Button>
        </div>
      ) : (
        <div className="flex flex-col gap-3">
          {filteredAccounts.map((acc) => {
            const isProbing = probingAccountId === acc.id;
            const isToggling = togglingAccountId === acc.id;

            return (
              <div
                key={acc.id}
                className="min-w-0 rounded-xl border border-border bg-card p-4 flex flex-col gap-3 transition-[color,background-color,border-color,box-shadow,opacity,transform] hover:border-border/80 shadow-xs"
              >
                {/* Tên tài khoản và nhãn: tên dài được cắt bằng "..." */}
                <div className="flex items-center gap-2 min-w-0">
                  <span className="min-w-0 truncate font-bold text-base text-foreground" title={acc.displayName}>
                    {acc.displayName}
                  </span>
                  <Badge variant="secondary" className="shrink-0 whitespace-nowrap text-[11px] gap-1">
                    {acc.providerKey?.toLowerCase().includes("tiktok") && (
                      <IconTikTok size={12} className="shrink-0" />
                    )}
                    {acc.providerKey?.toLowerCase().includes("facebook") && (
                      <IconFacebook size={12} className="shrink-0" />
                    )}
                    {PROVIDER_NAMES[acc.providerKey] || acc.providerKey}
                  </Badge>
                  {acc.enabled ? (
                    <Badge variant="success" className="shrink-0 whitespace-nowrap text-[11px] gap-1">
                      <span className="h-1.5 w-1.5 rounded-full bg-success" />
                      Đang hoạt động
                    </Badge>
                  ) : (
                    <Badge variant="outline" className="shrink-0 whitespace-nowrap text-[11px] text-muted-foreground">
                      Đang tắt
                    </Badge>
                  )}
                </div>

                {/* Danh tính và số tác vụ đồng thời */}
                <div className="flex items-center gap-x-4 min-w-0 text-xs text-muted-foreground">
                  <span className="flex min-w-0 items-center gap-1">
                    {hasVerifiedSession(acc) ? (
                      <>
                        <ShieldCheck className="h-3.5 w-3.5 shrink-0 text-success" />
                        <span className="shrink-0">Danh tính:</span>
                        <strong
                          className="min-w-0 truncate font-semibold text-success dark:text-success"
                          title={acc.verifiedIdentity ?? undefined}
                        >
                          {acc.verifiedIdentity}
                        </strong>
                      </>
                    ) : (
                      <>
                        <ShieldAlert className="h-3.5 w-3.5 shrink-0 text-warning" />
                        <span className="shrink-0">Danh tính:</span>
                        <span className="italic text-warning dark:text-warning">Chưa xác minh</span>
                      </>
                    )}
                  </span>
                  <span className="flex shrink-0 items-center gap-1 whitespace-nowrap">
                    <Laptop className="h-3.5 w-3.5 text-muted-foreground" />
                    <span>Đồng thời:</span>
                    <strong className="text-foreground">tối đa {acc.maxTabs} tác vụ</strong>
                  </span>
                </div>

                {/* Hành động: nhóm thao tác chính bên trái, sửa/xoá bên phải; cùng một vị trí ở mọi thẻ */}
                <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border/60 pt-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => void handleProbeSession(acc)}
                      disabled={isProbing || isToggling || editingAccountId === acc.id}
                      title="Kiểm tra trạng thái đăng nhập của tài khoản này"
                    >
                      <RefreshCw className={cn("h-3.5 w-3.5", isProbing && "animate-spin")} />
                      {isProbing ? "Đang kiểm tra..." : "Kiểm tra phiên"}
                    </Button>

                    {!hasVerifiedSession(acc) && <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => void handleLaunchLogin(acc)}
                      disabled={isProbing || isToggling || editingAccountId === acc.id}
                      title="Mở cửa sổ trình duyệt để đăng nhập tài khoản này"
                    >
                      Đăng nhập trình duyệt
                    </Button>}

                    <Button
                      type="button"
                      variant={acc.enabled ? "secondary" : "default"}
                      size="sm"
                      onClick={() => void handleToggleEnable(acc)}
                      disabled={isProbing || isToggling || editingAccountId === acc.id}
                      title={acc.enabled ? "Tạm ngắt tài khoản này khỏi hàng đợi" : "Bật tài khoản để nhận tác vụ từ hàng đợi"}
                    >
                      {isToggling ? "Đang xử lý..." : acc.enabled ? "Tắt nhận việc" : "Bật hoạt động"}
                    </Button>
                  </div>

                  <div className="flex items-center gap-1">
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={() => handleStartEdit(acc)}
                      disabled={isProbing || isToggling || editingAccountId === acc.id}
                      title="Chỉnh sửa tên gợi nhớ và giới hạn tác vụ đồng thời"
                    >
                      <Pencil className="h-3.5 w-3.5" />
                      Sửa
                    </Button>

                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={() => void handleDeleteAccount(acc)}
                      disabled={isProbing || isToggling || editingAccountId === acc.id}
                      title="Xoá tài khoản và toàn bộ trình duyệt, dữ liệu đăng nhập của tài khoản"
                      className="text-muted-foreground hover:text-destructive gap-1"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                      Xoá
                    </Button>
                  </div>
                </div>

                {/* Form chỉnh sửa inline khi được chọn */}
                {editingAccountId === acc.id && (
                  <div className="w-full pt-4 mt-2 border-t border-border/60 flex flex-col gap-3 bg-muted/20 p-3.5 rounded-lg">
                    <div className="flex items-center justify-between">
                      <span className="text-xs font-semibold text-foreground flex items-center gap-1.5">
                        <Pencil className="h-3.5 w-3.5 text-primary" />
                        Chỉnh sửa tài khoản: {acc.displayName}
                      </span>
                      <button
                        type="button"
                        onClick={handleCancelEdit}
                        className="text-muted-foreground hover:text-foreground text-xs"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </div>

                    {editError && (
                      <Alert variant="destructive" role="alert">
                        <AlertCircle className="h-4 w-4" />
                        <div className="text-xs font-medium">{editError}</div>
                      </Alert>
                    )}

                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                      <div className="space-y-1.5">
                        <label htmlFor={`edit-acc-name-${acc.id}`} className="text-xs font-semibold text-foreground">
                          Tên gợi nhớ (Display Name)
                        </label>
                        <input
                          id={`edit-acc-name-${acc.id}`}
                          type="text"
                          className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                          value={editDisplayName}
                          onChange={(e) => setEditDisplayName(e.target.value)}
                          disabled={isSubmittingEdit}
                          required
                        />
                      </div>

                      <div className="space-y-1.5">
                        <label htmlFor={`edit-acc-max-tabs-${acc.id}`} className="text-xs font-semibold text-foreground">
                          Số tác vụ chạy đồng thời
                        </label>
                        <input
                          id={`edit-acc-max-tabs-${acc.id}`}
                          type="number"
                          min={1}
                          max={10}
                          className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                          value={editMaxTabs}
                          onChange={(e) => setEditMaxTabs(Math.max(1, parseInt(e.target.value, 10) || 1))}
                          disabled={isSubmittingEdit}
                        />
                        <p className="text-[11px] text-muted-foreground">
                          Mỗi tác vụ sử dụng một tab riêng. Các tác vụ vượt giới hạn sẽ chờ trong hàng đợi.
                        </p>
                      </div>
                    </div>

                    <div className="flex justify-end gap-2 pt-1">
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={handleCancelEdit}
                        disabled={isSubmittingEdit}
                      >
                        Hủy
                      </Button>
                      <Button
                        type="button"
                        variant="default"
                        size="sm"
                        onClick={() => void handleSaveEdit(acc)}
                        disabled={isSubmittingEdit || !editDisplayName.trim()}
                      >
                        {isSubmittingEdit ? "Đang lưu..." : "Lưu thay đổi"}
                      </Button>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
