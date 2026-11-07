import { useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { facebookError, type FacebookApi, type FacebookDraft, type FacebookList } from "./facebookApi";

export function FacebookDrafts({ data, api, onOpen, onRefresh, onError }: {
  data: FacebookList;
  api: FacebookApi;
  onOpen: (draft: FacebookDraft) => void;
  onRefresh: () => Promise<void>;
  onError: (error: string | null) => void;
}) {
  const [target, setTarget] = useState("all");
  const [account, setAccount] = useState("all");
  const [search, setSearch] = useState("");
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);
  const lock = useRef(false);
  const drafts = (data.drafts ?? []).filter(draft =>
    (target === "all" || draft.targetIds.some(id => id.startsWith("personal:")) === (target === "personal")) &&
    (account === "all" || draft.accountId === account) &&
    `${draft.name} ${draft.text}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const remove = async (draft: FacebookDraft) => {
    if (lock.current) return;
    lock.current = true; setDeleting(true); onError(null);
    try { await api.deleteDraft(draft.draftId); setConfirmId(null); await onRefresh(); }
    catch (err) { onError(facebookError(err)); }
    finally { lock.current = false; setDeleting(false); }
  };
  const control = "h-10 rounded-md border border-input bg-background px-3 text-sm";
  return <div className="flex flex-col gap-3" aria-label="Nháp Facebook chung">
    <div className="flex flex-wrap gap-2">
      <Input aria-label="Tìm nháp Facebook" className="min-w-48 flex-1" placeholder="Tìm tên hoặc nội dung nháp…" value={search} onChange={e => setSearch(e.target.value)} />
      <select aria-label="Lọc nơi đăng nháp" className={control} value={target} onChange={e => setTarget(e.target.value)}><option value="all">Tất cả nơi đăng</option><option value="group">Nhóm</option><option value="personal">Cá nhân</option></select>
      <select aria-label="Lọc tài khoản nháp" className={control} value={account} onChange={e => setAccount(e.target.value)}><option value="all">Tất cả tài khoản</option>{data.accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.displayName}</option>)}</select>
    </div>
    {drafts.map(draft => <article key={draft.draftId} className="rounded-xl border border-border bg-card p-5">
      <h3 className="font-semibold">{draft.name}</h3>
      <p className="text-sm text-muted-foreground">{data.accounts.find(a => a.accountId === draft.accountId)?.displayName ?? "Tài khoản đã lưu"} · {draft.targetIds.some(id => id.startsWith("personal:")) ? "Trang cá nhân" : `${draft.targetIds.length} nhóm`}</p>
      <p className="mt-2 line-clamp-3 whitespace-pre-wrap break-words text-sm">{draft.text}</p>
      {draft.dueAtUtc && <p className="mt-2 text-sm text-muted-foreground">Giờ đã lưu: {new Date(draft.dueAtUtc).toLocaleString("vi-VN")}</p>}
      <div className="mt-3 flex flex-wrap gap-2">
        <Button variant="outline" disabled={deleting} onClick={() => onOpen(draft)}>Mở nháp</Button>
        <Button variant={confirmId === draft.draftId ? "destructive" : "outline"} disabled={deleting} onClick={() => confirmId === draft.draftId ? void remove(draft) : setConfirmId(draft.draftId)}>{confirmId === draft.draftId ? "Xác nhận xóa nháp" : "Xóa nháp"}</Button>
        {confirmId === draft.draftId && <Button variant="ghost" disabled={deleting} onClick={() => setConfirmId(null)}>Giữ nháp</Button>}
      </div>
    </article>)}
    {!drafts.length && <p className="rounded-xl border border-border p-8 text-center text-muted-foreground">{data.drafts?.length ? "Không có nháp khớp bộ lọc." : "Chưa có nháp. Lưu nháp từ màn Đăng bài nhóm hoặc Đăng bài cá nhân để tiếp tục sau."}</p>}
  </div>;
}
