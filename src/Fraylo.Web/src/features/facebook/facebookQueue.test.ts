import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookList, FacebookPost } from "./facebookApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookQueue } = await import("./FacebookQueue");

test("Hàng đợi chung: lọc nhóm/cá nhân/tài khoản, thao tác đúng bài, lỗi và lịch sử", async () => {
  const post = (id: string, personal: boolean, state = "Queued"): FacebookPost => ({
    batchId: id, name: `Bài ${id}`, accountId: id, paused: false, revision: 3, createdAtUtc: "",
    tasks: [{ taskId: id, batchId: id, accountId: id, targetId: personal ? `personal:${id}` : `group:${id}`,
      groupName: personal ? "Trang cá nhân" : "Nhóm đã chọn", groupUrl: "https://www.facebook.com/",
      text: `Nội dung ${id}`, state, message: "", createdAtUtc: "", updatedAtUtc: "", submittedAtUtc: null }],
  });
  const posts = [post("a", false), post("b", true), post("c", true, "Unknown")];
  posts[0].tasks[0].reasonCode = "browser_budget";
  posts[0].tasks[0].message = "Chờ chỗ trống để mở trình duyệt Facebook.";
  posts[2].tasks[0].activityLog = [
    { atUtc: "2026-11-07T10:00:00Z", code: "submit_checkpoint", message: "Đã bắt đầu gửi lệnh đăng." },
    { atUtc: "2026-11-07T10:00:01Z", code: "Unknown", reasonCode: "outcome_unknown", message: "Chưa xác định được kết quả." },
  ];
  const data: FacebookList = { accounts: ["a", "b", "c"].map(id => ({ accountId: id, displayName: `Tài khoản ${id}`, sessionStatus: "Connected", accountIdentity: id })), groups: [], posts, tasks: posts.flatMap(p => p.tasks) };
  const actions: unknown[] = [];
  let cloned: FacebookPost | undefined;
  let failList = false;
  const api = { list: async () => { if (failList) throw new Error("Không tải được"); return data; },
    postAction: async (...args: unknown[]) => { actions.push(args); throw new Error("Không thể tạm dừng"); },
  } as unknown as FacebookApi;
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  let history = false;
  const render = () => root.render(React.createElement(FacebookQueue, { active: true, history, api, onClone: p => { cloned = p; }, onHistoryChange: value => { history = value; render(); } }));
  const click = async (label: string) => React.act(async () => {
    Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent === label)!.click();
  });
  const select = async (label: string, value: string) => React.act(async () => {
    const el = container.querySelector<HTMLSelectElement>(`[aria-label="${label}"]`)!;
    el.value = value; el.dispatchEvent(new Event("change", { bubbles: true }));
  });
  const names = () => Array.from(container.querySelectorAll("article h3")).map(el => el.textContent);
  try {
    await React.act(async () => render());
    assert.deepEqual(names(), ["Bài a", "Bài b"]);
    assert.match(container.querySelector("article")!.textContent!, /Chờ chỗ trống để mở trình duyệt/);
    assert.match(container.querySelectorAll("article")[1].textContent!, /Trang cá nhân/);
    await select("Lọc nơi đăng", "personal"); assert.deepEqual(names(), ["Bài b"]);
    await click("Nhân bản"); assert.equal(cloned?.batchId, "b");
    await click("Tạm dừng"); assert.deepEqual(actions[0], ["b", 3, "pause", undefined]);
    assert.match(container.querySelector('[role="alert"]')!.textContent!, /Không thể tạm dừng/);
    await select("Lọc tài khoản Facebook", "a"); assert.deepEqual(names(), []);
    assert.match(container.textContent!, /Không có bài khớp bộ lọc/);
    await select("Lọc nơi đăng", "all"); assert.deepEqual(names(), ["Bài a"]);
    await select("Lọc tài khoản Facebook", "all");
    await click("Lịch sử"); assert.deepEqual(names(), ["Bài c"]);
    await click("Chi tiết"); assert.match(container.textContent!, /kiểm tra trang cá nhân/);
    const log = container.querySelector("article details")!;
    assert.equal(log.querySelector("summary")?.textContent, "Nhật ký tác vụ");
    assert.equal(log.querySelector("p code")?.textContent, "c");
    assert.deepEqual(Array.from(log.querySelectorAll("li code")).map(el => el.textContent), ["submit_checkpoint", "Unknown · outcome_unknown"]);
    assert.ok(!Array.from(container.querySelectorAll("button")).some(b => b.textContent === "Tiếp tục"));
    failList = true; await click("Cập nhật trạng thái");
    assert.match(container.querySelector('[role="alert"]')!.textContent!, /Không tải được/);
    assert.deepEqual(names(), ["Bài c"], "Lỗi tải giữ dữ liệu đã có");
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});
