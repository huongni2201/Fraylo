/**
 * Định nghĩa ambient types tối thiểu cho môi trường test Node.js
 * Giúp tsc trong cấu hình browser của frontend không báo lỗi thiếu @types/node
 */

declare module "node:module" {
  export function register(url: string | URL, parentURL?: string | URL): void;
}
