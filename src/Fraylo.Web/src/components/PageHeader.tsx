import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export interface PageHeaderProps {
  title: ReactNode;
  description?: ReactNode;
  /** Nút quay lại hoặc nhãn ngữ cảnh, đặt phía trên tiêu đề. */
  leading?: ReactNode;
  actions?: ReactNode;
  titleId?: string;
  className?: string;
}

/** Tiêu đề trang thống nhất: một cấp heading, mô tả ngắn và nhóm hành động bên phải. */
export function PageHeader({ title, description, leading, actions, titleId, className }: PageHeaderProps) {
  return (
    <div className={cn("flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between", className)}>
      <div className="min-w-0 space-y-1">
        {leading}
        <h2 id={titleId} className="truncate text-2xl font-semibold tracking-tight text-foreground">
          {title}
        </h2>
        {description && (
          <p className="max-w-2xl text-sm leading-relaxed text-muted-foreground">{description}</p>
        )}
      </div>
      {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}
