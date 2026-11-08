import { useCallback, useEffect, useRef, useState } from "react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { listAssets, uploadAsset, type AssetItem } from "../library/libraryApi";
import { AssetMediaPreview } from "../library/AssetMediaPreview";
import { facebookApi, facebookError, localInputToUtc, utcToLocalInput, type FacebookApi, type FacebookBatchRequest, type FacebookDraft, type FacebookList, type FacebookPost } from "./facebookApi";

const control = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";
const initial: FacebookList = { accounts: [], groups: [], tasks: [], posts: [], drafts: [] };
export function FacebookPersonalPage({ active, onOpenAccounts, onOpenQueue, openedDraft, clonePost, api = facebookApi }: { active: boolean; onOpenAccounts: () => void; onOpenQueue?: (history?: boolean) => void; openedDraft?: FacebookDraft | null; clonePost?: FacebookPost | null; api?: FacebookApi }) {
  const [data, setData] = useState(initial);
  const [accountId, setAccountId] = useState("");
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  const [images, setImages] = useState<string[]>([]);
  const [library, setLibrary] = useState<AssetItem[]>([]);
  const [imagePage, setImagePage] = useState(0);
  const [imageTotal, setImageTotal] = useState(0);
  const [previewImage, setPreviewImage] = useState<string | null>(null);
  const [due, setDue] = useState("");
  const [scheduled, setScheduled] = useState(false);
  const [draftId, setDraftId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<FacebookBatchRequest | null>(null);
  const request = useRef<{ signature: string; id: string } | null>(null);
  const lock = useRef(false);
  const mounted = useRef(true);
  const generation = useRef(0);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; generation.current++; }; }, []);
  const load = useCallback(async () => {
    const version = ++generation.current;
    try {
      const next = await api.list();
      if (!mounted.current || version !== generation.current) return;
      setDraftId(current => current && next.drafts && !next.drafts.some(d => d.draftId === current) ? undefined : current);
      const personal = (id: string) => id.startsWith("personal:");
      setData({ ...next, groups: [], tasks: next.tasks.filter(t => personal(t.targetId)),
        posts: next.posts?.filter(p => p.tasks.some(t => personal(t.targetId))),
        drafts: next.drafts?.filter(d => d.targetIds.some(personal)) });
    } catch (err) { if (mounted.current && version === generation.current) setError(facebookError(err)); }
  }, [api]);
  useEffect(() => { if (!active) return; void load(); const timer = setInterval(() => void load(), 5000); return () => clearInterval(timer); }, [active, load]);
  const account = data.accounts.find(a => a.accountId === accountId);
  const imageName = (id: string) => library.find(a => a.assetId === id)?.relativePath.split(/[\\/]/).pop() || "Ảnh đính kèm";
  const connected = account?.sessionStatus === "Connected" && !!account.accountIdentity && account.enabled !== false;
  let dueAtUtc: string | undefined;
  let validDue = !scheduled;
  if (scheduled) { try { dueAtUtc = localInputToUtc(due); const ms = Date.parse(dueAtUtc); validDue = ms > Date.now() && ms <= Date.now() + 365 * 86400000; } catch { /* Chưa nhập giờ hợp lệ. */ } }
  const valid = connected && (text.trim().length > 0 || images.length > 0) && Array.from(text).length <= 10000 && validDue && !busy && !loading;
  const clear = () => { setText(""); setName(""); setImages([]); setDue(""); setScheduled(false); setDraftId(undefined); request.current = null; };
  const payload = () => ({ accountId, targetIds: ["personal:" + accountId], text, imageAssetIds: [...images], name: name.trim() || undefined, dueAtUtc: scheduled ? dueAtUtc : undefined, targetKind: "Profile" as const });
  const review = () => {
    if (!valid) return;
    const content = payload(); const signature = JSON.stringify(content);
    if (request.current?.signature !== signature) request.current = { signature, id: crypto.randomUUID() };
    setConfirmation({ ...content, batchRequestId: request.current.id }); setError(null);
  };
  const create = async () => {
    if (!confirmation || lock.current) return;
    lock.current = true; setBusy(true); setError(null);
    try { await api.create(confirmation); setConfirmation(null); clear(); setMessage("Đã tạo bài đăng cá nhân. Theo dõi kết quả trong Hàng đợi."); onOpenQueue?.(); await load(); }
    catch (err) { setError(facebookError(err) + " Kiểm tra hàng đợi trước khi tạo lại. Xác nhận lại sẽ giữ nguyên mã yêu cầu."); }
    finally { lock.current = false; setBusy(false); }
  };
  const save = async () => {
    if (!accountId || busy || loading || !validDue || Array.from(text).length > 10000) return;
    setBusy(true); setError(null);
    try { const saved = await api.saveDraft({ ...payload(), draftId, name: name.trim() || "Nháp cá nhân" }); setDraftId(saved.draftId); setMessage("Đã lưu nháp cá nhân."); await load(); }
    catch (err) { setError(facebookError(err)); } finally { setBusy(false); }
  };
  const openDraft = (draft: FacebookDraft) => { setAccountId(draft.accountId); setName(draft.name); setText(draft.text); setImages(draft.imageAssetIds); setDue(draft.dueAtUtc ? utcToLocalInput(draft.dueAtUtc) : ""); setScheduled(!!draft.dueAtUtc); setDraftId(draft.draftId); request.current = null; };
  useEffect(() => {
    if (!clonePost) return;
    const task = clonePost.tasks[0]; if (!task) return;
    setAccountId(clonePost.accountId); setName((clonePost.name + " (bản sao)").slice(0, 80));
    setText(task.text); setImages(task.imageAssetIds ?? []); setScheduled(false); setDue("");
    setDraftId(undefined); setConfirmation(null); request.current = null;
  }, [clonePost]);
  useEffect(() => { if (openedDraft) openDraft(openedDraft); }, [openedDraft]);
  const loadImages = async (upload: boolean, page = 1) => {
    if (loading || busy) return; setLoading(true); setError(null);
    try {
      if (upload) { const result = await uploadAsset({ assetType: "Image", multiple: true }); const added = result.assets ?? (result.asset ? [result.asset] : []); if (result.errors?.length) setError(result.errors.join("; ")); setImages(current => [...new Set([...current, ...added.map(a => a.assetId)])].slice(0, 10)); }
      const result = await listAssets({ assetType: "Image", page, pageSize: 50 }); setLibrary(current => page === 1 ? result.assets : [...current, ...result.assets.filter(a => !current.some(old => old.assetId === a.assetId))]); setImagePage(page); setImageTotal(result.totalCount);
    } catch (err) { setError(facebookError(err)); } finally { setLoading(false); }
  };
  return <section className="mx-auto flex max-w-6xl flex-col gap-6">
    <PageHeader title="Đăng bài cá nhân" description="Đăng chữ, liên kết và tối đa 10 ảnh lên trang cá nhân của tài khoản đã chọn."
      actions={<Button variant="outline" onClick={() => void load()}>Cập nhật trạng thái</Button>} />
    {error && <p role="alert" className="rounded-lg border border-destructive/40 p-3 text-sm text-destructive">{error}</p>}
    {message && <p role="status" className="text-sm text-muted-foreground">{message}</p>}
    <div className="flex flex-col gap-4 rounded-xl border border-border bg-card p-5">
      <div className="flex flex-wrap items-end gap-3"><div className="min-w-48 flex-1"><label htmlFor="personal-account" className="mb-2 block text-sm font-medium">Tài khoản Facebook</label>
        <select id="personal-account" className={control} value={accountId} disabled={busy} onChange={e => { setAccountId(e.target.value); setDraftId(undefined); setConfirmation(null); request.current = null; }}><option value="">Chọn tài khoản</option>{data.accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.displayName} — {a.enabled === false ? "Đã tắt" : a.sessionStatus === "Connected" ? "Đã kết nối" : "Cần đăng nhập"}</option>)}</select></div><Button variant="outline" onClick={onOpenAccounts}>Cài đặt tài khoản</Button></div>
      {!connected && <p className="text-sm text-muted-foreground">Chọn tài khoản đã bật và xác minh đăng nhập trong Cài đặt để đăng bài.</p>}
      <p className="text-sm text-muted-foreground">Nơi đăng: {account ? `Trang cá nhân · ${account.displayName}` : "Chưa chọn tài khoản"}. Giữ đối tượng xem đang đặt trong Facebook; ứng dụng không tự đổi quyền riêng tư.</p>
      <Input aria-label="Tên bài cá nhân" placeholder="Tên bài (tùy chọn)" maxLength={80} value={name} disabled={busy} onChange={e => setName(e.target.value)} />
      <label htmlFor="personal-text" className="text-sm font-medium">Nội dung bài viết</label><Textarea id="personal-text" className="min-h-52 select-text" value={text} disabled={busy} onChange={e => setText(e.target.value)} />
      <p className="text-xs text-muted-foreground">{Array.from(text).length} / 10.000 ký tự · {images.length} / 10 ảnh</p>
      <div className="flex flex-wrap gap-2"><Button variant="outline" disabled={loading || busy} onClick={() => void loadImages(false)}>Chọn ảnh thư viện</Button><Button variant="outline" disabled={loading || busy || images.length >= 10} onClick={() => void loadImages(true)}>Tải ảnh lên</Button></div>
      {images.length > 0 && <ul aria-label="Ảnh đính kèm đã chọn" className="grid grid-cols-[repeat(auto-fill,minmax(140px,1fr))] gap-3">
        {images.map((id, index) => <li key={id} className="min-w-0 rounded-lg border border-border bg-card p-2">
          <button type="button" aria-label={`Xem ảnh ${index + 1}`} onClick={() => setPreviewImage(id)} className="block w-full rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
            <AssetMediaPreview assetId={id} assetType="Image" fileName={imageName(id)} heightClass="h-32" />
          </button>
          <div className="mt-2 flex items-center justify-between gap-2"><span className="text-xs text-muted-foreground">Ảnh {index + 1}</span><Button size="sm" variant="ghost" className="text-foreground" aria-label={`Bỏ ảnh ${index + 1}`} disabled={busy} onClick={() => setImages(current => current.filter(item => item !== id))}>Bỏ ảnh</Button></div>
        </li>)}
      </ul>}
      {library.length > 0 && <ul aria-label="Ảnh trong thư viện" className="grid max-h-72 grid-cols-[repeat(auto-fill,minmax(140px,1fr))] gap-3 overflow-y-auto rounded-lg border border-border p-3">
        {library.map(image => <li key={image.assetId} className="min-w-0"><label className={`flex cursor-pointer flex-col gap-2 rounded-lg border p-2 text-sm focus-within:ring-2 focus-within:ring-ring ${images.includes(image.assetId) ? "border-primary bg-primary/10" : "border-border bg-card"}`}>
          <AssetMediaPreview assetId={image.assetId} assetType="Image" fileName={imageName(image.assetId)} heightClass="h-24" />
          <span className="flex min-w-0 items-center gap-2"><input type="checkbox" checked={images.includes(image.assetId)} disabled={busy || (!images.includes(image.assetId) && images.length >= 10)} onChange={() => setImages(current => current.includes(image.assetId) ? current.filter(id => id !== image.assetId) : [...current, image.assetId])} /><span className="truncate" title={imageName(image.assetId)}>{imageName(image.assetId)}</span></span>
        </label></li>)}
      </ul>}
      {library.length < imageTotal && <Button variant="ghost" disabled={loading || busy} onClick={() => void loadImages(false, imagePage + 1)}>Tải thêm ảnh</Button>}
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={scheduled} disabled={busy} onChange={e => setScheduled(e.target.checked)} />Hẹn giờ đăng</label>
      {scheduled && <><label htmlFor="personal-due" className="text-sm">Giờ đăng (giờ máy)</label><input id="personal-due" className={control} type="datetime-local" value={due} disabled={busy} aria-invalid={!validDue} onChange={e => setDue(e.target.value)} />{!validDue && <p className="text-sm text-destructive">Chọn giờ tương lai, tối đa 365 ngày.</p>}</>}
      <p className="text-xs text-muted-foreground">Giữ máy và Fraylo chạy đúng giờ. Bài lỡ lịch chờ bạn xử lý; các bài cá nhân và nhóm cùng tài khoản dùng chung giãn cách ít nhất 5 phút.</p>
      <div className="flex flex-wrap gap-2"><Button disabled={!valid} onClick={review}>Xem lại bài đăng</Button><Button variant="outline" disabled={!accountId || busy || loading || !validDue} onClick={() => void save()}>Lưu nháp</Button></div>
    </div>
    <Dialog open={!!confirmation} onOpenChange={open => { if (!open && !busy) setConfirmation(null); }}><DialogContent><DialogHeader><DialogTitle>Xác nhận đăng lên trang cá nhân</DialogTitle><DialogDescription>{account?.displayName} · {confirmation?.dueAtUtc ? new Date(confirmation.dueAtUtc).toLocaleString("vi-VN") : "Đăng ngay khi đến lượt"}. Dùng đối tượng xem hiện tại trong Facebook.</DialogDescription></DialogHeader><p className="max-h-52 overflow-auto whitespace-pre-wrap break-words text-sm">{confirmation?.text}</p><p className="text-sm">{confirmation?.imageAssetIds.length} ảnh đính kèm</p>
      {!!confirmation?.imageAssetIds.length && <ul aria-label="Ảnh trong bài đăng" className="grid max-h-60 grid-cols-3 gap-2 overflow-y-auto">{confirmation.imageAssetIds.map((id, index) => <li key={id}><button type="button" aria-label={`Xem ảnh ${index + 1}`} onClick={() => setPreviewImage(id)} className="block w-full rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"><AssetMediaPreview assetId={id} assetType="Image" fileName={imageName(id)} heightClass="h-24" /></button></li>)}</ul>}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}<DialogFooter><Button variant="outline" disabled={busy} onClick={() => setConfirmation(null)}>Quay lại chỉnh sửa</Button><Button disabled={busy} onClick={() => void create()}>{busy ? "Đang tạo…" : "Xác nhận tạo bài đăng"}</Button></DialogFooter></DialogContent></Dialog>
    <Dialog open={!!previewImage} onOpenChange={open => { if (!open) setPreviewImage(null); }}><DialogContent className="sm:max-w-3xl"><DialogHeader><DialogTitle>Xem trước ảnh</DialogTitle><DialogDescription>{previewImage ? imageName(previewImage) : "Ảnh đính kèm"}</DialogDescription></DialogHeader>
      {previewImage && <AssetMediaPreview key={previewImage} assetId={previewImage} assetType="Image" fileName={imageName(previewImage)} heightClass="h-[60vh]" />}
      <DialogFooter><Button variant="outline" onClick={() => setPreviewImage(null)}>Đóng ảnh</Button></DialogFooter>
    </DialogContent></Dialog>
  </section>;
}
