import { useRef, useState } from "react";
import { ChevronLeft, ChevronRight, ExternalLink, Film, Loader2, Send, Settings } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import type { TikTokAnalyticsAccount } from "../analytics/analyticsApi";
import { blocksAnotherPublish, publishingError, requestIdFor, type PublishAttempt, type PublishingApi, type PublishVideo } from "./publishingApi";
import { PublishStateBadge } from "./PublishStateBadge";

export const VIDEO_LIST_PAGE_SIZE = 10;
const selectClass = "h-9 w-full rounded-md border border-input bg-background px-2 text-sm cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";

interface VideoListTabProps {
  videos: PublishVideo[];
  attempts: PublishAttempt[];
  accounts: TikTokAnalyticsAccount[];
  api: PublishingApi;
  /** Tab khác hoặc việc tải danh sách đang chạy; khóa mọi nút Đăng. */
  disabled: boolean;
  onAttempt: (attempt: PublishAttempt) => void;
  onBusyChange: (busy: boolean) => void;
  onSettled: () => void;
  onOpenAccounts: () => void;
}

const time = (value: string) => { const t = Date.parse(value); return Number.isNaN(t) ? 0 : t; };

export function VideoListTab({ videos, attempts, accounts, api, disabled, onAttempt, onBusyChange, onSettled, onOpenAccounts }: VideoListTabProps) {
  const [page, setPage] = useState(0);
  const [drafts, setDrafts] = useState<Record<string, { accountId?: string; caption?: string }>>({});
  const [publishingId, setPublishingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const requestIds = useRef(new Map<string, { signature: string; id: string }>());
  const busyRef = useRef(false);

  const sorted = [...videos].sort((a, b) => time(b.createdAtUtc) - time(a.createdAtUtc));
  const pageCount = Math.max(1, Math.ceil(sorted.length / VIDEO_LIST_PAGE_SIZE));
  const current = Math.min(page, pageCount - 1);
  const rows = sorted.slice(current * VIDEO_LIST_PAGE_SIZE, (current + 1) * VIDEO_LIST_PAGE_SIZE);
  const edit = (assetId: string, patch: { accountId?: string; caption?: string }) =>
    setDrafts(d => ({ ...d, [assetId]: { ...d[assetId], ...patch } }));

  const publish = async (video: PublishVideo, accountId: string, caption: string) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setPublishingId(video.assetId); onBusyChange(true); setError(null); setMessage(null);
    const id = requestIdFor(requestIds.current, video.assetId, JSON.stringify([video.projectId, video.assetId, accountId, caption]));
    try {
      const result = await api.publish({ clientRequestId: id, projectId: video.projectId, assetId: video.assetId, accountId, caption });
      onAttempt(result);
      setMessage(`${video.fileName}: ${result.message}`);
      if (result.state === "Failed") requestIds.current.delete(video.assetId);
    } catch (err) {
      setError(`${publishingError(err)} Tải lại lịch sử để kiểm tra kết quả; ứng dụng không tự gửi lại.`);
    } finally {
      busyRef.current = false;
      setPublishingId(null); onBusyChange(false); onSettled();
    }
  };

  return (
    <div className="space-y-4">
      {error && <p role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
      {message && <p role="status" className="rounded-lg border bg-muted/40 p-3 text-sm">{message}</p>}
      {!accounts.length && <div className="flex flex-wrap items-center gap-3 rounded-lg border border-dashed p-3 text-sm text-muted-foreground">
        Thêm tài khoản hoặc đăng nhập thủ công trong Cài đặt, sau đó tải lại.
        <Button variant="outline" size="sm" onClick={onOpenAccounts} className="gap-2"><Settings className="size-4" aria-hidden="true" />Cài đặt tài khoản</Button>
      </div>}
      {!sorted.length ? <p className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
        Chưa có video sẵn sàng. Hoàn tất một video trong Dự án rồi tải lại.</p> : <>
        <div className="overflow-x-auto rounded-lg border">
          <Table className="min-w-[960px]">
            <TableHeader><TableRow>
              <TableHead className="w-[22%]">Video</TableHead><TableHead className="w-[20%]">Tài khoản</TableHead>
              <TableHead>Nội dung</TableHead><TableHead className="w-[16%]">Trạng thái</TableHead><TableHead className="w-[120px] text-right">Hành động</TableHead>
            </TableRow></TableHeader>
            <TableBody>
              {rows.map(video => {
                const draft = drafts[video.assetId];
                const accountId = draft?.accountId ?? "";
                const caption = draft?.caption ?? video.caption;
                const length = Array.from(caption).length;
                const account = accounts.find(a => a.accountId === accountId);
                const forAccount = attempts.filter(a => a.assetId === video.assetId && a.accountId === accountId);
                const latest = forAccount[0];
                const blocking = forAccount.find(a => blocksAnotherPublish(a.state));
                const running = publishingId === video.assetId;
                const canPublish = !!account && account.sessionStatus === "Connected" && caption.trim().length > 0 && length <= 2200 &&
                  !blocking && !disabled && publishingId === null;
                return (
                  <TableRow key={video.assetId} className="hover:bg-muted/30">
                    <TableCell className="align-top">
                      <div className="flex gap-3">
                        <span className="flex size-10 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary"><Film className="size-5" aria-hidden="true" /></span>
                        <div className="min-w-0 space-y-0.5">
                          <p className="break-words text-sm font-medium">{video.projectName}</p>
                          <p className="break-all text-xs text-muted-foreground">{video.fileName} · {(video.byteSize / 1024 / 1024).toFixed(1)} MB</p>
                          <time dateTime={video.createdAtUtc} className="block text-xs text-muted-foreground">{new Date(video.createdAtUtc).toLocaleString("vi-VN")}</time>
                        </div>
                      </div>
                    </TableCell>
                    <TableCell className="align-top">
                      <select aria-label={`Tài khoản TikTok cho ${video.fileName}`} className={selectClass} value={accountId} disabled={running}
                        onChange={e => edit(video.assetId, { accountId: e.target.value })}>
                        <option value="">Chọn tài khoản</option>
                        {accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.accountIdentity} · {a.sessionStatus === "Connected" ? "Đã kết nối" : "Cần đăng nhập"}</option>)}
                      </select>
                    </TableCell>
                    <TableCell className="align-top">
                      <Textarea aria-label={`Caption cho ${video.fileName}`} rows={5} value={caption} disabled={running} aria-invalid={length > 2200}
                        onChange={e => edit(video.assetId, { caption: e.target.value })} className="select-text" />
                      <p className={length > 2200 ? "mt-1 text-xs text-destructive" : "mt-1 text-xs text-muted-foreground"}>{length}/2.200 ký tự</p>
                    </TableCell>
                    <TableCell className="align-top text-sm">
                      {latest ? <div className="space-y-1.5">
                        <PublishStateBadge state={latest.state} />
                        {latest.state === "Failed" && <p className="text-xs leading-relaxed text-muted-foreground">{latest.message}</p>}
                        {latest.externalPostUrl && <a href={latest.externalPostUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-xs text-primary underline-offset-2 hover:underline">Xem bài đăng<ExternalLink className="size-3" aria-hidden="true" /></a>}
                      </div> : <Badge variant="outline" className="whitespace-nowrap text-muted-foreground">Chưa đăng</Badge>}
                    </TableCell>
                    <TableCell className="text-right align-top">
                      <Button size="sm" className="gap-2" disabled={!canPublish} onClick={() => void publish(video, accountId, caption)}>
                        {running ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : <Send className="size-4" aria-hidden="true" />}
                        {running ? "Đang đăng…" : "Đăng"}
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </div>
        <nav aria-label="Phân trang video" className="flex flex-wrap items-center justify-between gap-3 text-sm">
          <span className="text-muted-foreground">{sorted.length} video · mới nhất trước</span>
          <div className="flex items-center gap-2">
            <Button variant="outline" size="sm" className="gap-1" disabled={current === 0} onClick={() => setPage(current - 1)}>
              <ChevronLeft className="size-4" aria-hidden="true" />Trước</Button>
            <span aria-live="polite">Trang {current + 1}/{pageCount}</span>
            <Button variant="outline" size="sm" className="gap-1" disabled={current >= pageCount - 1} onClick={() => setPage(current + 1)}>
              Sau<ChevronRight className="size-4" aria-hidden="true" /></Button>
          </div>
        </nav>
      </>}
    </div>
  );
}
