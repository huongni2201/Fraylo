import { useCallback, useEffect, useRef, useState } from "react";
import { Loader2, RefreshCw, Send, Settings } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { PageHeader } from "@/components/PageHeader";
import { TikTokPostPreview } from "../projects/TikTokPostPreview";
import type { TikTokAnalyticsAccount } from "../analytics/analyticsApi";
import { blocksAnotherPublish, publishingError, type PublishingApi, type PublishingList, type PublishPrivacy, type PublishState } from "./publishingApi";

const stateLabels: Record<PublishState, string> = {
  Preparing: "Đang tải video", Submitted: "Đang chờ xác nhận", Published: "Đã đăng",
  Uncertain: "Cần đối soát", Failed: "Chưa đăng", UserConfirmedPublished: "Đã đăng · người dùng xác nhận",
  UserConfirmedNotPublished: "Chưa đăng · người dùng xác nhận",
};
const privacyLabels: Record<PublishPrivacy, string> = { private: "Chỉ mình tôi", friends: "Bạn bè", public: "Mọi người" };
const selectClass = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";

export interface PublishingPageProps {
  active: boolean;
  api: PublishingApi;
  initialProjectId?: string | null;
  selectionRevision?: number;
  onOpenAccounts: () => void;
  onReviewVideo: (projectId: string) => void | Promise<void>;
}

export function PublishingPage({ active, api, initialProjectId, selectionRevision, onOpenAccounts, onReviewVideo }: PublishingPageProps) {
  const [data, setData] = useState<PublishingList>({ videos: [], attempts: [] });
  const [accounts, setAccounts] = useState<TikTokAnalyticsAccount[]>([]);
  const [assetId, setAssetId] = useState("");
  const [accountId, setAccountId] = useState("");
  const [caption, setCaption] = useState("");
  const [privacy, setPrivacy] = useState<PublishPrivacy>("private");
  const [loading, setLoading] = useState(false);
  const [publishing, setPublishing] = useState(false);
  const [reconciling, setReconciling] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [postUrls, setPostUrls] = useState<Record<string, string>>({});
  const [checkedStudio, setCheckedStudio] = useState<Record<string, boolean>>({});
  const requestRef = useRef<{ signature: string; id: string } | null>(null);
  const busyRef = useRef(false);
  const mounted = useRef(true);
  const appliedInitialProject = useRef<string | null>(null);
  const loadVersion = useRef(0);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  const load = useCallback(async () => {
    const version = ++loadVersion.current;
    setLoading(true);
    try {
      const [next, nextAccounts] = await Promise.all([api.list(), api.listAccounts()]);
      if (!mounted.current || version !== loadVersion.current) return;
      setData(next);
      setAccounts(nextAccounts);
      const selectionKey = `${initialProjectId}:${selectionRevision ?? 0}`;
      if (initialProjectId && !busyRef.current && appliedInitialProject.current !== selectionKey) {
        const selected = next.videos.find(video => video.projectId === initialProjectId);
        if (selected) {
          setAssetId(selected.assetId);
          setCaption(selected.caption);
          appliedInitialProject.current = selectionKey;
        }
      }
    } catch (err) { if (mounted.current && version === loadVersion.current) setError(publishingError(err)); }
    finally { if (mounted.current && version === loadVersion.current) setLoading(false); }
  }, [api, initialProjectId, selectionRevision]);

  useEffect(() => { if (active) void load(); }, [active, load]);
  useEffect(() => {
    if (!publishing) return;
    const timer = setInterval(() => {
      void api.list().then(next => { if (mounted.current) setData(next); }).catch(() => {});
    }, 3000);
    return () => clearInterval(timer);
  }, [publishing, api]);

  const video = data.videos.find(v => v.assetId === assetId);
  const account = accounts.find(a => a.accountId === accountId);
  const captionLength = Array.from(caption).length;
  const blocking = data.attempts.find(a => a.assetId === assetId && a.accountId === accountId && blocksAnotherPublish(a.state));
  const canPublish = !!video && !!account && account.sessionStatus === "Connected" && caption.trim().length > 0 &&
    captionLength <= 2200 && !blocking && !publishing && !loading && !reconciling;

  const publish = async () => {
    if (!canPublish || !video || !account || busyRef.current) return;
    busyRef.current = true;
    setPublishing(true); setError(null); setMessage(null);
    const signature = JSON.stringify([video.projectId, assetId, accountId, caption, privacy]);
    if (requestRef.current?.signature !== signature) requestRef.current = { signature, id: crypto.randomUUID() };
    try {
      const result = await api.publish({ clientRequestId: requestRef.current.id, projectId: video.projectId, assetId, accountId, caption, privacy });
      if (!mounted.current) return;
      setData(current => ({ ...current, attempts: [result, ...current.attempts.filter(a => a.attemptId !== result.attemptId)] }));
      setMessage(result.message);
      if (result.state === "Failed") requestRef.current = null;
    } catch (err) {
      if (!mounted.current) return;
      setError(`${publishingError(err)} Tải lại lịch sử để kiểm tra kết quả; ứng dụng không tự gửi lại.`);
    } finally {
      busyRef.current = false;
      if (mounted.current) { setPublishing(false); void load(); }
    }
  };

  const reconcile = async (attemptId: string, wasPublished: boolean) => {
    if (reconciling || !checkedStudio[attemptId]) return;
    setReconciling(attemptId); setError(null);
    try {
      const result = await api.reconcile({ attemptId, wasPublished, externalPostUrl: postUrls[attemptId] });
      setData(current => ({ ...current, attempts: current.attempts.map(a => a.attemptId === attemptId ? result : a) }));
      setMessage(result.message);
      requestRef.current = null;
    } catch (err) { setError(publishingError(err)); }
    finally { setReconciling(null); }
  };

  return (
    <section className="mx-auto flex max-w-6xl flex-col gap-6" aria-labelledby="publishing-title" aria-busy={publishing || loading}>
      <PageHeader
        titleId="publishing-title"
        title="Đăng video"
        description="Chọn video đã tạo, kiểm tra caption và đăng lên tài khoản TikTok của bạn."
        actions={
          <Button variant="outline" onClick={() => { setError(null); void load(); }} disabled={loading || publishing} className="gap-2">
            <RefreshCw className="size-4" aria-hidden="true" />Tải lại
          </Button>
        }
      />
      {error && <p role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
      {message && <p role="status" className="rounded-lg border bg-muted/40 p-3 text-sm">{message}</p>}
      <div className="grid gap-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <Card><CardHeader><CardTitle className="text-base">Xem trước bài đăng</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="flex justify-center">
              <TikTokPostPreview
                key={video?.assetId ?? "none"}
                videoSrc={video ? `https://media.titoai.local/media/${encodeURIComponent(video.assetId)}` : null}
                caption={caption}
                handle={account?.accountIdentity}
                showCopy={false}
              />
            </div>
            <Label htmlFor="publish-video">Video</Label>
            <select id="publish-video" className={selectClass} value={assetId} disabled={publishing || loading} onChange={e => {
              setAssetId(e.target.value); setCaption(data.videos.find(v => v.assetId === e.target.value)?.caption ?? ""); setMessage(null);
            }}><option value="">Chọn video hoàn chỉnh</option>
              {data.videos.map(v => <option key={v.assetId} value={v.assetId}>{v.projectName} · {v.fileName}</option>)}
            </select>
            {video ? <div className="flex items-center justify-between gap-3 text-xs text-muted-foreground">
              <span className="min-w-0 truncate">{video.fileName} · {(video.byteSize / 1024 / 1024).toFixed(1)} MB</span>
              <Button variant="outline" size="sm" onClick={() => {
                void Promise.resolve().then(() => onReviewVideo(video.projectId)).catch(err => setError(publishingError(err)));
              }} disabled={publishing}>Xem lại video trong dự án</Button>
            </div> : <p className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
              {loading ? "Đang tải video…" : data.videos.length ? "Chọn một video để chuẩn bị đăng." : "Chưa có video sẵn sàng. Hoàn tất một video trong Dự án rồi tải lại."}
            </p>}
            <p className="text-xs leading-relaxed text-muted-foreground">Phiên đăng nhập được giữ trong profile TikTok riêng trên máy. Video chỉ được gửi khi bạn bấm Đăng lên TikTok.</p>
          </CardContent></Card>
        <Card><CardHeader><CardTitle className="text-base">Nội dung bài đăng</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2"><Label htmlFor="publish-account">Tài khoản TikTok</Label>
              <select id="publish-account" className={selectClass} value={accountId} disabled={publishing || loading} onChange={e => setAccountId(e.target.value)}>
                <option value="">Chọn tài khoản</option>{accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.accountIdentity} · {a.sessionStatus === "Connected" ? "Đã kết nối" : "Cần đăng nhập"}</option>)}
              </select>
              {(!accounts.length || (account && account.sessionStatus !== "Connected")) && <div className="space-y-2">
                <p className="text-sm text-muted-foreground">Thêm tài khoản hoặc đăng nhập thủ công trong Cài đặt, sau đó tải lại.</p>
                <Button variant="outline" onClick={onOpenAccounts} disabled={publishing} className="gap-2"><Settings className="size-4" aria-hidden="true" />Cài đặt tài khoản</Button>
              </div>}
            </div>
            <div className="space-y-2"><Label htmlFor="publish-caption">Caption và hashtag</Label>
              <Textarea id="publish-caption" rows={7} value={caption} disabled={publishing} aria-describedby="publish-caption-length" aria-invalid={captionLength > 2200}
                onChange={e => setCaption(e.target.value)} placeholder="Mô tả video và #hashtag…" className="select-text" />
              <p id="publish-caption-length" className={captionLength > 2200 ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>{captionLength}/2.200 ký tự</p>
            </div>
            <div className="space-y-2"><Label htmlFor="publish-privacy">Ai có thể xem video?</Label>
              <select id="publish-privacy" className={selectClass} value={privacy} disabled={publishing} onChange={e => setPrivacy(e.target.value as PublishPrivacy)}>
                {Object.entries(privacyLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
              </select>
            </div>
            {blocking && <p role="status" className="text-sm text-muted-foreground">{stateLabels[blocking.state]}. Kiểm tra lịch sử bên dưới trước khi đăng lại.</p>}
            {account && <p className="text-sm">Đăng lên <strong>{account.accountIdentity}</strong> · {privacyLabels[privacy]}</p>}
            <Button onClick={() => void publish()} disabled={!canPublish} className="w-full gap-2">
              {publishing ? <Loader2 className="size-4 animate-spin motion-reduce:animate-none" aria-hidden="true" /> : <Send className="size-4" aria-hidden="true" />}
              {publishing ? "Đang tải và đăng video…" : "Đăng lên TikTok"}
            </Button>
          </CardContent></Card>
      </div>
      <Card><CardHeader><CardTitle className="text-base">Lịch sử đăng video</CardTitle></CardHeader><CardContent className="space-y-3">
        {!data.attempts.length && <p className="text-sm text-muted-foreground">Chưa có lần đăng nào.</p>}
        {data.attempts.map(attempt => <article key={attempt.attemptId} className="space-y-2 rounded-lg border p-4">
          <div className="flex flex-wrap items-center justify-between gap-2"><p className="text-sm font-medium">{attempt.accountIdentity} · {stateLabels[attempt.state]}</p>
            <time dateTime={attempt.createdAtUtc} className="text-xs text-muted-foreground">{new Date(attempt.createdAtUtc).toLocaleString("vi-VN")}</time></div>
          <p className="text-sm text-muted-foreground">{attempt.message}</p>
          {attempt.externalPostUrl && <a href={attempt.externalPostUrl} target="_blank" rel="noreferrer" className="text-sm text-primary underline">Xem bài đăng</a>}
          {attempt.state === "Uncertain" && <div className="space-y-3 border-t pt-3">
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={checkedStudio[attempt.attemptId] ?? false} onChange={e => setCheckedStudio(current => ({ ...current, [attempt.attemptId]: e.target.checked }))} />Tôi đã kiểm tra kết quả trong TikTok Studio</label>
            <Label htmlFor={`post-url-${attempt.attemptId}`}>URL video nếu đã đăng</Label>
            <input id={`post-url-${attempt.attemptId}`} type="url" className={selectClass} placeholder="https://www.tiktok.com/@taikhoan/video/…" value={postUrls[attempt.attemptId] ?? ""} onChange={e => setPostUrls(current => ({ ...current, [attempt.attemptId]: e.target.value }))} />
            <div className="flex flex-wrap gap-2"><Button variant="outline" disabled={!checkedStudio[attempt.attemptId] || !postUrls[attempt.attemptId] || !!reconciling} onClick={() => void reconcile(attempt.attemptId, true)}>Xác nhận đã đăng</Button>
              <Button variant="outline" disabled={!checkedStudio[attempt.attemptId] || !!reconciling} onClick={() => void reconcile(attempt.attemptId, false)}>Xác nhận chưa đăng, cho phép thử lại</Button></div>
          </div>}
        </article>)}
      </CardContent></Card>
    </section>
  );
}
