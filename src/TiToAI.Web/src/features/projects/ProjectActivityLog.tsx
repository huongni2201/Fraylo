import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { jobApi } from "../jobs/jobApi";
import type { JobActivityEntryDto, JobSummaryDto } from "../jobs/jobTypes";

type ActivityLine = JobActivityEntryDto & { jobId: string; taskType: string };

const taskToneClass: Record<string, string> = {
  Script: "bg-info/15 text-info dark:text-info",
  Image: "bg-violet-500/15 text-violet-700 dark:text-violet-300",
  VideoPrompt: "bg-warning/15 text-warning dark:text-warning",
  VideoRender: "bg-success/15 text-success dark:text-success",
};

export function ProjectActivityLog({ projectId }: { projectId: string }) {
  const [lines, setLines] = useState<ActivityLine[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async (alive: () => boolean) => {
    try {
      const [active, history] = await Promise.all([
        jobApi.listActive(),
        jobApi.listHistory({ projectId, pageSize: 20 }),
      ]);
      const jobs = [...active, ...history.items]
        .filter((job) => job.projectId === projectId)
        .filter((job, index, all) => all.findIndex((item) => item.id === job.id) === index)
        .sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc))
        .slice(0, 12);
      const details = await Promise.all(jobs.map((job: JobSummaryDto) => jobApi.getStatus(job.id)));
      if (!alive()) return;
      setLines(details.flatMap((job) => (job.activity ?? []).map((entry) => ({
        ...entry, jobId: job.id, taskType: job.taskType,
      }))).sort((a, b) => b.atUtc.localeCompare(a.atUtc)).slice(0, 30));
      setError(null);
    } catch (cause) {
      if (alive()) setError(cause instanceof Error ? cause.message : "Không thể tải nhật ký hoạt động.");
    } finally {
      if (alive()) setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    let mounted = true;
    setLoading(true);
    setLines([]);
    void refresh(() => mounted);
    const timer = window.setInterval(() => { void refresh(() => mounted); }, 2000);
    return () => { mounted = false; window.clearInterval(timer); };
  }, [refresh]);

  return (
    <section className="rounded-lg border border-border bg-muted/20 p-4" aria-label="Nhật ký hoạt động của dự án">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-sm font-semibold">Nhật ký hoạt động</h3>
        <Button type="button" variant="outline" size="sm" onClick={() => { void refresh(() => true); }}>Làm mới</Button>
      </div>
      {error && <p role="alert" className="mt-2 text-xs text-destructive">{error}</p>}
      {loading && <p className="mt-2 text-xs text-muted-foreground">Đang tải nhật ký…</p>}
      {!loading && !error && lines.length === 0 &&
        <p className="mt-2 text-xs text-muted-foreground">Chưa có nhật ký cho các tác vụ của dự án này.</p>}
      {lines.length > 0 && <ol className="mt-3 max-h-64 space-y-2 overflow-y-auto" aria-live="polite">
        {lines.map((line, index) => <li key={`${line.jobId}-${line.atUtc}-${index}`} className="flex flex-wrap gap-x-2 text-xs">
          <time className="text-muted-foreground">{new Date(line.atUtc).toLocaleTimeString("vi-VN")}</time>
          <span className={`rounded px-1.5 font-medium ${taskToneClass[line.taskType] ?? "bg-muted text-muted-foreground"}`}>{line.taskType}</span>
          <span>{line.message}</span>
        </li>)}
      </ol>}
    </section>
  );
}
