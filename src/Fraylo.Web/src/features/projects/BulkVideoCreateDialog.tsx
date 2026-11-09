import { useRef, useState } from "react";
import { AlertCircle, CheckCircle2, ImagePlus, Loader2, Plus, Rocket, Trash2, X } from "lucide-react";
import { projectApi } from "./projectApi";
import {
  AudioOptions,
  VideoStrategies,
  videoWorkflowApi,
} from "./videoWorkflowApi";
import { AssetPicker } from "../library/AssetPicker";
import type { AssetItem } from "../library/libraryApi";
import type { ErrorDto } from "../../contracts/bridge";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";

type RowState = "idle" | "creating" | "done" | "error";

interface BulkRow {
  key: string;
  name: string;
  prompt: string;
  productAssetId: string | null;
  projectId?: string;
  state: RowState;
  error?: string;
}

const MAX_ROWS = 50;
const newRow = (name = ""): BulkRow => ({
  key: crypto.randomUUID(),
  name,
  prompt: "",
  productAssetId: null,
  state: "idle",
});

export interface BulkVideoCreateDialogProps {
  isOpen: boolean;
  onClose: () => void;
  /** Gọi khi có ít nhất một video được tạo, để danh sách làm mới. */
  onCreated: () => void;
}

/**
 * Tạo nhiều video trong một màn hình: mỗi dòng là một video (một Project phía sau).
 * Prompt để trống thì dùng mẫu toàn cục; chỉ điền ở dòng nào cần khác.
 */
export function BulkVideoCreateDialog({ isOpen, onClose, onCreated }: BulkVideoCreateDialogProps) {
  const [rows, setRows] = useState<BulkRow[]>(() => [newRow(), newRow(), newRow()]);
  const [pasteText, setPasteText] = useState("");
  const [autoStart, setAutoStart] = useState(true);
  const [modelAssetId, setModelAssetId] = useState<string | null>(null);
  const [durationSeconds, setDurationSeconds] = useState(15);
  const [pickerRowKey, setPickerRowKey] = useState<string | null>(null);
  const [isRunning, setIsRunning] = useState(false);
  const running = useRef(false);

  const filledRows = rows.filter((r) => r.name.trim());
  const doneCount = rows.filter((r) => r.state === "done").length;
  const pending = filledRows.filter((r) => r.state !== "done");

  const patchRow = (key: string, patch: Partial<BulkRow>) =>
    setRows((prev) => prev.map((r) => (r.key === key ? { ...r, ...patch } : r)));

  const addPasted = () => {
    const names = pasteText.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
    if (names.length === 0) return;
    const kept = rows.filter(r => r.state !== "done" && (r.name.trim() || r.prompt || r.productAssetId));
    const available = MAX_ROWS - kept.length;
    setRows([...kept, ...names.slice(0, available).map(newRow)]);
    setPasteText(names.slice(available).join("\n"));
  };

  const createOne = async (row: BulkRow) => {
    if (row.name.trim().length > 100 || (autoStart && !row.projectId && (!modelAssetId || !row.productAssetId))) {
      patchRow(row.key, { state: "error", error: row.name.trim().length > 100
        ? "Tên video tối đa 100 ký tự."
        : "Chọn ảnh mẫu chung và ảnh sản phẩm trước khi chạy." });
      return;
    }
    patchRow(row.key, { state: "creating", error: undefined });
    try {
      const projectId = row.projectId ?? await projectApi.createProject({
        name: row.name.trim(),
      });
      patchRow(row.key, { projectId });
      // Lấy cấu hình mặc định, chỉ ghi đè phần riêng của dòng.
      const cfg = await videoWorkflowApi.getConfig(projectId);
      await videoWorkflowApi.saveConfig({
        projectId,
        workflowMode: cfg.workflowMode,
        goalPrompt: cfg.goalPrompt,
        modelAssetId: modelAssetId ?? cfg.modelAssetId,
        productAssetId: row.productAssetId ?? cfg.productAssetId,
        readyImageAssetId: cfg.readyImageAssetId,
        audioOption: cfg.audioAssetId ? cfg.audioOption : AudioOptions.None,
        audioAssetId: cfg.audioAssetId,
        targetSceneCount: 1,
        targetDurationPerSceneSeconds: durationSeconds,
        scriptGuidance: row.prompt.trim() || cfg.scriptGuidance || null,
        videoGuidance: cfg.videoGuidance ?? null,
        videoStrategy: VideoStrategies.SingleVideo,
      });
      if (autoStart) await videoWorkflowApi.startWorkflow(projectId, row.key);
      patchRow(row.key, { state: "done" });
    } catch (err: unknown) {
      patchRow(row.key, { state: "error", error: (err as ErrorDto)?.message || "Không tạo được video." });
    }
  };

  const handleSubmit = async () => {
    if (running.current) return;
    running.current = true;
    setIsRunning(true);
    try {
      for (const row of pending) await createOne(row);
    } finally {
      running.current = false;
      setIsRunning(false);
    }
    onCreated();
  };

  const picking = rows.find((r) => r.key === pickerRowKey) ?? null;

  return (
    <>
      <Dialog open={isOpen && !pickerRowKey} onOpenChange={(open) => { if (!open && !isRunning) onClose(); }}>
        <DialogContent className="sm:max-w-[820px] max-h-[90vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2"><Rocket className="size-5 text-primary" /> Tạo video hàng loạt</DialogTitle>
            <DialogDescription>
              Mỗi dòng là một video. Để trống prompt thì dùng mẫu toàn cục; chỉ điền ở dòng cần khác.
            </DialogDescription>
          </DialogHeader>

          <div className="flex flex-wrap items-end gap-4">
            <div className="space-y-1.5">
              <Label htmlFor="bulk-duration" className="text-sm font-medium">Thời lượng</Label>
              <select
                id="bulk-duration"
                value={durationSeconds}
                disabled={isRunning}
                onChange={(e) => setDurationSeconds(Number(e.target.value))}
                className="h-9 rounded-md border border-input bg-background px-3 text-sm"
              >
                <option value={10}>10 giây</option>
                <option value={15}>15 giây</option>
                <option value={30}>30 giây</option>
              </select>
            </div>
            <label className="flex items-center gap-2 h-9 text-sm cursor-pointer">
              <Switch checked={autoStart} onCheckedChange={setAutoStart} disabled={isRunning} />
              Chạy ngay
            </label>
            <Button type="button" variant="outline" disabled={isRunning} onClick={() => setPickerRowKey("model")}>
              <ImagePlus className="size-4" /> {modelAssetId ? "Đổi ảnh mẫu chung" : "Chọn ảnh mẫu chung"}
            </Button>
          </div>

          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <Label className="text-sm font-medium">Danh sách video ({filledRows.length})</Label>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                className="h-7 gap-1 text-xs"
                disabled={isRunning || rows.length - doneCount >= MAX_ROWS}
                onClick={() => setRows((prev) => [...prev.filter(row => row.state !== "done"), newRow()])}
              >
                <Plus className="size-3.5" /> Thêm dòng
              </Button>
            </div>

            <ul className="space-y-2" aria-label="Danh sách video cần tạo">
              {rows.map((row, i) => (
                <li
                  key={row.key}
                  className="grid gap-2 rounded-lg border bg-card p-2.5 sm:grid-cols-[28px_1fr_1.4fr_auto_auto] sm:items-start"
                >
                  <span className="hidden sm:flex size-7 items-center justify-center rounded-full bg-muted text-xs font-semibold text-muted-foreground">
                    {i + 1}
                  </span>
                  <Input
                    value={row.name}
                    disabled={isRunning || row.state === "done" || !!row.projectId}
                    placeholder="Tên video / sản phẩm"
                    aria-label={`Tên video ${i + 1}`}
                    onChange={(e) => patchRow(row.key, { name: e.target.value })}
                  />
                  <Textarea
                    value={row.prompt}
                    rows={1}
                    disabled={isRunning || row.state === "done"}
                    placeholder="Prompt riêng (trống = dùng mẫu toàn cục)"
                    aria-label={`Prompt riêng video ${i + 1}`}
                    className="min-h-9 resize-y"
                    onChange={(e) => patchRow(row.key, { prompt: e.target.value })}
                  />
                  <div className="flex items-center gap-1">
                    <Button
                      type="button"
                      variant={row.productAssetId ? "secondary" : "outline"}
                      size="sm"
                      className="h-9 gap-1.5 text-xs"
                      disabled={isRunning || row.state === "done"}
                      onClick={() => setPickerRowKey(row.key)}
                    >
                      <ImagePlus className="size-3.5" />
                      {row.productAssetId ? "Đã chọn ảnh" : "Thêm ảnh"}
                    </Button>
                    {row.productAssetId && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        className="size-8"
                        aria-label="Bỏ ảnh riêng"
                        disabled={isRunning}
                        onClick={() => patchRow(row.key, { productAssetId: null })}
                      >
                        <X className="size-3.5" />
                      </Button>
                    )}
                  </div>
                  <div className="flex items-center justify-end gap-1 sm:min-w-[84px]">
                    {row.state === "creating" && <Loader2 className="size-4 animate-spin text-primary" aria-label="Đang tạo" />}
                    {row.state === "done" && <CheckCircle2 className="size-4 text-success" aria-label="Đã tạo" />}
                    {row.state === "error" && <AlertCircle className="size-4 text-destructive" aria-label="Lỗi" />}
                    {row.state !== "done" && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        className="size-8 text-muted-foreground"
                        aria-label={`Xóa dòng ${i + 1}`}
                        disabled={isRunning}
                        onClick={() => setRows((prev) => prev.filter((r) => r.key !== row.key))}
                      >
                        <Trash2 className="size-3.5" />
                      </Button>
                    )}
                  </div>
                  {row.error && (
                    <p role="alert" className="text-xs text-destructive sm:col-span-5">
                      {row.projectId && "Đã lưu dự án. Thử lại sẽ dùng dự án này. "}{row.error}
                    </p>
                  )}
                </li>
              ))}
            </ul>

            <details className="rounded-lg border bg-muted/20 px-3 py-2 text-sm">
              <summary className="cursor-pointer font-medium text-muted-foreground">Dán nhanh nhiều tên (mỗi dòng một video)</summary>
              <p className="mt-1 text-xs text-muted-foreground">Tối đa {MAX_ROWS} dòng mỗi lô. Tên còn dư giữ trong ô dán. Bấm Thêm sau khi chạy lô để tiếp tục.</p>
              <div className="mt-2 flex gap-2">
                <Textarea
                  value={pasteText}
                  rows={4}
                  disabled={isRunning}
                  placeholder={"Váy hoa nhí\nÁo sơ mi lụa\nChân váy xếp ly"}
                  aria-label="Dán danh sách tên video"
                  onChange={(e) => setPasteText(e.target.value)}
                />
                <Button type="button" variant="outline" className="self-start" disabled={!pasteText.trim() || isRunning} onClick={addPasted}>
                  Thêm
                </Button>
              </div>
            </details>
          </div>

          <DialogFooter className="items-center gap-2 sm:justify-between">
            <span className="text-xs text-muted-foreground">
              {doneCount > 0 ? `Đã tạo ${doneCount}/${filledRows.length}. ` : ""}
              {autoStart ? "Video sẽ được đưa vào hàng đợi ngay." : "Video chỉ được tạo, chưa chạy."}
            </span>
            <div className="flex gap-2">
              <Button type="button" variant="outline" disabled={isRunning} onClick={onClose}>
                Đóng
              </Button>
              <Button type="button" className="gap-2" disabled={isRunning || pending.length === 0} onClick={() => void handleSubmit()}>
                {isRunning ? <Loader2 className="size-4 animate-spin" /> : <Rocket className="size-4" />}
                {isRunning ? "Đang tạo…" : `Tạo ${pending.length} video`}
              </Button>
            </div>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {isOpen && pickerRowKey && (
        <AssetPicker
          assetType="Image"
          onClose={() => setPickerRowKey(null)}
          onSelect={(asset: AssetItem) => {
            if (pickerRowKey === "model") setModelAssetId(asset.assetId);
            else if (picking) patchRow(picking.key, { productAssetId: asset.assetId });
            setPickerRowKey(null);
          }}
        />
      )}
    </>
  );
}
