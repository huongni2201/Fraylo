import type { LucideIcon, LucideProps } from "lucide-react";
import {
  AlertTriangle,
  Check,
  ChevronLeft,
  ChevronRight,
  Eye,
  Film,
  Folder,
  HardDrive,
  Image,
  Info,
  Music,
  RefreshCw,
  Search,
  Upload,
  X,
} from "lucide-react";
import type { SVGProps } from "react";

export type IconProps = SVGProps<SVGSVGElement> &
  Omit<LucideProps, "ref"> & {
    size?: number | string;
    color?: string;
    className?: string;
  };

function createIcon(IconComponent: LucideIcon, defaultSize = 16) {
  return function IconWrapper({
    size = defaultSize,
    color = "currentColor",
    className = "",
    ...props
  }: IconProps) {
    return (
      <IconComponent
        size={size}
        color={color}
        className={className}
        aria-hidden="true"
        {...props}
      />
    );
  };
}

export const IconSearch = createIcon(Search, 16);
export const IconClose = createIcon(X, 16);
export const IconCheck = createIcon(Check, 16);
export const IconInfo = createIcon(Info, 16);
export const IconRefresh = createIcon(RefreshCw, 16);
export const IconHardDrive = createIcon(HardDrive, 16);
export const IconChevronRight = createIcon(ChevronRight, 16);
export const IconChevronLeft = createIcon(ChevronLeft, 16);
export const IconWarning = createIcon(AlertTriangle, 16);
export const IconFilm = createIcon(Film, 16);
export const IconFolder = createIcon(Folder, 16);
export const IconImage = createIcon(Image, 16);
export const IconMusic = createIcon(Music, 16);
export const IconEye = createIcon(Eye, 16);
export const IconUpload = createIcon(Upload, 16);

export function IconFacebook({
  size = 16,
  color = "currentColor",
  className = "",
  ...props
}: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill={color}
      className={className}
      aria-hidden="true"
      {...props}
    >
      <path d="M24 12.073c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.99 4.388 10.954 10.125 11.854v-8.385H7.078v-3.47h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.47h-2.796v8.385C19.612 23.027 24 18.062 24 12.073z" />
    </svg>
  );
}

export function IconTikTok({
  size = 16,
  color = "currentColor",
  className = "",
  ...props
}: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill={color}
      className={className}
      aria-hidden="true"
      {...props}
    >
      <path d="M19.59 6.69a4.83 4.83 0 0 1-3.77-4.25V2h-3.45v13.67a2.89 2.89 0 0 1-5.2 1.74 2.89 2.89 0 0 1 2.31-4.64 2.93 2.93 0 0 1 .88.13V9.4a6.84 6.84 0 0 0-1-.05A6.33 6.33 0 0 0 3 15.68 6.34 6.34 0 0 0 9.35 22a6.34 6.34 0 0 0 6.34-6.32V8.92a8.28 8.28 0 0 0 4.84 1.55v-3.5a4.78 4.78 0 0 1-.94-.28Z" />
    </svg>
  );
}
