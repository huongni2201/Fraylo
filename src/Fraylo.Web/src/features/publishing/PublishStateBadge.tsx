import { CheckCircle2, Clock, Loader2, XCircle, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { PublishState } from "./publishingApi";

export const publishStateLabels: Record<PublishState, string> = {
  Preparing: "Đang tải video", Submitted: "Đang chờ xác nhận", Published: "Đã đăng", Failed: "Thất bại", Uncertain: "Chưa xác nhận",
};

// Icon + chữ đi cùng màu để trạng thái không chỉ dựa vào màu.
const styles: Record<PublishState, { icon: LucideIcon; className: string; variant: "success" | "info" | "warning" | "outline"; spin?: boolean }> = {
  Preparing: { icon: Loader2, variant: "warning", className: "", spin: true },
  Submitted: { icon: Clock, variant: "info", className: "" },
  Uncertain: { icon: Clock, variant: "warning", className: "" },
  Published: { icon: CheckCircle2, variant: "success", className: "" },
  Failed: { icon: XCircle, variant: "outline", className: "border-destructive/30 bg-destructive/15 text-destructive" },
};

export function PublishStateBadge({ state }: { state: PublishState }) {
  const { icon: Icon, variant, className, spin } = styles[state];
  return (
    <Badge variant={variant} className={cn("gap-1.5 whitespace-nowrap", className)}>
      <Icon className={cn("size-3.5", spin && "animate-spin")} aria-hidden="true" />
      {publishStateLabels[state]}
    </Badge>
  );
}
