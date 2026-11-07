import { useEffect, useMemo, useState } from "react";
import { CalendarClock, Check, CircleAlert, Copy, ImagePlus, Plus, Shuffle, Trash2, Upload, Users, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { listAssets, uploadAsset, type AssetItem } from "../library/libraryApi";
import { assetMediaUrl } from "../library/AssetMediaPreview";
import { facebookError, localInputToUtc, randomGroupIds, utcToLocalInput, type FacebookApi, type FacebookGroup, type FacebookList } from "./facebookApi";

const MAX_POSTS = 30;
const MAX_IMAGES = 10;
const MAX_TEXT = 10000;
// Backend giãn cách mỗi lượt 5 phút + 0–30 giây ngẫu nhiên; dự kiến dùng trung bình 5 phút 15 giây.
export const GAP_MS = 315_000;
const YEAR_MS = 365 * 24 * 3_600_000;
const control = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";
const panel = "rounded-xl border border-border bg-card p-5";
const INTERVALS = [[30, "30 phút"], [60, "1 giờ"], [120, "2 giờ"], [180, "3 giờ"], [360, "6 giờ"], [720, "12 giờ"], [1440, "1 ngày"]] as const;

export interface Row { id: string; name: string; text: string; rewriteContent: boolean; targetIds: string[]; imageIds: string[]; due: string; state: "idle" | "creating" | "done" | "error"; error?: string }
export const newRow = (due = ""): Row => ({ id: crypto.randomUUID(), name: "", text: "", rewriteContent: true, targetIds: [], imageIds: [], due, state: "idle" });

/** Giờ hẹn hợp lệ (tương lai, tối đa 365 ngày) → mili giây; ngược lại NaN. */
function dueMs(due: string): number {
  try {
    const ms = Date.parse(localInputToUtc(due));
    return ms > Date.now() && ms <= Date.now() + YEAR_MS ? ms : NaN;
  } catch { return NaN; }
}
const two = (value: number) => String(value).padStart(2, "0");
const fmt = (ms: number) => { const d = new Date(ms); return `${two(d.getHours())}:${two(d.getMinutes())} ${two(d.getDate())}/${two(d.getMonth() + 1)}`; };
const missing = (row: Row) => [
  row.text.trim().length === 0 && row.imageIds.length === 0 && "nội dung hoặc ảnh",
  Array.from(row.text).length > MAX_TEXT && "nội dung ngắn hơn 10.000 ký tự",
  row.targetIds.length === 0 && "nhóm đăng",
  Number.isNaN(dueMs(row.due)) && "giờ hẹn trong tương lai",
].filter(Boolean) as string[];

/** Cùng tài khoản gửi cách nhau 5 phút xuyên các bài: bài đến sau chờ bài trước, giống cách backend xếp lượt. */
export function simulate(rows: Row[]): Map<string, { start: number; end: number; delayed: boolean }> {
  const out = new Map<string, { start: number; end: number; delayed: boolean }>();
  const order = rows.map((row, index) => ({ row, index, start: dueMs(row.due) })).filter(item => !Number.isNaN(item.start) && item.row.targetIds.length > 0)
    .sort((a, b) => a.start - b.start || a.index - b.index);
  let cursor = -Infinity;
  for (const { row, start } of order) {
    const actual = Math.max(start, cursor + GAP_MS);
    const end = actual + (row.targetIds.length - 1) * GAP_MS;
    out.set(row.id, { start: actual, end, delayed: actual > start });
    cursor = end;
  }
  return out;
}

interface Props { data: FacebookList; api: FacebookApi; loading: boolean; onRefresh: () => Promise<void>; onCreated: (count: number) => void; onOpenAccounts: () => void }

export function FacebookBulkSchedule({ data, api, loading, onRefresh, onCreated, onOpenAccounts }: Props) {
  const [accountId, setAccountId] = useState("");
  const [rows, setRows] = useState<Row[]>(() => [newRow()]);
  const [quickStart, setQuickStart] = useState("");
  const [quickGap, setQuickGap] = useState<number>(60);
  const [groupRow, setGroupRow] = useState<string | null>(null);
  const [imageRow, setImageRow] = useState<string | null>(null);
  const [reviewing, setReviewing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const account = data.accounts.find(item => item.accountId === accountId);
  const connected = account?.sessionStatus === "Connected" && !!account.accountIdentity && account.enabled !== false;
  const groups = useMemo(() => data.groups.filter(group => group.accountId === accountId), [data.groups, accountId]);
  const open = rows.filter(row => row.state !== "done");
  const plan = useMemo(() => simulate(rows), [rows]);
  const ready = open.length > 0 && open.every(row => missing(row).length === 0);
  const canReview = connected && ready && !busy;
  const totalGroups = open.reduce((sum, row) => sum + row.targetIds.length, 0);
  const lastEnd = Math.max(...open.map(row => plan.get(row.id)?.end ?? -Infinity));
  const firstStart = Math.min(...open.map(row => dueMs(row.due)));

  // Sửa nội dung sau lỗi: cấp mã mới để không gửi lại cùng mã lô với nội dung khác.
  const patch = (id: string, change: Partial<Row>) => setRows(current => current.map(row => row.id !== id ? row
    : { ...row, ...change, ...(row.state === "error" ? { state: "idle" as const, error: undefined, id: crypto.randomUUID() } : {}) }));
  const addRow = (source?: Row) => setRows(current => current.length >= MAX_POSTS ? current : [...current, source ? { ...newRow(source.due), name: source.name ? `${source.name} (bản sao)`.slice(0, 80) : "", text: source.text, rewriteContent: source.rewriteContent, targetIds: [...source.targetIds], imageIds: [...source.imageIds] } : newRow()]);
  const removeRow = (id: string) => setRows(current => current.length === 1 ? [newRow()] : current.filter(row => row.id !== id));
  const changeAccount = (value: string) => { setAccountId(value); setRows(current => current.map(row => row.state === "done" ? row : { ...row, targetIds: [] })); setError(null); };

  const applyQuickTimes = () => {
    const first = dueMs(quickStart);
    if (Number.isNaN(first)) { setError("Chọn giờ bắt đầu trong tương lai trước khi giãn đều giờ."); return; }
    setError(null);
    let index = 0;
    setRows(current => current.map(row => row.state === "done" ? row : { ...row, due: utcToLocalInput(new Date(first + index++ * quickGap * 60_000).toISOString()) }));
  };

  const submit = async () => {
    if (!account || busy) return;
    setBusy(true); setError(null);
    let failed = 0, created = 0;
    const queue = [...open].sort((a, b) => dueMs(a.due) - dueMs(b.due));
    for (const row of queue) {
      setRows(current => current.map(item => item.id === row.id ? { ...item, state: "creating" } : item));
      try {
        await api.create({ batchRequestId: row.id, accountId, targetIds: row.targetIds, text: row.text, rewriteContent: row.rewriteContent, imageAssetIds: row.imageIds, dueAtUtc: new Date(dueMs(row.due)).toISOString(), name: row.name.trim() || undefined });
        created++;
        setRows(current => current.map(item => item.id === row.id ? { ...item, state: "done" } : item));
      } catch (err) {
        failed++;
        setRows(current => current.map(item => item.id === row.id ? { ...item, state: "error", error: facebookError(err) } : item));
      }
    }
    setBusy(false);
    await onRefresh();
    if (failed === 0) { setReviewing(false); setRows([newRow()]); onCreated(created); }
    else setError(`Đã lên lịch ${created} bài, ${failed} bài lỗi. Mở tab Bài đăng để kiểm tra trước khi tạo lại bài lỗi, tránh đăng trùng.`);
  };

  return <div className="flex flex-col gap-5">
    <div className={`${panel} grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)] xl:gap-y-2`}>
      <div className="grid min-w-0 gap-2 xl:row-span-3 xl:grid-rows-subgrid">
        <label htmlFor="bulk-account" className="block text-sm font-medium">Tài khoản Facebook</label>
        <select id="bulk-account" className={`${control} self-end`} value={accountId} onChange={event => changeAccount(event.target.value)} disabled={busy}>
          <option value="">Chọn tài khoản</option>
          {data.accounts.map(item => <option key={item.accountId} value={item.accountId}>{item.displayName} — {item.enabled === false ? "Đã tắt" : item.sessionStatus === "Connected" ? "Đã kết nối" : "Cần đăng nhập"}</option>)}
        </select>
        <div className="space-y-2">
          {!loading && data.accounts.length === 0 && <p className="text-sm text-muted-foreground">Chưa có tài khoản Facebook. <button type="button" className="underline underline-offset-4" onClick={onOpenAccounts}>Thêm trong Cài đặt</button> để bắt đầu.</p>}
          {account && !connected && <p className="text-sm text-muted-foreground">Bật tài khoản, đăng nhập và xác minh trong <button type="button" className="underline underline-offset-4" onClick={onOpenAccounts}>Cài đặt</button> trước khi lên lịch.</p>}
          <p className="text-xs text-muted-foreground">Mọi bài trong danh sách dùng chung một tài khoản. Tài khoản gửi lần lượt từng nhóm, cách nhau 5 phút cộng ngẫu nhiên 0–30 giây, kể cả giữa các bài.</p>
        </div>
      </div>
      <div className="grid min-w-0 gap-2 xl:row-span-3 xl:grid-rows-subgrid">
        <h3 id="bulk-quick-title" className="flex items-center gap-2 text-sm font-medium"><CalendarClock className="size-4 text-primary-text" aria-hidden="true" />Giãn đều giờ cho cả danh sách</h3>
        <fieldset className="min-w-0" aria-labelledby="bulk-quick-title" disabled={busy}>
          <div className="flex flex-wrap items-end gap-3">
            <div className="min-w-48 flex-1"><label htmlFor="bulk-quick-start" className="mb-1 block text-xs text-muted-foreground">Bài đầu tiên bắt đầu lúc</label>
              <input id="bulk-quick-start" type="datetime-local" className={control} value={quickStart} min={utcToLocalInput(new Date().toISOString())} onChange={event => setQuickStart(event.target.value)} /></div>
            <div><label htmlFor="bulk-quick-gap" className="mb-1 block text-xs text-muted-foreground">Mỗi bài cách nhau</label>
              <select id="bulk-quick-gap" className={`${control} w-32`} value={quickGap} onChange={event => setQuickGap(Number(event.target.value))}>{INTERVALS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
            <Button variant="outline" className="h-10" onClick={applyQuickTimes} disabled={!quickStart || open.length === 0}>Áp dụng cho {open.length} bài</Button>
          </div>
        </fieldset>
        <p className="text-xs text-muted-foreground">Chỉ điền sẵn giờ theo thứ tự; bạn vẫn sửa giờ từng bài bên dưới.</p>
      </div>
    </div>

    {error && <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}

    <ol className="flex flex-col gap-4" aria-label="Danh sách bài hẹn giờ">
      {rows.map((row, index) => {
        const locked = busy || row.state === "done";
        const gaps = missing(row);
        const slot = plan.get(row.id);
        return <li key={row.id} className={`${panel} flex flex-col gap-4 ${row.state === "done" ? "opacity-70" : ""} ${row.state === "error" ? "border-destructive/50" : ""}`}>
          <div className="flex items-center gap-3">
            <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary/10 text-sm font-semibold tabular-nums text-primary-text">{index + 1}</span>
            <Input aria-label={`Tên bài ${index + 1}`} placeholder="Tên bài (tùy chọn)" maxLength={80} value={row.name} disabled={locked} onChange={event => patch(row.id, { name: event.target.value })} />
            {row.state === "done" && <span className="inline-flex shrink-0 items-center gap-1 rounded-full border border-success/30 bg-success/10 px-3 py-1 text-xs font-medium text-success"><Check className="size-3.5" aria-hidden="true" />Đã lên lịch</span>}
            <Button size="icon" variant="ghost" aria-label={`Nhân bản bài ${index + 1}`} title="Nhân bản bài này" disabled={busy || rows.length >= MAX_POSTS} onClick={() => addRow(row)}><Copy className="size-4" aria-hidden="true" /></Button>
            <Button size="icon" variant="ghost" aria-label={`Xóa bài ${index + 1}`} title="Xóa bài này" disabled={busy} onClick={() => removeRow(row.id)}><Trash2 className="size-4" aria-hidden="true" /></Button>
          </div>
          <div className="grid gap-4 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)]">
            <div className="min-w-0">
              <label htmlFor={`bulk-text-${row.id}`} className="mb-1 block text-sm font-medium">Nội dung</label>
              <Textarea id={`bulk-text-${row.id}`} className="min-h-32 select-text" placeholder="Viết nội dung hoặc dán đường dẫn…" value={row.text} disabled={locked} onChange={event => patch(row.id, { text: event.target.value })} />
              <p className="mt-1 text-xs text-muted-foreground">{Array.from(row.text).length.toLocaleString("vi-VN")} / 10.000 ký tự</p>
              <fieldset className="mt-3" disabled={locked}>
                <legend className="text-sm font-medium">Cách đăng nội dung bài {index + 1}</legend>
                <div className="flex flex-wrap gap-x-6 gap-y-2">
                  <label className="flex min-h-10 items-center gap-2 text-sm"><input type="radio" name={`bulk-content-mode-${row.id}`} className="size-4 accent-primary focus-visible:outline-2 focus-visible:outline-ring" checked={row.rewriteContent} onChange={() => patch(row.id, { rewriteContent: true })} />Viết lại nội dung</label>
                  <label className="flex min-h-10 items-center gap-2 text-sm"><input type="radio" name={`bulk-content-mode-${row.id}`} className="size-4 accent-primary focus-visible:outline-2 focus-visible:outline-ring" checked={!row.rewriteContent} onChange={() => patch(row.id, { rewriteContent: false })} />Đăng nguyên bản</label>
                </div>
              </fieldset>
              <p className="text-xs text-muted-foreground">{row.rewriteContent ? "AI viết lại riêng cho mỗi nhóm, tối đa 10 bản/lượt. Bài chỉ có ảnh không tạo caption AI." : "Giữ nguyên nội dung và ảnh cho mọi nhóm, không dùng AI."}</p>
            </div>
            <div className="flex min-w-0 flex-col gap-3">
              <div><label htmlFor={`bulk-due-${row.id}`} className="mb-1 block text-sm font-medium">Giờ bắt đầu</label>
                <input id={`bulk-due-${row.id}`} type="datetime-local" className={control} value={row.due} disabled={locked} min={utcToLocalInput(new Date().toISOString())} aria-invalid={!!row.due && Number.isNaN(dueMs(row.due))} onChange={event => patch(row.id, { due: event.target.value })} />
                {!!row.due && Number.isNaN(dueMs(row.due)) && <p className="mt-1 text-xs text-destructive">Chọn giờ ở tương lai (tối đa 365 ngày).</p>}</div>
              <Button variant="outline" className="justify-start" disabled={locked || !account} onClick={() => setGroupRow(row.id)}><Users className="size-4" aria-hidden="true" />{row.targetIds.length ? `${row.targetIds.length} nhóm — sửa` : account ? "Chọn nhóm đăng" : "Chọn tài khoản trước"}</Button>
              <div className="flex items-center gap-2">
                <Button variant="outline" className="justify-start" disabled={locked} onClick={() => setImageRow(row.id)}><ImagePlus className="size-4" aria-hidden="true" />{row.imageIds.length ? `${row.imageIds.length}/${MAX_IMAGES} ảnh — sửa` : "Thêm ảnh"}</Button>
                <ul className="flex min-w-0 -space-x-2" aria-label="Ảnh đính kèm">{row.imageIds.slice(0, 4).map(id => <li key={id}><img src={assetMediaUrl(id)} alt="" className="size-9 rounded-md border-2 border-card object-cover" /></li>)}
                  {row.imageIds.length > 4 && <li className="flex size-9 items-center justify-center rounded-md border-2 border-card bg-muted text-xs font-medium">+{row.imageIds.length - 4}</li>}</ul>
              </div>
            </div>
          </div>
          <div className="border-t border-border pt-3 text-xs" aria-live="polite">
            {row.state === "error" ? <p className="flex items-start gap-1.5 text-destructive"><CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />{row.error}</p>
              : row.state === "done" ? <p className="text-muted-foreground">Đã gửi vào hàng đợi. Theo dõi tại tab Bài đăng.</p>
              : gaps.length > 0 ? <p className="text-muted-foreground">Còn thiếu: {gaps.join(", ")}.</p>
              : slot && <p className="text-muted-foreground">Dự kiến gửi nhóm đầu lúc <b className="text-foreground">{fmt(slot.start)}</b>, nhóm cuối khoảng <b className="text-foreground">{fmt(slot.end)}</b> ({row.targetIds.length} nhóm × ~5 phút).
                {slot.delayed && <span className="ml-1 text-warning"> Phải chờ bài trước của cùng tài khoản nên bắt đầu muộn hơn giờ đã chọn.</span>}</p>}
          </div>
        </li>;
      })}
    </ol>

    <div className="sticky bottom-3 z-10 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border bg-card/95 p-4 shadow-lg backdrop-blur">
      <div className="min-w-0 text-sm">
        <p className="font-medium"><span className="tabular-nums">{open.length}</span> bài · <span className="tabular-nums">{totalGroups}</span> lượt đăng nhóm</p>
        <p className="text-xs text-muted-foreground">{ready && Number.isFinite(lastEnd) ? `Từ ${fmt(firstStart)} đến khoảng ${fmt(lastEnd)}` : "Điền đủ nội dung, nhóm và giờ cho từng bài để xem lịch."}</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" onClick={() => addRow()} disabled={busy || rows.length >= MAX_POSTS}><Plus className="size-4" aria-hidden="true" />Thêm bài{rows.length >= MAX_POSTS ? ` (tối đa ${MAX_POSTS})` : ""}</Button>
        <Button onClick={() => { setError(null); setReviewing(true); }} disabled={!canReview}>Xem lại và lên lịch {open.length} bài</Button>
      </div>
    </div>

    <GroupPicker open={groupRow !== null} groups={groups} initial={rows.find(row => row.id === groupRow)?.targetIds ?? []} onClose={() => setGroupRow(null)} onSave={ids => { if (groupRow) patch(groupRow, { targetIds: ids }); setGroupRow(null); }} />
    <ImagePicker key={imageRow ?? "closed"} open={imageRow !== null} initial={rows.find(row => row.id === imageRow)?.imageIds ?? []} onClose={() => setImageRow(null)} onSave={ids => { if (imageRow) patch(imageRow, { imageIds: ids }); setImageRow(null); }} />

    <Dialog open={reviewing} onOpenChange={value => { if (!value && !busy) setReviewing(false); }}>
      <DialogContent className="max-w-3xl" onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onPointerDownOutside={event => { if (busy) event.preventDefault(); }}>
        <DialogHeader><DialogTitle>Xác nhận lên lịch {open.length} bài</DialogTitle><DialogDescription>{account?.displayName} sẽ đăng lần lượt, mỗi nhóm cách nhau 5 phút cộng ngẫu nhiên 0–30 giây. Chỉ khi xác nhận các bài mới vào hàng đợi.</DialogDescription></DialogHeader>
        <div className="max-h-96 overflow-y-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm"><thead className="sticky top-0 bg-muted text-xs text-muted-foreground"><tr><th className="p-2 font-medium">#</th><th className="p-2 font-medium">Bài</th><th className="p-2 font-medium">Bắt đầu (dự kiến)</th><th className="p-2 text-right font-medium">Nhóm</th><th className="p-2 font-medium">Xong khoảng</th><th className="p-2 font-medium">Kết quả</th></tr></thead>
            <tbody className="divide-y divide-border">{rows.map((row, index) => { const slot = plan.get(row.id); return <tr key={row.id}>
              <td className="p-2 tabular-nums">{index + 1}</td>
              <td className="max-w-56 p-2"><p className="truncate font-medium">{row.name || `Bài ${index + 1}`}</p><p className="truncate text-xs text-muted-foreground">{row.text.trim() || `${row.imageIds.length} ảnh`}</p><p className="text-xs text-muted-foreground">{row.rewriteContent && row.text.trim() ? "Viết lại nội dung" : "Đăng nguyên bản"}</p></td>
              <td className="p-2 tabular-nums">{slot ? fmt(slot.start) : "—"}{slot?.delayed && <span className="ml-1 text-xs text-warning">(chờ bài trước)</span>}</td>
              <td className="p-2 text-right tabular-nums">{row.targetIds.length}</td>
              <td className="p-2 tabular-nums">{slot ? fmt(slot.end) : "—"}</td>
              <td className="p-2 text-xs">{row.state === "creating" ? "Đang tạo…" : row.state === "done" ? <span className="text-success">Đã lên lịch</span> : row.state === "error" ? <span className="text-destructive">Lỗi</span> : "Chờ"}</td></tr>; })}</tbody></table>
        </div>
        <p className="text-xs text-muted-foreground">Giờ dự kiến có thể trễ do lượt trước, phiên đăng nhập hoặc mạng. Hẹn giờ chỉ chạy khi Fraylo và máy đang hoạt động; nếu lỡ giờ, bài không tự đăng muộn mà chờ bạn chọn.</p>
        {error && <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}
        <DialogFooter><Button variant="outline" disabled={busy} onClick={() => setReviewing(false)}>Quay lại chỉnh sửa</Button><Button disabled={busy || open.length === 0} onClick={() => void submit()}>{busy ? "Đang lên lịch…" : `Xác nhận lên lịch ${open.length} bài`}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </div>;
}

function GroupPicker({ open, groups, initial, onClose, onSave }: { open: boolean; groups: FacebookGroup[]; initial: string[]; onClose: () => void; onSave: (ids: string[]) => void }) {
  return <Dialog open={open} onOpenChange={value => { if (!value) onClose(); }}>
    <DialogContent className="max-w-2xl">{open && <GroupPickerBody groups={groups} initial={initial} onClose={onClose} onSave={onSave} />}</DialogContent>
  </Dialog>;
}

function GroupPickerBody({ groups, initial, onClose, onSave }: { groups: FacebookGroup[]; initial: string[]; onClose: () => void; onSave: (ids: string[]) => void }) {
  const [selected, setSelected] = useState<string[]>(initial);
  const [search, setSearch] = useState("");
  const [count, setCount] = useState("20");
  const [error, setError] = useState<string | null>(null);
  const visible = groups.filter(group => group.name.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const toggle = (id: string) => setSelected(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]);
  return <>
    <DialogHeader><DialogTitle>Chọn nhóm đăng</DialogTitle><DialogDescription>Đã chọn {selected.length}/{groups.length} nhóm. Thứ tự đăng theo thứ tự bạn chọn.</DialogDescription></DialogHeader>
    <div className="flex flex-col gap-2">
      <Input aria-label="Tìm nhóm" placeholder="Tìm nhóm theo tên…" value={search} onChange={event => setSearch(event.target.value)} />
      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="outline" disabled={!groups.length} onClick={() => setSelected(groups.map(group => group.targetId))}>Chọn tất cả</Button>
        <Button size="sm" variant="ghost" disabled={!selected.length} onClick={() => setSelected([])}>Bỏ chọn</Button>
        <div className="ml-auto flex items-center gap-2">
          <label htmlFor="bulk-random" className="sr-only">Số nhóm ngẫu nhiên</label>
          <Input id="bulk-random" type="number" min={1} max={groups.length} step={1} className="h-8 w-16 px-2" value={count} onChange={event => setCount(event.target.value)} />
          <Button size="sm" variant="outline" disabled={!groups.length} onClick={() => { try { setSelected(randomGroupIds(groups, Number(count))); setError(null); } catch (err) { setError(facebookError(err)); } }}><Shuffle className="size-4" aria-hidden="true" />Chọn ngẫu nhiên</Button>
        </div>
      </div>
      {error && <p role="alert" className="text-xs text-destructive">{error}</p>}
    </div>
    <div className="max-h-80 overflow-y-auto rounded-lg border border-border divide-y divide-border">
      {visible.map(group => <label key={group.targetId} title={group.name} className={`flex cursor-pointer items-center gap-3 px-3 py-2.5 transition-colors hover:bg-muted/50 ${selected.includes(group.targetId) ? "bg-primary/5" : ""}`}>
        <input type="checkbox" className="size-4 shrink-0 accent-primary" checked={selected.includes(group.targetId)} onChange={() => toggle(group.targetId)} />
        <span className="min-w-0 flex-1"><span className="block truncate text-sm font-medium">{group.name}</span><span className="block truncate text-xs text-muted-foreground">{group.url}</span></span>
      </label>)}
      {!visible.length && <p className="p-6 text-sm text-muted-foreground">{groups.length ? "Không có nhóm khớp từ khóa." : "Chưa có nhóm đã lưu cho tài khoản này. Mở tab Soạn bài và bấm Tải lại nhóm từ Facebook."}</p>}
    </div>
    <DialogFooter><Button variant="outline" onClick={onClose}>Hủy</Button><Button onClick={() => onSave(selected)}>Xong — {selected.length} nhóm</Button></DialogFooter>
  </>;
}

function ImagePicker({ open, initial, onClose, onSave }: { open: boolean; initial: string[]; onClose: () => void; onSave: (ids: string[]) => void }) {
  return <Dialog open={open} onOpenChange={value => { if (!value) onClose(); }}>
    <DialogContent className="max-w-2xl">{open && <ImagePickerBody initial={initial} onClose={onClose} onSave={onSave} />}</DialogContent>
  </Dialog>;
}

function ImagePickerBody({ initial, onClose, onSave }: { initial: string[]; onClose: () => void; onSave: (ids: string[]) => void }) {
  const [selected, setSelected] = useState<string[]>(initial);
  const [images, setImages] = useState<AssetItem[]>([]);
  const [page, setPage] = useState(0);
  const [total, setTotal] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const load = async (next = 1) => {
    if (busy) return;
    setBusy(true); setError(null);
    try {
      const result = await listAssets({ assetType: "Image", page: next, pageSize: 50 });
      setImages(current => next === 1 ? result.assets : [...current, ...result.assets.filter(image => !current.some(old => old.assetId === image.assetId))]);
      setPage(next); setTotal(result.totalCount);
    } catch (err) { setError(facebookError(err)); } finally { setBusy(false); }
  };
  // Thân hộp thoại chỉ mount khi mở, nên effect này tải thư viện đúng một lần mỗi lần mở.
  useEffect(() => { void load(); }, []);
  const toggle = (id: string) => setSelected(current => current.includes(id) ? current.filter(value => value !== id) : current.length >= MAX_IMAGES ? current : [...current, id]);
  const upload = async () => {
    if (busy || selected.length >= MAX_IMAGES) return;
    setBusy(true); setError(null); setNote(null);
    try {
      const result = await uploadAsset({ assetType: "Image", multiple: true });
      const uploaded = result.assets ?? (result.asset ? [result.asset] : []);
      if (result.errors?.length) setError(`Không tải được ${result.errors.length} ảnh: ${result.errors.join("; ")}`);
      if (!uploaded.length) return;
      setImages(current => [...uploaded, ...current.filter(image => !uploaded.some(item => item.assetId === image.assetId))]);
      const room = MAX_IMAGES - selected.length;
      setSelected(current => [...current, ...uploaded.map(item => item.assetId).filter(id => !current.includes(id))].slice(0, MAX_IMAGES));
      setNote(uploaded.length > room ? `Đã tải ${uploaded.length} ảnh lên thư viện; chỉ ${room} ảnh đầu được chọn (tối đa ${MAX_IMAGES}).` : `Đã tải và chọn ${uploaded.length} ảnh.`);
    } catch (err) { setError(facebookError(err)); } finally { setBusy(false); }
  };
  return <>
    <DialogHeader><DialogTitle>Ảnh đính kèm ({selected.length}/{MAX_IMAGES})</DialogTitle><DialogDescription>Chọn từ thư viện hoặc tải nhiều ảnh cùng lúc từ máy. Ảnh được lưu vào thư viện của Fraylo.</DialogDescription></DialogHeader>
    <div><Button variant="outline" size="sm" onClick={() => void upload()} disabled={busy || selected.length >= MAX_IMAGES}><Upload className="size-4" aria-hidden="true" />{busy ? "Đang xử lý…" : "Tải ảnh lên"}</Button></div>
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {note && <p role="status" className="text-sm text-muted-foreground">{note}</p>}
    <ul className="grid max-h-80 grid-cols-[repeat(auto-fill,minmax(88px,1fr))] gap-2 overflow-y-auto" aria-label="Ảnh trong thư viện">
      {images.map(image => { const picked = selected.includes(image.assetId); const name = image.relativePath.split(/[\\/]/).pop() ?? "Ảnh";
        return <li key={image.assetId}><button type="button" aria-pressed={picked} aria-label={name} title={name} disabled={!picked && selected.length >= MAX_IMAGES} onClick={() => toggle(image.assetId)} className={`relative block aspect-square w-full overflow-hidden rounded-md border-2 bg-muted transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 ${picked ? "border-primary" : "border-transparent hover:border-primary/40"}`}>
          <img src={assetMediaUrl(image.assetId)} alt="" loading="lazy" className="size-full object-cover" />
          {picked && <span className="absolute right-1 top-1 flex size-5 items-center justify-center rounded-full bg-primary text-primary-foreground"><Check className="size-3.5" aria-hidden="true" /></span>}</button></li>; })}
    </ul>
    {!busy && images.length === 0 && <p className="text-sm text-muted-foreground">Thư viện chưa có ảnh. Bấm Tải ảnh lên để thêm.</p>}
    {images.length < total && <Button variant="ghost" size="sm" onClick={() => void load(page + 1)} disabled={busy}>Tải thêm ảnh</Button>}
    <DialogFooter><Button variant="outline" onClick={onClose}><X className="size-4" aria-hidden="true" />Hủy</Button><Button onClick={() => onSave(selected)}>Xong — {selected.length} ảnh</Button></DialogFooter>
  </>;
}
