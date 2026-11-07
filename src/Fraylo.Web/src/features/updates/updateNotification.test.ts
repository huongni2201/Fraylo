import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { UpdateNotification } = await import("./UpdateNotification");
test("Báo bản mới, không tự cài và cho đóng thông báo", async () => {
  let applies = 0;
  const api = { check: async () => ({currentVersion:"1.0.4",latestVersion:"1.0.5",hasUpdate:true,releaseNotes:["Bản mới"],downloadUrl:null,sha256:null}), apply: async () => { applies++; return {started:true}; } };
  const container=document.createElement("div");document.body.append(container);const root=createRoot(container);
  try {
    await React.act(async()=>root.render(React.createElement(UpdateNotification,{api})));
    await React.act(async()=>{await new Promise(r=>setTimeout(r,2100));});
    assert.ok(document.body.querySelector('[role="dialog"]')?.textContent?.includes("1.0.5"));assert.equal(applies,0);
    await React.act(async()=>Array.from(document.body.querySelectorAll("button")).find(button=>button.textContent?.includes("Để sau"))!.click());
    assert.equal(document.body.querySelector('[role="dialog"]'),null);
  } finally {await React.act(async()=>root.unmount());container.remove();}
});
test("Bản mới nhất không hiện thông báo", async () => {
  const api={check:async()=>({currentVersion:"1.0.4",latestVersion:"1.0.4",hasUpdate:false,releaseNotes:[],downloadUrl:null,sha256:null}),apply:async()=>({started:true})};
  const container=document.createElement("div");document.body.append(container);const root=createRoot(container);
  try {await React.act(async()=>root.render(React.createElement(UpdateNotification,{api})));await React.act(async()=>{await new Promise(r=>setTimeout(r,2100));});assert.equal(container.textContent,"");}
  finally {await React.act(async()=>root.unmount());container.remove();}
});
