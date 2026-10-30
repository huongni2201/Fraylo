import { useEffect, useState } from "react";
import { projectGroupApi, type ProjectGroupDto } from "./projectApi";
import { AssetPicker } from "../library/AssetPicker";
import { listAssets } from "../library/libraryApi";
import type { ErrorDto } from "../../contracts/bridge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { AlertCircle, Loader2, Plus, RotateCcw, Trash2, X } from "lucide-react";
import { cn } from "@/lib/utils";

const MAX_GUIDANCE_LENGTH = 4000;

type AssetSlotField = "modelAssetId" | "productAssetId" | "readyImageAssetId" | "audioAssetId";

const ASSET_SLOTS: { field: AssetSlotField; label: string; assetType: "Image" | "Audio" }[] = [
  { field: "modelAssetId", label: "Ảnh mẫu người", assetType: "Image" },
  { field: "productAssetId", label: "Ảnh sản phẩm", assetType: "Image" },
  { field: "readyImageAssetId", label: "Ảnh có sẵn", assetType: "Image" },
  { field: "audioAssetId", label: "Âm thanh", assetType: "Audio" },
];

interface VariableRow {
  id: number;
  key: string;
  value: string;
}

let variableRowSeq = 0;
const newVariableRow = (key = "", value = ""): VariableRow => ({ id: ++variableRowSeq, key, value });

interface GroupDraft {
  name: string;
  description: string;
  scriptGuidance: string;
  videoGuidance: string;
  variables: VariableRow[];
  modelAssetId: string | null;
  productAssetId: string | null;
  readyImageAssetId: string | null;
  audioAssetId: string | null;
}

function toDraft(group: ProjectGroupDto): GroupDraft {
  return {
    name: group.name,
    description: group.description,
    scriptGuidance: group.scriptGuidance,
    videoGuidance: group.videoGuidance,
    variables: Object.entries(group.promptVariables).map(([key, value]) => newVariableRow(key, value)),
    modelAssetId: group.modelAssetId ?? null,
    productAssetId: group.productAssetId ?? null,
    readyImageAssetId: group.readyImageAssetId ?? null,
    audioAssetId: group.audioAssetId ?? null,
  };
}

/** So sánh nội dung, bỏ qua id dòng biến để biết còn thay đổi chưa lưu. */
function snapshotOf(draft: GroupDraft): string {
  return JSON.stringify({ ...draft, variables: draft.variables.map(({ key, value }) => [key, value]) });
}

/** Dòng trống bị bỏ qua; dòng có giá trị nhưng thiếu tên hoặc trùng tên là lỗi. */
function collectVariables(rows: VariableRow[]): { variables: Record<string, string>; error: string | null } {
  const variables: Record<string, string> = {};
  for (const row of rows) {
    const key = row.key.trim();
    const value = row.value.trim();
    if (!key && !value) continue;
    if (!key) return { variables: {}, error: "Có biến chưa đặt tên." };
    if (Object.prototype.hasOwnProperty.call(variables, key)) {
      return { variables: {}, error: `Biến "${key}" bị khai báo hai lần.` };
    }
    variables[key] = value;
  }
  return { variables, error: null };
}

function fileNameOf(relativePath: string): string {
  return relativePath.split("/").pop() || relativePath;
}

interface GuidanceFieldProps {
  id: string;
  label: string;
  hint: string;
  value: string;
  onChange: (value: string) => void;
}

function GuidanceField({ id, label, hint, value, onChange }: GuidanceFieldProps) {
  const tooLong = value.length > MAX_GUIDANCE_LENGTH;
  return (
    <div className="space-y-1.5">
      <div className="flex items-baseline justify-between gap-2">
        <Label htmlFor={id} className="text-sm font-medium">{label}</Label>
        <span className={cn("text-xs tabular-nums", tooLong ? "text-destructive" : "text-muted-foreground")}>
          {value.length}/{MAX_GUIDANCE_LENGTH}
        </span>
      </div>
      <Textarea
        id={id}
        rows={9}
        className="resize-y leading-relaxed"
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
      <p className="text-xs text-muted-foreground">{hint}</p>
    </div>
  );
}

export interface ProjectGroupSettingsProps {
  group: ProjectGroupDto;
  /** Gọi sau khi lưu để trang nhóm tải lại thông tin nhóm. */
  onSaved: () => Promise<void>;
  /** Gọi sau khi xóa nhóm; trang nhóm tự rời khỏi nhóm đó. */
  onDeleted: () => void;
}

export function ProjectGroupSettings({ group, onSaved, onDeleted }: ProjectGroupSettingsProps) {
  const [saved, setSaved] = useState<GroupDraft>(() => toDraft(group));
  const [draft, setDraft] = useState<GroupDraft>(() => toDraft(group));
  const [isSaving, setIsSaving] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [assetNames, setAssetNames] = useState<Record<string, string>>({});
  const [pickerSlot, setPickerSlot] = useState<AssetSlotField | null>(null);

  const isDirty = snapshotOf(draft) !== snapshotOf(saved);
  const pickerSpec = pickerSlot ? ASSET_SLOTS.find((slot) => slot.field === pickerSlot) : undefined;

  /** Tên tệp chỉ để hiển thị: lấy từ danh sách tệp của nhóm. */
  useEffect(() => {
    listAssets({ groupId: group.groupId, pageSize: 100 })
      .then((res) => {
        setAssetNames(Object.fromEntries(res.assets.map((asset) => [asset.assetId, fileNameOf(asset.relativePath)])));
      })
      .catch((err: unknown) => {
        console.error("[ProjectGroupSettings] Không tải được tên tệp của nhóm:", err);
      });
  }, [group.groupId]);

  const updateDraft = (patch: Partial<GroupDraft>) => {
    setDraft((prev) => ({ ...prev, ...patch }));
  };

  const updateVariable = (id: number, patch: Partial<Pick<VariableRow, "key" | "value">>) => {
    setDraft((prev) => ({
      ...prev,
      variables: prev.variables.map((row) => (row.id === id ? { ...row, ...patch } : row)),
    }));
  };

  const removeVariable = (id: number) => {
    setDraft((prev) => ({ ...prev, variables: prev.variables.filter((row) => row.id !== id) }));
  };

  const handleSave = async () => {
    if (isSaving || !isDirty) return;
    const name = draft.name.trim();
    const { variables, error } = collectVariables(draft.variables);
    if (!name) {
      setErrorMessage("Vui lòng nhập tên nhóm.");
      return;
    }
    if (error) {
      setErrorMessage(error);
      return;
    }
    if (draft.scriptGuidance.length > MAX_GUIDANCE_LENGTH || draft.videoGuidance.length > MAX_GUIDANCE_LENGTH) {
      setErrorMessage(`Hướng dẫn không được vượt quá ${MAX_GUIDANCE_LENGTH} ký tự.`);
      return;
    }

    setIsSaving(true);
    setErrorMessage(null);
    try {
      const next = toDraft(
        await projectGroupApi.saveGroup({
          groupId: group.groupId,
          name,
          description: draft.description.trim(),
          scriptGuidance: draft.scriptGuidance.trim(),
          videoGuidance: draft.videoGuidance.trim(),
          promptVariables: variables,
          modelAssetId: draft.modelAssetId,
          productAssetId: draft.productAssetId,
          readyImageAssetId: draft.readyImageAssetId,
          audioAssetId: draft.audioAssetId,
        }),
      );
      setSaved(next);
      setDraft(next);
      await onSaved();
    } catch (err: unknown) {
      setErrorMessage((err as ErrorDto)?.message || "Không lưu được cấu hình nhóm. Vui lòng thử lại.");
    } finally {
      setIsSaving(false);
    }
  };

  const handleDelete = async () => {
    if (isSaving) return;
    if (!window.confirm(`Xóa nhóm "${saved.name}"? Các dự án trong nhóm được giữ lại và chuyển về không nhóm.`)) {
      return;
    }

    setIsSaving(true);
    setErrorMessage(null);
    try {
      await projectGroupApi.deleteGroup(group.groupId);
      onDeleted();
    } catch (err: unknown) {
      setErrorMessage((err as ErrorDto)?.message || "Không xóa được nhóm. Vui lòng thử lại.");
      setIsSaving(false);
    }
  };

  return (
    <div className="space-y-6 pb-2">
      {errorMessage && (
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>{errorMessage}</AlertDescription>
        </Alert>
      )}

      <fieldset disabled={isSaving} className="grid min-w-0 grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="min-w-0 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>Hướng dẫn prompt</CardTitle>
              <CardDescription>
                Nối sau mẫu chung của app. Mọi dự án trong nhóm đều nhận hướng dẫn này; dự án có thể thêm phần riêng.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <GuidanceField
                id="grp-script"
                label="Hướng dẫn kịch bản"
                hint="Dùng cho bước sinh kịch bản. Không dùng dấu ngoặc nhọn { }; biến được điền trong mẫu chung."
                value={draft.scriptGuidance}
                onChange={(scriptGuidance) => updateDraft({ scriptGuidance })}
              />
              <GuidanceField
                id="grp-video"
                label="Hướng dẫn prompt video"
                hint="Dùng cho bước sinh prompt video. Không dùng dấu ngoặc nhọn { }."
                value={draft.videoGuidance}
                onChange={(videoGuidance) => updateDraft({ videoGuidance })}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Biến mặc định</CardTitle>
              <CardDescription>
                Giá trị điền vào biến trong mẫu chung, ví dụ chất liệu hay kiểu dáng. Dự án có thể ghi đè từng biến.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {draft.variables.length === 0 && (
                <p className="text-sm text-muted-foreground">Chưa có biến mặc định.</p>
              )}
              {draft.variables.map((row) => (
                <div key={row.id} className="flex items-center gap-2">
                  <Input
                    aria-label="Tên biến"
                    className="h-9 w-40 shrink-0 font-mono text-xs"
                    placeholder="chat_lieu"
                    maxLength={60}
                    value={row.key}
                    onChange={(e) => updateVariable(row.id, { key: e.target.value })}
                  />
                  <Input
                    aria-label="Giá trị biến"
                    className="h-9 min-w-0 flex-1"
                    placeholder="cotton"
                    maxLength={500}
                    value={row.value}
                    onChange={(e) => updateVariable(row.id, { value: e.target.value })}
                  />
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    className="size-9 shrink-0 text-muted-foreground hover:text-destructive"
                    aria-label="Xóa biến"
                    onClick={() => removeVariable(row.id)}
                  >
                    <X className="size-4" />
                  </Button>
                </div>
              ))}
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="gap-1.5"
                onClick={() => updateDraft({ variables: [...draft.variables, newVariableRow()] })}
              >
                <Plus className="size-3.5" /> Thêm biến
              </Button>
            </CardContent>
          </Card>
        </div>

        <div className="min-w-0 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>Thông tin nhóm</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-1.5">
                <Label htmlFor="grp-name" className="text-sm font-medium">
                  Tên nhóm <span className="text-destructive">*</span>
                </Label>
                <Input
                  id="grp-name"
                  value={draft.name}
                  maxLength={100}
                  onChange={(e) => updateDraft({ name: e.target.value })}
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="grp-desc" className="text-sm font-medium">Mô tả</Label>
                <Input
                  id="grp-desc"
                  value={draft.description}
                  maxLength={500}
                  onChange={(e) => updateDraft({ description: e.target.value })}
                />
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Tệp dùng chung</CardTitle>
              <CardDescription>
                Dự án để trống ô sẽ dùng tệp của nhóm. Tệp của dự án được chọn sẽ chuyển thành tệp của nhóm.
              </CardDescription>
            </CardHeader>
            <CardContent className="divide-y divide-border">
              {ASSET_SLOTS.map((slot) => {
                const assetId = draft[slot.field];
                return (
                  <div key={slot.field} className="flex items-center justify-between gap-3 py-3 first:pt-0 last:pb-0">
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-foreground">{slot.label}</p>
                      <p
                        className="truncate text-xs text-muted-foreground"
                        title={assetId ? assetNames[assetId] : undefined}
                      >
                        {assetId ? (assetNames[assetId] ?? "Đã chọn") : "Chưa chọn"}
                      </p>
                    </div>
                    <div className="flex shrink-0 items-center gap-1">
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        className="h-7 px-2.5 text-xs"
                        onClick={() => setPickerSlot(slot.field)}
                      >
                        {assetId ? "Đổi" : "Chọn"}
                      </Button>
                      {assetId && (
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          className="h-7 px-2.5 text-xs"
                          onClick={() => updateDraft({ [slot.field]: null } as Partial<GroupDraft>)}
                        >
                          Bỏ
                        </Button>
                      )}
                    </div>
                  </div>
                );
              })}
            </CardContent>
          </Card>

          <Card className="border-destructive/40">
            <CardHeader>
              <CardTitle className="text-destructive">Xóa nhóm</CardTitle>
              <CardDescription>
                Các dự án trong nhóm được giữ lại và chuyển về không nhóm. Tệp của nhóm trở thành thư viện chung.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Button type="button" variant="destructive" className="gap-1.5" onClick={handleDelete}>
                <Trash2 className="size-4" /> Xóa nhóm
              </Button>
            </CardContent>
          </Card>
        </div>
      </fieldset>

      <div className="sticky bottom-0 -mx-1 flex items-center justify-between gap-3 border-t border-border bg-background/95 px-1 py-3 backdrop-blur">
        <span className="text-xs text-muted-foreground">{isDirty ? "Có thay đổi chưa lưu" : "Đã lưu"}</span>
        <div className="flex items-center gap-2">
          <Button
            type="button"
            variant="ghost"
            className="gap-1.5"
            disabled={!isDirty || isSaving}
            onClick={() => {
              setDraft(saved);
              setErrorMessage(null);
            }}
          >
            <RotateCcw className="size-4" /> Hoàn tác
          </Button>
          <Button type="button" disabled={!isDirty || isSaving} onClick={handleSave}>
            {isSaving ? (
              <>
                <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                Đang lưu…
              </>
            ) : (
              "Lưu cấu hình"
            )}
          </Button>
        </div>
      </div>

      {pickerSpec && (
        <AssetPicker
          groupId={group.groupId}
          assetType={pickerSpec.assetType}
          onSelect={(asset) => {
            updateDraft({ [pickerSpec.field]: asset.assetId } as Partial<GroupDraft>);
            setAssetNames((prev) => ({ ...prev, [asset.assetId]: fileNameOf(asset.relativePath) }));
          }}
          onClose={() => setPickerSlot(null)}
        />
      )}
    </div>
  );
}
