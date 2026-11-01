import type { ImgHTMLAttributes } from "react";

export interface BrandLogoProps extends ImgHTMLAttributes<HTMLImageElement> {
  size?: number;
  className?: string;
}

export function BrandLogo({
  size = 24,
  className = "",
  ...props
}: BrandLogoProps) {
  return (
    <img
      src="./brand/fraylo-mark.svg"
      alt=""
      width={size}
      height={size}
      className={className}
      aria-hidden="true"
      {...props}
    />
  );
}
