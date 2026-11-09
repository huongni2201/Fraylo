>
> **Quyết định hiện hành về tạo video:** [Luồng chỉ chạy tự động](AUTOMATIC_VIDEO_WORKFLOW_PLAN.md) bỏ manual handoff/fallback trong quy trình tạo video. Các yêu cầu manual bên dưới chỉ là baseline lịch sử trong phạm vi này; không dùng làm acceptance mới.


> **Bổ sung P0 ngày 2026-10-26:** [§7.4](#74-tạo-ý-tưởngprompt-kèm-nội-dung-đăng-tiktok--p0) yêu cầu AI tạo ý tưởng/kịch bản hoặc prompt kèm luôn nội dung bài đăng TikTok để người dùng sửa và sao chép.

# AI Content Studio — Implementation Plan (C# .NET + SQLite)


**Phiên bản tài liệu:** 1.0  
**Ngày cập nhật:** 2026-10-26  
**Trạng thái:** Kiến trúc mục tiêu cho MVP desktop Windows; thay thế PostgreSQL bằng SQLite.

## 1. Mục tiêu và phạm vi

Xây dựng phần mềm desktop quản lý nhiều kênh TikTok Affiliate, từ ý tưởng/kịch bản → ảnh tham chiếu → prompt chuyển động → gửi ảnh + prompt + file audio tới website AI tạo video → nhận sản phẩm → xem thống kê được cấp quyền để cải thiện nội dung.

**Giữ nguyên các quyết định nghiệp vụ đã chốt:**

- MuseAI chạy browser/profile riêng theo từng tài khoản, có thể cấu hình nhiều browser worker **nếu nhà cung cấp cho phép**. Không tái sử dụng đồng thời một `userDataDir` giữa nhiều tiến trình.
- TikTok sử dụng profile riêng theo từng tài khoản, browser analytics tách biệt.
- Nội dung prompt template, lựa chọn provider và workflow **chỉnh sửa từ giao diện**; định nghĩa JSON do backend quản lý theo database.
- **Không có Audio Worker**: người dùng upload MP3/WAV, ứng dụng lưu file và gửi cùng ảnh + prompt vào MuseAI (hoặc provider khác có hỗ trợ). Không trộn, cắt hoặc ghép audio cục bộ.
- Ở dịch vụ web không cho phép tự động hóa hoặc trích xuất kết quả, cung cấp thao tác bán tự động / nhập-tải thủ công; không giả định mọi website đều cho phép automation.

### Ngoài phạm vi MVP

- Không tự huấn luyện AI, không trực tiếp sinh video bằng GPU cục bộ.
- Không xây hệ thống đăng nhập người dùng SaaS, không đồng bộ nhiều máy, không dựng video/ghép audio cục bộ.
- Không vượt CAPTCHA, giới hạn tài khoản, paywall hoặc cơ chế bảo vệ của nhà cung cấp.

## 2. Stack và phiên bản ổn định (xác nhận ngày 2026-10-26)

| Khối | Công nghệ / phiên bản đề xuất | Vai trò |
|---|---|---|
| Backend / desktop host | **.NET 10.0.13 LTS**, SDK **10.0.402**, **C# 14** | Business logic, scheduler, UI host |
| Desktop UI | **WPF** (`net10.0-windows`) | Cửa sổ, vòng đời ứng dụng, hệ thống Windows |
| Web UI host | **WebView2 Evergreen** | Chạy React trong WPF |
| Frontend | **React 19.3.1**, **TypeScript 7.0.2**, **Vite 8.3.5** | Giao diện quản lý |
| Data access | **EF Core SQLite 10.0.13** | ORM và migrations |
| Database engine | **SQLite 3.54.1** là upstream stable mới nhất; kiểm tra phiên bản thư viện SQLite **thực tế được bundle** | Database embedded, không có server |
| Browser automation | **Microsoft.Playwright 1.64.1** | Browser context, tab, download/upload |
| Scheduling | .NET `BackgroundService`, `Channel<T>`, `CancellationToken` | Job queue nội bộ |
| Files | Local filesystem | Ảnh, audio, video, profile |
| Installer | Windows installer + self-contained .NET | Phát hành |

**Version policy:** pin phiên bản NuGet/NPM bằng `Directory.Packages.props` và lockfile; kiểm tra bản vá bảo mật trước mỗi lần phát hành. SQLite 3.54.1 là upstream release, **không đồng nghĩa** gói `Microsoft.EntityFrameworkCore.Sqlite` đang đóng gói đúng 3.54.1; xác nhận bằng `SELECT sqlite_version();` sau khi build, và cập nhật native SQLite package có kiểm thử khi cần.

Nguồn kiểm tra:
- https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- https://www.sqlite.org/changes.html
- https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite/10.0.13
- https://www.nuget.org/packages/Microsoft.Playwright/1.64.1
- https://github.com/react/react/releases
- https://www.npmjs.com/package/typescript
- https://github.com/vitejs/vite/releases

## 3. Kiến trúc mới (không PostgreSQL, không Python, không Electron)

```text
AI Content Studio — Windows desktop
│
├── WPF Host (.NET 10)
│   └── WebView2 → React + TypeScript UI
│
├── .NET Application Core
│   ├── LinkedAccountManager
│   ├── ProviderRegistry / AdapterRegistry
│   ├── PromptTemplateManager
│   ├── WorkflowEngine / JobScheduler
│   ├── BrowserManager / SessionManager
│   ├── AssetManager / FileTransferManager
│   └── TikTokAnalyticsService
│
├── Playwright .NET
│   ├── Shared AI browser/profile
│   │   ├── Script tabs (configurable)
│   │   ├── Image tabs (configurable)
│   │   └── Video prompt tabs (configurable)
│   ├── MuseAI browser/profile pool (theo linked account)
│   └── TikTok browser/profile pool (theo TikTok account)
│
└── Local Storage
    ├── SQLite database (app.db; toàn app)
    ├── Browser profiles (phân tách account)
    ├── Logs (ẩn thông tin nhạy cảm)
    └── Backups
```


**Tổ chức mã:** Clean-ish modular monolith, không tách microservices/MQ trong MVP. Không bắt buộc chạy ASP.NET Core localhost; nếu web UI cần API nội bộ, dùng Minimal API chỉ bind `127.0.0.1` và token phiên, hoặc WebView2 message bridge có danh sách lệnh được phép. Không bật public API mặc định.

## 4. Cấu trúc thư mục dữ liệu Windows

```text
%LOCALAPPDATA%/AIContentStudio/
├── data/
│   ├── app.db
│   ├── app.db-wal           (SQLite tạo khi dùng WAL)
│   └── app.db-shm           (SQLite tạo khi dùng WAL)
│   │   └── projects/<project-guid>/
│   │       ├── images/
│   │       ├── audio/
│   │       ├── videos/
│   │       └── exports/
│   └── ...
├── browser-profiles/
│   ├── shared-ai/
│   ├── museai-<account-guid>/
│   └── tiktok-<account-guid>/
├── backups/
└── logs/
```

- Không lưu database dưới `Program Files` (không có quyền ghi thông thường).
- Không lưu blob ảnh/video/audio lớn trong SQLite; lưu file và metadata/relative path trong DB.
- Đặt giới hạn dung lượng, dọn file tạm có kiểm soát và đối soát file DB → filesystem.
- Profile browser có cookie/session nhạy cảm: folder ACL theo Windows user, không đọc profile Chrome cá nhân, không log token/cookie, cân nhắc mã hóa bí mật qua DPAPI/Credential Manager.

## 5. Database schema (SQLite)

### 5.1. Bảng lõi

| Table | Trường chính / mục đích |
|---|---|
| `browser_profiles` | `id`, `profile_kind`, `relative_path`, `account_id`, `status`, `last_used_at_utc` |
| `providers` | `id`, `provider_key`, `display_name`, `capabilities_json`, `enabled` |
| `job_attempts` | `id`, `job_id`, `worker_id`, `status`, `error_code`, `started_at_utc`, `ended_at_utc` |
| `job_outputs` | `id`, `job_id`, `asset_id nullable`, `prompt_snapshot`, `provider_result_ref` |
| `app_settings` | `key`, `json_value`, `updated_at_utc` |

**Ràng buộc quan trọng:**
- Thời gian lưu UTC; số liệu tiền tệ/hoa hồng nên lưu minor units (`INTEGER`) hoặc fixed-scale number dạng `TEXT`, không dùng `double` cho tiền.
- Analytics giữ dấu vết `source`, thời điểm thu thập và trạng thái đầy đủ/khuyết của dữ liệu.

### 5.2. Cấu hình SQLite khuyến nghị

```sql
PRAGMA journal_mode = WAL;       -- bật khi khởi tạo database, lưu vào file
PRAGMA foreign_keys = ON;        -- áp dụng trên mỗi connection
PRAGMA busy_timeout = 5000;     -- áp dụng trên mỗi connection (có thể điều chỉnh)
PRAGMA synchronous = NORMAL;    -- phù hợp WAL cho đa số desktop apps; cân nhắc FULL theo yêu cầu durability
```

**Nguyên tắc concurrency:** WAL cho phép reader hoạt động đồng thời với writer, nhưng tại một thời điểm vẫn chỉ có 1 writer. Các transaction phải ngắn; **không mở transaction SQLite trong lúc chờ ChatGPT, Gemini, MuseAI hoặc TikTok**. Không chia sẻ cùng một EF `DbContext` giữa các job/thread: dùng `IDbContextFactory<AppDbContext>` hoặc scope riêng cho mỗi đơn vị công việc. Có retry giới hạn và ghi log khi xảy ra `SQLITE_BUSY`.

**Lưu ý EF Core + SQLite:** không có SQL schemas/sequences kiểu PostgreSQL; một số lệnh `ALTER TABLE` cần rebuild; `decimal` và `DateTimeOffset` có các giới hạn query. Thiết kế entity/migrations từ đầu phù hợp SQLite. Migrations chỉ do 1 tiến trình chạy khi startup, có backup trước khi cập nhật, kiểm tra abandoned EF migrations lock sau crash.

## 6. Cấu hình C# / EF Core

### 6.1. Cài package

```powershell
# Chạy bên trong thư mục dự án .NET phù hợp
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.13
dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.0.13
dotnet add package Microsoft.Playwright --version 1.64.1
```

Không thêm `Npgsql.EntityFrameworkCore.PostgreSQL`; xóa mọi Docker Compose/PostgreSQL dependency và các biến môi trường `POSTGRES_*`, `DB_HOST`, `DB_PORT` của plan cũ.

### 6.2. Connection string tạo từ thư mục người dùng

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var root = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AIContentStudio");
var dataFolder = Path.Combine(root, "data");
Directory.CreateDirectory(dataFolder);

var sqliteConnection = new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(dataFolder, "app.db"),
    Mode = SqliteOpenMode.ReadWriteCreate,
    ForeignKeys = true,
    DefaultTimeout = 5
}.ToString();

// DI registration (đặt trong bootstrap/host):
services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(sqliteConnection));
```

- Khởi tạo database và bật WAL **một lần** trong startup bootstrap, rồi chạy migrations. Truy vấn `SELECT sqlite_version();` để log engine version thực tế.
- Không lưu đường dẫn tùy người dùng dưới dạng absolute path hardcode.
- Trong production, không ghi plaintext bí mật đăng nhập vào `app_settings`.

### 6.3. Migrations

```powershell
dotnet ef migrations add InitialCreate --project src/AIContentStudio.Infrastructure --startup-project src/AIContentStudio.Desktop
dotnet ef database update --project src/AIContentStudio.Infrastructure --startup-project src/AIContentStudio.Desktop
```

Thực tế câu lệnh phụ thuộc cách bootstrap `DbContext` tại design time; có thể tạo `IDesignTimeDbContextFactory`. Mỗi bản cập nhật có migration version và bản backup nhất quán trước khi áp dụng.

## 7. Workflow và phân phối browser


```text
  ├── kịch bản + prompt templates A
  ├── projects/scenes/assets A
  ├── MuseAI account A → riêng browser profile
  └── TikTok account A → riêng browser profile, analytics A

  ├── kịch bản + prompt templates B
  ├── projects/scenes/assets B
  ├── MuseAI account B → riêng browser profile
  └── TikTok account B → riêng browser profile, analytics B

Shared AI browser (1 profile đã đăng nhập ChatGPT/Gemini)
  ├── N script tabs
  ├── N image tabs
  └── N prompt tabs
```

### 7.2. Job lifecycle

`queued → reserved → running → awaiting_user / succeeded / failed / canceled`.

- `queued`: lưu job + snapshot prompt vào SQLite **trước khi** thực thi.
- `reserved`: scheduler gán job cho tab/browser còn rảnh theo tài khoản/provider.
- `running`: thực thi trong browser; cập nhật heartbeat/trạng thái theo mốc.
- `awaiting_user`: cần login/xác minh/nhập kết quả thủ công hoặc website không hỗ trợ automation.
- `succeeded`: xác nhận asset thực sự được tải/lưu thành công.
- `failed`: lỗi có mã, retry có giới hạn và backoff.
- `canceled`: không tự xóa asset đã lưu nếu chưa được xác nhận.


### 7.3. Pipeline nội dung

1. **Kịch bản**: nhập brief, lấy prompt template kênh, tạo và kiểm duyệt ý tưởng/outline/scenes kèm bài đăng TikTok theo §7.4.
2. **Ảnh**: giao job tới image tab phù hợp, lưu prompt snapshot và ảnh tham chiếu.
3. **Video prompt**: tạo prompt dựa trên brief và (nếu cần) ảnh thực tế, kèm phương án bài đăng TikTok theo §7.4; có editor để người dùng sửa lại.
4. **Audio input**: người dùng thêm tệp audio (MP3/WAV) ở mức scene hoặc project; lưu nguyên bản, không xử lý/cắt.
5. **Video**: khi đủ ảnh + prompt + audio, gửi tới MuseAI provider account tương ứng nếu khả năng và quyền truy cập website cho phép; nếu không, hỗ trợ bàn giao thủ công.
7. **Analytics**: đọc/nhập/xuất dữ liệu TikTok có quyền truy cập và đưa insight về prompt/kịch bản từng kênh.

### 7.4. Tạo ý tưởng/prompt kèm nội dung đăng TikTok — P0

Yêu cầu bổ sung ngày 2026-10-26: khi người dùng yêu cầu AI lên ý tưởng/kịch bản hoặc viết/tối ưu prompt nội dung, kết quả phải có luôn **nội dung bài đăng TikTok sẵn để sao chép**, không cần yêu cầu AI viết caption ở lượt riêng. Mục này là nguồn yêu cầu cho template, adapter, pipeline, UI và xuất tài liệu; schema/DTO mở rộng phải được coordinator chốt trước triển khai.

- **Đầu ra:** ý tưởng/kịch bản và prompt theo tác vụ, kèm `tiktokPost` gồm `caption` và `hashtags`. Caption là bài viết hoàn chỉnh: câu mở đầu thu hút (hook), nội dung chính và lời kêu gọi hành động (CTA) phù hợp; hashtags liên quan, không trùng. Bản sao chép ghép caption + xuống dòng + hashtags, không lẫn chỉ dẫn cho AI, nhãn trường hoặc placeholder chưa điền.
- **Phạm vi:** bài đăng thuộc project/video cuối cùng, không nhân thành một bài cho mỗi scene. Khi tạo/tối ưu prompt ở scene, AI trả kèm phương án cập nhật bài đăng dựa trên ngữ cảnh project; người dùng review trước khi thay bản hiện hành. Render preview template thuần túy và tác vụ sinh ảnh/video không tự tạo thêm lượt AI viết bài đăng.
- **Provider và template:** tạo bài đăng ngay trong kết quả text của tác vụ đang chạy, dùng provider/template snapshot hiện hành. Giữ ba purpose Script/Image/VideoPrompt và routing đã chốt; không thêm provider, queue hay bước AI riêng chỉ để viết caption. Mỗi purpose có nhiều mẫu và một mẫu đang dùng; job giữ snapshot nội dung đã dùng.
- **Mẫu mặc định trong luồng tạo video từ ảnh (2026-11-09).** Fraylo dùng prompt riêng của dự án cho Kịch bản hoặc Video Prompt nếu có, sau đó mới xét mẫu toàn ứng dụng đang dùng và mẫu tích hợp. Luồng này có thể chạy với danh sách mẫu trống. Caption mặc định bám chi tiết nhìn thấy, có lời mời phù hợp và 3–5 hashtag liên quan. Video mặc định chỉ định góc máy, cỡ cảnh, điểm nét và chuyển động theo từng đoạn. Ánh sáng ưu tiên khớp ảnh nguồn, có phương án ánh sáng mềm khi ảnh chưa cho đủ thông tin. Các mẫu tích hợp dùng chung giữa trình chỉnh prompt và workflow.
- **UI và xuất:** vùng “Nội dung đăng TikTok” tại review ý tưởng/kịch bản và review kết quả prompt, có caption, hashtag, Sửa/Lưu/Áp dụng, “Sao chép bài đăng” và xuất TXT UTF-8. Bản sao chép/xuất dùng nội dung đã chọn/lưu; draft chưa lưu cần lưu hoặc xác nhận bản muốn dùng. Đây là soạn nội dung để người dùng tự đăng, không thêm chức năng tự đăng TikTok.
- **Validation và tương thích:** text output mới thiếu caption, hashtag hoặc sai kiểu phải báo chưa đủ kết quả, giữ phần hợp lệ để sửa/nhập thủ công; không báo hoàn tất chỉ vì đã có prompt. Quy tắc này áp dụng cả automation và manual import. Job/kết quả lịch sử giữ schema/snapshot cũ, hiện “Chưa có nội dung đăng TikTok”; không tự chạy lại hay làm mất dữ liệu cũ. Giới hạn độ dài là validation của app do coordinator chốt, không gán thành giới hạn TikTok khi chưa xác minh.


## 8. Provider plugin & prompt không hardcode

Interface đề xuất:

```csharp
public interface IProviderAdapter
{
    string ProviderKey { get; }
    ProviderCapabilities Capabilities { get; }
    Task<ProviderRunResult> ExecuteAsync(
        ProviderRunRequest request,
        CancellationToken cancellationToken);
}
```

- `ProviderCapabilities`: supportsText, supportsImage, supportsVideo, supportsAudioUpload, supportsFileDownload, supportsAutomation, manualHandoffMode.
- Không đồng nhất “cấu hình provider” với “automation không cần code”: thêm website mới thường vẫn phải viết adapter/selector riêng, kiểm thử và cập nhật khi UI đổi.
- Tách logic browser automation khỏi UI để có thể thay nhà cung cấp mà không ảnh hưởng Job Scheduler và DB.

## 9. TikTok analytics

- `Overview` và `Viewers` lưu chuỗi số liệu theo ngày; `Content` lưu từng video. Metric vắng mặt giữ `null`, số 0 từ file giữ nguyên là 0; file `Content.csv` chỉ có header là báo cáo hợp lệ với 0 video.
- Giai đoạn MVP: lượt xem, like, comment, share, tỷ lệ tương tác nếu nguồn cung cấp; affiliate như click/GMV/hoa hồng chỉ khi dữ liệu được quyền truy cập.
- Không suy đoán số liệu còn thiếu thành số thực tế.

## 10. Backup, restore, nâng cấp và bảo mật

- **DB backup:** sử dụng `Microsoft.Data.Sqlite.SqliteConnection.BackupDatabase()` khi ứng dụng đang chạy (có thể khóa ghi trong thời gian backup). Không copy thô `app.db` khi database đang hoạt động ở WAL mode, vì có thể thiếu dữ liệu trong `-wal`.
- **Restore:** khóa scheduler, đóng các DB connections, xác thực backup, phục hồi atomically, đối soát asset tồn tại, bật ứng dụng lại.
- **Database health:** `PRAGMA quick_check`, kiểm tra dung lượng ổ đĩa và file bị thiếu.
- **Migration:** tự động tạo backup, nâng schema theo số phiên bản, thông báo/rollback nếu migration lỗi.
- **Windows security:** giới hạn quyền profile/cookie, bí mật dùng DPAPI/Credential Manager, sanitize log và đường dẫn import/export.
- **App security:** mọi route/bridge đều xác minh nguồn gọi, token phiên/allow-list, tuyệt đối không lộ file profile hoặc key qua React.

## 11. Test bắt buộc

2. Dùng 1 browser AI với nhiều tab và 3 MuseAI profiles, 3 TikTok profiles; đăng nhập thủ công, restart vẫn giữ đúng session khi được phép.
3. Xếp 100–1.000 job metadata vào SQLite, thử parallel worker, không xảy ra `database is locked` thường xuyên; job được resume/reconcile sau crash.
4. Cùng lúc tải ảnh/video lớn và ghi metadata; file không lỗi, kết quả thuộc scene đúng.
5. Tắt máy giữa chừng; job chưa xác định không tự resend gây trùng đầu ra.
6. Backup khi WAL đang hoạt động; restore ra máy kiểm thử, kiểm tra dự án và asset.
7. Thử migration từ bản trước lên bản mới, backup/rollback khi migration lỗi.
8. Chạy EXE/MSI trên Windows sạch chưa cài .NET/Python/PostgreSQL; kiểm tra WebView2 Runtime/browser dependencies được cài hoặc hướng dẫn đầy đủ.
9. Sai phiên đăng nhập, mất mạng, website đổi UI, website không cho phép tự động hóa: báo trạng thái hữu ích và có manual handoff.

## 12. Lộ trình triển khai MVP (ưu tiên)

| Giai đoạn | Deliverable | Mức |
|---|---|---|
| 1. Foundation | WPF host + WebView2 + React + SQLite/EF Core + migrations; một file DB cục bộ | P0 |
| 3. Browser | Shared AI tab pool + MuseAI/TikTok profile pools, Browser Manager UI | P0 |
| 4. Scheduler | Channel, jobs + attempts + retry/recovery, per-account quotas, Web UI progress | P0 |
| 5. Providers | ChatGPT/Gemini theo chế độ được phép; MuseAI adapter với kiểm tra upload audio/ảnh/prompt; manual fallback | P0 |
| 6. Production workflow | Scene → image → prompt → audio input → video output → library | P0 |
| 7. TikTok Analytics | Nhập hoặc truy cập metrics hợp lệ, tách account, dashboard/insights | P1 |
| 8. Reliability | Backup/restore, logging, settings, installer Windows, automated tests | P0 trước release |
| 9. Scale option | Nếu mở multi-machine/server, đánh giá migration PostgreSQL | P2 |

### Definition of Done MVP

- Người dùng cài được ứng dụng Windows mà không cài PostgreSQL, Python hay SDK .NET.
- Dùng được 1 browser AI với nhiều tab và các MuseAI/TikTok profile riêng.
- Xử lý hàng đợi, restart không mất job; không gửi trùng khi không biết trạng thái.
- Input video job gồm ảnh + prompt tùy chỉnh + file audio gốc (không audio worker).
- AI tạo ý tưởng/kịch bản hoặc prompt trả kèm bài đăng TikTok; người dùng sửa, lưu, sao chép/xuất đúng bản cấp project theo §7.4.
- Có SQLite backup/restore, migrations, đầy đủ logs và dữ liệu không lẫn tài khoản.
- Website nào không cho phép hoặc không hỗ trợ automation phải có chế độ thao tác thủ công rõ ràng.

## 13. Đối chiếu plan cũ → mới

| Hạng mục cũ (PostgreSQL) | Hạng mục mới (SQLite) |
|---|---|
| PostgreSQL 18.x Server | SQLite embedded (upstream 3.54.1) |
| Npgsql EF provider | `Microsoft.EntityFrameworkCore.Sqlite` 10.0.13 |
| PostgreSQL service, port 5432, user/password | Không database server, không database port/user/password |
| PostgreSQL connection string | `Data Source=<LocalAppData>/AIContentStudio/data/app.db` |
| Docker Compose cho DB | Không cần |
| `pg_dump` / `pg_restore` | SQLite Online Backup API + backup thư mục asset |
| Multi-writer row locking | SQLite WAL + transaction ngắn, kiểm soát một writer |
| PostgreSQL-specific SQL / jsonb / schemas | SQLite JSON-as-TEXT, constraints/index hỗ trợ, no schema/sequence |
| Migration dùng PostgreSQL provider | Tạo lại migrations SQLite, không tái sử dụng SQL migration đặc thù Postgres |

---

**Quyết định cuối:** C# .NET + SQLite là nền tảng MVP local-first. Chỉ dùng PostgreSQL khi phát sinh nhu cầu thực sự về multi-machine/multi-user server hoặc tải ghi lớn vượt khả năng SQLite.
