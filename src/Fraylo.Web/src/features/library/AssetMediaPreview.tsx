import { useState } from "react";

export function AssetMediaPreview({ assetId, assetType, fileName, heightClass = "h-36" }: { assetId: string; assetType: string; fileName: string; heightClass?: string }) {
  const [failed, setFailed] = useState(false);
  const src = `https://media.titoai.local/media/${encodeURIComponent(assetId)}`;
  const type = assetType.toLowerCase();

  if (failed || !["image", "audio", "video"].includes(type)) {
    return <div role="status" className={`${heightClass} flex items-center justify-center rounded bg-muted text-muted-foreground text-xs`}>Không thể xem trước tệp này.</div>;
  }
  if (type === "image") {
    return <img src={src} alt={fileName} onError={() => setFailed(true)} className={`w-full ${heightClass} rounded bg-muted object-contain`} loading="lazy" />;
  }
  if (type === "video") {
    return <video src={src} aria-label={fileName} controls preload="metadata" onError={() => setFailed(true)} className={`w-full ${heightClass} rounded bg-muted object-contain`} />;
  }
  return <div className={`${heightClass} flex items-center justify-center rounded bg-muted p-3`}><audio src={src} aria-label={fileName} controls preload="metadata" onError={() => setFailed(true)} className="w-full" /></div>;
}
