import { useEffect, useId, useRef, useState } from "react";
import { Heart, Loader2, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { publishingError, type FavoriteSoundsResponse, type PublishingApi } from "./publishingApi";

interface FavoriteSoundPickerProps {
  api: PublishingApi;
  projectId: string;
  assetId: string;
  accountId: string;
  connected: boolean;
  disabled: boolean;
  value: string;
  onChange: (key: string) => void;
  onBusyChange: (busy: boolean) => void;
}

/** Caller dùng key account + asset để loại dữ liệu/response cũ khi đổi lựa chọn. */
export function FavoriteSoundPicker({ api, projectId, assetId, accountId, connected, disabled, value, onChange, onBusyChange }: FavoriteSoundPickerProps) {
  const id = useId();
  const [data, setData] = useState<FavoriteSoundsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  const busy = useRef(false);
  const notifyBusy = useRef(onBusyChange);
  notifyBusy.current = onBusyChange;
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; if (busy.current) notifyBusy.current(false); };
  }, []);

  const load = async () => {
    if (busy.current || disabled || !connected || !assetId || !accountId) return;
    busy.current = true; setLoading(true); setError(null); setData(null); onChange(""); notifyBusy.current(true);
    try {
      const next = await api.favoriteSounds({ projectId, assetId, accountId });
      if (!mounted.current) return;
      if (next.accountId !== accountId) throw new Error("Danh sách nhạc không khớp tài khoản đã chọn. Hãy tải lại.");
      setData(next);
    } catch (err) { if (mounted.current) setError(publishingError(err)); }
    finally {
      busy.current = false;
      if (mounted.current) { setLoading(false); notifyBusy.current(false); }
    }
  };

  return <div className="space-y-2 rounded-lg border p-3" aria-busy={loading}>
    <div className="flex flex-wrap items-center justify-between gap-2">
      <span id={`${id}-label`} className="flex items-center gap-2 text-sm font-medium"><Heart className="size-4 text-muted-foreground" aria-hidden="true" />Nhạc yêu thích</span>
      <Button type="button" size="sm" variant="outline" className="gap-2" disabled={disabled || loading || !connected || !assetId || !accountId} onClick={() => void load()}>
        {loading ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : <RefreshCw className="size-4" aria-hidden="true" />}
        {loading ? "Đang tải nhạc…" : data ? "Tải lại nhạc" : "Tải nhạc yêu thích"}
      </Button>
    </div>
    {!accountId || !assetId ? <p className="text-xs text-muted-foreground">Chọn video và tài khoản để tải nhạc yêu thích.</p>
      : !connected ? <p className="text-xs text-muted-foreground">Đăng nhập tài khoản TikTok trong Cài đặt để tải nhạc.</p>
      : <p className="text-xs leading-relaxed text-muted-foreground">Lấy Favorites của tài khoản đã chọn. TikTok cần tải video vào trình chỉnh sửa để đọc danh sách; thao tác này không đăng bài.</p>}
    {loading && <p role="status" className="text-xs text-muted-foreground">Đang mở trình chỉnh sửa TikTok và đọc nhạc yêu thích…</p>}
    {error && <p role="alert" className="break-words text-sm text-destructive">{error}</p>}
    {data && <>
      <p role="status" className="text-xs text-muted-foreground">{data.sounds.length ? `${data.sounds.length} bài yêu thích` : "Chưa có nhạc yêu thích khả dụng trên TikTok web. Lưu bài trên TikTok rồi tải lại."}</p>
      {!data.isComplete && <p className="text-xs text-muted-foreground">Danh sách chưa đầy đủ do đạt giới hạn tải. Chỉ các bài bên dưới đã được đọc.</p>}
      {!!data.sounds.length && <div role="radiogroup" aria-labelledby={`${id}-label`} className="max-h-64 space-y-1 overflow-y-auto">
        <label className="flex min-h-10 cursor-pointer items-center gap-2 rounded-md p-2 text-sm hover:bg-muted/50">
          <input type="radio" name={id} checked={!value} disabled={disabled || loading} onChange={() => onChange("")} />Không thêm nhạc yêu thích
        </label>
        {data.sounds.map(sound => <label key={sound.key} className={`flex min-h-11 cursor-pointer items-start gap-2 rounded-md p-2 text-sm hover:bg-muted/50 ${value === sound.key ? "bg-primary/10" : ""}`}>
          <input type="radio" name={id} value={sound.key} checked={value === sound.key} disabled={disabled || loading} onChange={() => onChange(sound.key)} className="mt-1 shrink-0" />
          <span className="min-w-0"><span className="block break-words font-medium">{sound.title}</span><span className="block break-words text-xs text-muted-foreground">{sound.duration} · {sound.author}</span></span>
        </label>)}
      </div>}
    </>}
  </div>;
}
