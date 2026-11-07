import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { FacebookActivityLog } from "./FacebookActivityLog";
import { facebookDisplayState, facebookError, facebookStateLabels, isFacebookTaskPending, localInputToUtc, rewriteReasonMessage, type FacebookApi, type FacebookList, type FacebookPost } from "./facebookApi";

export function postStatus(post: FacebookPost): string {
  const tasks = post.tasks;
  if (tasks.some(t => t.state === "NeedsUser")) return tasks.some(t => facebookDisplayState(t) === "Missed") ? "Đã lỡ lịch" : "Cần bạn xử lý";
  if (post.paused && tasks.some(t => isFacebookTaskPending(t.state))) return "Tạm dừng";
  if (tasks.some(t => isFacebookTaskPending(t.state)) && post.rewriteChunks?.some(c => !c.applied)) {
    if (post.rewriteChunks.some(c => c.state === "Failed" || c.state === "AwaitingUser" || c.state === "Canceled")) return "Cần xử lý AI";
    return post.rewriteChunks.some(c => c.state === "Running" || c.state === "Reserved") ? "Đang viết lại" : "Chờ AI";
  }
  if (tasks.some(t => t.state === "Submitting")) return "Chờ Facebook xác nhận";
  if (tasks.some(t => t.state === "Preparing")) return "Đang chuẩn bị";
  if (tasks.some(t => t.state === "Queued")) return tasks.some(t => facebookDisplayState(t) === "Scheduled") ? "Đã hẹn giờ" : "Đang chờ lượt";
  if (tasks.some(t => t.state === "Unknown")) return "Chưa xác định kết quả";
  if (tasks.every(t => t.state === "Failed")) return "Thất bại";
  if (tasks.every(t => t.state === "Canceled")) return "Đã hủy";
  if (tasks.some(t => t.state === "Failed" || t.state === "Canceled")) return "Đã xử lý · Có lỗi/hủy";
  return tasks.some(t => t.state === "PendingApproval") ? "Đã gửi hết · Có bài chờ duyệt" : "Hoàn tất";
}

export function FacebookPosts({ data, api, history, onRefresh, onError, onClone, personal = false }: {
  personal?: boolean; data: FacebookList; api: FacebookApi; history: boolean; onRefresh: () => Promise<void>;
  onError: (error: string | null) => void; onClone: (post: FacebookPost) => void;
}) {
  const [expanded, setExpanded] = useState<string | null>(null);
  const [filter, setFilter] = useState("all");
  const [search, setSearch] = useState("");
  const [target, setTarget] = useState("all");
  const [account, setAccount] = useState("all");
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState(false);
  const [cancel, setCancel] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [due, setDue] = useState("");
  const posts = data.posts ?? [];
  // Hàng đợi chỉ giữ lượt còn chờ; lượt đã xử lý rời hàng đợi sang Lịch sử (một bài nhiều nhóm có thể nằm ở cả hai với phần lượt tương ứng).
  const inView = posts.map(post => ({ post, shown: post.tasks.filter(t => isFacebookTaskPending(t.state) !== history) })).filter(item => item.shown.length > 0)
    .map(item => ({ ...item, status: postStatus({ ...item.post, tasks: item.shown }) }));
  const filtered = inView.filter(item => (account === "all" || item.post.accountId === account) && (target === "all" || item.post.tasks.every(t => t.targetId.startsWith("personal:") === (target === "personal"))) && (filter === "all" || item.status === filter) && `${item.post.name} ${item.post.tasks[0]?.text ?? ""}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const currentPage = Math.min(page, Math.max(1, Math.ceil(filtered.length / 20)));
  const action = async (post: FacebookPost, type: string) => {
    if (busy || !api.postAction) return;
    setBusy(true); onError(null);
    try { await api.postAction(post.batchId, post.revision, type, type === "reschedule" ? localInputToUtc(due) : undefined); setCancel(null); setEditing(null); await onRefresh(); }
    catch (err) { onError(facebookError(err)); await onRefresh(); }
    finally { setBusy(false); }
  };
  const retryRewrite = async (post: FacebookPost, chunkId: string) => {
    if (busy || !api.retryRewrite) return;
    setBusy(true); onError(null);
    try { await api.retryRewrite(post.batchId, chunkId, post.revision); await onRefresh(); }
    catch (err) { onError(facebookError(err)); await onRefresh(); }
    finally { setBusy(false); }
  };
  return <div className="flex flex-col gap-3">
    <p className="text-sm text-muted-foreground">Mỗi tài khoản cách ít nhất 5 phút giữa hai lượt gửi, kể cả khác bài. Không giới hạn lượt/ngày. Giữ máy và Fraylo hoạt động; bài lỡ lịch chờ bạn tiếp tục.</p>
    <div className="flex flex-wrap gap-2"><Input className="min-w-48 flex-1" aria-label="Tìm bài đăng" placeholder="Tìm bài đăng…" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} /><select aria-label="Lọc nơi đăng" className="h-10 rounded-md border border-input bg-background px-3 text-sm" value={target} onChange={e => { setTarget(e.target.value); setPage(1); }}><option value="all">Tất cả nơi đăng</option><option value="group">Nhóm</option><option value="personal">Cá nhân</option></select><select aria-label="Lọc tài khoản Facebook" className="h-10 rounded-md border border-input bg-background px-3 text-sm" value={account} onChange={e => { setAccount(e.target.value); setPage(1); }}><option value="all">Tất cả tài khoản</option>{data.accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.displayName}</option>)}</select><select aria-label="Lọc bài đăng" className="h-10 w-full rounded-md sm:w-auto border border-input bg-background px-3 text-sm" value={filter} onChange={e => { setFilter(e.target.value); setPage(1); }}><option value="all">Mọi trạng thái</option>{[...new Set(inView.map(item => item.status))].map(s => <option key={s}>{s}</option>)}</select></div>
    {filtered.slice((currentPage - 1) * 20, currentPage * 20).map(({ post, shown, status }) => {
      const tasks = post.tasks;
      const isPersonal = personal || tasks.every(t => t.targetId.startsWith("personal:"));
      const pending = tasks.filter(t => isFacebookTaskPending(t.state));
      const current = shown.find(t => t.state === "NeedsUser")
        ?? shown.find(t => t.state === "Submitting" || t.state === "Preparing")
        ?? shown.find(t => t.reasonCode);
      const published = tasks.filter(t => t.state === "Published").length;
      const approval = tasks.filter(t => t.state === "PendingApproval").length;
      const start = tasks.find(t => t.dueAtUtc)?.dueAtUtc;
      const lastSent = data.tasks.filter(t => t.accountId === post.accountId && t.submittedAtUtc).reduce((max, t) => Math.max(max, Date.parse(t.submittedAtUtc!)), 0);
      const earliest = Math.max(start ? Date.parse(start) : 0, Date.now(), lastSent ? lastSent + 300000 : 0);
      const canChangeTime = tasks.every(t => !t.submittedAtUtc && !["Preparing", "Submitting"].includes(t.state)) && pending.length > 0;
      return <article key={post.batchId} className="rounded-xl border border-border bg-card p-5">
        <div className="flex flex-wrap items-start justify-between gap-3"><div><h3 className="font-semibold">{post.name || tasks[0]?.text.slice(0, 60) || "Bài đăng Facebook"}</h3><p className="text-sm text-muted-foreground">{data.accounts.find(a => a.accountId === post.accountId)?.displayName ?? "Tài khoản đã lưu"} · {isPersonal ? "Trang cá nhân" : `${tasks.length} nhóm`}</p></div><span className="rounded-full border border-border px-3 py-1 text-xs">{status}</span></div>
        <p className="mt-3 text-sm">{tasks.length - pending.length}/{tasks.length} lượt đã xử lý · {published} đã đăng · {approval} chờ duyệt</p>
        {!history && !post.paused && current && <p className="mt-2 break-words text-sm text-muted-foreground">{current.message}</p>}
        {!!post.rewriteChunks?.length && <p className="mt-2 text-sm" role="status">AI: đã có {post.rewriteChunks.filter(c => c.applied).reduce((sum, c) => sum + c.count, 0)}/{tasks.length} nội dung · {post.rewriteChunks.length} lượt, tối đa 10 bản/lượt.</p>}
        {start && <p className="text-sm">Bắt đầu: {new Date(start).toLocaleString("vi-VN")}</p>}
        {!history && pending.length > 0 && pending.every(t => t.state === "Queued") && !post.paused && !post.rewriteChunks?.some(c => !c.applied) && <p className="text-xs text-muted-foreground">Lượt cuối sớm nhất: {new Date(earliest + Math.max(0, pending.length - 1) * 300000).toLocaleString("vi-VN")} · Có thể trễ do bài trước, phiên hoặc mạng.</p>}
        <div className="mt-3 flex flex-wrap gap-2"><Button size="sm" variant="outline" aria-expanded={expanded === post.batchId} onClick={() => setExpanded(expanded === post.batchId ? null : post.batchId)}>Chi tiết</Button><Button size="sm" variant="outline" onClick={() => onClone(post)}>Nhân bản</Button>
          {!history && pending.length > 0 && <>
            {!post.paused && <Button size="sm" variant="outline" disabled={busy} onClick={() => void action(post, "pause")}>Tạm dừng</Button>}
            {(post.paused || tasks.some(t => t.state === "NeedsUser")) && <Button size="sm" disabled={busy} onClick={() => void action(post, "resume")}>Tiếp tục</Button>}
            {canChangeTime && <><Button size="sm" disabled={busy} onClick={() => void action(post, "now")}>Đăng ngay</Button><Button size="sm" variant="outline" disabled={busy} onClick={() => { setEditing(post.batchId); setDue(""); }}>Đổi giờ</Button></>}
            <Button size="sm" variant={cancel === post.batchId ? "destructive" : "outline"} disabled={busy} onClick={() => cancel === post.batchId ? void action(post, "cancel") : setCancel(post.batchId)}>{cancel === post.batchId ? "Xác nhận hủy phần chưa gửi" : "Hủy phần chưa gửi"}</Button>
          </>}
        </div>
        {editing === post.batchId && <div className="mt-3 flex flex-wrap gap-2"><input aria-label="Giờ hẹn mới" type="datetime-local" className="rounded-md border border-input bg-background px-3" value={due} onChange={e => setDue(e.target.value)} /><Button disabled={busy || !due} onClick={() => void action(post, "reschedule")}>Lưu giờ hẹn</Button><Button variant="ghost" onClick={() => setEditing(null)}>Đóng</Button></div>}
        {expanded === post.batchId && <div className="mt-4"><div className="space-y-2">{post.rewriteChunks?.map(chunk => <div key={chunk.chunkId} className="rounded-lg border border-border p-3 text-sm">
          <p>Lượt AI {chunk.ordinal} · {chunk.count} bản · {chunk.applied ? "Đã lưu" : chunk.state === "Running" ? "Đang viết lại" : chunk.state === "Queued" ? "Chờ AI" : chunk.state === "Reserved" ? "Đang chuẩn bị" : "Cần xử lý"}</p>
          {chunk.jobId && <p className="break-all text-xs text-muted-foreground">Mã tác vụ trong Tiến trình <code className="select-all">{chunk.jobId}</code></p>}
          {chunk.reason && <p className="break-words text-xs text-muted-foreground">{rewriteReasonMessage(chunk.reason)}</p>}
          {chunk.uncertain && <p className="text-sm">AI có thể đã nhận yêu cầu. Kiểm tra tác vụ trong Tiến trình trước khi tạo lượt mới; ứng dụng không tự gửi lại.</p>}
          {!history && chunk.canRetry && api.retryRewrite && <Button className="mt-2" size="sm" variant="outline" disabled={busy} onClick={() => void retryRewrite(post, chunk.chunkId)}>Tiếp tục / thử lại lượt AI {chunk.ordinal}</Button>}
        </div>)}</div><ol className="mt-3 divide-y divide-border">{shown.map(task => <li key={task.taskId} className="py-3 text-sm"><a href={task.groupUrl} target="_blank" rel="noreferrer" className="underline">{task.groupName}</a> · {facebookStateLabels[facebookDisplayState(task)]}<p className="my-2 whitespace-pre-wrap break-words text-sm">{task.text}</p><p className="text-xs text-muted-foreground">{task.message} {task.reasonCode ? `· ${task.reasonCode}` : ""}{task.submittedAtUtc ? ` · ${new Date(task.submittedAtUtc).toLocaleString("vi-VN")}` : ""}</p><FacebookActivityLog id={task.taskId} entries={task.activityLog} />{task.state === "Unknown" && <p>{isPersonal ? "Hãy kiểm tra trang cá nhân trước khi tạo bài mới để tránh đăng trùng." : "Hãy kiểm tra nhóm trước khi tạo bài mới để tránh đăng trùng."}</p>}</li>)}</ol></div>}
      </article>;
    })}
    {filtered.length === 0 && <p className="rounded-xl border border-border p-8 text-center text-muted-foreground">{inView.length === 0 || !(search || filter !== "all" || target !== "all" || account !== "all") ? (history ? "Chưa có bài đã hoàn tất." : "Hàng đợi trống. Mở Đăng bài nhóm hoặc Đăng bài cá nhân để tạo bài mới.") : "Không có bài khớp bộ lọc."}</p>}
    {filtered.length > 20 && <div className="flex items-center gap-3"><Button variant="outline" disabled={currentPage === 1} onClick={() => setPage(currentPage - 1)}>Trang trước</Button><span>{currentPage}/{Math.ceil(filtered.length / 20)}</span><Button variant="outline" disabled={currentPage * 20 >= filtered.length} onClick={() => setPage(currentPage + 1)}>Trang sau</Button></div>}
  </div>;
}
