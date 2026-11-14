# Handoff — R-002 เฟส 1: settings แหล่งเดียว

จาก Coding Owner (Claude Code) ถึง Writing Owner (Antigravity) · branch `agent/antigravity-settings-single-source`
ฐาน: `feat/csharp-rewrite` @ commit `5dca791`

## Objective

แก้บั๊ก **ค่าตั้งถูกเขียนทับตอนปิดหน้าต่าง** โดยทำให้ settings มีแหล่งความจริงเดียวในหน่วยความจำที่ทุกส่วนใช้ร่วมกัน

### บั๊กที่พิสูจน์แล้วจากโค้ด

- `MainWindow` และ `WidgetWindow` แต่ละตัวถือ `AppSettings` สำเนาตอนสร้าง (`_currentSettings`)
- `SaveGeometry()` เรียก `WindowGeometryManager.SavePlacement(_currentSettings, ...)` แล้ว `_ = _settingsStore.SaveAsync(updated)`
  ซึ่งเขียน **สำเนาเก่า** ทับทั้งก้อน
- `SettingsViewModel.SaveAsync()` เขียน settings ใหม่ลงไฟล์ แต่หน้าต่างทั้งสองไม่รู้ ผลคือ theme, interval,
  opacity ฯลฯ ที่เพิ่งบันทึกถูกย้อนกลับเมื่อปิดหน้าต่าง
- `SaveAsync` ถูกเรียกแบบ fire-and-forget (`_ =`) ใน `Closing` ตอนแอปกำลังออก อาจไม่ทันเขียนเสร็จ
- `App.OpenSettingsDialog` โหลดค่าใหม่เข้า `App._currentSettings` แต่ไม่ส่งต่อให้ใคร

## Scope (อนุญาตให้แก้)

- ใหม่: `src/AIMonitor.Application/Settings/SettingsSession.cs` (ไม่พึ่ง WPF)
- แก้: `src/AIMonitor.Presentation.Wpf/App.xaml.cs`, `MainWindow.xaml.cs`, `WidgetWindow.xaml.cs`,
  `ViewModels/SettingsViewModel.cs`, `ViewModels/MainWindowViewModel.cs` (ถ้าจำเป็น)
- ใหม่/แก้ tests ใน `tests/AIMonitor.Application.Tests/Settings/` และ `tests/AIMonitor.Presentation.Wpf.Tests/`

## ข้อกำหนดของ `SettingsSession`

- ถือ `AppSettings Current` ค่าเดียวต่อแอป สร้างจาก `ISettingsStore` ที่ `App` มีอยู่แล้ว
- `Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default)`:
  serialize ด้วย `SemaphoreSlim` — ใช้ `change` กับ `Current` **ล่าสุด** ณ ตอนได้คิว, `Normalize()`, เขียนลง store,
  แล้วค่อยตั้ง `Current` และยิง event `Changed(AppSettings)`
- ถ้า store เขียนล้มเหลว ห้ามเปลี่ยน `Current` และต้องโยน exception ให้ผู้เรียกเห็น (ไม่กลืน)
- `Task FlushAsync()` รอให้ update ที่ค้างอยู่เสร็จทั้งหมด
- ห้ามอ่านไฟล์ซ้ำจาก store ในทุก `UpdateAsync` (ใช้ `Current` ในหน่วยความจำ) แต่ `LoadAsync` ครั้งแรกตอนสร้างผ่าน factory `CreateAsync(store)`
- ห้ามแตะ `secrets.dat` หรือ `DpapiSecretStore` ในเฟสนี้

## การเปลี่ยนที่ต้องทำ

1. `App`: สร้าง `SettingsSession` หลัง migration/โหลดครั้งแรก ส่งให้ `MainWindow`, `WidgetWindow`, `SettingsViewModel`, `MainWindowViewModel`
   แทนการส่ง `ISettingsStore` + `AppSettings` สำเนา และเลิกถือ `_currentSettings` ซ้ำ
2. `MainWindow`/`WidgetWindow`: `SaveGeometry` เปลี่ยนเป็น `await session.UpdateAsync(s => WindowGeometryManager.SavePlacement(s, ...))`
   อ่านค่าที่ใช้ตอน restore จาก `session.Current`
3. `SettingsViewModel.SaveAsync`: ใช้ `session.UpdateAsync(s => s with { ... })` แทนการสร้างจากสำเนา `_originalSettings`
   (เปลี่ยนเฉพาะ field ที่ dialog เป็นเจ้าของ ไม่เขียนทับ geometry/providers ที่เปลี่ยนไประหว่างเปิด dialog)
4. `App.OnExit`/ปิดหน้าต่างจริง: `await session.FlushAsync()` ก่อนปล่อย resource ให้ geometry ที่บันทึกตอนปิดไม่หาย
   (ถ้าเป็น `void` event handler ที่ await ไม่ได้ ให้ block ด้วย timeout สั้น ๆ ที่ OnExit และบันทึกเหตุผลใน comment)
5. `App.OpenSettingsDialog`: เลิก `Task.Run` โหลดซ้ำจากไฟล์ ใช้ event `Changed` ปรับ refresh timer interval

## Non-goals

- ไม่ผูก opacity/topmost/ShowTrayIcon เข้ากับ widget/tray (เฟส 2)
- ไม่เพิ่ม UI ใหม่ ไม่แตะ XAML
- ไม่เปลี่ยน schema ของ `AppSettings` หรือ `JsonSettingsStore`
- ไม่ refactor ส่วนที่ไม่เกี่ยวข้อง ไม่เพิ่ม NuGet dependency

## Acceptance criteria

1. บันทึก Settings (เช่น theme = dark, refresh interval = 300) แล้ว `MainWindow.SaveGeometry` ทำงาน ค่าใน `Current` และในไฟล์ยังเป็นค่าที่เพิ่งบันทึก และ geometry ถูกอัปเดต
2. `UpdateAsync` สองงานพร้อมกัน (แก้คนละ field) ได้ผลทั้งสองค่า ไม่มีค่าไหนหาย
3. `UpdateAsync` ที่ store ล้ม: `Current` ไม่เปลี่ยน, exception ถึงผู้เรียก
4. `FlushAsync` คืนเมื่อ update ที่ค้างอยู่เสร็จทั้งหมด
5. ปิดแอปทันทีหลังบันทึกค่าตั้ง ค่าไม่ย้อนกลับ (ทดสอบผ่าน session ไม่ต้องเปิดหน้าต่างจริง)

## ข้อกำหนดของ tests (สำคัญ)

- ทุก test ต้อง **แดงเมื่อย้อน production code** ให้รันยืนยันและรายงานผล (ย้อนเฉพาะส่วนที่ test คุ้มครอง)
- อย่าใช้ fake ที่บางจนไม่ผ่านตรรกะจริง: ใช้ `JsonSettingsStore` จริงกับไฟล์ใน temp dir สำหรับ test ของ session
- ห้ามใช้ real credentials, network, registry จริง

## Commands (ต้องรันและแนบผล)

```
dotnet build AIMonitor.sln
dotnet test AIMonitor.sln --no-build
dotnet format AIMonitor.sln --verify-no-changes
```

ค่าฐานก่อนแก้: 939 tests ผ่าน, build 0 warning/0 error

## Constraints

- commit บน branch ของคุณเท่านั้น ห้าม merge, push, force-push, ลบ branch
- commit message ท้ายบรรทัดใส่ `Assisted-by: Antigravity (Google)` ห้าม `Co-Authored-By:`
- ถ้าพบว่ากติกาในเอกสารนี้ขัดกับโค้ดจริง ให้หยุดและรายงาน อย่าเลือกเอง

## Open questions / known risks

- `SaveGeometry` ใน `Closing` เป็น sync event: ต้องเลือกวิธีรอที่ไม่ทำให้ UI thread deadlock (ระวัง `.Result`) ให้อธิบายทางเลือกที่ใช้
- ผู้ตรวจรับ UI จริงคือผู้ใช้ (🧪 ไม่ใช่ ✔️)
