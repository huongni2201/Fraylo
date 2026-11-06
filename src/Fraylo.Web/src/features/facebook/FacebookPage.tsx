import { useCallback, useEffect, useRef, useState } from "react";
import { RefreshCw, Shuffle, Upload } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { listAssets, uploadAsset, type AssetItem } from "../library/libraryApi";
import { facebookApi, facebookError, facebookStateLabels, isFacebookTaskPending, randomGroupIds,
  type FacebookApi, type FacebookBatchRequest, type FacebookGroup, type FacebookList, type FacebookTask } from "./facebookApi";

export type FacebookView = "compose" | "queue" | "history";
interface Props {
  active: boolean;
  view: FacebookView;
  onNavigate: (view: FacebookView) => void;
  onOpenAccounts: () => void;
  onPendingCount?: (count: number) => void;
  api?: FacebookApi;
}
const control = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";
const panel = "rounded-xl border border-border bg-card p-5";
const empty: FacebookList = { accounts: [], groups: [], tasks: [] };
const titles: Record<FacebookView, string> = { compose: "Soạn bài Facebook", queue: "Hàng đợi Facebook", history: "Lịch sử Facebook" };

export function FacebookPage({ active, view, onNavigate, onOpenAccounts, onPendingCount, api = facebookApi }: Props) {
  const [data, setData] = useState<FacebookList>(empty);
  const [accountId, setAccountId] = useState("");
  const accountRef = useRef(accountId); accountRef.current = accountId;
  const [selected, setSelected] = useState<string[]>([]);
  const [search, setSearch] = useState("");
  const [randomCount, setRandomCount] = useState("20");
  const [text, setText] = useState("");
  const [imageIds, setImageIds] = useState<string[]>([]);
  const [images, setImages] = useState<AssetItem[]>([]);
  const [imagePage, setImagePage] = useState(0);
  const [imageTotal, setImageTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [syncing, setSyncing] = useState<string | null>(null);
  const [syncStatus, setSyncStatus] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [imageBusy, setImageBusy] = useState(false);
  const [taskBusy, setTaskBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<{ request: FacebookBatchRequest; groups: FacebookGroup[]; accountName: string } | null>(null);
  const pendingRequest = useRef<{ signature: string; id: string } | null>(null);
  const mounted = useRef(true);
  const loadVersion = useRef(0);
  const creating = useRef(false);
  const syncRef = useRef(false);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; loadVersion.current++; }; }, []);
  const load = useCallback(async () => {
    const version = ++loadVersion.current;
    setLoading(true);
    try {
      const next = await api.list();
      if (!mounted.current || version !== loadVersion.current) return;
      setData(next);
    } catch (err) { if (mounted.current && version === loadVersion.current) setError(facebookError(err)); }
    finally { if (mounted.current && version === loadVersion.current) setLoading(false); }
  }, [api]);
  useEffect(() => { if (active) void load(); }, [active, load]);
  const pendingCount = data.tasks.filter(task => isFacebookTaskPending(task.state)).length;
  useEffect(() => { onPendingCount?.(pendingCount); }, [pendingCount, onPendingCount]);
  useEffect(() => {
    if (!active && pendingCount === 0) return;
    const timer = setInterval(() => { if (!syncRef.current) void load(); }, 5000);
    return () => clearInterval(timer);
  }, [active, pendingCount, load]);

  const account = data.accounts.find(item => item.accountId === accountId);
  const groups = data.groups.filter(group => group.accountId === accountId);
  const selectedGroups = groups.filter(group => selected.includes(group.targetId));
  const visibleGroups = groups.filter(group => group.name.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const connected = account?.sessionStatus === "Connected" && !!account.accountIdentity && account.enabled !== false;
  const textLength = Array.from(text).length;
  const canCreate = connected && selectedGroups.length > 0 && (text.trim().length > 0 || imageIds.length > 0) && textLength <= 10000 && !busy && !syncing && !imageBusy;

  const changeAccount = (value: string) => {
    setAccountId(value); setSelected([]); setSearch(""); setError(null); setMessage(null); setConfirmation(null);
  };
  const sync = async () => {
    if (!account || !connected || syncRef.current) return;
    const id = accountId;
    syncRef.current = true; setSyncing(id); setError(null); ++loadVersion.current;
    try {
      const result = await api.sync(id);
      if (!mounted.current) return;
      setData(current => ({ ...current, groups: [...current.groups.filter(group => group.accountId !== id), ...result.groups] }));
      setSyncStatus(current => ({ ...current, [id]: `${result.isComplete ? "Đã tải đầy đủ" : "Chưa xác nhận đã tải hết"}: ${result.groups.length} nhóm. ${result.message}` }));
      if (accountRef.current === id) setSelected(current => current.filter(targetId => result.groups.some(group => group.targetId === targetId)));
    } catch (err) { if (mounted.current && accountRef.current === id) setError(facebookError(err)); }
    finally { syncRef.current = false; if (mounted.current) { setSyncing(null); setLoading(false); } }
  };
  const chooseRandom = () => {
    try { setSelected(randomGroupIds(groups, Number(randomCount))); setError(null); }
    catch (err) { setError(facebookError(err)); }
  };
  const toggleGroup = (id: string) => setSelected(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]);
  const loadImages = async (page = 1) => {
    if (imageBusy) return;
    setImageBusy(true); setError(null);
    try {
      const result = await listAssets({ assetType: "Image", page, pageSize: 50 });
      if (!mounted.current) return;
      setImages(current => page === 1 ? result.assets : [...current, ...result.assets.filter(image => !current.some(old => old.assetId === image.assetId))]);
      setImagePage(page); setImageTotal(result.totalCount);
    } catch (err) { if (mounted.current) setError(facebookError(err)); }
    finally { if (mounted.current) setImageBusy(false); }
  };
  const upload = async () => {
    if (imageBusy || imageIds.length >= 10) return;
    setImageBusy(true); setError(null);
    try {
      const result = await uploadAsset({ assetType: "Image" });
      if (!mounted.current || !result.asset) return;
      const asset = result.asset;
      setImages(current => [asset, ...current.filter(image => image.assetId !== asset.assetId)]);
      setImageIds(current => current.includes(asset.assetId) ? current : [...current, asset.assetId]);
    } catch (err) { if (mounted.current) setError(facebookError(err)); }
    finally { if (mounted.current) setImageBusy(false); }
  };
  const review = () => {
    if (!canCreate || !account) return;
    const content = { accountId, targetIds: selectedGroups.map(group => group.targetId).sort(), text, imageAssetIds: [...imageIds] };
    const signature = JSON.stringify(content);
    if (pendingRequest.current?.signature !== signature) pendingRequest.current = { signature, id: crypto.randomUUID() };
    setError(null);
    setConfirmation({ request: { batchRequestId: pendingRequest.current.id, ...content }, groups: [...selectedGroups], accountName: account.displayName });
  };
  const create = async () => {
    if (!confirmation || creating.current) return;
    creating.current = true; setBusy(true); setError(null);
    try {
      const tasks = await api.create(confirmation.request);
      if (!mounted.current) return;
      setData(current => ({ ...current, tasks: [...tasks, ...current.tasks.filter(task => !tasks.some(created => created.taskId === task.taskId))] }));
      setMessage(`Đã tạo ${tasks.length} tác vụ. Theo dõi kết quả từng nhóm trong Hàng đợi.`);
      setConfirmation(null); setSelected([]); pendingRequest.current = null;
      onNavigate("queue");
    } catch (err) {
      if (mounted.current) setError(`${facebookError(err)} Tải lại hàng đợi để kiểm tra. Gửi lại cùng bản xác nhận sẽ dùng cùng mã lô, không tạo thêm bài.`);
    } finally { creating.current = false; if (mounted.current) setBusy(false); }
  };
  const taskAction = async (task: FacebookTask, action: "cancel" | "resume") => {
    if (taskBusy) return;
    setTaskBusy(task.taskId); setError(null);
    try {
      const result = await api[action](task.taskId);
      if (mounted.current) setData(current => ({ ...current, tasks: current.tasks.map(item => item.taskId === result.taskId ? result : item) }));
    } catch (err) { if (mounted.current) setError(facebookError(err)); }
    finally { if (mounted.current) setTaskBusy(null); }
  };
  const tasks = data.tasks.filter(task => view === "queue" ? isFacebookTaskPending(task.state) : !isFacebookTaskPending(task.state));

  return <section className="mx-auto flex max-w-6xl flex-col gap-6" aria-labelledby="facebook-title">
    <PageHeader titleId="facebook-title" title={titles[view]} description="Đăng bài vào các nhóm bạn đã tham gia, theo tài khoản Facebook đã đăng nhập."
      actions={<Button variant="outline" onClick={() => void load()} disabled={loading || !!syncing}><RefreshCw className="mr-2 size-4" aria-hidden="true" />Cập nhật trạng thái</Button>} />
    <div className="grid grid-cols-3 gap-3" aria-label="Tổng quan Facebook">
      {[{ label: "Nhóm đã lưu", value: accountId ? data.groups.filter(g => g.accountId === accountId).length : data.groups.length }, { label: "Đang chờ xử lý", value: pendingCount }, { label: "Đã xuất bản trong lịch sử gần đây", value: data.tasks.filter(t => t.state === "Published").length }].map(item => <div key={item.label} className="rounded-xl border border-border bg-card px-4 py-3"><p className="text-xs font-medium text-muted-foreground">{item.label}</p><p className="mt-1 text-2xl font-semibold tracking-tight tabular-nums">{item.value}</p></div>)}
    </div>
    {error && !confirmation && <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
    {message && <p role="status" className="rounded-lg border border-border bg-muted p-3 text-sm">{message}</p>}
    {(view === "compose") && <div className={view === "compose" ? "grid items-start gap-5 min-[1200px]:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]" : "flex flex-col gap-5"}>
      <div className={`${panel} col-span-full flex flex-col gap-4 sm:flex-row sm:items-end`}>
        <div className="min-w-0 flex-1"><label htmlFor="facebook-account" className="mb-2 block text-sm font-medium">Tài khoản Facebook</label>
          <select id="facebook-account" className={control} value={accountId} onChange={event => changeAccount(event.target.value)} disabled={busy}>
            <option value="">Chọn tài khoản</option>{data.accounts.map(item => <option key={item.accountId} value={item.accountId}>{item.displayName} — {item.enabled === false ? "Đã tắt" : item.sessionStatus === "Connected" ? "Đã kết nối" : "Cần đăng nhập"}</option>)}
          </select>
          {!loading && data.accounts.length === 0 && <p className="mt-2 text-sm text-muted-foreground">Chưa có tài khoản Facebook. Thêm tài khoản trong Cài đặt để bắt đầu.</p>}
          {account && !connected && <p className="mt-2 text-sm text-muted-foreground">Bật tài khoản, đăng nhập và xác minh trong Cài đặt trước khi tải nhóm hoặc đăng.</p>}
        </div>
        <Button variant="outline" onClick={onOpenAccounts}>Cài đặt tài khoản</Button>
        <Button onClick={() => void sync()} disabled={!connected || !!syncing || busy}><RefreshCw className="mr-2 size-4" aria-hidden="true" />{syncing === accountId ? "Đang tải nhóm…" : "Tải lại nhóm từ Facebook"}</Button>
      </div>
      {account && <div className={`${panel} min-w-0`}>
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3"><div><h3 className="font-semibold">Nhóm đã tham gia <span className="ml-1 text-muted-foreground">({groups.length})</span></h3>
          <p className="mt-1 text-sm text-muted-foreground">{syncStatus[accountId] ?? "Danh sách nhóm được lưu trên máy theo tài khoản và dùng lại khi mở ứng dụng. Chỉ bấm Tải lại nhóm từ Facebook khi cần cập nhật."}</p></div>
          <span className="rounded-full bg-primary/10 px-3 py-1 text-sm font-medium text-primary-text">Đã chọn {selectedGroups.length} nhóm</span></div>
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <Input className="min-w-40 flex-1" aria-label="Tìm nhóm" placeholder="Tìm nhóm theo tên…" value={search} onChange={event => setSearch(event.target.value)} />
          <Button variant="outline" disabled={!groups.length || busy} onClick={() => setSelected(groups.map(group => group.targetId))}>Chọn tất cả {groups.length} nhóm</Button>
          <Button variant="ghost" disabled={!selected.length || busy} onClick={() => setSelected([])}>Bỏ chọn</Button>
          <label htmlFor="facebook-random-count" className="sr-only">Số nhóm ngẫu nhiên</label>
          <Input id="facebook-random-count" type="number" min={1} max={groups.length} step={1} className="w-20" value={randomCount} onChange={event => setRandomCount(event.target.value)} />
          <Button variant="outline" onClick={chooseRandom} disabled={!groups.length || busy}><Shuffle className="mr-2 size-4" aria-hidden="true" />Chọn ngẫu nhiên</Button>
        </div>
        <p className="mb-3 text-xs text-muted-foreground">Chọn tất cả / ngẫu nhiên áp dụng trên toàn bộ {groups.length} nhóm đã tải, kể cả nhóm bị ẩn bởi tìm kiếm.</p>
        <div className="max-h-[32rem] overflow-y-auto rounded-lg border border-border divide-y divide-border">
          {visibleGroups.map(group => <label key={group.targetId} className="flex cursor-pointer items-start gap-3 p-3 hover:bg-muted/50">
            <input type="checkbox" className="mt-1 size-4 accent-primary focus-visible:outline-2 focus-visible:outline-ring" checked={selected.includes(group.targetId)} disabled={busy} onChange={() => toggleGroup(group.targetId)} />
            <span className="min-w-0"><span className="block text-sm font-medium">{group.name}</span><span className="mt-1 block break-all text-xs text-muted-foreground">{group.url}</span></span>
          </label>)}
          {!visibleGroups.length && <p className="p-6 text-sm text-muted-foreground">{groups.length ? "Không có nhóm khớp từ khóa." : "Chưa có nhóm đã lưu. Bấm Tải lại nhóm từ Facebook để lấy và lưu danh sách đã tham gia."}</p>}
        </div>
      </div>}
      {view === "compose" && <div className="flex min-w-0 flex-col gap-5">
        <div className={`${panel} flex flex-col gap-4`}>
          <label htmlFor="facebook-text" className="text-sm font-medium">Nội dung bài viết</label>
          <Textarea id="facebook-text" className="min-h-52 select-text" placeholder="Viết nội dung hoặc dán đường dẫn…" value={text} disabled={busy} onChange={event => setText(event.target.value)} />
          <p className="text-xs text-muted-foreground">{textLength.toLocaleString("vi-VN")} / 10.000 ký tự · Link được giữ trong nội dung bài.</p>
          <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="text-sm font-medium">Ảnh đính kèm ({imageIds.length}/10)</h3><div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => void loadImages()} disabled={imageBusy || busy}>Chọn ảnh thư viện</Button>
            <Button size="sm" variant="outline" onClick={() => void upload()} disabled={imageBusy || busy || imageIds.length >= 10}><Upload className="mr-2 size-4" aria-hidden="true" />Tải ảnh</Button></div></div>
          {images.length > 0 && <div className="max-h-48 overflow-y-auto rounded-lg border border-border divide-y divide-border">{images.map(image => <label key={image.assetId} className="flex cursor-pointer items-center gap-3 p-3 text-sm">
            <input type="checkbox" className="size-4 accent-primary" checked={imageIds.includes(image.assetId)} disabled={busy || (!imageIds.includes(image.assetId) && imageIds.length >= 10)} onChange={() => setImageIds(current => current.includes(image.assetId) ? current.filter(id => id !== image.assetId) : [...current, image.assetId])} />
            <span className="min-w-0 truncate">{image.relativePath.split(/[\\/]/).pop()}</span></label>)}
            {images.length < imageTotal && <Button variant="ghost" onClick={() => void loadImages(imagePage + 1)} disabled={imageBusy}>Tải thêm ảnh</Button>}</div>}
          {!images.length && imagePage > 0 && <p className="text-sm text-muted-foreground">Thư viện chưa có ảnh. Bấm Tải ảnh để thêm.</p>}
        </div>
        <aside className={`${panel} flex flex-col gap-4 self-start`}><h3 className="font-semibold">Sẵn sàng tạo lô</h3><p className="text-sm">{account?.displayName ?? "Chưa chọn tài khoản"}</p><p className="text-3xl font-semibold tracking-tight tabular-nums">{selectedGroups.length} <span className="text-sm font-normal text-muted-foreground">nhóm</span></p>
          <p className="text-sm text-muted-foreground">Mỗi nhóm là một tác vụ riêng. Cùng tài khoản đăng cách nhau ít nhất 5 phút, tối đa 20 lần gửi trong 24 giờ; các bài còn lại tiếp tục chờ.</p>
          <p className="text-sm text-muted-foreground">Giữ Fraylo và máy đang chạy để hàng đợi tiếp tục.</p><Button onClick={review} disabled={!canCreate}>Xem lại lô đăng</Button>
        </aside>
      </div>}
    </div>}
    {(view === "queue" || view === "history") && <div className="flex flex-col gap-3">
      {view === "queue" && <p className="text-sm text-muted-foreground">Các nhóm chạy tuần tự theo tài khoản và giãn cách. Bài đã gửi lệnh đăng không thể hủy. Không tự gửi lại bài có kết quả chưa rõ.</p>}
      {tasks.map(task => <article key={task.taskId} className={`${panel} flex flex-col gap-3`}>
        <div className="flex flex-wrap items-start justify-between gap-3"><div className="min-w-0"><h3 className="font-semibold">{task.groupName}</h3><p className="mt-1 text-xs text-muted-foreground">{data.accounts.find(item => item.accountId === task.accountId)?.displayName ?? "Tài khoản đã lưu"} · {new Date(task.createdAtUtc).toLocaleString("vi-VN")}</p></div>
          <span className={`rounded-full border px-3 py-1 text-xs font-medium ${task.state === "Published" ? "border-primary/25 bg-primary/10 text-primary-text" : task.state === "Failed" || task.state === "Unknown" ? "border-destructive/25 bg-destructive/10 text-destructive" : "border-border bg-muted text-foreground"}`}>{facebookStateLabels[task.state] ?? task.state}</span></div>
        <p className="line-clamp-3 whitespace-pre-wrap break-words text-sm select-text">{task.text}</p><p className="text-sm text-muted-foreground">{task.message}</p>
        {task.state === "Unknown" && <p className="text-sm text-muted-foreground">Bài có thể đã được gửi. Hãy tự kiểm tra trong nhóm trước khi tạo bài mới để tránh đăng trùng.</p>}
        <div className="flex flex-wrap gap-2"><a href={task.groupUrl} target="_blank" rel="noreferrer" className="inline-flex h-9 items-center rounded-md border border-border px-3 text-sm underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-ring">Xem nhóm</a>
          {["Queued", "Preparing", "NeedsUser", "Missed", "Scheduled"].includes(task.state) && <Button size="sm" variant="outline" disabled={!!taskBusy} onClick={() => void taskAction(task, "cancel")}>Hủy tác vụ</Button>}
          {task.state === "NeedsUser" && <><Button size="sm" variant="outline" onClick={onOpenAccounts}>Cài đặt tài khoản</Button><Button size="sm" disabled={!!taskBusy} onClick={() => void taskAction(task, "resume")}>Tiếp tục</Button></>}
        </div>
      </article>)}
      {!tasks.length && <div className={`${panel} py-12 text-center text-sm text-muted-foreground`}>{loading ? "Đang tải…" : view === "queue" ? "Hàng đợi trống. Chọn nhóm và soạn bài để bắt đầu." : "Chưa có kết quả đăng Facebook."}</div>}
    </div>}
    <Dialog open={!!confirmation} onOpenChange={open => { if (!open && !busy) setConfirmation(null); }}>
      <DialogContent className="max-w-2xl" onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onPointerDownOutside={event => { if (busy) event.preventDefault(); }}>
        <DialogHeader><DialogTitle>Xác nhận lô đăng vào {confirmation?.groups.length} nhóm</DialogTitle><DialogDescription>Kiểm tra tài khoản, nội dung và toàn bộ nhóm đích. Sau khi xác nhận, các tác vụ bắt đầu vào hàng đợi đăng.</DialogDescription></DialogHeader>
        {confirmation && <><div className="rounded-lg border border-border bg-muted/40 p-3"><p className="text-sm font-medium">{confirmation.accountName}</p><p className="mt-2 max-h-32 overflow-y-auto whitespace-pre-wrap break-words text-sm select-text">{confirmation.request.text || "Bài chỉ có ảnh"}</p><p className="mt-2 text-xs text-muted-foreground">{confirmation.request.imageAssetIds.length} ảnh · {confirmation.groups.length} tác vụ · Giãn cách tối thiểu 5 phút</p></div>
          <ol className="max-h-60 overflow-y-auto rounded-lg border border-border divide-y divide-border">{confirmation.groups.map(group => <li key={group.targetId} className="p-3 text-sm"><span className="text-muted-foreground">{confirmation.accountName} → </span><span className="font-medium">{group.name}</span><span className="mt-1 block break-all text-xs text-muted-foreground">{group.url}</span></li>)}</ol></>}
        {error && <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
        <DialogFooter><Button variant="outline" disabled={busy} onClick={() => setConfirmation(null)}>Quay lại chỉnh sửa</Button><Button onClick={() => void create()} disabled={busy}>{busy ? "Đang tạo lô…" : `Xác nhận tạo ${confirmation?.groups.length ?? 0} tác vụ`}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </section>;
}
