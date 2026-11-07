import { useCallback, useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { bridgeClient } from "@/services/bridgeClient";
import { formatReasonCode } from "@/features/jobs/jobTypes";

type Metric = { value: number | null; approximate: boolean; raw: string | null; missing: string | null };
type EvidenceRow = { id: string; group: string; date: string | null; title: string | null; metrics: Record<string, Metric> };
type EvidenceTotal = { id: string; group: string; metric: string; value: number; available: number; rows: number; approximate: boolean };
type Input = { source: string; capturedAt: string; scope: string; periodStart: string | null; periodEnd: string | null; partial: boolean;
  totalRecords: number; omittedRecords: number; totals: EvidenceTotal[]; sample: EvidenceRow[] };
type Finding = { text: string; evidence: string[] };
type Report = { id: string; sourceId: string; createdAtUtc: string; providerKey: string | null; state: string; reason: string | null;
  input: Input | null; result: { summary: string; highlights: Finding[]; improvements: Finding[]; recommendations: Finding[]; limitations: string[] } | null };
type History = { latestSourceId: string | null; reports: Report[]; hasMore: boolean };

const states: Record<string, string> = {
  PendingEnqueue: "Đang chuẩn bị dữ liệu", Queued: "Đang chờ xử lý", Reserved: "Đang mở phiên AI",
  Running: "AI đang phân tích", AwaitingUser: "Cần thao tác", Succeeded: "Đã có báo cáo",
  Skipped: "Chưa phân tích", Failed: "Phân tích chưa thành công", Canceled: "Đã dừng",
};
const reasons: Record<string, string> = {
  ProviderNotConfigured: "Chọn AI phân tích số liệu trong Cài đặt → AI & Prompt.",
  CapabilityUnavailable: "Nhà cung cấp đã chọn chưa sẵn sàng cho tác vụ phân tích.",
  NoConnectedProvider: formatReasonCode("provider_account_required"),
  provider_account_required: formatReasonCode("provider_account_required"),
  NoMetrics: "Lần cập nhật này chưa có chỉ số để phân tích.",
  AnalysisRestarted: "Fraylo đã khởi động lại. Bấm Tiếp tục để chạy lượt phân tích đang chờ.",
  InputTooLarge: "Bản tóm tắt vượt giới hạn gửi của nhà cung cấp.",
  InvalidAnalysisResult: "Phản hồi AI thiếu cấu trúc hoặc căn cứ hợp lệ.",
  SourceUnavailable: "Không tìm thấy bản số liệu gốc.",
  login_required: "Cần đăng nhập lại tài khoản AI.",
  session_expired: "Phiên AI đã hết hạn.",
  auth_closed: "Phiên AI đã đóng.",
  wrong_account: "Phiên AI chưa khớp với tài khoản đã cấu hình.",
  reconcile_required: "AI có thể đã nhận dữ liệu. Kiểm tra cuộc trò chuyện trước khi chạy lượt mới.",
};
const metricLabels: Record<string, string> = {
  views: "Lượt xem", profileViews: "Lượt xem hồ sơ", likes: "Lượt thích", reactions: "Cảm xúc",
  comments: "Bình luận", shares: "Chia sẻ", averageWatchSeconds: "Thời gian xem TB (giây)",
  totalWatchSeconds: "Tổng thời gian xem", affiliateClicks: "Lượt nhấp affiliate", totalViewers: "Người xem",
  newViewers: "Người xem mới", returningViewers: "Người xem quay lại",
};
const groupLabels: Record<string, string> = { content: "Video", overview: "Tổng quan theo ngày", viewers: "Người xem theo ngày", reel: "Reel", post: "Bài viết" };
const format = (value: number) => value.toLocaleString("vi-VN", { maximumFractionDigits: 3 });
const when = (value: string) => new Date(value).toLocaleString("vi-VN");
const isActive = (r: Report) => ["Queued", "Reserved", "Running"].includes(r.state);
const loginReasons = ["NoConnectedProvider", "provider_account_required", "login_required", "session_expired", "auth_closed", "wrong_account"];
const errorMessage = (error: unknown) => error && typeof error === "object" && "message" in error
  ? String(error.message) : "Chưa đọc được báo cáo AI.";

function Evidence({ id, input }: { id: string; input: Input }) {
  const total = input.totals.find(t => t.id === id);
  const row = input.sample.find(r => r.id === id);
  if (total) return <li>{groupLabels[total.group]} · {metricLabels[total.metric]} {total.approximate ? "≈ " : ""}{format(total.value)}
    {" "}({total.available}/{total.rows} dòng có số liệu)</li>;
  if (!row) return null;
  const values = Object.entries(row.metrics).flatMap(([key, metric]) => metric.value === null ? [] :
    [`${metricLabels[key] ?? key} ${metric.approximate ? "≈ " : ""}${format(metric.value)}`]);
  return <li>{[groupLabels[row.group], row.date, row.title, values.join(" · ")].filter(Boolean).join(" · ")}</li>;
}

function ReportBody({ report }: { report: Report }) {
  const { input, result } = report;
  if (!input || !result) return null;
  return <div className="space-y-4 text-sm">
    <p className="whitespace-pre-line break-words">{result.summary}</p>
    {([["Điểm nổi bật", result.highlights], ["Cần cải thiện", result.improvements],
      ["Việc nên thử", result.recommendations]] as const).map(([label, items]) => items.length > 0 &&
      <div key={label} className="space-y-2">
        <h4 className="font-semibold">{label}</h4>
        <ul className="list-disc space-y-2 pl-5">
          {items.map((item, index) => <li className="break-words" key={index}>{item.text}
            <ul className="mt-1 space-y-1 text-xs text-muted-foreground">
              {Array.from(new Set(item.evidence)).map(id => <Evidence key={id} id={id} input={input} />)}
            </ul>
          </li>)}
        </ul>
      </div>)}
    <div className="rounded-md bg-muted p-3">
      <h4 className="font-semibold">Giới hạn</h4>
      <ul className="mt-1 list-disc space-y-1 pl-5">
        {result.limitations.map((item, index) => <li className="break-words" key={index}>{item}</li>)}
      </ul>
    </div>
  </div>;
}

export function AnalyticsAnalysisPanel({ active, platform, accountId, pageId, refresh, onOpenAccounts, onOpenAi }: {
  active: boolean; platform: "tiktok" | "facebook"; accountId: string; pageId?: string; refresh?: string | number | null;
  onOpenAccounts?: () => void; onOpenAi?: () => void;
}) {
  const [history, setHistory] = useState<History | null>(null);
  const [selectedId, setSelectedId] = useState("");
  const [page, setPage] = useState(1);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const seq = useRef(0);
  const action = useRef<{ key: string; id: string } | null>(null);
  const mounted = useRef(false);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  const load = useCallback(async () => {
    const current = ++seq.current;
    try {
      const data = await bridgeClient.invoke<History>("analytics.analysis.list", { platform, accountId, pageId, page });
      if (!data || !Array.isArray(data.reports)) throw new Error("Dữ liệu báo cáo AI chưa sẵn sàng.");
      if (current === seq.current) { setHistory(data); setError(""); }
    } catch (err) { if (current === seq.current) setError(errorMessage(err)); }
  }, [platform, accountId, pageId, page]);
  const polling = !!history?.reports.some(r => (isActive(r) && r.reason !== "NoConnectedProvider") ||
    (r.state === "PendingEnqueue" && Date.now() - Date.parse(r.createdAtUtc) < 20000));
  useEffect(() => {
    if (!active) return;
    void load();
    const unsubscribe = bridgeClient.subscribeEvent<{ projectId: string | null }>("job.state_changed", event => {
      if (event.payload.projectId == null) void load();
    });
    return () => { seq.current++; unsubscribe(); };
  }, [active, load, refresh]);
  useEffect(() => {
    if (!active || !polling) return;
    const timer = setInterval(() => void load(), 3000);
    return () => clearInterval(timer);
  }, [active, polling, load]);

  const reports = history?.reports ?? [];
  const selected = reports.find(r => r.id === selectedId) ?? reports[0];
  const run = async (command: "start" | "retry" | "cancel") => {
    if (busy) return;
    const sourceId = command === "start" ? history?.latestSourceId : selected?.sourceId;
    const reportId = command === "start" ? undefined : selected?.id;
    const key = [command, sourceId, reportId].join(":");
    const clientRequestId = action.current?.key === key ? action.current.id : crypto.randomUUID();
    action.current = { key, id: clientRequestId };
    setBusy(true); setError("");
    try {
      const response = await bridgeClient.invoke<{ reportId?: string }>(`analytics.analysis.${command}`,
        { platform, accountId, pageId, sourceId, reportId, clientRequestId });
      if (!mounted.current) return;
      action.current = null;
      if (response.reportId) { setSelectedId(response.reportId); setPage(1); }
      if (page === 1 || !response.reportId) await load();
    } catch (err) { if (mounted.current) setError(errorMessage(err)); }
    finally { if (mounted.current) setBusy(false); }
  };

  const showRetry = selected && !isActive(selected) && selected.reason !== "reconcile_required" && selected.reason !== "NoMetrics";
  const showCancel = selected && (isActive(selected) || selected.state === "AwaitingUser");
  const showStart = history?.latestSourceId && page === 1 && !reports.some(r => r.sourceId === history.latestSourceId);
  return <section aria-label="AI phân tích số liệu" className="space-y-4 rounded-xl border border-border bg-card p-4 sm:p-5">
    <div>
      <h3 className="text-base font-semibold">AI phân tích số liệu</h3>
      <p className="mt-1 text-sm text-muted-foreground">Fraylo gửi bản tóm tắt của mỗi lần cập nhật có số liệu đến AI đã chọn.</p>
    </div>
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {!history && !error && <p role="status" className="text-sm text-muted-foreground">Đang đọc báo cáo đã lưu…</p>}
    {history && !selected && <p className="text-sm text-muted-foreground">Chưa có báo cáo AI cho phạm vi này.</p>}
    {reports.length > 0 && <div className="flex flex-wrap items-center gap-2">
      <label htmlFor={`analysis-history-${platform}`} className="text-sm font-medium">Lịch sử báo cáo</label>
      <select id={`analysis-history-${platform}`} value={selected?.id ?? ""} onChange={e => setSelectedId(e.target.value)}
        className="max-w-full rounded-md border border-input bg-background px-3 py-2 text-sm">
        {reports.map(r => <option key={r.id} value={r.id}>{when(r.createdAtUtc)} · {states[r.state] ?? r.state}</option>)}
      </select>
      {page > 1 && <Button size="sm" variant="outline" onClick={() => setPage(page - 1)}>Trang trước</Button>}
      {history?.hasMore && <Button size="sm" variant="outline" onClick={() => setPage(page + 1)}>Trang sau</Button>}
    </div>}
    {selected && <>
      <p role="status" className="text-sm font-medium">{states[selected.state] ?? selected.state}
        {selected.providerKey && ` · ${selected.providerKey === "chatgpt-web" ? "ChatGPT Web" : "Gemini Web"}`}</p>
      {selected.reason && <p className="text-sm text-muted-foreground">{reasons[selected.reason] ?? "Kiểm tra kết nối và trạng thái tài khoản AI."}</p>}
      {selected.input && <div className="space-y-1 text-xs text-muted-foreground">
        <p>Số liệu lưu lúc {when(selected.input.capturedAt)} · {selected.input.source}</p>
        <p>{selected.input.scope}{selected.input.partial ? " · Dữ liệu một phần" : ""}</p>
        {selected.input.periodStart && <p>Kỳ số liệu {selected.input.periodStart} đến {selected.input.periodEnd ?? "chưa rõ"}</p>}
        <p>Chi tiết {selected.input.totalRecords - selected.input.omittedRecords}/{selected.input.totalRecords} dòng.
          {selected.input.omittedRecords > 0 && " Tổng hợp dùng toàn bộ các dòng có chỉ số phù hợp."}</p>
      </div>}
      <ReportBody report={selected} />
    </>}
    <div className="flex flex-wrap gap-2">
      {showStart && <Button size="sm" variant="outline" disabled={busy} onClick={() => void run("start")}>Phân tích dữ liệu đã lưu</Button>}
      {showRetry && <Button size="sm" variant="outline" disabled={busy} onClick={() => void run("retry")}>
        {selected.state === "PendingEnqueue" || selected.state === "AwaitingUser" ? "Tiếp tục" : "Phân tích lại"}
      </Button>}
      {showCancel && <Button size="sm" variant="outline" disabled={busy} onClick={() => void run("cancel")}>Dừng phân tích</Button>}
      {selected?.reason === "ProviderNotConfigured" && onOpenAi && <Button size="sm" variant="ghost" onClick={onOpenAi}>Chọn AI phân tích</Button>}
      {selected?.reason && loginReasons.includes(selected.reason) && onOpenAccounts &&
        <Button size="sm" variant="ghost" onClick={onOpenAccounts}>Mở tài khoản AI</Button>}
      <Button size="sm" variant="ghost" disabled={busy} onClick={() => void load()}>Tải lại báo cáo</Button>
    </div>
  </section>;
}
