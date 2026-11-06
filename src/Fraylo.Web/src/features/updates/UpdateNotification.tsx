import { useEffect, useState } from "react";
import { Download, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { updatesApi, type UpdateInfo } from "../settings/updatesApi";

export function UpdateNotification({ api = updatesApi }: { api?: typeof updatesApi }) {
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout>;
    const check = async (attempt: number) => {
      try {
        const result = await api.check();
        if (alive && result.hasUpdate) setInfo(result);
      } catch {
        // Host có thể chưa đăng ký handler lúc WebView vừa mở; lỗi mạng không cản người dùng.
        if (alive && attempt < 2) timer = setTimeout(() => void check(attempt + 1), 10000);
      }
    };
    timer = setTimeout(() => void check(0), 2000);
    return () => { alive = false; clearTimeout(timer); };
  }, [api]);
  const apply = async () => {
    if (busy) return;
    setBusy(true); setError(null);
    try { await api.apply(); }
    catch (err) { setError((err as { message?: string })?.message ?? "Không thể cập nhật. Hãy thử lại trong Cài đặt."); }
    finally { setBusy(false); }
  };
  if (!info || dismissed) return null;
  return <>
    <div role="status" className="flex shrink-0 flex-wrap items-center gap-3 border-b border-primary/20 bg-primary/10 px-6 py-3 text-sm">
      <Download className="size-4 shrink-0 text-primary-text" aria-hidden="true" />
      <p className="min-w-0 flex-1">Đã có Fraylo <strong>{info.latestVersion}</strong><span className="ml-2 text-muted-foreground">Bạn đang dùng {info.currentVersion}.</span></p>
      <Button size="sm" onClick={() => setOpen(true)}>Xem bản cập nhật</Button>
      <button type="button" aria-label="Để lần sau" onClick={() => setDismissed(true)} className="inline-flex size-9 items-center justify-center rounded-md hover:bg-muted focus-visible:outline-2 focus-visible:outline-ring"><X className="size-4" aria-hidden="true" /></button>
    </div>
    <Dialog open={open} onOpenChange={value => { if (!busy) setOpen(value); }}>
      <DialogContent onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onPointerDownOutside={event => { if (busy) event.preventDefault(); }}>
        <DialogHeader><DialogTitle>Fraylo {info.latestVersion}</DialogTitle><DialogDescription>Lưu công việc và chờ tác vụ đang chạy hoàn tất trước khi cập nhật. Ứng dụng sẽ đóng để cài phiên bản mới.</DialogDescription></DialogHeader>
        {info.releaseNotes.length > 0 && <ul className="max-h-60 list-disc space-y-2 overflow-y-auto pl-5 text-sm">{info.releaseNotes.map((note, index) => <li key={index}>{note}</li>)}</ul>}
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <DialogFooter><Button variant="outline" disabled={busy} onClick={() => setOpen(false)}>Để sau</Button><Button disabled={busy} onClick={() => void apply()}>{busy ? "Đang tải bản cập nhật…" : "Cập nhật ngay"}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </>;
}
