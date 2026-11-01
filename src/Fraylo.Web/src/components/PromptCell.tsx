import { useState } from "react";
import { CheckCircle2, Copy, Maximize2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";

/** Ô prompt không cuộn: cắt gọn, kèm nút mở to xem chi tiết và nút sao chép. */
export function PromptCell({ text, emptyText, title }: { text?: string | null; emptyText: string; title: string }) {
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const value = text?.trim() ?? "";
  const copy = () => {
    navigator.clipboard.writeText(value).then(() => {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    }, () => { /* Không có quyền clipboard: bỏ qua. */ });
  };
  return (
    <div className="relative p-2 pr-14 rounded bg-muted/60 border border-border font-mono text-[11px] leading-relaxed">
      <p className="line-clamp-5 whitespace-pre-wrap break-words">{value || emptyText}</p>
      {value && (
        <div className="absolute right-1 top-1 flex flex-col gap-1">
          <Button type="button" variant="ghost" size="icon" className="h-6 w-6" aria-label="Xem chi tiết" title="Xem chi tiết" onClick={() => setOpen(true)}>
            <Maximize2 className="h-3.5 w-3.5" />
          </Button>
          <Button type="button" variant="ghost" size="icon" className="h-6 w-6" aria-label="Sao chép" title={copied ? "Đã sao chép" : "Sao chép"} onClick={copy}>
            {copied ? <CheckCircle2 className="h-3.5 w-3.5 text-success" /> : <Copy className="h-3.5 w-3.5" />}
          </Button>
        </div>
      )}
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
          </DialogHeader>
          <pre className="max-h-[60vh] overflow-y-auto whitespace-pre-wrap break-words rounded border border-border bg-muted/60 p-3 font-mono text-xs leading-relaxed">{value}</pre>
          <DialogFooter>
            <Button type="button" variant="outline" size="sm" className="gap-1.5" onClick={copy}>
              <Copy className="h-3.5 w-3.5" /> {copied ? "Đã sao chép" : "Sao chép"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
