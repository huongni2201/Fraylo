import "../../test/setupDom.ts";
const React = (await import("react")).default;
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const { promptApi } = await import("./promptApi.ts");
const { PromptTemplateManager } = await import("./PromptTemplateManager.tsx");

let templates = [{ templateId: "1", purpose: "Script", name: "Mẫu đầu", isActive: true, createdAtUtc: "", updatedAtUtc: "" }];
const contents: Record<string, string> = { "1": "Viết một video" };
let saved: unknown;
promptApi.listTemplates = async () => templates;
promptApi.getTemplate = async (params) => {
  const id = typeof params === "object" && params ? params.templateId : params;
  const template = templates.find((item) => item.templateId === id)!;
  return { template, content: { templateId: template.templateId, templateText: contents[template.templateId], variableDefinitionsJson: "[]", updatedAtUtc: "" } };
};
promptApi.saveTemplate = async (request) => {
  saved = request;
  const template = { templateId: request.templateId ?? "2", purpose: request.purpose, name: request.name, isActive: false, createdAtUtc: "", updatedAtUtc: "" };
  templates = [...templates.filter((item) => item.templateId !== template.templateId), template];
  contents[template.templateId] = request.templateText;
  return { template, content: { templateId: template.templateId, templateText: request.templateText, variableDefinitionsJson: "[]", updatedAtUtc: "" } };
};
promptApi.activateTemplate = async (id) => {
  templates = templates.map((item) => ({ ...item, isActive: item.templateId === id }));
  return templates.find((item) => item.templateId === id)!;
};

const container = document.createElement("div");
document.body.appendChild(container);
const root = createRoot(container);
const button = (label: string) => {
  const result = [...container.querySelectorAll("button")].find((item) => item.textContent?.trim() === label);
  if (!result) throw new Error(`Không tìm thấy nút ${label}.`);
  return result;
};
const changeValue = (element: HTMLInputElement | HTMLTextAreaElement, value: string) => {
  Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), "value")!.set!.call(element, value);
  element.dispatchEvent(new Event("input", { bubbles: true }));
};
await act(async () => { root.render(React.createElement(PromptTemplateManager)); });
if (container.querySelectorAll('[role="tab"]').length !== 5 || container.querySelectorAll("textarea").length !== 1 || container.textContent?.includes("JSON")) {
  throw new Error("Cần có năm tab và một ô nội dung, không có ô JSON.");
}
await act(async () => { button("Tạo mới").click(); });
await act(async () => {
  changeValue(container.querySelector("#prompt-name") as HTMLInputElement, "Mẫu thứ hai");
  changeValue(container.querySelector("#prompt-template-text") as HTMLTextAreaElement, "Nội dung mới");
});
await act(async () => { button("Lưu prompt").click(); });
if (JSON.stringify(saved) !== JSON.stringify({ purpose: "Script", templateId: null, name: "Mẫu thứ hai", templateText: "Nội dung mới" })) {
  throw new Error("Phải tạo mẫu mới với tên và nội dung mà không gửi JSON.");
}
await act(async () => { button("Chọn sử dụng").click(); });
if (!templates.find((item) => item.templateId === "2")?.isActive || templates.find((item) => item.templateId === "1")?.isActive) {
  throw new Error("Mẫu vừa chọn phải là mẫu duy nhất đang dùng.");
}
await act(async () => { button("Image").dispatchEvent(new MouseEvent("mousedown", { bubbles: true, button: 0 })); });
if (container.querySelector('[role="tab"][data-state="active"]')?.textContent !== "Image" || !container.textContent?.includes("Chưa có mẫu")) {
  throw new Error("Chọn tab Image phải hiện danh sách Image riêng.");
}
await act(async () => {
  changeValue(container.querySelector("#prompt-name") as HTMLInputElement, "Bản nháp ảnh");
  changeValue(container.querySelector("#prompt-template-text") as HTMLTextAreaElement, "Ảnh thử");
});
await act(async () => { root.unmount(); });
const reopened = createRoot(container);
await act(async () => { reopened.render(React.createElement(PromptTemplateManager)); });
await act(async () => { button("Image").dispatchEvent(new MouseEvent("mousedown", { bubbles: true, button: 0 })); });
if ((container.querySelector("#prompt-name") as HTMLInputElement).value !== "Bản nháp ảnh" ||
    (container.querySelector("#prompt-template-text") as HTMLTextAreaElement).value !== "Ảnh thử") {
  throw new Error("Bản nháp phải được giữ khi rời và mở lại trang.");
}
await act(async () => { reopened.unmount(); });
console.log("PromptTemplateManager: PASS");
