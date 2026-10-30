import { useState } from "react";
import { Bookmark, Check, Copy, Heart, MessageCircle, Music2, Share2 } from "lucide-react";
import { Button } from "@/components/ui/button";

export interface TikTokPostPreviewProps {
  /** Nguồn video; để trống thì hiện khung chờ. */
  videoSrc?: string | null;
  caption: string;
  /** Tên hiển thị của tài khoản đăng, ví dụ @shop.ao. */
  handle?: string;
  title?: string;
  /** Ẩn nút sao chép khi caption đã nằm ngay cạnh ô nhập. */
  showCopy?: boolean;
}

/** Tách hashtag để tô màu giống bài đăng TikTok thật. */
function CaptionText({ text }: { text: string }) {
  const parts = text.split(/(#[^\s#]+)/g);
  return (
    <>
      {parts.map((part, i) =>
        part.startsWith("#") ? (
          <span key={i} className="font-semibold">{part}</span>
        ) : (
          <span key={i}>{part}</span>
        ),
      )}
    </>
  );
}

/**
 * Xem trước bài đăng TikTok theo tỉ lệ 9:16: video, tên tài khoản, caption/hashtag và cột nút bên phải.
 * Chỉ mô phỏng giao diện; không gửi gì lên TikTok.
 */
export function TikTokPostPreview({ videoSrc, caption, handle: handleProp, title, showCopy = true }: TikTokPostPreviewProps) {
  const handle = handleProp || "@tai_khoan_cua_ban";
  const [copied, setCopied] = useState(false);
  const [videoFailed, setVideoFailed] = useState(false);
  const hasCaption = caption.trim().length > 0;

  const copyCaption = async () => {
    try {
      await navigator.clipboard.writeText(caption.trim());
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      // Clipboard bị chặn: người dùng vẫn chọn và sao chép thủ công được.
    }
  };

  return (
    <figure className="flex w-[232px] shrink-0 flex-col gap-2.5">
      <div
        className="relative aspect-[9/16] w-full overflow-hidden rounded-[22px] border-[5px] border-neutral-900 bg-neutral-900 shadow-lg"
        aria-label={title ? `Xem trước bài đăng: ${title}` : "Xem trước bài đăng TikTok"}
      >
        {videoSrc && !videoFailed ? (
          <video
            src={videoSrc}
            controls
            playsInline
            preload="metadata"
            onError={() => setVideoFailed(true)}
            className="absolute inset-0 h-full w-full object-cover"
          />
        ) : (
          <div role="status" className="absolute inset-0 flex items-center justify-center p-4 text-center text-xs text-neutral-400">
            {videoSrc ? "Không phát được video." : "Chưa có video."}
          </div>
        )}

        {/* Lớp phủ giống TikTok; pointer-events-none để không chặn thanh điều khiển video */}
        <div className="pointer-events-none absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/80 via-black/40 to-transparent pb-12 pl-3 pr-14 pt-16 text-white">
          <p className="text-[13px] font-semibold">{handle}</p>
          <p className="mt-1 line-clamp-3 text-[12px] leading-snug">
            {hasCaption ? <CaptionText text={caption} /> : <span className="opacity-60">Chưa có caption</span>}
          </p>
          <p className="mt-1.5 flex items-center gap-1 text-[11px] opacity-90">
            <Music2 className="size-3" aria-hidden="true" /> Âm thanh gốc
          </p>
        </div>
        <div className="pointer-events-none absolute bottom-24 right-2 flex flex-col items-center gap-3 text-white" aria-hidden="true">
          <div className="flex flex-col items-center text-[10px]"><Heart className="size-6 fill-white" />12.4K</div>
          <div className="flex flex-col items-center text-[10px]"><MessageCircle className="size-6 fill-white" />348</div>
          <div className="flex flex-col items-center text-[10px]"><Bookmark className="size-6 fill-white" />920</div>
          <div className="flex flex-col items-center text-[10px]"><Share2 className="size-6" />Chia sẻ</div>
        </div>
      </div>

      <figcaption className="space-y-1.5">
        {title && <p className="truncate text-xs font-semibold text-foreground">{title}</p>}
        {showCopy && <Button
          type="button"
          variant="outline"
          size="sm"
          className="h-8 w-full gap-1.5 text-xs"
          disabled={!hasCaption}
          onClick={() => void copyCaption()}
        >
          {copied ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}
          {copied ? "Đã sao chép caption" : "Sao chép caption"}
        </Button>}
      </figcaption>
    </figure>
  );
}
