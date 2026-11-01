import { Send } from "lucide-react";
import { cn } from "@/lib/utils";

/** Cùng kiểu với các mục điều hướng khác trong sidebar (xem navItemClass trong App.tsx). */
export function PublishingNavItem({ active, onClick }: { active: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? "page" : undefined}
      className={cn(
        "nav-item relative flex h-10 w-full items-center gap-3 rounded-lg px-3 text-left text-sm transition-colors cursor-pointer",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-sidebar",
        active
          ? "bg-primary/15 font-medium text-primary-text before:absolute before:inset-y-2 before:left-0 before:w-[3px] before:rounded-full before:bg-primary"
          : "text-sidebar-foreground hover:bg-muted hover:text-foreground",
      )}
    >
      <Send className="size-4 shrink-0" aria-hidden="true" />
      <span className="truncate">Đăng video</span>
    </button>
  );
}
