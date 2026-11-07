import type { FacebookActivityEntry } from "./facebookApi";

export function FacebookActivityLog({ id, entries }: { id: string; entries?: FacebookActivityEntry[] }) {
  return <details className="mt-2 rounded-md border border-border px-3 py-2 text-xs">
    <summary className="cursor-pointer rounded-sm font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">Nhật ký tác vụ</summary>
    <p className="mt-2 break-all text-muted-foreground">Mã tác vụ <code className="select-all">{id}</code></p>
    {entries?.length ? <ol className="mt-2 space-y-2 border-l border-border pl-3">
      {entries.map((entry, index) => <li key={`${entry.atUtc}-${index}`} className="break-words">
        <time className="tabular-nums text-muted-foreground" dateTime={entry.atUtc}>{new Date(entry.atUtc).toLocaleString("vi-VN")}</time>
        <p>{entry.message}</p>
        <code className="text-muted-foreground">{entry.code}{entry.reasonCode ? ` · ${entry.reasonCode}` : ""}</code>
      </li>)}
    </ol> : <p className="mt-2 text-muted-foreground">Chưa có mốc nhật ký cho tác vụ này.</p>}
  </details>;
}
