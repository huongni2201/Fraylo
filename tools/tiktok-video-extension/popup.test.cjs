const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const test = require('node:test')
const vm = require('node:vm')

const popup = fs.readFileSync(path.join(__dirname, 'extension', 'popup.js'), 'utf8')
const page = 'https://www.tiktok.com/@example/video/123'
const media = 'https://example.test/clip.mp4?signature=abc'

function createPopup(tabUrl = page, anchors = [], locationUrl = tabUrl, detail, feed = {}) {
  const downloads = []
  const elements = Object.fromEntries(['source', 'permission', 'find', 'save', 'status', 'download'].map(id => [id, {
    value: '',
    checked: false,
    addEventListener(type, callback) { this[type] = callback },
  }]))
  const document = {
    querySelector: selector => elements[selector.slice(1)],
    querySelectorAll: selector => selector.endsWith(' video') ? feed.players ?? [] : anchors,
    getElementById: id => id === '__UNIVERSAL_DATA_FOR_REHYDRATION__' && {
      textContent: JSON.stringify({ __DEFAULT_SCOPE__: { 'webapp.video-detail': detail, 'webapp.foryou.video': feed.data } }),
    },
    createElement: () => ({ content: { getElementById: () => ({ textContent: feed.postData }) } }),
  }
  const chrome = {
    tabs: { query: async () => [{ id: 1, url: tabUrl }] },
    scripting: { executeScript: async ({ func, args }) => [{ result: await func(...args) }] },
    downloads: {
      download: async options => downloads.push(options),
      onChanged: { addListener(callback) { this.emit = callback } },
    },
  }
  vm.runInNewContext(popup, { document, chrome, URL, location: new URL(locationUrl), innerHeight: 800, innerWidth: 1280, ...feed.globals })
  return { ...elements, downloads, chrome, submit: () => elements.download.submit({ preventDefault() {} }) }
}

const link = (href, download = 'clip.mp4', visible = true) => ({ href, download, checkVisibility: () => visible })

test('lưu URL tệp nguyên vẹn sau khi xác nhận quyền tải', async () => {
  const ui = createPopup()
  ui.source.value = media
  await ui.submit()
  assert.equal(ui.downloads.length, 0)

  ui.permission.checked = true
  await ui.submit()
  assert.equal(ui.downloads.length, 1)
  assert.equal(ui.downloads[0].url, media)
  assert.equal(ui.downloads[0].saveAs, true)
  assert.match(ui.status.textContent, /Đã gửi yêu cầu tải/)
})

test('từ chối link chia sẻ, playlist và URL không an toàn', async () => {
  const ui = createPopup()
  ui.permission.checked = true
  for (const value of [page, 'https://vm.tiktok.com/abc/', 'https://example.test/live.m3u8', 'http://example.test/video.mp4', 'https://user:pass@example.test/video.mp4', 'blob:https://example.test/abc']) {
    ui.source.value = value
    await ui.submit()
    assert.match(ui.status.textContent, /Cần link HTTPS/)
  }
  assert.equal(ui.downloads.length, 0)
})

test('chọn đúng một link tải hiển thị và yêu cầu xác nhận lại', async () => {
  const ui = createPopup(page, [link(media), link(media), link('https://example.test/hidden.mp4', '', false)])
  ui.permission.checked = true
  await ui.find.click()
  assert.equal(ui.source.value, media)
  assert.equal(ui.permission.checked, false)
  assert.equal(ui.downloads.length, 0)
})

test('tìm URL tải trong dữ liệu bài TikTok khi trang không có thẻ tải', async () => {
  const address = 'https://v16-webapp-prime.tiktok.com/video/tos/clip/?mime_type=video_mp4&signature=abc'
  const ui = createPopup(page, [], page, {
    statusCode: 0,
    itemInfo: { itemStruct: { id: '123', video: { downloadAddr: address } } },
  })
  await ui.find.click()
  assert.equal(ui.source.value, address)
  ui.permission.checked = true
  await ui.submit()
  assert.equal(ui.downloads[0].url, address)
})

test('bỏ địa chỉ phát, địa chỉ của bài khác và địa chỉ của bài riêng tư', async () => {
  for (const itemStruct of [
    { id: '123', video: { playAddr: media, downloadAddr: '' } },
    { id: '456', video: { downloadAddr: media } },
    { id: '123', privateItem: true, video: { downloadAddr: media } },
  ]) {
    const ui = createPopup(page, [], page, { statusCode: 0, itemInfo: { itemStruct } })
    await ui.find.click()
    assert.equal(ui.source.value, '')
    assert.match(ui.status.textContent, /Trang chưa cung cấp link/)
  }
})

test('bỏ qua tab ngoài TikTok, trang đã đổi và nhiều nguồn tải', async () => {
  for (const ui of [
    createPopup('https://www.tiktok.com.example.test/@example/video/123', [link(media)]),
    createPopup(page, [link(media)], page.replace('123', '456')),
    createPopup(page, [link(media), link('https://example.test/other.mp4')]),
  ]) {
    await ui.find.click()
    assert.equal(ui.source.value, '')
    assert.equal(ui.find.disabled, false)
  }
})

test('hiện lỗi khi Chrome từ chối tải và cho phép thử lại', async () => {
  const ui = createPopup()
  ui.source.value = media
  ui.permission.checked = true
  ui.chrome.downloads.download = async () => { throw new Error('Download rejected') }
  await ui.submit()
  assert.match(ui.status.textContent, /Chrome chưa bắt đầu tải/)
  assert.equal(ui.save.disabled, false)
})

test('phân biệt máy chủ chặn tải với lỗi tìm link', async () => {
  const ui = createPopup()
  ui.source.value = media
  ui.permission.checked = true
  await ui.submit()
  ui.chrome.downloads.onChanged.emit({ id: 1, state: { current: 'interrupted' }, error: { current: 'SERVER_FORBIDDEN' } })
  assert.match(ui.status.textContent, /Máy chủ từ chối tải/)
})

const feedPage = 'https://www.tiktok.com/foryou'
const player = (id, top, author) => ({
  checkVisibility: () => true,
  getBoundingClientRect: () => ({ top, bottom: top + 768, left: 400, right: 900 }),
  closest: selector => selector.startsWith('article')
    ? { querySelector: () => author && { href: `https://www.tiktok.com/@${author}` } }
    : { id: `xgwrapper-0-${id}` },
})

test('Đề xuất chọn bài giữa màn hình thay vì bài đã nạp ở bên dưới', async () => {
  const data = { statusCode: 0, itemList: [
    { id: '123', video: { downloadAddr: media } },
    { id: '456', video: { downloadAddr: 'https://example.test/other.mp4' } },
  ] }
  const ui = createPopup(feedPage, [], feedPage, undefined, { data, players: [player('456', 816), player('123', 16)] })
  await ui.find.click()
  assert.equal(ui.source.value, media)
})

test('Đề xuất tra bài được nạp sau và bỏ kết quả nếu người xem đã cuộn', async () => {
  for (const switched of [false, true]) {
    const players = [player('789', 16, 'creator')]
    const ui = createPopup(feedPage, [], feedPage, undefined, {
      players,
      postData: JSON.stringify({ __DEFAULT_SCOPE__: { 'webapp.video-detail': {
        statusCode: 0, itemInfo: { itemStruct: { id: '789', video: { downloadAddr: media } } },
      } } }),
      globals: {
        AbortSignal,
        fetch: async url => {
          assert.equal(url, 'https://www.tiktok.com/@creator/video/789')
          if (switched) players[0] = player('999', 16, 'other')
          return { ok: true, text: async () => '<html></html>' }
        },
      },
    })
    await ui.find.click()
    assert.equal(ui.source.value, switched ? '' : media)
  }
})

test('Đề xuất không đoán khi có hai trình phát giữa màn hình', async () => {
  const ui = createPopup(feedPage, [], feedPage, undefined, { players: [player('123', 16), player('456', 30)] })
  await ui.find.click()
  assert.equal(ui.source.value, '')
  assert.match(ui.status.textContent, /Chưa tìm được link tải của video đang xem/)
})
