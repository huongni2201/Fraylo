import { useCallback, useEffect, useRef, useState } from "react";
import { ExternalLink, History, Loader2, RefreshCw, Send, Settings } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { PageHeader } from "@/components/PageHeader";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { TikTokPostPreview } from "../projects/TikTokPostPreview";
import type { TikTokAnalyticsAccount } from "../analytics/analyticsApi";
import { blocksAnotherPublish, confirmUncertainPublishRetry, publishingError, requestIdFor, type PublishingApi, type PublishingList } from "./publishingApi";
import { PublishStateBadge, publishStateLabels } from "./PublishStateBadge";
import { VideoListTab } from "./VideoListTab";
import { FavoriteSoundPicker } from "./FavoriteSoundPicker";

const selectClass = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";

export interface PublishingPageProps {
  active: boolean;
  api: PublishingApi;
  initialProjectId?: string | null;
  initialAssetId?: string;
  selectionRevision?: number;
  onOpenAccounts: () => void;
  onReviewVideo: (projectId: string) => void | Promise<void>;
}

export function PublishingPage({ active, api, initialProjectId, initialAssetId, selectionRevision, onOpenAccounts, onReviewVideo }: PublishingPageProps) {
  const [data, setData] = useState<PublishingList>({ videos: [], attempts: [] });
  const [accounts, setAccounts] = useState<TikTokAnalyticsAccount[]>([]);
  const [assetId, setAssetId] = useState("");
  const [accountId, setAccountId] = useState("");
  const [caption, setCaption] = useState("");
  const [soundName, setSoundName] = useState("");
  const [favoriteSoundKey, setFavoriteSoundKey] = useState("");
  const [soundBusy, setSoundBusy] = useState(false);
  const [loading, setLoading] = useState(false);
  const [publishing, setPublishing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [listBusy, setListBusy] = useState(false);
  const [tab, setTab] = useState("single");
  const requestIds = useRef(new Map<string, { signature: string; id: string }>());
  const busyRef = useRef(false);
  const mounted = useRef(true);
  const appliedInitialProject = useRef<string | null>(null);
  const loadVersion = useRef(0);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  const load = useCallback(async () => {
    const version = ++loadVersion.current;
    const selectionKey = `${initialProjectId}:${initialAssetId ?? ""}:${selectionRevision ?? 0}`;
    setLoading(true);
    try {
      const [next, nextAccounts] = await Promise.all([api.list(), api.listAccounts()]);
      if (!mounted.current || version !== loadVersion.current) return;
      setData(next);
      setAccounts(nextAccounts);
      if (initialProjectId && !busyRef.current && appliedInitialProject.current !== selectionKey) {
        const selected = next.videos.find(video => video.projectId === initialProjectId && (!initialAssetId || video.assetId === initialAssetId));
        setAssetId(selected?.assetId ?? "");
        setCaption(selected?.caption ?? "");
        setTab("single");
        setMessage(null);
        setError(selected ? null : "Clip đã chọn chưa sẵn sàng để đăng.");
        appliedInitialProject.current = selectionKey;
      }
    } catch (err) {
      if (mounted.current && version === loadVersion.current) {
        if (initialProjectId && !busyRef.current && appliedInitialProject.current !== selectionKey) {
          setAssetId("");
          setCaption("");
          setTab("single");
        }
        setError(publishingError(err));
      }
    }
    finally { if (mounted.current && version === loadVersion.current) setLoading(false); }
  }, [api, initialProjectId, initialAssetId, selectionRevision]);

  useEffect(() => { if (active) void load(); }, [active, load]);
  useEffect(() => { setFavoriteSoundKey(""); setSoundName(""); }, [accountId, assetId]);
  useEffect(() => {
    if (!publishing && !listBusy) return;
    const timer = setInterval(() => {
      void api.list().then(next => { if (mounted.current) setData(next); }).catch(() => {});
    }, 3000);
    return () => clearInterval(timer);
  }, [publishing, listBusy, api]);

  const video = data.videos.find(v => v.assetId === assetId);
  const account = accounts.find(a => a.accountId === accountId);
  const captionLength = Array.from(caption).length;
  const relatedAttempts = data.attempts.filter(a => a.assetId === assetId && a.accountId === accountId);
  const blocking = relatedAttempts.find(a => blocksAnotherPublish(a.state));
  const uncertain = relatedAttempts[0]?.state === "Uncertain" ? relatedAttempts[0] : undefined;
  const canPublish = !!video && !!account && account.sessionStatus === "Connected" && caption.trim().length > 0 &&
    captionLength <= 2200 && soundName.trim().length <= 100 && !blocking && !publishing && !listBusy && !soundBusy && !loading;

  const publish = async () => {
    if (!canPublish || !video || !account || busyRef.current) return;
    if (uncertain && !confirmUncertainPublishRetry(uncertain)) return;
    busyRef.current = true;
    setPublishing(true); setError(null); setMessage(null);
    const sound = soundName.trim();
    const signature = JSON.stringify([video.projectId, assetId, accountId, caption, sound, favoriteSoundKey, uncertain?.attemptId]);
    try {
      const result = await api.publish({ clientRequestId: requestIdFor(requestIds.current, "single", signature), projectId: video.projectId, assetId, accountId, caption, ...(sound && { soundName: sound }), ...(favoriteSoundKey && { favoriteSoundKey }), ...(uncertain && { retryAfterAttemptId: uncertain.attemptId }) });
      if (!mounted.current) return;
      setData(current => ({ ...current, attempts: [result, ...current.attempts.filter(a => a.attemptId !== result.attemptId)] }));
      setMessage(result.message);
      if (result.state === "Failed") requestIds.current.delete("single");
    } catch (err) {
      if (!mounted.current) return;
      setError(`${publishingError(err)} Tải lại lịch sử để kiểm tra kết quả; ứng dụng không tự gửi lại.`);
    } finally {
      busyRef.current = false;
      if (mounted.current) { setPublishing(false); void load(); }
    }
  };

  return (
    <section className="mx-auto flex max-w-6xl flex-col gap-6" aria-labelledby="publishing-title" aria-busy={publishing || loading}>
      <PageHeader
        titleId="publishing-title"
        title="Đăng video"
        description="Chọn video đã tạo, kiểm tra caption và đăng lên tài khoản TikTok của bạn."
        actions={
          <Button variant="outline" onClick={() => { setError(null); void load(); }} disabled={loading || publishing || listBusy || soundBusy} className="gap-2">
            <RefreshCw className="size-4" aria-hidden="true" />Tải lại
          </Button>
        }
      />
      {error && <p role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
      {message && <p role="status" className="rounded-lg border bg-muted/40 p-3 text-sm">{message}</p>}
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="single">Đăng một video</TabsTrigger>
          <TabsTrigger value="list">Danh sách video</TabsTrigger>
        </TabsList>
        {/* forceMount + ẩn bằng class để giữ caption đang sửa khi đổi tab. */}
        <TabsContent value="list" forceMount className="data-[state=inactive]:hidden">
          <VideoListTab videos={data.videos} attempts={data.attempts} accounts={accounts} api={api}
            disabled={publishing || loading || soundBusy || listBusy}
            onAttempt={result => setData(current => ({ ...current, attempts: [result, ...current.attempts.filter(a => a.attemptId !== result.attemptId)] }))}
            onBusyChange={setListBusy} onSettled={() => { if (mounted.current) void load(); }} onOpenAccounts={onOpenAccounts} />
        </TabsContent>
        <TabsContent value="single" forceMount className="data-[state=inactive]:hidden">
      <div className="grid gap-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <Card><CardHeader><CardTitle className="text-base">Xem trước bài đăng</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="flex justify-center">
              <TikTokPostPreview
                key={video?.assetId ?? "none"}
                videoSrc={video ? `https://media.fraylo.local/media/${encodeURIComponent(video.assetId)}` : null}
                caption={caption}
                handle={account?.accountIdentity}
                showCopy={false}
              />
            </div>
            <Label htmlFor="publish-video">Video</Label>
            <select id="publish-video" className={selectClass} value={assetId} disabled={publishing || loading || soundBusy || listBusy} onChange={e => {
              setFavoriteSoundKey(""); setSoundName(""); setAssetId(e.target.value); setCaption(data.videos.find(v => v.assetId === e.target.value)?.caption ?? ""); setMessage(null);
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
              <select id="publish-account" className={selectClass} value={accountId} disabled={publishing || loading || soundBusy || listBusy} onChange={e => { setFavoriteSoundKey(""); setSoundName(""); setAccountId(e.target.value); }}>
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
            <FavoriteSoundPicker key={`${accountId}:${assetId}`} api={api} projectId={video?.projectId ?? ""} assetId={assetId}
              accountId={accountId} connected={account?.sessionStatus === "Connected"} disabled={publishing || listBusy || loading || soundBusy}
              value={favoriteSoundKey} onChange={key => { setFavoriteSoundKey(key); if (key) setSoundName(""); }} onBusyChange={setSoundBusy} />
            <div className="space-y-2"><Label htmlFor="publish-sound">Tìm nhạc theo tên (tùy chọn)</Label>
              <Input id="publish-sound" value={soundName} disabled={publishing || soundBusy || listBusy} maxLength={100} placeholder="Tên sound cần tìm trong TikTok"
                onChange={e => { setSoundName(e.target.value); setFavoriteSoundKey(""); }} className="select-text" />
              <p className="text-xs text-muted-foreground">TikTok chọn kết quả đầu tiên khớp tên và tự giới hạn sound theo độ dài video. Không tìm thấy thì ứng dụng dừng, không đăng.</p>
            </div>
            {blocking && <p role="status" className="text-sm text-muted-foreground">{publishStateLabels[blocking.state]}. Kiểm tra lịch sử bên dưới trước khi đăng lại.</p>}
            {uncertain && <p className="text-sm text-muted-foreground">{uncertain.message}</p>}
            {account && <p className="text-sm">Đăng lên <strong>{account.accountIdentity}</strong>. Quyền xem theo thiết lập hiện có trên TikTok.</p>}
            <Button onClick={() => void publish()} disabled={!canPublish} className="w-full gap-2">
              {publishing ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : <Send className="size-4" aria-hidden="true" />}
              {publishing ? "Đang tải và đăng video…" : uncertain ? "Kiểm tra và gửi lại" : "Đăng lên TikTok"}
            </Button>
          </CardContent></Card>
      </div>
        </TabsContent>
      </Tabs>
      <Card><CardHeader><CardTitle className="flex items-center gap-2 text-base"><History className="size-4 text-muted-foreground" aria-hidden="true" />Lịch sử đăng video</CardTitle></CardHeader>
        <CardContent>
          {!data.attempts.length ? <p className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">Chưa có lần đăng nào.</p> :
            <ul className="max-h-[480px] divide-y overflow-y-auto rounded-lg border">
              {data.attempts.map(attempt => <li key={attempt.attemptId} className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2 p-4">
                <div className="min-w-0 flex-1 space-y-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium">{attempt.accountIdentity}</span>
                    <PublishStateBadge state={attempt.state} />
                  </div>
                  <p className="break-words text-sm text-muted-foreground">{attempt.message}</p>
                  {attempt.externalPostUrl && <a href={attempt.externalPostUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-sm text-primary underline-offset-2 hover:underline">Xem bài đăng<ExternalLink className="size-3.5" aria-hidden="true" /></a>}
                </div>
                <time dateTime={attempt.createdAtUtc} className="shrink-0 text-xs text-muted-foreground">{new Date(attempt.createdAtUtc).toLocaleString("vi-VN")}</time>
              </li>)}
            </ul>}
        </CardContent></Card>
    </section>
  );
}
