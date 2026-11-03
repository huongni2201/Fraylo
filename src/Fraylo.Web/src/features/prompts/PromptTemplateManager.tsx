import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { promptApi, type PromptPurpose, type PromptTemplateDto } from "./promptApi";
import { DEFAULT_PRESET_PROMPTS } from "./promptStorage";

const tabs: { purpose: PromptPurpose; label: string }[] = [
  { purpose: "Script", label: "Script" },
  { purpose: "ComposeImage", label: "Ghép ảnh" },
  { purpose: "VideoPrompt", label: "Video Prompt" },
  { purpose: "Storyboard", label: "Storyboard" },
  { purpose: "Image", label: "Image" },
  { purpose: "TikTokContent", label: "Nội dung TikTok" },
];

type PromptDraft = { templateId: string | null; name: string; templateText: string };
const draftKey = (purpose: PromptPurpose) => `Fraylo_prompt_draft_${purpose}`;
function readDraft(purpose: PromptPurpose): PromptDraft | null {
  try {
    const value = JSON.parse(window.sessionStorage.getItem(draftKey(purpose)) || "null") as PromptDraft | null;
    return value && (value.templateId === null || typeof value.templateId === "string") &&
      typeof value.name === "string" && typeof value.templateText === "string" ? value : null;
  } catch { return null; }
}
function writeDraft(purpose: PromptPurpose, draft: PromptDraft | null) {
  try {
    if (draft) window.sessionStorage.setItem(draftKey(purpose), JSON.stringify(draft));
    else window.sessionStorage.removeItem(draftKey(purpose));
  } catch { /* Editing remains available if session storage is unavailable. */ }
}

export function PromptTemplateManager() {
  const [purpose, setPurpose] = useState<PromptPurpose>("Script");
  const [templates, setTemplates] = useState<PromptTemplateDto[]>([]);
  const [templateId, setTemplateId] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [templateText, setTemplateText] = useState("");
  const [dirty, setDirty] = useState(false);
  const [preview, setPreview] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const selected = templates.find((item) => item.templateId === templateId);
  const purposeTemplates = templates.filter((item) => item.purpose === purpose);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setMessage(null);
    setPreview(null);
    setTemplateId(null);
    setName("");
    setTemplateText("");
    setDirty(false);
    void (async () => {
      try {
        const list = await promptApi.listTemplates(undefined, controller.signal);
        if (controller.signal.aborted) return;
        setTemplates(list);
        const draft = readDraft(purpose);
        if (draft) {
          setTemplateId(list.some((item) => item.templateId === draft.templateId && item.purpose === purpose) ? draft.templateId : null);
          setName(draft.name);
          setTemplateText(draft.templateText);
          setDirty(true);
          return;
        }
        const first = list.find((item) => item.purpose === purpose && item.isActive)
          ?? list.find((item) => item.purpose === purpose);
        if (first) {
          const result = await promptApi.getTemplate({ templateId: first.templateId }, undefined, controller.signal);
          if (controller.signal.aborted) return;
          setTemplateId(first.templateId);
          setName(result.template.name);
          setTemplateText(result.content.templateText);
        } else if (DEFAULT_PRESET_PROMPTS[purpose][0]) {
          // Chưa có mẫu nào cho mục đích này: gợi ý mẫu mặc định, người dùng bấm "Lưu prompt" để dùng.
          setName(DEFAULT_PRESET_PROMPTS[purpose][0].name);
          setTemplateText(DEFAULT_PRESET_PROMPTS[purpose][0].templateText);
          setDirty(true);
        }
      } catch (error) {
        if (!controller.signal.aborted) setMessage((error as { message?: string }).message || "Không thể tải danh sách prompt.");
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    })();
    return () => controller.abort();
  }, [purpose]);

  const canLeave = () => {
    if (!dirty) return true;
    if (!window.confirm("Nội dung chưa lưu sẽ bị bỏ. Bạn muốn tiếp tục?")) return false;
    writeDraft(purpose, null);
    return true;
  };

  const openTemplate = async (id: string) => {
    if (id === templateId || !canLeave()) return;
    setLoading(true);
    setMessage(null);
    try {
      const result = await promptApi.getTemplate({ templateId: id });
      setTemplateId(id);
      setName(result.template.name);
      setTemplateText(result.content.templateText);
      setDirty(false);
      setPreview(null);
    } catch (error) {
      setMessage((error as { message?: string }).message || "Không thể tải mẫu prompt.");
    } finally { setLoading(false); }
  };

  const createTemplate = () => {
    if (!canLeave()) return;
    setTemplateId(null);
    setName("");
    setTemplateText("");
    setDirty(false);
    setPreview(null);
    setMessage(null);
  };

  const save = async () => {
    setLoading(true);
    setMessage(null);
    try {
      const result = await promptApi.saveTemplate({ purpose, templateId, name: name.trim(), templateText });
      setTemplateId(result.template.templateId);
      setName(result.template.name);
      setDirty(false);
      writeDraft(purpose, null);
      setTemplates((items) => [...items.filter((item) => item.templateId !== result.template.templateId), result.template]);
      setMessage("Đã lưu mẫu prompt.");
    } catch (error) {
      setMessage((error as { message?: string }).message || "Không thể lưu mẫu prompt.");
    } finally { setLoading(false); }
  };

  const activate = async () => {
    if (!templateId) return;
    setLoading(true);
    setMessage(null);
    try {
      const active = await promptApi.activateTemplate(templateId);
      setTemplates((items) => items.map((item) => item.purpose === purpose ? { ...item, isActive: item.templateId === active.templateId } : item));
      setMessage("Đã chọn mẫu đang dùng.");
    } catch (error) {
      setMessage((error as { message?: string }).message || "Không thể chọn mẫu prompt.");
    } finally { setLoading(false); }
  };

  const renderPreview = async () => {
    setLoading(true);
    setMessage(null);
    try {
      const result = await promptApi.preview({ templateId, purpose, templateText, variables: {} });
      setPreview(result.renderedText);
    } catch (error) {
      setMessage((error as { message?: string }).message || "Không thể tạo bản xem trước.");
    } finally { setLoading(false); }
  };

  return (
    <section className="flex flex-col gap-4 rounded-lg border border-border bg-card p-4">
      <div>
        <h3 className="font-semibold text-foreground">Mẫu prompt toàn ứng dụng</h3>
        <p className="text-sm text-muted-foreground">Tạo nhiều mẫu và chọn một mẫu đang dùng cho từng mục đích.</p>
      </div>
      <Tabs value={purpose} onValueChange={(value) => { if (value !== purpose && canLeave()) setPurpose(value as PromptPurpose); }}>
        <TabsList aria-label="Loại prompt">
          {tabs.map((tab) => <TabsTrigger key={tab.purpose} value={tab.purpose} disabled={loading}>{tab.label}</TabsTrigger>)}
        </TabsList>
      </Tabs>

      <div className="grid gap-4 md:grid-cols-[minmax(180px,240px)_minmax(0,1fr)]">
        <div className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <span className="text-sm font-medium">Danh sách mẫu</span>
            <Button variant="outline" size="sm" onClick={createTemplate} disabled={loading}>Tạo mới</Button>
          </div>
          {purposeTemplates.length === 0 && <p className="text-sm text-muted-foreground">Chưa có mẫu. Tạo mẫu đầu tiên để sử dụng.</p>}
          <div className="flex flex-col gap-1">
            {purposeTemplates.map((item) => (
              <button key={item.templateId} type="button" disabled={loading} onClick={() => void openTemplate(item.templateId)}
                aria-current={item.templateId === templateId ? "true" : undefined}
                className={`rounded-md border px-3 py-2 text-left text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${item.templateId === templateId ? "border-primary bg-primary/5" : "border-border hover:bg-muted"}`}>
                <span className="block break-words font-medium">{item.name}</span>
                {item.isActive && <span className="text-xs text-muted-foreground">Đang dùng</span>}
              </button>
            ))}
          </div>
        </div>
        <div className="flex min-w-0 flex-col gap-4">
          <div className="flex flex-col gap-2">
            <Label htmlFor="prompt-name">Tên mẫu</Label>
            <Input id="prompt-name" value={name} maxLength={120} disabled={loading} placeholder="Ví dụ: Giới thiệu sản phẩm" onChange={(event) => { setName(event.target.value); setDirty(true); writeDraft(purpose, { templateId, name: event.target.value, templateText: readDraft(purpose)?.templateText ?? templateText }); }} />
          </div>
          <div className="flex flex-col gap-2">
            <Label htmlFor="prompt-template-text">Nội dung prompt</Label>
            <Textarea id="prompt-template-text" value={templateText} rows={12} disabled={loading} onChange={(event) => { setTemplateText(event.target.value); setDirty(true); setPreview(null); writeDraft(purpose, { templateId, name: readDraft(purpose)?.name ?? name, templateText: event.target.value }); }} />
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button onClick={() => void save()} disabled={loading || !name.trim() || !templateText.trim()}>Lưu prompt</Button>
            <Button variant="outline" onClick={() => void activate()} disabled={loading || !templateId || dirty || selected?.isActive} title={dirty ? "Lưu thay đổi trước khi chọn mẫu" : undefined}>
              {selected?.isActive ? "Đang dùng" : "Chọn sử dụng"}
            </Button>
            <Button variant="outline" onClick={() => void renderPreview()} disabled={loading || !templateText.trim()}>Xem trước</Button>
            {dirty && <span className="text-xs text-muted-foreground">Chưa lưu thay đổi</span>}
          </div>
          {message && <p role="status" className="text-sm text-muted-foreground">{message}</p>}
          {preview !== null && <pre className="whitespace-pre-wrap rounded-md border border-border bg-muted p-3 text-sm text-foreground">{preview}</pre>}
        </div>
      </div>
    </section>
  );
}
