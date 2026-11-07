import { useCallback, useEffect, useRef, useState } from "react";
import { Check, ImagePlus, RefreshCw, Shuffle, Upload, X } from "lucide-react";
import { FacebookPosts } from "./FacebookPosts";
import { FacebookBulkSchedule } from "./FacebookBulkSchedule";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { listAssets, uploadAsset, type AssetItem } from "../library/libraryApi";
import { assetMediaUrl } from "../library/AssetMediaPreview";
import { facebookApi, facebookError, isFacebookTaskPending, localInputToUtc, randomGroupIds, utcToLocalInput,
  type FacebookApi, type FacebookBatchRequest, type FacebookDraft, type FacebookGroup, type FacebookList, type FacebookPost } from "./facebookApi";

export type FacebookView = "compose" | "queue" | "history" | "posts" | "drafts" | "schedule";
interface Props {
  active: boolean;
  view: FacebookView;
  onNavigate: (view: FacebookView) => void;
  onOpenAccounts: () => void;
  onPendingCount?: (count: number) => void;
  clonePost?: FacebookPost | null;
  openedDraft?: FacebookDraft | null;
  api?: FacebookApi;
}
const control = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";
const panel = "rounded-xl border border-border bg-card p-5";
const empty: FacebookList = { accounts: [], groups: [], tasks: [] };
const MAX_IMAGES = 10;
const titles: Record<FacebookView, string> = { compose: "Soạn bài Facebook", queue: "Hàng đợi Facebook", posts: "Hàng đợi Facebook", drafts: "Nháp Facebook", schedule: "Hẹn giờ đăng hàng loạt", history: "Lịch sử Facebook" };

export function FacebookPage({ active, view, onNavigate, onOpenAccounts, onPendingCount, clonePost, openedDraft, api = facebookApi }: Props) {
  const [data, setData] = useState<FacebookList>(empty);
  const [accountId, setAccountId] = useState("");
  const accountRef = useRef(accountId); accountRef.current = accountId;
  const [selected, setSelected] = useState<string[]>([]);
  const [search, setSearch] = useState("");
  const [randomCount, setRandomCount] = useState("20");
  const [text, setText] = useState("");
  const [rewriteContent, setRewriteContent] = useState(true);
  const [imageIds, setImageIds] = useState<string[]>([]);
  const [images, setImages] = useState<AssetItem[]>([]);
  const [imagePage, setImagePage] = useState(0);
  const [imageTotal, setImageTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [syncing, setSyncing] = useState<string | null>(null);
  const [syncStatus, setSyncStatus] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [imageBusy, setImageBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [mode, setMode] = useState<"now" | "scheduled">("now");
  const [dueInput, setDueInput] = useState("");
  const [draftName, setDraftName] = useState("");
  const [draftId, setDraftId] = useState<string | null>(null);
  const [draftBusy, setDraftBusy] = useState(false);
  const [confirmation, setConfirmation] = useState<{ request: FacebookBatchRequest; groups: FacebookGroup[]; accountName: string } | null>(null);
  const pendingRequest = useRef<{ signature: string; id: string } | null>(null);
  const mounted = useRef(true);
  const loadVersion = useRef(0);
  const creating = useRef(false);
  const syncRef = useRef(false);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; loadVersion.current++; }; }, []);
  // silent: lần làm mới định kỳ không bật cờ loading, tránh nút bị khóa và trạng thái rỗng nhấp nháy mỗi 5 giây.
  const load = useCallback(async (silent = false) => {
    const version = ++loadVersion.current;
    if (!silent) setLoading(true);
    try {
      const next = await api.list();
      if (!mounted.current || version !== loadVersion.current) return;
      setDraftId(current => current && next.drafts && !next.drafts.some(d => d.draftId === current) ? null : current);
      setData({ ...next, groups: next.groups.filter(g => !g.targetId.startsWith("personal:")),
        tasks: next.tasks.filter(t => !t.targetId.startsWith("personal:")),
        posts: next.posts?.filter(p => !p.tasks.some(t => t.targetId.startsWith("personal:"))),
        drafts: next.drafts?.filter(d => !d.targetIds.some(id => id.startsWith("personal:"))) });
    } catch (err) { if (mounted.current && version === loadVersion.current) setError(facebookError(err)); }
    finally { if (mounted.current && version === loadVersion.current && !silent) setLoading(false); }
  }, [api]);
  useEffect(() => { if (active) void load(); }, [active, load]);
  const pendingCount = new Set(data.tasks.filter(task => isFacebookTaskPending(task.state)).map(task => task.batchId)).size;
  useEffect(() => { onPendingCount?.(pendingCount); }, [pendingCount, onPendingCount]);
  useEffect(() => {
    if (!active && pendingCount === 0) return;
    const timer = setInterval(() => { if (!syncRef.current) void load(true); }, 5000);
    return () => clearInterval(timer);
  }, [active, pendingCount, load]);

  const account = data.accounts.find(item => item.accountId === accountId);
  const groups = data.groups.filter(group => group.accountId === accountId);
  const selectedGroups = selected.map(id => groups.find(group => group.targetId === id)).filter((group): group is FacebookGroup => !!group);
  const visibleGroups = groups.filter(group => group.name.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const connected = account?.sessionStatus === "Connected" && !!account.accountIdentity && account.enabled !== false;
  const textLength = Array.from(text).length;
  // undefined = đăng ngay; chuỗi = giờ hẹn hợp lệ (tương lai); null = chưa nhập hoặc không hợp lệ.
  const dueUtc = (() => {
    if (mode !== "scheduled") return undefined;
    try { const iso = localInputToUtc(dueInput); return Date.parse(iso) > Date.now() ? iso : null; } catch { return null; }
  })();
  const drafts = data.drafts ?? [];
  const canCreate = connected && selectedGroups.length > 0 && (text.trim().length > 0 || imageIds.length > 0) && textLength <= 10000 && dueUtc !== null && !busy && !syncing && !imageBusy;

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
    if (imageBusy || imageIds.length >= MAX_IMAGES) return;
    setImageBusy(true); setError(null);
    try {
      const result = await uploadAsset({ assetType: "Image", multiple: true });
      if (!mounted.current) return;
      const uploaded = result.assets ?? (result.asset ? [result.asset] : []);
      if (result.errors?.length) setError(`Không tải được ${result.errors.length} ảnh: ${result.errors.join("; ")}`);
      if (!uploaded.length) return;
      setImages(current => [...uploaded, ...current.filter(image => !uploaded.some(item => item.assetId === image.assetId))]);
      const room = MAX_IMAGES - imageIds.length;
      setImageIds(current => [...current, ...uploaded.map(item => item.assetId).filter(id => !current.includes(id))].slice(0, MAX_IMAGES));
      setMessage(uploaded.length > room ? `Đã tải ${uploaded.length} ảnh lên thư viện; chỉ ${room} ảnh đầu được đính kèm (tối đa ${MAX_IMAGES} ảnh mỗi bài).` : `Đã tải và đính kèm ${uploaded.length} ảnh.`);
    } catch (err) { if (mounted.current) setError(facebookError(err)); }
    finally { if (mounted.current) setImageBusy(false); }
  };
  const toggleImage = (id: string) => setImageIds(current => current.includes(id) ? current.filter(value => value !== id) : current.length >= MAX_IMAGES ? current : [...current, id]);
  const review = () => {
    if (!canCreate || !account) return;
    const content = { accountId, targetIds: selectedGroups.map(group => group.targetId), name: draftName.trim(), text, rewriteContent, imageAssetIds: [...imageIds], ...(dueUtc ? { dueAtUtc: dueUtc } : {}) };
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
      setMessage(`Đã tạo bài đăng vào ${tasks.length} nhóm${confirmation.request.dueAtUtc ? " theo lịch hẹn" : ""}. Mở Chi tiết để theo dõi kết quả từng nhóm.`);
      setConfirmation(null); setSelected([]); pendingRequest.current = null;
      setText(""); setRewriteContent(true); setImageIds([]); setDraftId(null); setDraftName(""); setMode("now"); setDueInput("");
      await load(true); onNavigate("posts");
    } catch (err) {
      if (mounted.current) setError(`${facebookError(err)} Tải lại hàng đợi để kiểm tra. Gửi lại cùng bản xác nhận sẽ dùng cùng mã lô, không tạo thêm bài.`);
    } finally { creating.current = false; if (mounted.current) setBusy(false); }
  };
  const saveDraft = async () => {
    if (!accountId || draftBusy) return;
    setDraftBusy(true); setError(null);
    try {
      const saved = await api.saveDraft({ draftId: draftId ?? undefined, name: draftName.trim() || `Nháp ${new Date().toLocaleString("vi-VN")}`, accountId, text, rewriteContent, targetIds: selected, imageAssetIds: imageIds, ...(dueUtc ? { dueAtUtc: dueUtc } : {}) });
      if (!mounted.current) return;
      setData(current => ({ ...current, drafts: [saved, ...(current.drafts ?? []).filter(item => item.draftId !== saved.draftId)] }));
      setDraftId(saved.draftId); setDraftName(saved.name); setMessage(`Đã lưu nháp "${saved.name}".`);
    } catch (err) { if (mounted.current) setError(facebookError(err)); }
    finally { if (mounted.current) setDraftBusy(false); }
  };
  const openDraft = (draft: FacebookDraft) => {
    if (!data.accounts.some(item => item.accountId === draft.accountId)) { setError("Tài khoản của nháp không còn trong danh sách."); return; }
    const known = new Set(data.groups.filter(group => group.accountId === draft.accountId).map(group => group.targetId));
    const kept = draft.targetIds.filter(id => known.has(id));
    setAccountId(draft.accountId); setSelected(kept); setSearch(""); setText(draft.text); setImageIds(draft.imageAssetIds);
    setRewriteContent(draft.rewriteContent ?? true);
    setMode(draft.dueAtUtc ? "scheduled" : "now"); setDueInput(draft.dueAtUtc ? utcToLocalInput(draft.dueAtUtc) : "");
    onNavigate("compose");
    setDraftId(draft.draftId); setDraftName(draft.name); setConfirmation(null); setError(null);
    setMessage(kept.length < draft.targetIds.length ? `Đã mở nháp; ${draft.targetIds.length - kept.length} nhóm không còn trong danh sách nên bị bỏ chọn.` : `Đã mở nháp "${draft.name}".`);
    if (draft.imageAssetIds.length > 0) void loadImages();
  };
  useEffect(() => {
    if (!clonePost) return;
    const first = clonePost.tasks[0]; if (!first) return;
    setAccountId(clonePost.accountId); setSelected(clonePost.tasks.map(t => t.targetId));
    setText(clonePost.originalText ?? first.text); setImageIds(first.imageAssetIds ?? []); setDraftId(null);
    setRewriteContent(!!clonePost.rewriteChunks?.length);
    setDraftName((clonePost.name + " (bản sao)").slice(0, 80)); setMode("now"); setDueInput("");
    setConfirmation(null); pendingRequest.current = null;
  }, [clonePost]);
  const consumedDraft = useRef<FacebookDraft | null>(null);
  useEffect(() => {
    if (!openedDraft || consumedDraft.current === openedDraft || loading) return;
    if (!data.accounts.some(a => a.accountId === openedDraft.accountId)) {
      setError("Tài khoản của nháp không còn trong danh sách. Kiểm tra Cài đặt tài khoản."); return;
    }
    consumedDraft.current = openedDraft;
    openDraft(openedDraft);
  }, [openedDraft, loading, data.accounts, data.groups]);
  const removeDraft = async (draft: FacebookDraft) => {
    if (draftBusy) return;
    setDraftBusy(true); setError(null);
    try {
      await api.deleteDraft(draft.draftId);
      if (!mounted.current) return;
      setData(current => ({ ...current, drafts: (current.drafts ?? []).filter(item => item.draftId !== draft.draftId) }));
      if (draftId === draft.draftId) setDraftId(null);
    } catch (err) { if (mounted.current) setError(facebookError(err)); }
    finally { if (mounted.current) setDraftBusy(false); }
  };
  return <section className="mx-auto flex max-w-6xl flex-col gap-6" aria-labelledby="facebook-title">
    <PageHeader titleId="facebook-title" title={titles[view]} description="Đăng bài vào các nhóm bạn đã tham gia, theo tài khoản Facebook đã đăng nhập."
      actions={<Button variant="outline" onClick={() => void load()} disabled={loading || !!syncing}><RefreshCw className="mr-2 size-4" aria-hidden="true" />Cập nhật trạng thái</Button>} />
    <div className="grid grid-cols-3 gap-3" aria-label="Tổng quan Facebook">
      {[{ label: "Nhóm đã lưu", value: accountId ? data.groups.filter(g => g.accountId === accountId).length : data.groups.length }, { label: "Đang chờ xử lý", value: pendingCount }, { label: "Đã xuất bản trong lịch sử gần đây", value: data.tasks.filter(t => t.state === "Published").length }].map(item => <div key={item.label} className="rounded-xl border border-border bg-card px-4 py-3"><p className="text-xs font-medium text-muted-foreground">{item.label}</p><p className="mt-1 text-2xl font-semibold tracking-tight tabular-nums">{item.value}</p></div>)}
    </div>
    {error && !confirmation && <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
    {message && <p role="status" className="rounded-lg border border-border bg-muted p-3 text-sm">{message}</p>}
    <nav className="flex flex-wrap gap-2" aria-label="Đăng bài Facebook">
      <Button variant={view === "compose" ? "default" : "outline"} aria-current={view === "compose" ? "page" : undefined} onClick={() => { if (view === "compose") return; setText(""); setRewriteContent(true); setImageIds([]); setSelected([]); setDraftId(null); setDraftName(""); setMode("now"); setDueInput(""); setConfirmation(null); pendingRequest.current = null; onNavigate("compose"); }}>Tạo bài đăng</Button>
      {([['schedule', 'Hẹn giờ đăng']] as const).map(([key, label]) => <Button key={key} variant={view === key ? "default" : "outline"} onClick={() => onNavigate(key)}>{label}</Button>)}
    </nav>
    {(view === "posts" || view === "queue" || view === "history") && <FacebookPosts data={data} api={api} history={view === "history"} onRefresh={() => load(true)} onError={setError} onClone={post => {
      const first = post.tasks[0]; if (!first) return;
      setAccountId(post.accountId); setSelected(post.tasks.map(t => t.targetId)); setText(post.originalText ?? first.text); setImageIds(first.imageAssetIds ?? []);
      setRewriteContent(!!post.rewriteChunks?.length);
      setDraftId(null); setDraftName(`${post.name || "Bài đăng"} (bản sao)`.slice(0, 80)); setMode("now"); setDueInput(""); setConfirmation(null); pendingRequest.current = null; onNavigate("compose");
    }} />}
    {/* Luôn mount (chỉ ẩn khi ở tab khác) để danh sách bài đang soạn không mất khi đổi tab. */}
    <div hidden={view !== "schedule"}><FacebookBulkSchedule data={data} api={api} loading={loading} onRefresh={() => load(true)} onOpenAccounts={onOpenAccounts}
      onCreated={count => { setMessage(`Đã lên lịch ${count} bài. Theo dõi tiến độ từng bài trong Hàng đợi Facebook.`); onNavigate("posts"); }} /></div>
    {view === "drafts" && <div className={panel}><h3 className="font-semibold">Nháp đã lưu</h3>{drafts.length === 0 && <p>Chưa có nháp.</p>}{drafts.map(draft => <div key={draft.draftId} className="flex flex-wrap items-center gap-3 border-b border-border py-3"><span className="flex-1">{draft.name} · {draft.targetIds.length} nhóm</span><Button variant="outline" onClick={() => openDraft(draft)}>Mở nháp</Button><Button variant="ghost" onClick={() => void removeDraft(draft)}>Xóa nháp</Button></div>)}</div>}
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
        <div className="mb-4 flex flex-wrap items-start justify-between gap-3"><div className="min-w-0"><h3 className="font-semibold">Nhóm đã tham gia <span className="ml-1 text-muted-foreground">({groups.length})</span></h3>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{syncStatus[accountId] ?? "Danh sách nhóm được lưu trên máy theo tài khoản và dùng lại khi mở ứng dụng. Chỉ bấm Tải lại nhóm từ Facebook khi cần cập nhật."}</p></div>
          <span className="shrink-0 rounded-full bg-primary/10 px-3 py-1 text-sm font-medium text-primary-text" aria-live="polite">Đã chọn {selectedGroups.length} nhóm</span></div>
        <div className="mb-3 flex flex-col gap-2">
          <Input aria-label="Tìm nhóm" placeholder="Tìm nhóm theo tên…" value={search} onChange={event => setSearch(event.target.value)} />
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" variant="outline" disabled={!groups.length || busy} onClick={() => setSelected(groups.map(group => group.targetId))}>Chọn tất cả</Button>
            <Button size="sm" variant="ghost" disabled={!selected.length || busy} onClick={() => setSelected([])}>Bỏ chọn</Button>
            <div className="ml-auto flex items-center gap-2">
              <label htmlFor="facebook-random-count" className="sr-only">Số nhóm ngẫu nhiên</label>
              <Input id="facebook-random-count" type="number" min={1} max={groups.length} step={1} className="h-8 w-16 px-2" value={randomCount} onChange={event => setRandomCount(event.target.value)} />
              <Button size="sm" variant="outline" onClick={chooseRandom} disabled={!groups.length || busy}><Shuffle className="size-4" aria-hidden="true" />Chọn ngẫu nhiên</Button>
            </div>
          </div>
        </div>
        <p className="mb-3 text-xs text-muted-foreground">Chọn tất cả / ngẫu nhiên áp dụng trên toàn bộ {groups.length} nhóm đã tải, kể cả nhóm bị ẩn bởi tìm kiếm.</p>
        <div className="max-h-[32rem] overflow-y-auto rounded-lg border border-border divide-y divide-border">
          {visibleGroups.map(group => <label key={group.targetId} title={group.name} className={`flex cursor-pointer items-center gap-3 px-3 py-2.5 transition-colors hover:bg-muted/50 ${selected.includes(group.targetId) ? "bg-primary/5" : ""}`}>
            <input type="checkbox" className="size-4 shrink-0 accent-primary focus-visible:outline-2 focus-visible:outline-ring" checked={selected.includes(group.targetId)} disabled={busy} onChange={() => toggleGroup(group.targetId)} />
            <span className="min-w-0 flex-1"><span className="block truncate text-sm font-medium">{group.name}</span><span className="block truncate text-xs text-muted-foreground">{group.url}</span></span>
          </label>)}
          {!visibleGroups.length && <p className="p-6 text-sm text-muted-foreground">{groups.length ? "Không có nhóm khớp từ khóa." : "Chưa có nhóm đã lưu. Bấm Tải lại nhóm từ Facebook để lấy và lưu danh sách đã tham gia."}</p>}
        </div>
      </div>}
      {view === "compose" && <div className="flex min-w-0 flex-col gap-5">
        <div className={`${panel} flex flex-col gap-4`}>
          <label htmlFor="facebook-text" className="text-sm font-medium">Nội dung bài viết</label>
          <Textarea id="facebook-text" className="min-h-52 select-text" placeholder="Viết nội dung hoặc dán đường dẫn…" value={text} disabled={busy} onChange={event => setText(event.target.value)} />
          <p className="text-xs text-muted-foreground">{textLength.toLocaleString("vi-VN")} / 10.000 ký tự · Link được giữ trong nội dung bài.</p>
          <fieldset className="flex flex-col gap-2" disabled={busy}>
            <legend className="mb-1 text-sm font-medium">Cách đăng nội dung</legend>
            <div className="flex flex-wrap gap-x-6 gap-y-2">
              <label className="flex min-h-10 items-center gap-2 text-sm"><input type="radio" name="facebook-content-mode" className="size-4 accent-primary focus-visible:outline-2 focus-visible:outline-ring" checked={rewriteContent} onChange={() => setRewriteContent(true)} />Viết lại nội dung</label>
              <label className="flex min-h-10 items-center gap-2 text-sm"><input type="radio" name="facebook-content-mode" className="size-4 accent-primary focus-visible:outline-2 focus-visible:outline-ring" checked={!rewriteContent} onChange={() => setRewriteContent(false)} />Đăng nguyên bản</label>
            </div>
          </fieldset>
          <p className="text-sm text-muted-foreground">{rewriteContent ? <>AI viết lại riêng cho mỗi nhóm, tối đa 10 bản/lượt, giữ ý chính và độ dài tương tự. AI: {data.rewriteProvider === "chatgpt-web" ? "ChatGPT Web" : data.rewriteProvider === "gemini-web" ? "Gemini Web" : "Chưa chọn — vào Cài đặt → AI & Prompt"}. Chỉ đăng sau khi đủ nội dung hợp lệ. Bài chỉ có ảnh không tạo caption AI.</> : "Giữ nguyên nội dung và ảnh đã nhập cho mọi nhóm. Không dùng AI."}</p>
          <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="text-sm font-medium">Ảnh đính kèm ({imageIds.length}/{MAX_IMAGES})</h3><div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => void loadImages()} disabled={imageBusy || busy}>Chọn ảnh thư viện</Button>
            <Button size="sm" variant="outline" onClick={() => void upload()} disabled={imageBusy || busy || imageIds.length >= MAX_IMAGES}><Upload className="size-4" aria-hidden="true" />{imageBusy ? "Đang tải…" : "Tải ảnh lên"}</Button></div></div>
          {imageIds.length === 0
            ? <button type="button" onClick={() => void upload()} disabled={imageBusy || busy} className="flex flex-col items-center gap-1 rounded-lg border border-dashed border-input px-4 py-6 text-center text-sm text-muted-foreground transition-colors hover:border-primary/50 hover:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50">
                <ImagePlus className="size-6" aria-hidden="true" /><span className="font-medium text-foreground">Chưa có ảnh đính kèm</span><span className="text-xs">Bấm để chọn nhiều ảnh cùng lúc từ máy (tối đa {MAX_IMAGES} ảnh mỗi bài)</span></button>
            : <ul className="grid grid-cols-[repeat(auto-fill,minmax(92px,1fr))] gap-2" aria-label="Ảnh đính kèm đã chọn">{imageIds.map((id, index) => {
                const name = images.find(image => image.assetId === id)?.relativePath.split(/[\\/]/).pop() ?? "Ảnh đính kèm";
                return <li key={id} className="relative aspect-square overflow-hidden rounded-lg border border-border bg-muted">
                  <img src={assetMediaUrl(id)} alt={name} title={name} loading="lazy" className="size-full object-cover" />
                  <span className="absolute bottom-1 left-1 rounded bg-background/85 px-1.5 text-xs font-semibold tabular-nums text-foreground">{index + 1}</span>
                  <button type="button" disabled={busy} aria-label={`Bỏ ảnh ${name}`} onClick={() => toggleImage(id)} className="absolute right-1 top-1 flex size-7 items-center justify-center rounded-full bg-background/90 text-foreground shadow-sm transition-colors hover:bg-destructive hover:text-destructive-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50"><X className="size-4" aria-hidden="true" /></button>
                </li>; })}</ul>}
          {images.length > 0 && <div className="flex flex-col gap-2 rounded-lg border border-border p-3"><p className="text-xs font-medium text-muted-foreground">Thư viện ảnh — bấm để chọn hoặc bỏ chọn</p>
            <ul className="grid max-h-60 grid-cols-[repeat(auto-fill,minmax(72px,1fr))] gap-2 overflow-y-auto" aria-label="Ảnh trong thư viện">{images.map(image => {
              const picked = imageIds.includes(image.assetId); const name = image.relativePath.split(/[\\/]/).pop() ?? "Ảnh";
              const locked = busy || (!picked && imageIds.length >= MAX_IMAGES);
              return <li key={image.assetId}><button type="button" aria-pressed={picked} aria-label={name} title={name} disabled={locked} onClick={() => toggleImage(image.assetId)} className={`relative block aspect-square w-full overflow-hidden rounded-md border-2 bg-muted transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 ${picked ? "border-primary" : "border-transparent hover:border-primary/40"}`}>
                <img src={assetMediaUrl(image.assetId)} alt="" loading="lazy" className="size-full object-cover" />
                {picked && <span className="absolute right-1 top-1 flex size-5 items-center justify-center rounded-full bg-primary text-primary-foreground"><Check className="size-3.5" aria-hidden="true" /></span>}</button></li>; })}</ul>
            {images.length < imageTotal && <Button variant="ghost" size="sm" onClick={() => void loadImages(imagePage + 1)} disabled={imageBusy}>Tải thêm ảnh</Button>}</div>}
          {!images.length && imagePage > 0 && <p className="text-sm text-muted-foreground">Thư viện chưa có ảnh. Bấm Tải ảnh lên để thêm.</p>}
        </div>
        <aside className={`${panel} flex flex-col gap-4 self-start`}><h3 className="font-semibold">Sẵn sàng tạo bài</h3><Input aria-label="Tên bài đăng" placeholder="Tên bài đăng" maxLength={80} value={draftName} onChange={event => setDraftName(event.target.value)} /><p className="text-3xl font-semibold tracking-tight tabular-nums">{selectedGroups.length} <span className="text-sm font-normal text-muted-foreground">nhóm</span></p>
          <p className="text-sm text-muted-foreground">Mỗi nhóm là một tác vụ riêng. Cùng tài khoản đăng cách nhau ít nhất 5 phút, không giới hạn số lượt gửi trong ngày.</p>
          <fieldset className="flex flex-col gap-2" disabled={busy}><legend className="mb-1 text-sm font-medium">Thời điểm đăng</legend>
            <div className="flex flex-wrap items-center gap-x-6 gap-y-2">
              <label className="flex items-center gap-2 text-sm"><input type="radio" name="facebook-mode" className="size-4 accent-primary" checked={mode === "now"} onChange={() => setMode("now")} />Đăng ngay</label>
              <label className="flex items-center gap-2 text-sm"><input type="radio" name="facebook-mode" className="size-4 accent-primary" checked={mode === "scheduled"} onChange={() => setMode("scheduled")} />Hẹn giờ</label>
            </div>
            {mode === "scheduled" && <><label htmlFor="facebook-due" className="sr-only">Giờ hẹn đăng</label>
              <input id="facebook-due" type="datetime-local" className={control} value={dueInput} min={utcToLocalInput(new Date().toISOString())} onChange={event => setDueInput(event.target.value)} aria-invalid={dueUtc === null} />
              {dueUtc === null && <p className="text-xs text-destructive">Chọn giờ ở tương lai (tối đa 365 ngày).</p>}</>}
          </fieldset>
          <p className="text-sm text-muted-foreground">{mode === "scheduled" ? "Hẹn giờ chỉ chạy khi Fraylo và máy đang hoạt động đúng giờ; nếu lỡ giờ, bài không tự đăng muộn mà chờ bạn chọn." : "Giữ Fraylo và máy đang chạy để hàng đợi tiếp tục."}</p><Button onClick={review} disabled={!canCreate}>Xem lại lô đăng</Button>
          {!canCreate && !busy && <p className="-mt-2 text-xs text-muted-foreground">Còn thiếu: {[!connected && "tài khoản đã kết nối", selectedGroups.length === 0 && "ít nhất 1 nhóm", text.trim().length === 0 && imageIds.length === 0 && "nội dung hoặc ảnh", textLength > 10000 && "nội dung ngắn hơn 10.000 ký tự", dueUtc === null && "giờ hẹn hợp lệ"].filter(Boolean).join(", ") || "chờ tác vụ đang chạy"}.</p>}
          <div className="flex flex-col gap-2 border-t border-border pt-4"><h3 className="text-sm font-medium">Nháp</h3>
            <Input aria-label="Tên nháp" placeholder="Tên nháp (tùy chọn)" value={draftName} maxLength={80} disabled={draftBusy} onChange={event => setDraftName(event.target.value)} />
            <Button variant="outline" onClick={() => void saveDraft()} disabled={!accountId || draftBusy || busy}>{draftId ? "Cập nhật nháp" : "Lưu nháp"}</Button>
            {draftId && <Button variant="ghost" size="sm" onClick={() => { setDraftId(null); setDraftName(""); }}>Lưu thành nháp mới</Button>}
            <p className="text-xs text-muted-foreground">Quản lý nháp đã lưu tại mục Nháp trong menu Facebook.</p>
          </div>
        </aside>
      </div>}
    </div>}
    <Dialog open={!!confirmation} onOpenChange={open => { if (!open && !busy) setConfirmation(null); }}>
      <DialogContent className="max-w-2xl" onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onPointerDownOutside={event => { if (busy) event.preventDefault(); }}>
        <DialogHeader><DialogTitle>Xác nhận lô đăng vào {confirmation?.groups.length} nhóm</DialogTitle><DialogDescription>Kiểm tra tài khoản, nội dung và toàn bộ nhóm đích. Sau khi xác nhận, các tác vụ bắt đầu vào hàng đợi đăng.</DialogDescription></DialogHeader>
        {confirmation && <><div className="rounded-lg border border-border bg-muted/40 p-3"><p className="text-sm font-medium">{confirmation.accountName}</p><p className="mt-2 max-h-32 overflow-y-auto whitespace-pre-wrap break-words text-sm select-text">{confirmation.request.text || "Bài chỉ có ảnh"}</p><p className="mt-2 font-medium">{confirmation.request.name || "Bài đăng Facebook"}</p><p className="text-xs text-muted-foreground">Nhóm cuối sớm nhất: {new Date((confirmation.request.dueAtUtc ? Date.parse(confirmation.request.dueAtUtc) : Date.now()) + Math.max(0, confirmation.groups.length - 1) * 300000).toLocaleString("vi-VN")} (có thể trễ do lượt trước, phiên hoặc mạng)</p><p className="mt-2 text-xs text-muted-foreground">{confirmation.request.imageAssetIds.length} ảnh · {confirmation.groups.length} tác vụ · Giãn cách tối thiểu 5 phút</p>
          <p className="mt-1 text-xs font-medium">{confirmation.request.dueAtUtc ? `Hẹn giờ đăng: ${new Date(confirmation.request.dueAtUtc).toLocaleString("vi-VN")}` : "Đăng ngay khi đến lượt"}</p></div>
          <ol className="max-h-60 overflow-y-auto rounded-lg border border-border divide-y divide-border">{confirmation.groups.map(group => <li key={group.targetId} className="p-3 text-sm"><span className="text-muted-foreground">{confirmation.accountName} → </span><span className="font-medium">{group.name}</span><span className="mt-1 block break-all text-xs text-muted-foreground">{group.url}</span></li>)}</ol></>}
        {error && <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
        <p className="text-sm text-muted-foreground">{confirmation?.request.rewriteContent && confirmation.request.text.trim() ? <>AI tạo {confirmation.groups.length} nội dung riêng trong {Math.ceil(confirmation.groups.length / 10)} lượt (tối đa 10 bản/lượt), sau đó lưu thay nội dung từng nhóm trước khi đăng. Provider: {data.rewriteProvider || "Chưa chọn"}.</> : "Đăng nguyên bản: giữ nguyên nội dung và ảnh đã nhập cho mọi nhóm, không dùng AI."}</p>
        <DialogFooter><Button variant="outline" disabled={busy} onClick={() => setConfirmation(null)}>Quay lại chỉnh sửa</Button><Button onClick={() => void create()} disabled={busy}>{busy ? "Đang tạo lô…" : `Xác nhận tạo ${confirmation?.groups.length ?? 0} tác vụ`}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </section>;
}
