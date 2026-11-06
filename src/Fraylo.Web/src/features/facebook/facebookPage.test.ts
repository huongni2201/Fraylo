import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookBatchRequest, FacebookGroup, FacebookList, FacebookSync } from "./facebookApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookPage } = await import("./FacebookPage");
const { randomGroupIds } = await import("./facebookApi");
const groups: FacebookGroup[] = Array.from({ length: 4 }, (_, index) => ({ targetId: `a${index}`, accountId: "a", name: `Nhóm ${index}`, url: `https://www.facebook.com/groups/${index + 1}/` }));
const unused = async (): Promise<never> => { throw new Error("unused"); };
const unusedApi = { postNow: unused, reschedule: unused, saveDraft: unused, deleteDraft: unused };

test("Chọn ngẫu nhiên không lặp, không vượt danh sách và từ chối số sai", () => {
  assert.deepEqual(randomGroupIds(groups, 4, () => 0).sort(), groups.map(group => group.targetId).sort());
  assert.equal(new Set(randomGroupIds([...groups, groups[0]], 3)).size, 3);
  for (const count of [0, -1, 1.5, NaN, Infinity, 5]) assert.throws(() => randomGroupIds(groups, count));
});

test("Lô nhóm phải xác nhận, giữ mã lô khi timeout, không áp nhóm tài khoản cũ", async () => {
  const data: FacebookList = { accounts: ["a", "b"].map(id => ({ accountId: id, displayName: `Tài khoản ${id}`, accountIdentity: id, sessionStatus: "Connected" })), groups, tasks: [] };
  const requests: FacebookBatchRequest[] = [];
  let releaseSync: ((result: FacebookSync) => void) | undefined;
  let navigated = "";
  const api: FacebookApi = {
    list: async () => data,
    sync: async () => new Promise(resolve => { releaseSync = resolve; }),
    create: async request => { requests.push(request); throw new Error("Mất kết nối"); },
    cancel: async () => { throw new Error("Không được hủy"); }, resume: async () => { throw new Error("Không được tiếp tục"); }, ...unusedApi,
  };
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const render = (view: "compose", active = true) => React.createElement(FacebookPage, { active, view, api, onNavigate: next => { navigated = next; }, onOpenAccounts: () => {} });
  const button = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>("button")).find(item => item.textContent?.trim() === label)!;
  const selectAccount = async (id: string) => React.act(async () => { const select = container.querySelector<HTMLSelectElement>("#facebook-account")!; select.value = id; select.dispatchEvent(new Event("change", { bubbles: true })); });
  const input = async (selector: string, value: string) => React.act(async () => {
    const element = container.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!;
    Object.getOwnPropertyDescriptor(element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype, "value")!.set!.call(element, value);
    element.dispatchEvent(new Event("input", { bubbles: true }));
  });
  try {
    await React.act(async () => root.render(render("compose")));
    await selectAccount("a");
    await input('[aria-label="Tìm nhóm"]', "Nhóm 0");
    await React.act(async () => button("Chọn tất cả 4 nhóm").click());
    assert.match(container.textContent!, /Đã chọn 4 nhóm/);
    await input("#facebook-text", "Nội dung đã kiểm tra 😀");
    await React.act(async () => button("Xem lại lô đăng").click());
    assert.equal(requests.length, 0, "Mở xác nhận chưa tạo tác vụ");
    const dialog = document.querySelector('[role="dialog"]')!;
    assert.match(dialog.textContent!, /Xác nhận lô đăng vào 4 nhóm/);
    assert.equal(dialog.querySelectorAll("ol li").length, 4, "Xác nhận cả nhóm ẩn bởi bộ lọc");
    await React.act(async () => button("Xác nhận tạo 4 tác vụ").click());
    assert.equal(requests.length, 1); assert.equal(navigated, "");
    await React.act(async () => button("Xác nhận tạo 4 tác vụ").click());
    assert.equal(requests.length, 2); assert.equal(requests[0].batchRequestId, requests[1].batchRequestId, "Timeout không sinh mã lô mới");
    await React.act(async () => button("Quay lại chỉnh sửa").click());
    await React.act(async () => root.render(render("compose", false)));
    await React.act(async () => root.render(render("compose")));
    assert.equal(container.querySelector<HTMLTextAreaElement>("#facebook-text")!.value, "Nội dung đã kiểm tra 😀");
    await React.act(async () => button("Tải lại nhóm từ Facebook").click());
    await selectAccount("b");
    await React.act(async () => releaseSync!({ groups, isComplete: false, message: "Chưa đủ" }));
    assert.match(container.textContent!, /Đã chọn 0 nhóm/);
    assert.equal(container.querySelector<HTMLSelectElement>("#facebook-account")!.value, "b");
    assert.equal(container.querySelectorAll('input[type="checkbox"]:checked').length, 0, "Không chọn nhầm nhóm account cũ");
    assert.doesNotMatch(container.textContent!, /Đã tải đầy đủ/);
    data.accounts[1].enabled = false;
    await React.act(async () => root.render(render("compose", false)));
    await React.act(async () => root.render(render("compose")));
    assert.equal(button("Tải lại nhóm từ Facebook").disabled, true, "Tài khoản bị tắt không được tải nhóm dù phiên vẫn Connected");
    assert.equal(button("Xem lại lô đăng").disabled, true);
    assert.match(container.querySelector("#facebook-account")!.textContent!, /Đã tắt/);
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});


test("Mở lại trang dùng nhóm đã lưu, chỉ tải từ Facebook khi bấm nút", async () => {
  const data: FacebookList = { accounts: [{ accountId: "a", displayName: "Facebook A", accountIdentity: "Facebook (1)", sessionStatus: "Connected" }], groups, tasks: [] };
  let syncCalls = 0;
  const api: FacebookApi = {
    list: async () => data,
    sync: async () => { syncCalls++; return { groups, isComplete: true, message: "Đã lưu nhóm" }; },
    create: async () => [], cancel: unused, resume: unused, ...unusedApi,
  };
  for (let opening = 0; opening < 2; opening++) {
    const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
    try {
      await React.act(async () => root.render(React.createElement(FacebookPage, { active: true, view: "compose", api, onNavigate: () => {}, onOpenAccounts: () => {} })));
      await React.act(async () => { const select = container.querySelector<HTMLSelectElement>("#facebook-account")!; select.value = "a"; select.dispatchEvent(new Event("change", { bubbles: true })); });
      assert.match(container.textContent!, /Nhóm 0/);
      assert.match(container.textContent!, /được lưu trên máy/);
      assert.equal(syncCalls, 0, "Mở và chọn tài khoản chỉ đọc danh sách đã lưu");
      if (opening === 1) {
        const reload = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(button => button.textContent?.trim() === "Tải lại nhóm từ Facebook")!;
        await React.act(async () => reload.click());
        assert.equal(syncCalls, 1, "Chỉ thao tác chủ động mới tải lại từ Facebook");
      }
    } finally { await React.act(async () => root.unmount()); container.remove(); }
  }
});
