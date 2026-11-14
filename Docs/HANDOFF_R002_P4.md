# Handoff — R-002 เฟส 4: Settings live preview + Cancel/ปิดหน้าต่างคืนค่า

Coding Owner → Writing Owner (Antigravity) · branch `agent/antigravity-settings-preview` · ฐาน `feat/csharp-rewrite` @ c8b3d35 (หลังเฟส 3)
อ้างอิง 1.3.3 (อ่านอย่างเดียว): `D:\Dev\Apps\AIMonitor\ai_usage_monitor\settings_dialog.py` (`_preview_*`, `reject`, `_entry`), `main_window.py` (`_apply_appearance`), `settings.py` (`WINDOW_SIZE_OPTIONS`, `OPACITY_MIN/MAX`)

## ช่องว่างที่พิสูจน์จากโค้ด 2.0
- `SettingsViewModel` พรีวิวได้เฉพาะ Theme; opacity และ always-on-top ไม่มีพรีวิว (1.3.3 พรีวิว theme/opacity/on-top/window size)
- ปิด dialog ด้วยปุ่ม X / Esc / Alt+F4 **ไม่คืนค่า theme** (มีแต่ปุ่ม Cancel ที่เรียก `Cancel()`) ทำให้ค่าที่พรีวิวค้างอยู่ทั้งที่ไม่ได้บันทึก
- ไม่มีตัวเลือก "Default window size" (Compact 960×680 / Standard 1120×820 / Wide 1400×900 / Remember last size = 0×0) ทั้งที่ `AppSettings.DashboardWidth/Height` รองรับ
- ไม่มี swatch พรีวิว opacity และไม่แสดงเปอร์เซ็นต์เป็นจำนวนเต็มแบบ 1.3.3 (ช่วง 25–100)

## รอบ 4a — ViewModel + applier + tests (ไม่แตะ XAML)
1. `AppearanceApplier` (ใหม่, `src/AIMonitor.Presentation.Wpf/`): ctor `(Action<string> applyTheme, WidgetViewModel? widget, Action<int,int> resizeDashboard)`; `Apply(AppSettings)`:
   เรียก applyTheme เมื่อ theme ต่างจากที่ apply ล่าสุด (ครั้งแรกเรียกเสมอ), `widget?.ApplySettings(settings)`, และ `resizeDashboard(w,h)` **เฉพาะเมื่อ (w,h) เปลี่ยนจากครั้งก่อนและไม่ใช่ (0,0)** ห้ามแตะเครือข่าย/worker/tray/interval (พรีวิวทุกครั้งที่ลาก slider)
2. `WindowSizeOption` (ใหม่): 4 ตัวเลือกตามตารางข้างบน (ป้ายข้อความตรง 1.3.3 คำต่อคำ) + หา option จาก (w,h) ปัจจุบัน; ค่าที่ไม่ตรง preset → ตกไป Standard
3. `SettingsViewModel`:
   - ctor รับ `Action<AppSettings>? applyPreview = null` เพิ่ม; เก็บค่า "entry" ของ 4 field ที่พรีวิว (Theme, WidgetOpacity, WidgetAlwaysOnTop, DashboardWidth/Height) จาก `session.Current` ตอนเปิด
   - setter ของ Theme/WidgetOpacity/WidgetAlwaysOnTop/`SelectedWindowSize` เรียก `applyPreview(session.Current with { 4 field ที่พรีวิวเป็นค่าปัจจุบันของ dialog })`; **ห้ามเขียนไฟล์ ห้ามเปลี่ยน session** ก่อน Save
   - `WidgetOpacity` clamp 0.25–1.0 (slider แสดงเป็น %, จำนวนเต็ม); เพิ่ม `OpacityPercent` อ่านอย่างเดียวสำหรับ label
   - `Discard()` (public, idempotent): ถ้ายังไม่ Save สำเร็จ ให้ `applyPreview(session.Current with { 4 field = ค่า entry })` ครั้งเดียว (revert เฉพาะ 4 field บน `Current` ล่าสุด ไม่ทับ field อื่นที่เปลี่ยนไประหว่างเปิด dialog); หลัง Save สำเร็จเป็น no-op; `Cancel()` = `Discard()` แล้ว `RequestClose(false)`
   - Save: เขียน DashboardWidth/Height จาก `SelectedWindowSize`; หลัง Save สำเร็จ ห้าม revert
   - ตัด `ThemeManager.Instance.ApplyTheme` ออกจาก VM (ย้ายไปอยู่ที่ applier ผ่าน callback) เพื่อให้ test ไม่แตะ singleton
4. tests (แดงเมื่อย้อน production): applier แต่ละสาขา (theme เมื่อเปลี่ยนเท่านั้น, widget ถูกเรียกทุกครั้ง, resize เฉพาะเมื่อเปลี่ยนและไม่ใช่ 0×0, ไม่ resize ครั้งแรกถ้า 0×0); VM: พรีวิวเรียก callback พร้อมค่าถูกต้องและ session/ไฟล์ไม่เปลี่ยน; Cancel → callback คืน entry; Discard ซ้ำไม่เรียกซ้ำ; Save แล้ว Discard ไม่ revert; revert ไม่ทับ field อื่นที่ถูกเปลี่ยนใน session ระหว่างเปิด dialog (ใช้ BlockingSettingsStore/UpdateAsync จริง); Clear/Cancel ไม่เขียน secret ใด ๆ (ไม่เกี่ยว)

## รอบ 4b — XAML + wiring (ส่งแยก)
`SettingsDialog.xaml`: ComboBox "Default window size" + หมายเหตุ, slider opacity เป็น % พร้อม swatch ("Widget preview" Border ที่ Opacity ผูกกับค่า), ปุ่ม Cancel `IsCancel="True"`; code-behind: `Closing` → `viewModel.Discard()` (ครอบคลุม X/Esc/Alt+F4) ก่อนปิด; `App.OpenSettingsDialog` สร้าง `AppearanceApplier` (theme → `ThemeManager.Instance.ApplyTheme`, widget VM, resize → MainWindow ถ้าเปิดอยู่ในโหมด dashboard) และส่ง `applier.Apply` เป็น `applyPreview`; หลัง Save ปิด dialog ปกติ
tests: STA test ปิด dialog ด้วย `Close()` โดยไม่ Save แล้ว callback ได้ค่า entry; Save แล้วปิดไม่ revert; contract test ว่า `App.OpenSettingsDialog` ส่ง callback

## Constraints
ไม่ push/merge · trailer `Assisted-by: Antigravity (Google)` · ห้าม NuGet ใหม่ · ห้ามแตะ schema `AppSettings` · ห้ามให้ preview แตะเครือข่าย/worker · ถ้ากติกาขัดกับโค้ดจริงให้หยุดรายงาน
