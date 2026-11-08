const source = document.querySelector('#source')
const permission = document.querySelector('#permission')
const findButton = document.querySelector('#find')
const saveButton = document.querySelector('#save')
const status = document.querySelector('#status')
let trackedDownload

function directUrl(value) {
  const url = URL.parse(value)
  if (!url || url.protocol !== 'https:' || url.username || url.password) return null

  const tiktokPage = /^(?:(?:www|m|vm|vt)\.)?tiktok\.com$/i.test(url.hostname)
  if (tiktokPage || /\.(?:m3u8|mpd)(?:\/|$)/i.test(url.pathname)) return null
  return url.href
}

async function findDownloadLink(expectedPage, videoId) {
  if (`${location.origin}${location.pathname}` !== expectedPage) return []

  function readScope(root) {
    const data = root.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__')?.textContent
    try {
      return data ? JSON.parse(data)?.__DEFAULT_SCOPE__ : null
    } catch {
      return null
    }
  }

  function fileAddress(item) {
    const address = item?.video?.downloadAddr
    if (item?.privateItem || item?.secret || typeof address !== 'string') return []
    const url = URL.parse(address)
    return url?.protocol === 'https:' && !url.username && !url.password ? [url.href] : []
  }

  function detailAddress(scope, id) {
    const detail = scope?.['webapp.video-detail']
    const item = detail?.itemInfo?.itemStruct
    return detail?.statusCode === 0 && item?.id === id ? fileAddress(item) : []
  }

  const scope = readScope(document)
  if (!videoId) {
    const card = 'article[data-e2e="recommend-list-item-container"]'
    function currentPlayer() {
      const players = [...document.querySelectorAll(`${card} video`)].filter(player => {
        if (!player.checkVisibility({ visibilityProperty: true, opacityProperty: true })) return false
        const { top, bottom, left, right } = player.getBoundingClientRect()
        return top < innerHeight / 2 && bottom > innerHeight / 2 && right > 0 && left < innerWidth
      })
      if (players.length !== 1) return null
      const player = players[0]
      const id = player.closest('[id^="xgwrapper-"]')?.id.match(/^xgwrapper-\d+-(\d+)$/)?.[1]
      return id ? { player, id } : null
    }

    const current = currentPlayer()
    if (!current) return []

    const feed = scope?.['webapp.foryou.video']
    const item = feed?.statusCode === 0 && Array.isArray(feed.itemList) && feed.itemList.find(entry => entry?.id === current.id)
    if (item) return fileAddress(item)

    const author = current.player.closest(card)?.querySelector('a[data-e2e="video-author-avatar"]')
    const profile = URL.parse(author?.href ?? '')
    if (profile?.origin !== location.origin || !/^\/@[\w.]+\/?$/.test(profile.pathname)) return []

    const post = `${location.origin}${profile.pathname.replace(/\/$/, '')}/video/${current.id}`
    const html = await fetch(post, { redirect: 'error', signal: AbortSignal.timeout(6000) })
      .then(response => response.ok ? response.text() : null)
      .catch(() => null)
    if (!html) return []
    if (`${location.origin}${location.pathname}` !== expectedPage || currentPlayer()?.id !== current.id) return []

    const template = document.createElement('template')
    template.innerHTML = html
    return detailAddress(readScope(template.content), current.id)
  }

  const address = detailAddress(scope, videoId)
  if (address.length) return address

  const links = [...document.querySelectorAll('a[download][href]')].filter(link => {
    const url = URL.parse(link.href)
    return link.checkVisibility({ visibilityProperty: true, opacityProperty: true }) &&
      url?.protocol === 'https:' &&
      [link.download, url.pathname].some(value => /\.(?:mp4|webm|mov)$/i.test(value))
  })
  return [...new Set(links.map(link => link.href))]
}

source.addEventListener('input', () => {
  permission.checked = false
  trackedDownload = undefined
})

chrome.downloads.onChanged.addListener(change => {
  if (change.id !== trackedDownload || change.state?.current !== 'interrupted') return
  if (change.error?.current === 'SERVER_FORBIDDEN') {
    status.textContent = 'Máy chủ từ chối tải. Nếu TikTok có nút Lưu video, hãy dùng nút đó trong ứng dụng.'
  } else if (change.error?.current !== 'USER_CANCELED') {
    status.textContent = 'Quá trình tải bị gián đoạn. Xem lý do trong mục Tệp đã tải xuống của Chrome.'
  }
})

findButton.addEventListener('click', async () => {
  findButton.disabled = true
  trackedDownload = undefined
  const previousSource = source.value
  status.textContent = 'Đang kiểm tra tab hiện tại…'

  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true })
    const page = URL.parse(tab?.url ?? '')
    const tiktok = ['https://www.tiktok.com', 'https://tiktok.com'].includes(page?.origin)
    const videoId = tiktok && page.pathname.match(/^\/@[^/]+\/video\/(\d+)\/?$/)?.[1]
    const feed = tiktok && /^\/(?:foryou\/?)?$/.test(page.pathname)
    if ((!videoId && !feed) || tab.id == null) {
      status.textContent = 'Hãy mở mục Đề xuất hoặc trang riêng của một video TikTok.'
      return
    }

    const [injection] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: findDownloadLink,
      args: [`${page.origin}${page.pathname}`, videoId ?? null],
    })
    if (source.value !== previousSource) {
      status.textContent = 'Đã giữ lại link bạn vừa nhập.'
      return
    }
    const urls = (injection?.result ?? []).map(directUrl).filter(Boolean)
    if (urls.length !== 1) {
      status.textContent = urls.length > 1
        ? 'Trang có nhiều liên kết tải. Hãy dán link tệp của đúng video.'
        : feed
          ? 'Chưa tìm được link tải của video đang xem. Dừng cuộn rồi thử lại, hoặc mở riêng bài video.'
          : 'Trang chưa cung cấp link tệp phù hợp. Nếu TikTok cho phép tải, bạn có thể dùng Chia sẻ → Lưu video trong ứng dụng.'
      return
    }

    source.value = urls[0]
    permission.checked = false
    status.textContent = 'Đã tìm thấy link tệp. Xác nhận quyền tải rồi chọn Lưu tệp.'
  } catch {
    status.textContent = 'Không đọc được tab. Hãy mở lại tiện ích trên trang video và thử lại.'
  } finally {
    findButton.disabled = false
  }
})

document.querySelector('#download').addEventListener('submit', async event => {
  event.preventDefault()
  if (!permission.checked || findButton.disabled) return

  const url = directUrl(source.value)
  if (!url) {
    status.textContent = 'Cần link HTTPS của tệp video. Link bài TikTok và playlist không phải link tệp.'
    return
  }

  findButton.disabled = true
  saveButton.disabled = true
  trackedDownload = undefined
  status.textContent = 'Đang mở hộp thoại lưu…'

  try {
    trackedDownload = await chrome.downloads.download({ url, saveAs: true })
    status.textContent = 'Đã gửi yêu cầu tải. Xem kết quả trong mục Tệp đã tải xuống của Chrome.'
  } catch {
    status.textContent = 'Chrome chưa bắt đầu tải. Kiểm tra lại link hoặc thử một link tải mới.'
  } finally {
    findButton.disabled = false
    saveButton.disabled = false
  }
})
