import React, { useState } from "react";
import type {
  BrowserProfileDto,
  OperationalState,
  SessionStatus,
  VisibilityState,
} from "./browserManagerApi";
import { browserManagerApi } from "./browserManagerApi";
import { Card, CardContent, CardFooter, CardHeader } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Bot,
  Video,
  Smartphone,
  Globe,
  RefreshCw,
  ExternalLink,
  X,
  Layers,
} from "lucide-react";
import { cn } from "@/lib/utils";

interface ProfileCardProps {
  profile: BrowserProfileDto;
  onActionComplete?: () => void;
}

export const ProfileCard: React.FC<ProfileCardProps> = ({
  profile,
  onActionComplete,
}) => {
  const [isRequestingLogin, setIsRequestingLogin] = useState(false);
  const [isCheckingSession, setIsCheckingSession] = useState(false);
  const [isCanceling, setIsCanceling] = useState(false);
  const [actionMessage, setActionMessage] = useState<string | null>(null);

  // 1. Phân loại cấu hình tiêu đề và icon theo kind
  const renderProviderHeader = () => {
    switch (profile.kind) {
      case "SharedAI":
        return {
          title:
            profile.displayName ||
            (profile.providerKey === "chatgpt-web"
              ? "ChatGPT Web"
              : profile.providerKey === "gemini-web"
                ? "Gemini Web"
                : "Tài khoản AI"),
          subtitle: profile.accountIdentity || "Chưa xác minh danh tính",
          badgeScope: "Hệ thống AI",
          icon: <Bot className="h-5 w-5" />,
        };
      case "MuseAI":
        return {
          title: profile.displayName || "MuseAI Video",
          subtitle: profile.accountIdentity
            ? `Tài khoản: ${profile.accountIdentity}`
            : "Chưa liên kết tài khoản",
          badgeScope: "Video Generator",
          icon: <Video className="h-5 w-5" />,
        };
      case "TikTok":
        return {
          title: profile.displayName || "TikTok Creator",
          subtitle: profile.accountIdentity
            ? `Kênh: ${profile.accountIdentity}`
            : "Chưa liên kết kênh",
          badgeScope: "Kênh TikTok",
          icon: <Smartphone className="h-5 w-5" />,
        };
      default:
        return {
          title: profile.displayName || profile.profileId,
          subtitle: profile.accountIdentity || "Chưa xác minh",
          badgeScope: "Trình duyệt",
          icon: <Globe className="h-5 w-5" />,
        };
    }
  };

  const headerInfo = renderProviderHeader();

  // 2. Chiều 1: Tiến trình (Operational State)
  const getOperationalBadge = (state: OperationalState) => {
    switch (state) {
      case "Running":
        return (
          <Badge variant="success" className="gap-1.5 text-[11px]" title="Tiến trình browser đang chạy nền">
            <span className="h-1.5 w-1.5 rounded-full bg-success animate-pulse" aria-hidden="true" />
            Đang chạy ẩn
          </Badge>
        );
      case "Idle":
        return (
          <Badge variant="info" className="gap-1.5 text-[11px]" title="Tiến trình sẵn sàng nhận tác vụ">
            <span className="h-1.5 w-1.5 rounded-full bg-info" aria-hidden="true" />
            Chờ việc
          </Badge>
        );
      case "Stopped":
        return (
          <Badge variant="secondary" className="gap-1.5 text-[11px]" title="Trình duyệt chưa khởi động hoặc đã đóng">
            <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/60" aria-hidden="true" />
            Đã dừng
          </Badge>
        );
      case "Quiescing":
        return (
          <Badge variant="warning" className="gap-1.5 text-[11px]" title="Đang thu hồi tài nguyên an toàn">
            <span className="h-1.5 w-1.5 rounded-full bg-warning animate-pulse" aria-hidden="true" />
            Đang thu hồi
          </Badge>
        );
      case "Error":
        return (
          <Badge variant="destructive" className="gap-1.5 text-[11px]" title="Gặp sự cố tiến trình">
            <span className="h-1.5 w-1.5 rounded-full bg-destructive" aria-hidden="true" />
            Lỗi tiến trình
          </Badge>
        );
      default:
        return (
          <Badge variant="secondary" className="text-[11px]">
            Đã dừng
          </Badge>
        );
    }
  };

  // 3. Chiều 2: Hiển thị (Visibility State)
  const getVisibilityBadge = (state: VisibilityState) => {
    switch (state) {
      case "Hidden":
        return (
          <Badge variant="secondary" className="text-[11px]" title="Chạy hoàn toàn ẩn trên desktop">
            Chạy ẩn
          </Badge>
        );
      case "VisibleActive":
        return (
          <Badge variant="default" className="text-[11px] gap-1" title="Cửa sổ trình duyệt đang mở để đăng nhập">
            <span className="h-1.5 w-1.5 rounded-full bg-primary-foreground animate-ping" aria-hidden="true" />
            Đang mở cửa sổ
          </Badge>
        );
      default:
        return (
          <Badge variant="secondary" className="text-[11px]" title="Chạy hoàn toàn ẩn trên desktop">
            Chạy ẩn
          </Badge>
        );
    }
  };

  // 4. Chiều 3: Phiên (Session Status)
  const getSessionBadge = (status: SessionStatus) => {
    switch (status) {
      case "Connected":
        return (
          <Badge variant="success" className="text-[11px]" title="Phiên đăng nhập hợp lệ và sẵn sàng">
            Đã kết nối
          </Badge>
        );
      case "SessionExpired":
        return (
          <Badge variant="warning" className="text-[11px]" title="Phiên đăng nhập đã hết hạn hoặc bị hủy">
            Hết phiên
          </Badge>
        );
      case "Unverified":
        return (
          <Badge variant="outline" className="text-[11px]" title="Chưa kiểm tra hoặc chưa xác minh phiên">
            Chưa xác minh
          </Badge>
        );
      default:
        return (
          <Badge variant="outline" className="text-[11px]">
            Chưa xác minh
          </Badge>
        );
    }
  };

  // Các actions
  const handleRequestLogin = async () => {
    setIsRequestingLogin(true);
    setActionMessage(null);
    try {
      await browserManagerApi.requestLogin(profile.profileId, {
        providerKey: profile.providerKey ?? undefined,
      });
      setActionMessage("Cửa sổ đăng nhập đã được mở.");
      onActionComplete?.();
    } catch (err) {
      setActionMessage(`Lỗi đăng nhập: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setIsRequestingLogin(false);
    }
  };

  const handleCancelLogin = async () => {
    setIsCanceling(true);
    setActionMessage(null);
    try {
      await browserManagerApi.cancelLogin(profile.profileId);
      setActionMessage("Đã đóng cửa sổ đăng nhập.");
      onActionComplete?.();
    } catch (err) {
      setActionMessage(`Lỗi đóng cửa sổ: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setIsCanceling(false);
    }
  };

  const handleCheckSession = async () => {
    setIsCheckingSession(true);
    setActionMessage(null);
    try {
      const res = await browserManagerApi.checkSession(profile.profileId);
      setActionMessage(`Kiểm tra phiên: ${res.sessionStatus === "Connected" ? "Hợp lệ" : "Hết hạn"}`);
      onActionComplete?.();
    } catch (err) {
      setActionMessage(`Lỗi kiểm tra phiên: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setIsCheckingSession(false);
    }
  };

  const isBusy = isRequestingLogin || isCheckingSession || isCanceling;
  const isActive = profile.visibilityState === "VisibleActive";

  return (
    <Card
      className={cn(
        "flex flex-col transition-[color,background-color,border-color,box-shadow,opacity,transform] shadow-xs hover:border-border/80",
        isActive && "border-primary ring-2 ring-primary/20 shadow-md"
      )}
      aria-label={`Trình duyệt ${headerInfo.title}`}
    >
      {/* HEADER CARD */}
      <CardHeader className="p-4 pb-3">
        <div className="flex items-center gap-3">
          <div
            className="h-10 w-10 rounded-lg bg-primary/10 text-primary flex items-center justify-center shrink-0"
            aria-hidden="true"
          >
            {headerInfo.icon}
          </div>
          <div className="flex flex-col min-w-0 flex-1">
            <div className="flex items-center justify-between gap-2">
              <h4 className="font-bold text-sm text-foreground truncate">{headerInfo.title}</h4>
              <Badge variant="outline" className="text-[10px] px-1.5 py-0 shrink-0">
                {headerInfo.badgeScope}
              </Badge>
            </div>
            <p className="text-xs text-muted-foreground truncate mt-0.5">{headerInfo.subtitle}</p>
          </div>
        </div>
      </CardHeader>

      <CardContent className="p-4 pt-0 space-y-3 flex-1">
        {/* 3 CHIỀU TRẠNG THÁI */}
        <div className="rounded-lg border border-border/60 bg-muted/40 p-2.5 space-y-2 text-xs">
          <div className="flex items-center justify-between">
            <span className="text-muted-foreground font-medium">Tiến trình:</span>
            {getOperationalBadge(profile.operationalState)}
          </div>
          <div className="flex items-center justify-between">
            <span className="text-muted-foreground font-medium">Hiển thị:</span>
            {getVisibilityBadge(profile.visibilityState)}
          </div>
          <div className="flex items-center justify-between">
            <span className="text-muted-foreground font-medium">Phiên đăng nhập:</span>
            {getSessionBadge(profile.sessionStatus)}
          </div>
        </div>

        {/* THÔNG TIN CHI TIẾT TABS ĐANG MỞ */}
        {profile.kind === "SharedAI" && (
          <div className="rounded-lg border border-border/50 bg-background/50 p-2.5 space-y-1.5 text-xs">
            <div className="flex items-center justify-between">
              <span className="text-muted-foreground flex items-center gap-1 font-medium">
                <Layers className="h-3.5 w-3.5 text-primary" />
                Tabs đang mở:
              </span>
              <span className="font-semibold text-primary">
                {profile.activeJobCount > 0
                  ? `${profile.activeJobCount} tác vụ đang chạy`
                  : "0 tác vụ"}
              </span>
            </div>
            <div className="flex flex-wrap gap-1 pt-0.5">
              {profile.activeTabs && profile.activeTabs.length > 0 ? (
                profile.activeTabs.map((tab) => (
                  <Badge key={tab} variant="secondary" className="text-[10px] px-2 py-0">
                    {tab === "Script"
                      ? "Kịch bản"
                      : tab === "Image"
                        ? "Sinh ảnh"
                        : tab === "VideoPrompt"
                          ? "Prompt Video"
                          : tab}
                  </Badge>
                ))
              ) : (
                <span className="text-[11px] text-muted-foreground italic">Chưa có tab hoạt động</span>
              )}
            </div>
            <p className="text-xs text-muted-foreground italic pt-1 border-t border-border/40">
              Khép kín: Quản lý độc lập, không lộ thông tin phiên hoặc thư mục lưu trữ.
            </p>
          </div>
        )}

        {/* THÔNG BÁO PHẢN HỒI THAO TÁC */}
        {actionMessage && (
          <p className="text-xs text-muted-foreground italic px-1" role="status">
            {actionMessage}
          </p>
        )}
      </CardContent>

      {/* FOOTER ACTIONS */}
      <CardFooter className="p-4 pt-2 border-t border-border/50 flex items-center justify-end gap-2 flex-wrap mt-auto">
        {isActive ? (
          <div className="flex items-center justify-between w-full gap-2">
            <div className="flex items-center gap-2 text-xs font-semibold text-primary">
              <span className="h-2 w-2 rounded-full bg-primary animate-ping" aria-hidden="true" />
              <span>Cửa sổ đăng nhập đang mở...</span>
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={handleCancelLogin}
              disabled={isBusy}
              className="gap-1.5 h-8 text-xs shrink-0"
            >
              <X className="h-3.5 w-3.5" />
              {isCanceling ? "Đang đóng..." : "Đóng cửa sổ"}
            </Button>
          </div>
        ) : profile.sessionStatus === "Connected" ? (
          <>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={handleCheckSession}
              disabled={isBusy}
              title="Kiểm tra lại tính hợp lệ của phiên làm việc"
              className="gap-1.5"
            >
              <RefreshCw className={cn("h-3.5 w-3.5", isCheckingSession && "animate-spin")} />
              {isCheckingSession ? "Đang kiểm tra..." : "Kiểm tra phiên"}
            </Button>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={handleRequestLogin}
              disabled={isBusy}
              title="Mở lại cửa sổ để đăng nhập lại hoặc đổi tài khoản"
              className="gap-1.5"
            >
              <ExternalLink className="h-3.5 w-3.5" />
              {isRequestingLogin ? "Đang gửi..." : "Đăng nhập lại"}
            </Button>
          </>
        ) : profile.sessionStatus === "SessionExpired" ? (
          <>
            <Button
              type="button"
              variant="default"
              size="sm"
              onClick={handleRequestLogin}
              disabled={isBusy}
              title="Phiên đã hết hạn, nhấn để mở cửa sổ đăng nhập"
              className="gap-1.5"
            >
              <ExternalLink className="h-3.5 w-3.5" />
              {isRequestingLogin ? "Đang gửi..." : "Đăng nhập lại"}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={handleCheckSession}
              disabled={isBusy}
              className="gap-1.5"
            >
              <RefreshCw className={cn("h-3.5 w-3.5", isCheckingSession && "animate-spin")} />
              {isCheckingSession ? "Đang kiểm tra..." : "Kiểm tra phiên"}
            </Button>
          </>
        ) : (
          /* Unverified */
          <>
            <Button
              type="button"
              variant="default"
              size="sm"
              onClick={handleRequestLogin}
              disabled={isBusy}
              title="Mở cửa sổ trình duyệt để đăng nhập tài khoản lần đầu"
              className="gap-1.5"
            >
              <ExternalLink className="h-3.5 w-3.5" />
              {isRequestingLogin ? "Đang gửi..." : "Đăng nhập"}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={handleCheckSession}
              disabled={isBusy}
              className="gap-1.5"
            >
              <RefreshCw className={cn("h-3.5 w-3.5", isCheckingSession && "animate-spin")} />
              {isCheckingSession ? "Đang kiểm tra..." : "Kiểm tra phiên"}
            </Button>
          </>
        )}
      </CardFooter>
    </Card>
  );
};
