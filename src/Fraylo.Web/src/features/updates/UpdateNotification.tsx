import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { updatesApi, type UpdateInfo } from "../settings/updatesApi";

export function UpdateNotification({ api = updatesApi }: { api?: typeof updatesApi }) {
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout>;
    const check = async (attempt: number) => {
      try {
        const result = await api.check();
        if (alive && result.hasUpdate) {
          setInfo(result);
          setOpen(true);
        }
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
  if (!info) return null;
  return <>
    <Dialog open={open} onOpenChange={value => { if (!busy) setOpen(value); }}>
      <DialogContent onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onPointerDownOutside={event => { if (busy) event.preventDefault(); }}>
        <DialogHeader><DialogTitle>Đã có Fraylo {info.latestVersion}</DialogTitle><DialogDescription>Bạn đang dùng {info.currentVersion}. Lưu công việc và chờ tác vụ đang chạy hoàn tất trước khi cập nhật. Ứng dụng sẽ đóng để cài phiên bản mới.</DialogDescription></DialogHeader>
        {info.releaseNotes.length > 0 && <ul className="max-h-60 list-disc space-y-2 overflow-y-auto pl-5 text-sm">{info.releaseNotes.map((note, index) => <li key={index}>{note}</li>)}</ul>}
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <DialogFooter><Button variant="outline" disabled={busy} onClick={() => setOpen(false)}>Để sau</Button><Button disabled={busy} onClick={() => void apply()}>{busy ? "Đang tải bản cập nhật…" : "Cập nhật ngay"}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </>;
}
