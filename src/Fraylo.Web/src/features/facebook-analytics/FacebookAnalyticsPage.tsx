import { useCallback, useEffect, useRef, useState } from "react";
import { FacebookActivityLog } from "../facebook/FacebookActivityLog";
import { BarChart3, RefreshCw, X } from "lucide-react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { AnalyticsAnalysisPanel } from "../analytics/AnalyticsAnalysisPanel";
import { facebookError } from "../facebook/facebookApi";
import { facebookAnalyticsApi, formatMetric, metricLabels, type FacebookAnalyticsApi, type FacebookCatalog, type FacebookContent, type FacebookDashboard, type FacebookObservation } from "./facebookAnalyticsApi";

const control = "h-10 rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";
const countKeys = ["views", "reactions", "comments", "shares"];
const captureLabels: Record<string, string> = { Running: "Đang cập nhật", Complete: "Đã cập nhật", Partial: "Dữ liệu một phần", Failed: "Chưa cập nhật được", Canceled: "Đã dừng" };
const when = (date: string) => new Date(date).toLocaleString("vi-VN");

export function FacebookAnalyticsPage({ active, onOpenAccounts, onOpenAi, api = facebookAnalyticsApi, embedded = false }: { active: boolean; onOpenAccounts: () => void; onOpenAi?: () => void; api?: FacebookAnalyticsApi; embedded?: boolean }) {
  const [catalog, setCatalog] = useState<FacebookCatalog>({ accounts: [], pages: [] });
  const [accountId, setAccountId] = useState("");
  const [pageId, setPageId] = useState("");
  const [data, setData] = useState<FacebookDashboard | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState("");
  const [days, setDays] = useState(0);
  const [sort, setSort] = useState("views");
  const [page, setPage] = useState(1);
  const [detail, setDetail] = useState<FacebookContent | null>(null);
  const [history, setHistory] = useState<FacebookObservation[]>([]);
  const [historyLoading, setHistoryLoading] = useState(false);
  const requestSeq = useRef(0);
  const detailSeq = useRef(0);
  const scope = useRef(""); scope.current = accountId + ":" + pageId;
  const mounted = useRef(true);
  const pending = useRef<{ scope: string; id: string } | null>(null);
  const busyRef = useRef(false);
  const account = catalog.accounts.find(a => a.accountId === accountId);
  const pages = catalog.pages.filter(p => p.accountId === accountId);
  const selectedPage = pages.find(p => p.id === pageId);
  const running = data?.latestAttempt?.status === "Running";
  const query = { accountId, pageId, page, search, kind, days, sort };

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; requestSeq.current++; detailSeq.current++; }; }, []);
  useEffect(() => {
    let current = true;
    api.catalog().then(value => {
      if (!current) return;
      setCatalog(value); setAccountId(previous => previous || value.accounts[0]?.accountId || "");
    }).catch(err => { if (current) setError(facebookError(err)); });
    return () => { current = false; };
  }, [api]);
  useEffect(() => {
    setPageId(previous => catalog.pages.some(p => p.id === previous && p.accountId === accountId) ? previous : catalog.pages.find(p => p.accountId === accountId)?.id || "");
  }, [accountId, catalog.pages]);

  const load = useCallback(async () => {
    if (!accountId || !pageId) return;
    const seq = ++requestSeq.current;
    setLoading(true);
    try {
      const value = await api.dashboard({ accountId, pageId, page, search, kind, days, sort });
      if (!mounted.current || seq !== requestSeq.current) return;
      setData(value); setError("");
      if (value.latestAttempt?.status !== "Running") pending.current = null;
    } catch (err) { if (mounted.current && seq === requestSeq.current) setError(facebookError(err)); }
    finally { if (mounted.current && seq === requestSeq.current) setLoading(false); }
  }, [accountId, pageId, page, search, kind, days, sort, api]);

  useEffect(() => {
    if (!active || !accountId || !pageId) return;
    void load();
    const timer = running ? setInterval(() => void load(), 2000) : undefined;
    return () => { requestSeq.current++; clearInterval(timer); };
  }, [active, load, running, accountId, pageId]);

  const changeScope = () => { requestSeq.current++; detailSeq.current++; setData(null); setDetail(null); setHistory([]); setError(""); setNotice(""); setPage(1); setLoading(false); };
  const syncPages = async () => {
    if (busyRef.current) return;
    busyRef.current = true; setBusy(true); setError("");
    const selected = accountId;
    try { const value = await api.syncPages(selected); if (mounted.current) { setCatalog(value); if (scope.current.startsWith(selected + ":")) setNotice("Đã tải danh sách Fanpage. Quyền Insights sẽ được kiểm tra khi cập nhật số liệu."); } }
    catch (err) { if (mounted.current && scope.current.startsWith(selected + ":")) setError(facebookError(err)); }
    finally { busyRef.current = false; if (mounted.current) setBusy(false); }
  };
  const sync = async () => {
    if (busyRef.current) return;
    busyRef.current = true; setBusy(true); setError("");
    const selected = scope.current;
    const id = pending.current?.scope === selected ? pending.current.id : crypto.randomUUID();
    pending.current = { scope: selected, id };
    try {
      const capture = await api.sync(query, id);
      if (mounted.current && scope.current === selected) { setData(previous => ({ capture: null, content: [], totalCount: 0, page: 1, totals: {}, captures: [], ...previous, latestAttempt: capture })); await load(); }
    } catch (err) { if (mounted.current && scope.current === selected) { setError(facebookError(err)); setNotice("Nếu yêu cầu đã được nhận, cập nhật trạng thái trước khi thử lại."); } }
    finally { busyRef.current = false; if (mounted.current) setBusy(false); }
  };
  const cancel = async () => {
    const capture = data?.latestAttempt;
    if (!capture || busyRef.current) return;
    const selected = scope.current;
    busyRef.current = true; setBusy(true);
    try { await api.cancel(query, capture.id); if (mounted.current && scope.current === selected) await load(); }
    catch (err) { if (mounted.current && scope.current === selected) setError(facebookError(err)); }
    finally { busyRef.current = false; if (mounted.current) setBusy(false); }
  };
  const openDetail = async (item: FacebookContent) => {
    const seq = ++detailSeq.current;
    setDetail(item); setHistory([]); setHistoryLoading(true);
    try { const value = await api.history(query, item.contentId); if (mounted.current && seq === detailSeq.current) setHistory(value); }
    catch (err) { if (mounted.current && seq === detailSeq.current) setError(facebookError(err)); }
    finally { if (mounted.current && seq === detailSeq.current) setHistoryLoading(false); }
  };

  return <div className="flex h-full flex-col overflow-y-auto">
    {!embedded && <PageHeader className="px-4 pt-5 md:px-6" title="Facebook Analytics" description="Theo dõi bài viết và Reels của Fanpage" actions={<Button variant="outline" onClick={onOpenAccounts}>Tài khoản Facebook</Button>} />}
    <div className={embedded ? "space-y-5" : "space-y-5 p-4 md:p-6"}>
      <section className="rounded-xl border border-border bg-card p-5" aria-label="Chọn Fanpage">
        <div className="grid gap-4 xl:grid-cols-2">
          <label className="grid min-w-0 gap-2 text-sm">Tài khoản<select aria-label="Tài khoản Facebook" className={control} value={accountId} onChange={e => { changeScope(); setAccountId(e.target.value); setPageId(""); }}>
            <option value="">Chọn tài khoản</option>{catalog.accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.name}{a.enabled ? "" : " · Đã tắt"}</option>)}</select></label>
          <label className="grid min-w-0 gap-2 text-sm">Fanpage<select aria-label="Fanpage" className={control} value={pageId} onChange={e => { changeScope(); setPageId(e.target.value); }}>
            <option value="">Chọn Fanpage</option>{pages.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        </div>
        <div className="mt-4 flex flex-wrap gap-2">
          <Button variant="outline" disabled={!accountId || busy || running || !account?.enabled || account.sessionStatus !== "Connected"} onClick={() => void syncPages()}>Tải Fanpage từ Facebook</Button>
          <Button disabled={!pageId || busy || running || !account?.enabled || account.sessionStatus !== "Connected"} onClick={() => void sync()}><RefreshCw className="mr-2 h-4 w-4" aria-hidden="true" />Cập nhật số liệu</Button>
          {running && <Button variant="outline" disabled={busy} onClick={() => void cancel()}><X className="mr-2 h-4 w-4" aria-hidden="true" />Dừng cập nhật</Button>}
          <Button variant="ghost" disabled={!pageId || loading} onClick={() => void load()}>Cập nhật trạng thái</Button>
        </div>
        <p className="mt-3 text-xs text-muted-foreground">Mở trang dùng dữ liệu đã lưu. Facebook chỉ được truy cập khi bạn tải Fanpage hoặc cập nhật số liệu.</p>
        {account && (!account.enabled || account.sessionStatus !== "Connected") && <p className="mt-2 text-sm text-warning">Bật và xác minh tài khoản trong Cài đặt để cập nhật. Dữ liệu đã lưu vẫn xem được.</p>}
      </section>
      {error && <p role="alert" className="rounded-lg border border-destructive/30 bg-destructive/10 p-4 text-sm text-foreground">{error}</p>}
      {notice && <p role="status" className="text-sm text-muted-foreground">{notice}</p>}
      {data?.latestAttempt && <div className="rounded-lg border border-border p-4 text-sm"><p role="status">
        <span className="font-medium">{captureLabels[data.latestAttempt.status] ?? data.latestAttempt.status}</span> · {data.latestAttempt.message}</p>
        <FacebookActivityLog id={data.latestAttempt.id} entries={data.latestAttempt.activityLog} />
        {data.capture && data.latestAttempt.id !== data.capture.id && <p className="mt-1 text-muted-foreground">Đang hiển thị dữ liệu đã lưu lúc {when(data.capture.startedAtUtc)}.</p>}
      </div>}
      {data?.capture ? <>
        <div><h2 className="text-lg font-semibold">{selectedPage?.name}</h2><p className="mt-1 text-sm text-muted-foreground">Số liệu tích lũy · cập nhật {when(data.capture.startedAtUtc)}. {data.capture.scope}.</p>
          <p className="mt-1 text-xs text-muted-foreground">Tổng chỉ tính trong các nội dung đã đọc và khớp bộ lọc; không phải tổng toàn Fanpage. “—” là chưa có dữ liệu.</p></div>
        <div className="flex flex-wrap gap-3" aria-label="Bộ lọc nội dung">
          <Input aria-label="Tìm nội dung" placeholder="Tìm trong nội dung đã đọc…" className="h-10 w-full sm:max-w-xs" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
          <select aria-label="Loại nội dung" className={control} value={kind} onChange={e => { setKind(e.target.value); setPage(1); }}><option value="">Bài viết và Reels</option><option value="Reel">Reels</option><option value="Post">Bài viết</option></select>
          <select aria-label="Ngày đăng" className={control} value={days} onChange={e => { setDays(Number(e.target.value)); setPage(1); }}><option value={0}>Mọi ngày đăng đã thu thập</option>{[7, 30, 90].map(d => <option key={d} value={d}>Đăng trong {d} ngày qua</option>)}</select>
          <select aria-label="Sắp xếp" className={control} value={sort} onChange={e => { setSort(e.target.value); setPage(1); }}>{countKeys.map(k => <option key={k} value={k}>{metricLabels[k]} giảm dần</option>)}<option value="published">Ngày đăng mới nhất</option></select>
        </div>
        <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">{countKeys.map(key => { const total = data.totals[key]; return <div key={key} className="rounded-xl border border-border bg-card p-4"><p className="text-sm text-muted-foreground">{metricLabels[key]}</p><p className="mt-2 text-2xl font-semibold tabular-nums">{formatMetric(total)}</p><p className="mt-1 text-xs text-muted-foreground">Từ {total?.available ?? 0}/{total?.total ?? 0} nội dung có số liệu</p></div>; })}</div>
        <div className="overflow-x-auto rounded-xl border border-border"><table className="w-full text-sm"><caption className="sr-only">Hiệu quả các nội dung đã thu thập</caption><thead className="bg-muted/50"><tr><th className="p-4 text-left">Nội dung</th>{countKeys.map(k => <th key={k} className="whitespace-nowrap p-3 text-right">{metricLabels[k]}</th>)}<th className="whitespace-nowrap p-3 text-right">Xem TB (giây)</th></tr></thead>
          <tbody>{data.content.map(item => <tr key={item.contentId} className="border-t border-border"><td className="min-w-64 max-w-lg p-4"><button type="button" onClick={() => void openDetail(item)} className="w-full rounded text-left font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"><span className="line-clamp-2">{item.title || item.contentId}</span></button><p className="mt-1 text-xs text-muted-foreground">{item.kind === "Reel" ? "Reel" : "Bài viết"} · {item.publishedLabel}</p></td>{[...countKeys, "averageWatchSeconds"].map(k => <td key={k} className="p-3 text-right tabular-nums" title={item.metrics[k]?.missingReason ? `Chưa có dữ liệu · ${item.metrics[k]?.raw || "Facebook không cung cấp"}` : item.metrics[k]?.raw}>{formatMetric(item.metrics[k])}</td>)}</tr>)}</tbody></table>
          {data.content.length === 0 && <p className="p-6 text-center text-muted-foreground">Không có nội dung khớp bộ lọc. Ngày đăng thiếu năm không được đưa vào bộ lọc ngày.</p>}
        </div>
        <div className="flex items-center justify-between gap-3 text-sm"><p>{data.totalCount} nội dung · Trang {page}</p><div className="flex gap-2"><Button variant="outline" disabled={page <= 1 || loading} onClick={() => setPage(p => p - 1)}>Trước</Button><Button variant="outline" disabled={page * 20 >= data.totalCount || loading} onClick={() => setPage(p => p + 1)}>Sau</Button></div></div>
      </> : !running && <div className="rounded-xl border border-dashed border-border p-10 text-center"><BarChart3 className="mx-auto mb-3 h-8 w-8 text-muted-foreground" aria-hidden="true" /><h2 className="font-semibold">{loading ? "Đang đọc dữ liệu đã lưu…" : "Chưa có số liệu Fanpage"}</h2><p className="mt-2 text-sm text-muted-foreground">Chọn tài khoản và Fanpage, sau đó bấm Cập nhật số liệu.</p></div>}
      {!!data?.captures.length && <details className="rounded-xl border border-border p-4">
        <summary className="cursor-pointer text-sm font-medium">Lịch sử cập nhật Fanpage</summary>
        <ul className="mt-3 space-y-3 text-sm">{data.captures.map(c => <li key={c.id}>
          <p>{when(c.startedAtUtc)} · {captureLabels[c.status]} · {c.recordCount} nội dung</p>
          {c.id !== data.latestAttempt?.id && <FacebookActivityLog id={c.id} entries={c.activityLog} />}
        </li>)}</ul>
      </details>}
    </div>
    {selectedPage && <AnalyticsAnalysisPanel key={accountId + ":" + pageId} active={active} platform="facebook" accountId={accountId}
      pageId={pageId} refresh={data?.capture?.id} onOpenAccounts={onOpenAccounts} onOpenAi={onOpenAi} />}
    <Dialog open={detail !== null} onOpenChange={open => { if (!open) { detailSeq.current++; setDetail(null); } }}><DialogContent className="max-h-[85vh] max-w-3xl overflow-y-auto"><DialogHeader><DialogTitle>Chi tiết {detail?.kind === "Reel" ? "Reel" : "bài viết"}</DialogTitle><DialogDescription>Số tích lũy ở từng lần cập nhật; không phải lượt phát sinh theo ngày.</DialogDescription></DialogHeader>
      {detail && <><p className="whitespace-pre-wrap break-words text-sm">{detail.title}</p><p className="text-xs text-muted-foreground">Đăng: {detail.publishedLabel} · ID {detail.contentId}</p>
        <label className="grid gap-2 text-sm">Link Facebook<Input aria-label="Link Facebook" readOnly value={detail.url} onFocus={e => e.currentTarget.select()} /></label>
        <dl className="grid grid-cols-2 gap-3">{Object.keys(metricLabels).map(k => <div key={k} className="rounded-lg border border-border p-3"><dt className="text-xs text-muted-foreground">{metricLabels[k]}</dt><dd className="mt-1 font-medium">{formatMetric(detail.metrics[k])}{k.endsWith("Seconds") && detail.metrics[k]?.value != null ? " giây" : ""}</dd>{detail.metrics[k]?.value == null && <p className="mt-1 text-xs text-muted-foreground">{k === "totalWatchSeconds" && detail.metrics[k]?.missingReason === "unit_unverified" ? `Facebook hiển thị “${detail.metrics[k].raw}”; chưa xác minh đơn vị.` : "Facebook chưa cung cấp số liệu đọc được."}</p>}</div>)}</dl>
        <h3 className="font-semibold">Các mốc quan sát gần nhất</h3>{historyLoading ? <p role="status">Đang tải lịch sử…</p> : <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr><th className="p-2 text-left">Cập nhật lúc</th>{countKeys.map(k => <th key={k} className="p-2 text-right">{metricLabels[k]}</th>)}</tr></thead><tbody>{history.map(h => <tr key={h.captureId} className="border-t border-border"><td className="p-2">{when(h.capturedAtUtc)}</td>{countKeys.map(k => <td key={k} className="p-2 text-right tabular-nums">{formatMetric(h.content.metrics[k])}</td>)}</tr>)}</tbody></table>{history.length === 0 && <p>Chưa có mốc quan sát.</p>}</div>}</>}
    </DialogContent></Dialog>
  </div>;
}
