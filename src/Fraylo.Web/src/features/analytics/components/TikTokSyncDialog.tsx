import { useState, useEffect, useCallback } from "react";
import {
  RefreshCw,
  ExternalLink,
  CheckCircle2,
  AlertTriangle,
  AlertCircle,
  Loader2,
  Globe,
  Layers,
} from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  checkTikTokSession,
  openTikTokLogin,
  syncTikTokChannel,
} from "../analyticsApi";
import type {
  TikTokCheckSessionResult,
  TikTokSyncChannelResult,
} from "../analyticsTypes";

interface TikTokSyncDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  accountId: string | null;
  onSyncSuccess: () => void;
}

export function TikTokSyncDialog({
  open,
  onOpenChange,
  accountId,
  onSyncSuccess,
}: TikTokSyncDialogProps) {
  // Session check state
  const [checkingSession, setCheckingSession] = useState(false);
  const [sessionData, setSessionData] = useState<TikTokCheckSessionResult | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);

  // Login browser opening state
  const [openingLogin, setOpeningLogin] = useState(false);
  const [loginMessage, setLoginMessage] = useState<string | null>(null);

  // Sync execution state
  const [syncing, setSyncing] = useState(false);
  const [syncResult, setSyncResult] = useState<TikTokSyncChannelResult | null>(null);
  const [syncError, setSyncError] = useState<string | null>(null);


  // Check session function
  const handleCheckSession = useCallback(async () => {
    if (!accountId) return;
    setCheckingSession(true);
    setSessionError(null);
    setSyncError(null);
    try {
      const res = await checkTikTokSession(accountId);
      setSessionData(res);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Không thể kiểm tra phiên TikTok.";
      setSessionError(msg);
    } finally {
      setCheckingSession(false);
    }
  }, [accountId]);

  // When dialog opens, reset states and check session
  useEffect(() => {
    if (open && accountId) {
      setSessionData(null);
      setSyncResult(null);
      setSyncError(null);
      setLoginMessage(null);
      handleCheckSession();
    }
  }, [open, accountId, handleCheckSession]);

  // Handle opening TikTok login browser window
  const handleOpenLogin = async () => {
    if (!accountId) return;
    setOpeningLogin(true);
    setLoginMessage(null);
    setSyncError(null);
    try {
      const res = await openTikTokLogin(accountId);
      if (res.success) {
        setLoginMessage(
          res.message ||
            "Trình duyệt TikTok đang mở trên máy tính của bạn. Vui lòng hoàn tất đăng nhập tài khoản kênh TikTok, sau đó bấm nút Kéo dữ liệu bên dưới."
        );
      } else {
        setSessionError(res.message || "Không thể mở trình duyệt đăng nhập TikTok.");
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Lỗi khi mở trình duyệt TikTok.";
      setSessionError(msg);
    } finally {
      setOpeningLogin(false);
    }
  };

  // Handle triggering sync
  const handleStartSync = async () => {
    if (!accountId || syncing) return;
    setSyncing(true);
    setSyncError(null);
    setSyncResult(null);

    try {
      const res = await syncTikTokChannel(accountId);

      if (res.success) {
        setSyncResult(res);
        onSyncSuccess();
      } else {
        if (res.requiresLogin) {
          setSyncError(res.message || "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
          // Update session status
          if (sessionData) {
            setSessionData({ ...sessionData, isLoggedIn: false });
          }
        } else {
          setSyncError(res.message || "Quá trình đồng bộ kênh thất bại.");
        }
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Đã xảy ra lỗi khi kết nối TikTok.";
      setSyncError(msg);
    } finally {
      setSyncing(false);
    }
  };

  const handleClose = () => {
    onOpenChange(false);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="sm:max-w-[520px] grid-cols-[minmax(0,1fr)] p-6 gap-5"
        data-testid="tiktok-sync-dialog"
      >
        <DialogHeader className="space-y-1.5">
          <div className="flex items-center gap-2 pr-6">
            <div className="w-8 h-8 shrink-0 rounded-lg bg-primary flex items-center justify-center text-primary-foreground shadow-xs">
              <Layers className="w-4 h-4" />
            </div>
            <DialogTitle className="text-lg font-bold text-foreground">
              Xuất báo cáo TikTok Studio tự động
            </DialogTitle>
          </div>
          <DialogDescription className="text-xs text-muted-foreground">
            Thu thập tự động dữ liệu video, lượt xem, tương tác và GMV affiliate từ trình duyệt TikTok Studio của bạn.
          </DialogDescription>
        </DialogHeader>

        <div className="min-w-0 space-y-4 break-words">
          {/* 1. TikTok Account & Session Status Box */}
          <div
            className="p-3.5 rounded-xl border border-border/70 bg-card/60 space-y-2.5"
            data-testid="tiktok-session-status"
          >
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="text-xs font-semibold text-foreground flex items-center gap-1.5">
                <Globe className="w-3.5 h-3.5 text-muted-foreground" />
                Phiên trình duyệt TikTok
              </span>

              {checkingSession ? (
                <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  <span>Đang kiểm tra...</span>
                </div>
              ) : sessionData?.isLoggedIn ? (
                <Badge
                  variant="success"
                  className="gap-1 text-[11px] font-medium py-0.5"
                  data-testid="tiktok-session-connected-badge"
                >
                  <CheckCircle2 className="w-3 h-3" />
                  Đã kết nối phiên TikTok
                </Badge>
              ) : (
                <Badge
                  variant="warning"
                  className="gap-1 text-[11px] font-medium py-0.5"
                  data-testid="tiktok-session-disconnected-badge"
                >
                  <AlertTriangle className="w-3 h-3" />
                  {sessionData?.sessionStatus === "SessionExpired" ? "Phiên TikTok hết hạn" : "Chưa xác minh được phiên TikTok"}
                </Badge>
              )}
            </div>

            {/* Session Details / Account Info */}
            {!checkingSession && sessionData && (
              <div className="text-xs text-muted-foreground pt-1 border-t border-border/40 flex flex-wrap items-center justify-between gap-2">
                <span className="min-w-0 break-all">
                  Tài khoản:{" "}
                  <strong className="text-foreground font-mono">
                    {sessionData.accountIdentity || (sessionData.isLoggedIn ? "Đã xác thực" : "Chưa liên kết")}
                  </strong>
                </span>

                <Button
                  variant="ghost"
                  size="sm"
                  onClick={handleCheckSession}
                  disabled={checkingSession || syncing}
                  className="h-6 px-2 text-[11px] text-muted-foreground hover:text-foreground"
                  title="Kiểm tra lại trạng thái phiên"
                  data-testid="tiktok-recheck-session-btn"
                >
                  <RefreshCw className="w-3 h-3 mr-1" />
                  Kiểm tra lại
                </Button>
              </div>
            )}

            {/* If not logged in or requested login: Guide & Action */}
            {!checkingSession && sessionData && !sessionData.isLoggedIn && (
              <div className="mt-2 p-2.5 rounded-lg bg-warning/10 border border-warning/20 text-xs text-warning dark:text-warning space-y-2">
                <p className="leading-relaxed">
                  {loginMessage ||
                    sessionData.errorMessage ||
                    "Trình duyệt TikTok chưa có phiên đăng nhập hợp lệ. Bấm nút dưới đây để mở trình duyệt và hoàn tất đăng nhập."}
                </p>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={handleOpenLogin}
                  disabled={openingLogin || syncing}
                  className="w-full text-xs font-semibold gap-1.5 h-8 border-warning/30 hover:bg-warning/15"
                  data-testid="tiktok-open-login-btn"
                >
                  {openingLogin ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : (
                    <ExternalLink className="w-3.5 h-3.5" />
                  )}
                  Mở trình duyệt đăng nhập
                </Button>
              </div>
            )}

            {/* Session Error */}
            {sessionError && (
              <div className="p-2.5 rounded-lg bg-destructive/10 border border-destructive/20 text-xs text-destructive flex items-start gap-2">
                <AlertCircle className="w-4 h-4 shrink-0 mt-0.5" />
                <span>{sessionError}</span>
              </div>
            )}
          </div>

          {/* 2. Export status */}
          {syncing && (
            <div className="space-y-3 p-3.5 rounded-xl border border-primary/20 bg-primary/5" data-testid="tiktok-sync-progress">
              <div className="flex items-center gap-2 text-xs">
                <span className="font-semibold text-primary flex items-center gap-1.5">
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  Đang xuất các báo cáo TikTok Studio...
                </span>
              </div>
            </div>
          )}

          {/* 3. Sync Success Notification */}
          {syncResult && (
            <div
              className="p-4 rounded-xl border border-success/30 bg-success/10 text-success dark:text-success space-y-2"
              data-testid="tiktok-sync-success"
            >
              <div className="flex items-center gap-2 font-semibold text-sm">
                <CheckCircle2 className="w-4 h-4 text-success dark:text-success shrink-0" />
                <span>Đã xử lý báo cáo TikTok Studio</span>
              </div>
              <p className="text-xs leading-relaxed">
                {syncResult.message ||
                  `Đã kéo thành công ${syncResult.recordCount} video từ kênh ${syncResult.channelHandle || ""}!`
                }
              </p>
              <p className="text-[11px] text-muted-foreground">
                Bảng phân tích sẽ tự động cập nhật ngay bây giờ.
              </p>
            </div>
          )}

          {/* 4. Sync Error Notification */}
          {syncError && (
            <div
              className="p-3.5 rounded-xl border border-destructive/20 bg-destructive/10 text-xs text-destructive space-y-2"
              data-testid="tiktok-sync-error"
            >
              <div className="flex items-center gap-2 font-semibold">
                <AlertCircle className="w-4 h-4 shrink-0" />
                <span>Không thể đồng bộ dữ liệu</span>
              </div>
              <p className="leading-relaxed">{syncError}</p>
              {sessionData && !sessionData.isLoggedIn && (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={handleOpenLogin}
                  disabled={openingLogin}
                  className="text-xs gap-1.5 h-7 mt-1 border-destructive/30 hover:bg-destructive/15 text-destructive"
                >
                  <ExternalLink className="w-3 h-3" />
                  Mở trình duyệt đăng nhập TikTok
                </Button>
              )}
            </div>
          )}
        </div>

        <DialogFooter className="flex sm:justify-between items-center gap-2 pt-2 border-t border-border/40">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleClose}
            disabled={syncing}
            className="text-xs"
            data-testid="tiktok-sync-cancel-btn"
          >
            {syncResult ? "Đóng" : "Hủy"}
          </Button>

          <Button
            type="button"
            onClick={handleStartSync}
            disabled={syncing || checkingSession || !accountId}
            className="gap-2 text-xs font-semibold bg-primary hover:bg-primary/90 text-primary-foreground shadow-xs"
            data-testid="tiktok-start-sync-btn"
          >
            {syncing ? (
              <>
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
                <span>Đang kéo dữ liệu...</span>
              </>
            ) : (
              <>
                <RefreshCw className="w-3.5 h-3.5" />
                <span>Bắt đầu kéo dữ liệu về</span>
              </>
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
