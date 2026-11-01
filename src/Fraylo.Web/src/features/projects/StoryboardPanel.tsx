import { useCallback, useEffect, useState } from "react";
import { Button } from "../../components/ui/button";
import { Card } from "../../components/ui/card";
import { videoWorkflowApi, type StoryboardContent } from "./videoWorkflowApi";
import { AssetPreviewDialog } from "../library/AssetPreviewDialog";

export function StoryboardPanel({ projectId }: { projectId: string }) {
  const [result, setResult] = useState<{ projectId: string; storyboard: StoryboardContent | null; error: string | null } | null>(null);
  const [revision, setRevision] = useState(0);
  const [previewAssetId, setPreviewAssetId] = useState<string | null>(null);
  const refresh = useCallback(() => setRevision((value) => value + 1), []);

  useEffect(() => {
    let current = true;
    videoWorkflowApi.getStoryboard(projectId).then(
      (storyboard) => { if (current) setResult({ projectId, storyboard, error: null }); },
      () => { if (current) setResult({ projectId, storyboard: null, error: "Không thể tải kịch bản. Vui lòng thử lại." }); },
    );
    return () => { current = false; };
  }, [projectId, revision]);

  const data = result?.projectId === projectId ? result : null;
  return (
    <Card className="p-5 space-y-4" aria-label="Kịch bản nhiều phân cảnh">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-base font-semibold">Kịch bản video</h2>
          <p className="text-sm text-muted-foreground">Các phân cảnh trong cùng một video.</p>
        </div>
        <Button variant="outline" size="sm" onClick={refresh}>Làm mới kịch bản</Button>
      </div>
      {!data ? <p className="text-sm text-muted-foreground">Đang tải kịch bản…</p>
        : data.error ? <p role="alert" className="text-sm text-destructive">{data.error}</p>
        : !data.storyboard ? <p className="text-sm text-muted-foreground">Chưa có kịch bản nhiều phân cảnh. Tính năng tự tạo kịch bản đang được phát triển.</p>
        : <>
          <p className="text-sm text-muted-foreground">Thời lượng: {data.storyboard.durationSeconds} giây · {data.storyboard.shots.length} phân cảnh</p>
          <ol className="space-y-3">
            {data.storyboard.shots.map((shot, index) => (
              <li key={`${shot.startSeconds}-${shot.endSeconds}`} className="rounded-md border p-4 flex flex-col sm:flex-row gap-4">
                <div className="w-full sm:w-32 shrink-0 rounded-md bg-muted p-3 flex items-center justify-center text-center text-sm text-muted-foreground">
                  {shot.imageAssetId ? <Button type="button" variant="outline" size="sm" onClick={() => setPreviewAssetId(shot.imageAssetId!)}>Xem thông tin ảnh</Button> : "Chưa có ảnh"}
                </div>
                <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2 text-sm font-medium">
                  <span>Phân cảnh {index + 1}</span>
                  <span className="text-muted-foreground">{shot.startSeconds}–{shot.endSeconds} giây</span>
                </div>
                <p className="mt-2 text-sm whitespace-pre-wrap">{shot.description}</p>
                <p className="mt-2 text-sm text-muted-foreground"><span className="font-medium">Yêu cầu ảnh: </span>{shot.imagePrompt}</p>
                </div>
              </li>
            ))}
          </ol>
        </>}
      {previewAssetId && <AssetPreviewDialog assetId={previewAssetId} onClose={() => setPreviewAssetId(null)} />}
    </Card>
  );
}
