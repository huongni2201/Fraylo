import { useCallback, useEffect, useRef, useState } from "react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { FacebookPosts } from "./FacebookPosts";
import { FacebookDrafts } from "./FacebookDrafts";
import { facebookApi, facebookError, type FacebookApi, type FacebookDraft, type FacebookList, type FacebookPost } from "./facebookApi";

const empty: FacebookList = { accounts: [], groups: [], tasks: [], posts: [] };

export function FacebookQueue({ active, history, drafts = false, onHistoryChange, onClone, onOpenDraft, api = facebookApi }: {
  active: boolean;
  history: boolean;
  drafts?: boolean;
  onOpenDraft?: (draft: FacebookDraft) => void;
  onHistoryChange: (history: boolean) => void;
  onClone: (post: FacebookPost) => void;
  api?: FacebookApi;
}) {
  const [data, setData] = useState(empty);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const generation = useRef(0);
  const load = useCallback(async () => {
    const version = ++generation.current;
    setLoading(true);
    try {
      const next = await api.list();
      if (version !== generation.current) return;
      setData(next); setLoaded(true);
    } catch (err) {
      if (version === generation.current) setError(facebookError(err));
    } finally {
      if (version === generation.current) setLoading(false);
    }
  }, [api]);
  useEffect(() => {
    if (!active) return;
    void load();
    const timer = setInterval(() => void load(), 5000);
    return () => { clearInterval(timer); generation.current++; };
  }, [active, load]);

  return <section className="mx-auto flex max-w-6xl flex-col gap-5" aria-label="Hàng đợi Facebook chung">
    <PageHeader title={drafts ? "Nháp Facebook" : history ? "Lịch sử Facebook" : "Hàng đợi Facebook"} description="Quản lý bài đăng nhóm và trang cá nhân của tất cả tài khoản Facebook."
      actions={<Button variant="outline" disabled={loading} onClick={() => { setError(null); void load(); }}>Cập nhật trạng thái</Button>} />
    {!drafts && <nav className="flex flex-wrap gap-2" aria-label="Hàng đợi và lịch sử Facebook">
      <Button variant={!history ? "default" : "outline"} aria-current={!history ? "page" : undefined} onClick={() => onHistoryChange(false)}>Đang chờ xử lý</Button>
      <Button variant={history ? "default" : "outline"} aria-current={history ? "page" : undefined} onClick={() => onHistoryChange(true)}>Lịch sử</Button>
    </nav>}
    <p className="text-sm text-muted-foreground">Đăng bài Fanpage chưa được hỗ trợ.</p>
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {!loaded && loading && <p role="status">Đang tải hàng đợi…</p>}
    {loaded && <>
      <div hidden={drafts}><FacebookPosts data={data} api={api} history={history} onRefresh={load} onError={setError} onClone={onClone} /></div>
      <div hidden={!drafts}><FacebookDrafts data={data} api={api} onOpen={draft => onOpenDraft?.(draft)} onRefresh={load} onError={setError} /></div>
    </>}
  </section>;
}
