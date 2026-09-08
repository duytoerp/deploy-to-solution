# Deploy To Solution

App WPF để **add hàng loạt component Dataverse / Dynamics 365 vào nhiều solution cùng lúc**.
Thay vì mở Power Apps rồi `Add existing → chọn loại → tìm tên → tick → Add` cho từng component,
từng solution (UAT, PROD...), bạn điền 1 file CSV rồi bấm 1 nút.

- Không cần cài NuGet package, không cần `pac` CLI, không cần Visual Studio để chạy.
- Đăng nhập bằng **device code** (tài khoản của bạn, không cần app registration) hoặc **client id + secret** cho pipeline.
- Chạy được nhiều lần: component đã có trong solution sẽ bị bỏ qua, không lỗi, không nhân bản.

---

## 1. Chạy app

```powershell
cd C:\Source\SoureDeployToSolution
dotnet run --project src\DeployToSolution
```

Build ra file exe để phát cho cả team (không cần cài .NET nếu dùng self-contained):

```powershell
# Cần .NET 8 Desktop Runtime trên máy đích (~2 MB output)
dotnet publish src\DeployToSolution -c Release -r win-x64 --self-contained false -o publish

# Không cần cài gì trên máy đích (~150 MB output, 1 file exe)
dotnet publish src\DeployToSolution -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish-standalone
```

---

## 2. Quy trình 3 bước trong app

| Bước | Làm gì |
|------|--------|
| **Kết nối** | Điền URL môi trường (`https://vus-dev.crm5.dynamics.com`) → **Kết nối**. App hiện mã device code, tự copy vào clipboard và mở trình duyệt. Dán mã, đăng nhập xong quay lại app. |
| **1. Chọn solution đích** | Tick **nhiều** solution unmanaged — ví dụ tick cả `..._UAT_TEST_PATCH_T5_03` và `..._PROD_PATCH_T5_03`. |
| **2. Danh sách component** | **Nạp CSV** hoặc **Dán từ Excel**. |
| **3. Chạy** | **Kiểm tra tên** → **Chạy thử** (dry run) → **ADD VÀO SOLUTION**. |

Lần sau mở app: phiên đăng nhập, danh sách môi trường và các solution đã tick được nhớ sẵn.

---

## 3. Định dạng danh sách component

**File mẫu:** bấm nút **Tải template** trong app — file Excel được nhúng sẵn trong exe, lưu ra đĩa
rồi mở lên luôn. Bản trong repo: [`samples/components-template.xlsx`](samples/components-template.xlsx).
Ba sheet: `Components` (điền vào đây, cột Type có dropdown), `Huong dan`, `DanhMucType`.

Đưa vào app bằng 1 trong 2 cách:

- **Nạp Excel / CSV:** chọn thẳng file `.xlsx` — app đọc sheet tên `Components`, không có thì lấy
  sheet đầu tiên. **Không cần Save As CSV nữa.** File `.csv` / `.txt` vẫn nạp được như cũ.
- **Dán từ Excel:** bôi đen vùng dữ liệu (kể cả dòng tiêu đề) → `Ctrl+C` → bấm **Dán từ Excel**.
  Không cần lưu file.

App đọc `.xlsx` bằng `ZipArchive` + `XDocument` có sẵn trong .NET — vẫn không cần thư viện ngoài.

### Định dạng CSV

```csv
Type,Name,IncludeAll
Table,hs_api_log,N
Table,hs_care_certificate_test,Y
Column,hs_api_log.hs_retry_count,N
BPF,[BPF] Incident Report Process,N
Workflow,[WF] Incident Report Change Stage Process,N
PluginAssembly,VUS.Core.Plugins,N
PluginStep,VUS.Core.Plugins.PreUpdateInvoice,N
WebResource,hs_/js/invoice_form.js,N
Choice,hs_care_status_choice,N
View,hs_api_log|Active API Logs,N
Form,hs_api_log|Information,N
```

- Ngăn cách bằng **dấu phẩy, chấm phẩy hoặc tab** — copy thẳng từ Excel là chạy được.
- Dòng bắt đầu bằng `#` là chú thích.
- **`IncludeAll`**: chỉ có ý nghĩa với `Table`.
  - `N` (mặc định) = chỉ add bản thân table, **không** kéo theo toàn bộ column/form/view/relationship. Nên dùng cho hotfix.
  - `Y` = add table kèm tất cả sub-component (tương đương *Include all objects* trên UI).
- Muốn add đúng vài column/form/view thì khai riêng từng dòng `Column` / `Form` / `View`.

### Cột `Name` viết thế nào

| Type | Name điền gì | Ví dụ |
|------|--------------|-------|
| `Table` | logical name, schema name hoặc display name | `hs_api_log` / `API Log` |
| `Column` | `bang.cot` | `hs_api_log.hs_retry_count` |
| `View` / `Form` / `Chart` | `bang|tên`, thêm `|biến thể` nếu trùng | `hs_api_log|Information|Main` |
| `Workflow` / `BPF` / `CloudFlow` | đúng tên process | `[WF][Invoice] Update Canceled Date` |
| `PluginAssembly` | tên assembly | `VUS.Core.Plugins` |
| `PluginStep` | tên step (cột **Name** trong Plug-in steps) | `VUS.Core.Plugins.PreUpdateInvoice` |
| `WebResource` | name của web resource | `hs_/js/invoice_form.js` |
| `Choice` | tên global choice | `hs_care_status_choice` |
| `ConnectionReference` | logical name của connection reference | `hs_sharedcommon..._abc12` |
| `EnvironmentVariable` | schema name | `hs_ApiBaseUrl` |
| `SecurityRole` | tên role (lấy role gốc ở root business unit) | `VUS Core User` |
| `Relationship` | schema name của relationship | `hs_account_hs_api_log` |

### Gợi ý tên tự động

Bấm vào ô **Name** của một dòng, app sẽ nạp danh sách tên có thật trong môi trường **đúng theo Type**
của dòng đó, rồi đổ vào dropdown (gõ để lọc). Nạp lười — chỉ truy vấn khi bạn thật sự sửa ô đó,
và cache lại cho các dòng sau cùng Type.

| Type | Gợi ý gì |
|------|----------|
| `Table` | logical name của mọi table |
| `Column` / `Key` | chưa gõ bảng thì gợi ý `hs_api_log.` để chọn bảng trước; gõ xong bảng, bấm lại vào ô thì gợi ý đủ `bảng.cột` |
| `View` / `Form` / `Chart` | gợi ý sẵn dạng `bảng|tên`, dán vào là chạy |
| `Choice` | tên global choice |
| `Relationship` | schema name |
| còn lại | tên lấy từ chính bảng tương ứng (workflow, plugin step, web resource...) |

Danh sách ưu tiên lọc `ismanaged eq false` (component của bạn, không lẫn hàng ngàn bản ghi hệ thống),
bỏ bản ghi đã xoá (`componentstate` 2/3 — add không bao giờ được), và giới hạn 2000 dòng. Lọc xong
mà rỗng thì app tự nới ra lấy cả managed, nên component managed vẫn tìm được.

**Gõ ít nhất 3 ký tự với những loại nhiều bản ghi.** `PluginStep`, `WebResource`, `Workflow`,
`SecurityRole`... nằm trong bảng dữ liệu có tới hàng chục nghìn dòng, phần lớn là bản ghi nội bộ của
nền tảng. Lấy 2000 dòng đầu theo alphabet thì `Hs.Vus.*` bị cắt mất sạch. Nên từ ký tự thứ 3 trở đi,
app đẩy luôn `contains(...)` xuống Dataverse thay vì lọc trên máy — gõ `Hs.` là ra đúng cụm của bạn.
Ba ký tự đầu quyết định mẻ dữ liệu, gõ tiếp chỉ lọc lại tại chỗ nên không bắn thêm truy vấn.
`Table`, `Column`, `Choice`, `Form`, `View` không cần: metadata nhỏ, hoặc đã hẹp sẵn theo bảng.

### Thêm nhiều dòng cùng lúc

Nút **Thêm dòng** mở một popup: chọn **một Type**, rồi **tick nhiều tên cùng lúc** — mỗi tên thành
một dòng riêng. Không phải thêm từng dòng trống rồi gõ lại từ đầu nữa.

- Ô **Lọc** thu hẹp danh sách, ví dụ gõ `Hs.Vus.Warehouse` rồi bấm **Chọn hết đang hiện** để lấy
  trọn cụm step của một assembly. Với `PluginStep` / `WebResource` / `Workflow` / `SecurityRole`,
  **phải gõ ít nhất 3 ký tự** thì app mới tìm thẳng trên môi trường — danh sách lúc chưa gõ gì chỉ là
  2000 tên đầu theo alphabet nên thường toàn bản ghi hệ thống. Dòng chữ xám dưới ô Type nói rõ đang
  ở trạng thái nào.
- Với `Column` / `Key` / `View` / `Form` / `Chart`, popup hiện thêm ô **của bảng**: chọn bảng trước,
  danh sách chỉ còn component của đúng bảng đó và tên được ghép sẵn đúng cú pháp (`bảng.cột`,
  `bảng|tên`).
- **IncludeAll** chỉ bật được khi Type là `Table`.
- Dòng chữ xám nói luôn **đang đọc bảng nào của Dataverse** (`đọc từ sdkmessageprocessingsteps`),
  để phân biệt ngay `PluginStep` với `PluginType` — tên plugin type và tên step rất giống nhau.
- Chọn `PluginType` thì có **cảnh báo đỏ**: PluginType là *class* plugin (componenttype 90), không
  phải step. Class hay được đặt tên y hệt step (`Hs.Vus.Plugins2.ClassTeacher.PostDeleteAsynchronous`)
  nên nhìn danh sách không phân biệt được — muốn add step thì phải chọn `PluginStep`.
- Dropdown Type không còn hiện hai mục cho cùng một loại (trước đây có cả `PluginStep` lẫn
  `SDK Message Processing Step`, cả `PluginType` lẫn `Plugin Type` — rất dễ chọn nhầm).
- Ô Type **không gõ được nữa, chỉ bấm chọn** (gõ chữ vẫn nhảy tới mục tương ứng). Ô Type gõ được thì
  WPF tự hoàn thành chữ và nhảy qua lại giữa `PluginAssembly` / `PluginType` / `PluginStep`.
  **Lăn chuột khi dropdown đang đóng cũng không đổi lựa chọn nữa** — đây là kiểu đổi nhầm âm thầm
  nhất: chọn đúng `PluginStep` rồi lăn chuột một nấc là thành `PluginType` mà không hay biết.
- Ô **gõ / dán tên** ở dưới dùng được cả khi chưa kết nối. Mỗi dòng một tên; dòng bắt đầu bằng `#`
  bị bỏ qua, dấu `- ` đầu dòng được cắt nên **dán thẳng từ Báo cáo deploy** cũng chạy. Đang ở phạm vi
  một bảng thì tên gõ tay tự được gắn tiền tố bảng.
- **Thêm & chọn tiếp** đẩy các tên đang chọn vào danh sách rồi ở lại popup, để làm tiếp Type khác.
- Cả mẻ mới nằm **trên đầu** danh sách, giữ nguyên thứ tự đã chọn. Tên đã có sẵn trong danh sách
  bị bỏ qua và app báo rõ bỏ bao nhiêu.

### Form và View trùng tên

Một bảng thường có nhiều form **cùng tên** khác loại (Main, QuickView, QuickCreate, Card...),
view cũng vậy (MainView, QuickFind, Lookup...). Khi đó ghi thêm đoạn thứ ba:

```
Form,hs_api_log|Information|Main
Form,hs_api_log|Information|QuickView
View,hs_api_log|Active API Logs|MainView
```

Biến thể form: `Main` `QuickView` `QuickCreate` `Card` `Dialog` `Dashboard` `Preview`
`MobileExpress` `TaskFlow` `MainInteractive` `ContextualDashboard` — hoặc điền thẳng số (`2` = Main).

Biến thể view: `MainView` `AdvancedFind` `SubGrid` `QuickFind` `Lookup` `Reporting` ...

Dropdown gợi ý đã sinh sẵn dạng 3 đoạn nên chọn từ đó là không bao giờ mơ hồ.
Bản ghi đã xoá (`componentstate` 2/3) bị loại khỏi kết quả tìm kiếm.

**Trùng tên ở loại khác?** Dán thẳng **GUID** vào cột `Name` — app nhận GUID và bỏ qua bước tìm kiếm.
Khi trùng, app in sẵn danh sách ứng viên kèm GUID ở cột *Chi tiết* để bạn copy.

### Type nhận những giá trị nào

Dropdown cột **Type** được nạp **từ chính môi trường** (option set `solutioncomponent.componenttype`),
nên luôn khớp phiên bản Dataverse của bạn. Ngoài ra app hiểu các bí danh quen thuộc:

`Table` `Column` `Choice` `View` `Form` `Chart` `Workflow` `BPF` `CloudFlow` `Process`
`PluginAssembly` `PluginType` `PluginStep` `WebResource` `App` `CanvasApp` `SecurityRole`
`ColumnSecurityProfile` `ConnectionReference` `EnvironmentVariable` `Relationship` `Key`
`CustomApi` `CustomControl`/`PCF` `SiteMap` `ServiceEndpoint` `Report` `EmailTemplate` `SLA`

Nếu một loại lạ chưa có sẵn, điền **số component type** vào cột `Type` (ví dụ `29`) là được.

---

## 4. Các nút

| Nút | Tác dụng |
|-----|----------|
| **Thêm dòng** | Mở popup chọn một Type rồi tick nhiều tên cùng lúc — mỗi tên thành một dòng. Xem mục 3. |
| **Kiểm tra tên** | Chỉ tra ID của từng component. **Không ghi gì** lên môi trường. Dòng sai hiện đỏ kèm gợi ý tên gần giống. |
| **Chạy thử** | Liệt kê chính xác sẽ add gì vào solution nào. **Không ghi gì.** |
| **ADD VÀO SOLUTION** | Add thật, có hộp thoại xác nhận liệt kê các solution đích. |
| **Hủy** | Dừng giữa chừng; những gì đã add vẫn giữ nguyên. |
| **Xuất Excel** | Xuất danh sách đang có ra `.xlsx` (đúng 3 cột như template, nạp lại được ngay). Chọn `.csv` trong hộp thoại lưu nếu muốn CSV. |
| **Lưu báo cáo** | Xuất CSV đầy đủ: type, name, objectid, trạng thái, thông báo — đính kèm ticket hotfix. |
| **Add required components** | Mặc định **tắt**. Bật lên nếu muốn Dataverse tự kéo theo component phụ thuộc. |
| **Gỡ MetadataForArchival** | Mặc định **bật**. Xem mục 5 bên dưới. |

---

## 5. MetadataForArchival - dòng "lạ" cùng tên table

Khi add một Table vào solution, Dataverse **tự** kéo thêm bản ghi `MetadataForArchival` của table đó
(bảng hệ thống giữ metadata cho long-term data retention). Trong màn hình Objects nó hiện thành
dòng thứ hai **trùng tên table**, Type = `MetadataForArchival`, Status = `Off`.

Đây là hành vi của nền tảng, không phải app gửi lên — app chỉ gửi đúng 1 lệnh
`AddSolutionComponent` với `ComponentType = Entity` và `DoNotIncludeSubcomponents = true`.
Add bằng tay trên Power Apps cũng ra kết quả y hệt.

Tick **Gỡ MetadataForArchival** (mặc định bật) để app dọn giúp: sau mỗi lần add table, app tìm bản ghi
`metadataforarchival` gắn với table đó rồi gọi `RemoveSolutionComponent` để loại khỏi solution.

**Quy tắc giữ / gỡ:** app chỉ giữ lại khi `isreadyforarchival = true`, còn lại thì gỡ.

Lưu ý: `isavailableforarchival` **không** dùng để quyết định — cờ này chỉ nói table *đủ điều kiện*
dùng retention và nền tảng tự bật cho hầu hết bảng (bảng `hs_api_log` chưa hề cấu hình retention
vẫn có `isavailableforarchival = True`). Tài liệu Microsoft không định nghĩa rạch ròi hai cờ này,
nên quy tắc trên là lựa chọn dựa trên hành vi quan sát được.

**Vì sao vẫn an toàn:** `RemoveSolutionComponent` chỉ bỏ component **khỏi solution**, không xoá bản ghi
khỏi môi trường và không tắt retention. Gỡ nhầm thì add lại là xong. Mọi quyết định đều in ra Nhật ký
kèm giá trị cờ, ví dụ:

```
· archival: 'hs_api_log' chưa bật retention (isavailableforarchival=True, isreadyforarchival=False, statecode=1) -> sẽ gỡ khỏi solution
· archival: giữ 'hs_contract': retention đã bật (isreadyforarchival=True)
```

Nếu tổ chức bạn **có** dùng long-term retention, xem log trước khi chạy hàng loạt, hoặc bỏ tick.

Chạy lại app trên table đã lỡ add cũng dọn được: dòng table báo "đã có", phần MetadataForArchival vẫn bị gỡ.
Muốn xem trước mà không đụng gì, bấm **Chạy thử** — log in ra `[sẽ gỡ] MetadataForArchival ...`.

---

## 6. Đăng nhập

---


**Device code (mặc định)** — không cần app registration. Client id dùng sẵn là public client
của Power Platform tooling (`51f81489-...`). Quyền add component = quyền của chính tài khoản bạn,
nên cần role **System Customizer** hoặc **System Administrator**.

**Client secret** — khi bạn chỉ có `Client id` + `Client secret` + `Url`:

1. Tick **Dùng Client secret (app registration)**.
2. Điền **Môi trường** = URL, **Client id**, **Secret**.
3. Ô **Tenant** cứ **để trống** — app tự dò Tenant ID từ URL môi trường
   (gọi thử API không token, đọc `WWW-Authenticate: ... authorization_uri=.../{tenantId}/...`)
   rồi điền ngược lại vào ô Tenant cho bạn thấy.
4. Bấm **Kết nối**.

Điều kiện phía Dataverse (làm 1 lần, cần quyền admin):
app registration đó phải có **Application user** trong môi trường và được gán security role
**System Customizer** hoặc **System Administrator**
(Power Platform admin center → Environments → chọn môi trường → Settings → Users + permissions →
Application users → New app user → chọn app theo Client id → gán role).

Nếu thiếu bước này, app lấy được token nhưng Dataverse trả 401/403 — app sẽ báo đúng nguyên nhân
và nhắc bạn tạo Application user.

Refresh token được lưu mã hoá bằng **DPAPI** (chỉ user Windows hiện tại giải mã được),
tại `%APPDATA%\DeployToSolution\token.dat`. **Đăng xuất** sẽ xoá file này.
Client secret **không bao giờ** được ghi xuống đĩa.

---

## 7. Sau khi add xong

App chỉ **add component vào solution**. Các bước còn lại vẫn làm như cũ:
`Publish all customizations` → export solution (managed/unmanaged) → import vào môi trường UAT/PROD.

---

## Cấu trúc mã nguồn

```
src/DeployToSolution/
  Models/            ComponentRow (1 dòng CSV), SolutionTarget, AppSettings
  Services/
    TokenService     OAuth2 device code + client credentials + refresh (không dùng MSAL)
    DataverseClient  Web API v9.2: bearer token, phân trang @odata.nextLink, đọc lỗi
    ComponentCatalog Nạp option set componenttype từ môi trường; tra tên -> GUID cho từng loại
    SolutionService  Liệt kê solution, kiểm tra trùng, gọi action AddSolutionComponent
    CsvService       Đọc/ghi CSV (phẩy / chấm phẩy / tab, có nháy kép)
    SettingsStore    settings.json + refresh token mã hoá DPAPI
  ViewModels/        MainViewModel: kết nối, resolve, dry run, chạy thật, log
  MainWindow.xaml    Giao diện
```
