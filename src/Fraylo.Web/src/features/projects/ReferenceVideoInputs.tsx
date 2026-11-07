import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { AssetPicker } from "../library/AssetPicker";
import { AssetMediaPreview } from "../library/AssetMediaPreview";
import { uploadAsset, type AssetItem } from "../library/libraryApi";
import type { ProjectWorkflowConfigDto } from "./videoWorkflowApi";

export function ReferenceVideoInputs({ config, disabled, onChange }: {
  config: ProjectWorkflowConfigDto;
  disabled: boolean;
  onChange: (patch: Partial<ProjectWorkflowConfigDto>) => void;
}) {
  const [picker, setPicker] = useState<"Image" | "Video" | null>(null);
  const [uploading, setUploading] = useState<"Image" | "Video" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const select = (asset: AssetItem) => {
    if (asset.assetType === "Video") onChange({ referenceVideoAssetId: asset.assetId });
    else if (asset.assetType === "Image") onChange({ readyImageAssetId: asset.assetId });
  };
  const upload = async (type: "Image" | "Video") => {
    setUploading(type); setError(null);
    try {
      const result = await uploadAsset({ projectId: config.projectId, assetType: type });
      if (alive.current && result.asset) select(result.asset);
    } catch (e) { if (alive.current) setError(e instanceof Error ? e.message : "Không thể nhập tệp."); }
    finally { if (alive.current) setUploading(null); }
  };
  return <section aria-label="Dữ liệu video theo mẫu" className="space-y-4">
    <div className="rounded-lg border border-border bg-muted/30 p-4 text-sm space-y-2">
      <p className="font-semibold">MuseAI · Chuyển động gần đúng</p>
      <p>Giữ người và bộ đồ trong ảnh. Phân tích video thành prompt để mô phỏng động tác; không chuyển động chính xác từng khung hình.</p>
      <p>Thời lượng có thể khác video mẫu. Video mẫu được gửi kèm cả âm thanh để MuseAI tự tách, ghép và co giãn theo độ dài video mới; không thay bằng nhạc khác. Bạn nghe lại để duyệt nhạc và nhịp sau khi tạo. Mẫu không có âm thanh thì không thêm nhạc.</p>
      <p className="text-xs text-muted-foreground">Video mẫu: MP4, tối đa 30 giây, 50 MB và 4096 px. Hai tệp được lưu trong Thư viện.</p>
    </div>
    {!config.referenceVideoAvailable && <div role="status" className="rounded-lg border border-warning/30 bg-warning/10 p-3 text-sm">
      {config.referenceVideoUnavailableReason || "MuseAI chưa xác minh tự ghép âm thanh cho mẫu này. Bạn có thể lưu nháp đầu vào."}
    </div>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    <fieldset disabled={disabled || uploading !== null} className="grid gap-4 sm:grid-cols-2">
      {(["Image", "Video"] as const).map(type => {
        const id = type === "Image" ? config.readyImageAssetId : config.referenceVideoAssetId;
        const label = type === "Image" ? "Ảnh người mẫu mặc sản phẩm" : "Video mẫu chuyển động và âm thanh";
        return <div key={type} className="space-y-3 rounded-lg border border-border p-4">
          <h3 className="text-sm font-semibold">{label} (bắt buộc)</h3>
          {id ? <AssetMediaPreview key={id} assetId={id} assetType={type} fileName={label} heightClass="h-56" />
            : <div className="h-32 flex items-center justify-center rounded bg-muted text-sm text-muted-foreground">Chưa chọn {type === "Image" ? "ảnh" : "video"}</div>}
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" onClick={() => upload(type)}>{uploading === type ? "Đang nhập…" : type === "Image" ? "Tải ảnh từ máy" : "Tải video từ máy"}</Button>
            <Button type="button" variant="outline" onClick={() => setPicker(type)}>Chọn {type === "Image" ? "ảnh" : "video"} từ Thư viện</Button>
            {id && <Button type="button" variant="ghost" aria-label={`Gỡ ${type === "Image" ? "ảnh" : "video"} mẫu`} onClick={() => onChange(type === "Image" ? { readyImageAssetId: null } : { referenceVideoAssetId: null })}>Gỡ</Button>}
          </div>
        </div>;
      })}
    </fieldset>
    <div className="space-y-2 text-sm">
      <label htmlFor="reference-target-duration" className="block font-medium">Thời lượng video mục tiêu</label>
      <select id="reference-target-duration" aria-describedby="reference-duration-help"
        className="h-9 w-full rounded-md border border-input bg-background px-3 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring sm:w-48"
        disabled={disabled} value={config.targetDurationPerSceneSeconds}
        onChange={e => onChange({ targetDurationPerSceneSeconds: Number(e.target.value) })}>
        {[10, 15, 20, 30].map(seconds => <option key={seconds} value={seconds}>{seconds} giây</option>)}
      </select>
      <p id="reference-duration-help" className="text-xs text-muted-foreground">Thời lượng thực tế có thể chênh lệch tùy kết quả MuseAI.</p>
    </div>
    <label className="block space-y-2 text-sm font-medium">
      <span>Yêu cầu bổ sung (tùy chọn)</span>
      <textarea className="w-full rounded-md border border-input bg-background p-3 text-sm" rows={3} disabled={disabled}
        value={config.videoGuidance ?? ""} onChange={e => onChange({ videoGuidance: e.target.value })} placeholder="Ví dụ: giữ màu sắc và phom dáng bộ đồ." />
    </label>
    {picker && <AssetPicker key={`${config.projectId}-${picker}`} projectId={config.projectId} includeShared assetType={picker} onSelect={select} onClose={() => setPicker(null)} />}
  </section>;
}
