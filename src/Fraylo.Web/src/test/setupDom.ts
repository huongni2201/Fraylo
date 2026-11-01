/**
 * Thiết lập chung cho kiểm thử frontend chạy bằng `node --test`.
 * Import file này TRƯỚC mọi module React/.tsx: đăng ký loader (.tsx, alias @/*) rồi dựng môi trường DOM Happy-DOM.
 */
import { register } from "node:module";
import { GlobalWindow } from "happy-dom";

register("./test-loader.mjs", import.meta.url);

export const domWindow = new GlobalWindow({ url: "http://localhost:3000" });

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

Object.assign(globalThis, {
  window: domWindow,
  document: domWindow.document,
  localStorage: domWindow.localStorage,
  HTMLElement: domWindow.HTMLElement,
  HTMLButtonElement: domWindow.HTMLButtonElement,
  HTMLDivElement: domWindow.HTMLDivElement,
  HTMLInputElement: domWindow.HTMLInputElement,
  HTMLSelectElement: domWindow.HTMLSelectElement,
  HTMLTextAreaElement: domWindow.HTMLTextAreaElement,
  HTMLFormElement: domWindow.HTMLFormElement,
  Event: domWindow.Event,
  CustomEvent: domWindow.CustomEvent,
  KeyboardEvent: domWindow.KeyboardEvent,
  MouseEvent: domWindow.MouseEvent,
  UIEvent: domWindow.UIEvent,
  FocusEvent: domWindow.FocusEvent,
  NodeFilter: domWindow.NodeFilter,
  MutationObserver: domWindow.MutationObserver,
  getComputedStyle: domWindow.getComputedStyle.bind(domWindow),
  ResizeObserver: ResizeObserverStub,
  requestAnimationFrame: (cb: FrameRequestCallback) => setTimeout(cb, 0),
  cancelAnimationFrame: (id: number) => clearTimeout(id),
  IS_REACT_ACT_ENVIRONMENT: true,
});

// Event tạo từ global Event không thuộc window của Happy-DOM; bọc thành CustomEvent để dispatch được.
function wrapDispatchEvent(target: { dispatchEvent?: (event: Event) => boolean } | null | undefined) {
  if (!target?.dispatchEvent) return;
  const original = target.dispatchEvent;
  target.dispatchEvent = function (this: unknown, event: Event) {
    if (event && !(event instanceof domWindow.Event)) {
      const wrapped = new domWindow.CustomEvent(event.type, {
        bubbles: event.bubbles,
        cancelable: event.cancelable,
        detail: (event as CustomEvent).detail,
      });
      return original.call(this, wrapped);
    }
    return original.call(this, event);
  };
}

wrapDispatchEvent(domWindow.document);
wrapDispatchEvent(domWindow);
wrapDispatchEvent(domWindow.Node?.prototype);
wrapDispatchEvent(domWindow.EventTarget?.prototype);
