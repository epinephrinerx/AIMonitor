# Review — R-002 เฟส 2: settings มีผลทันทีกับ widget และ tray

branch `agent/antigravity-widget-tray-live-settings` · commits `a4d18ab` (งานรอบ 1 + แก้ test) และ `c2ba166` (แก้ตามรีวิว)
Reviewer 1 = Claude Fable 5.1 (High) · Reviewer 2 = Codex gpt-6.1-sol (High, read-only) · ทำงานแยกกัน ไม่เห็นผลของกันและกัน

## ผลตรวจก่อนรีวิว (Coding Owner)

รอบแรกของ Antigravity แก้ตรรกะใน `App` ตรง ๆ แล้ว mutation 4 จุดเขียวหมด จึงส่งกลับให้ย้ายตรรกะไป `LiveSettingsApplier`
ที่ทดสอบพฤติกรรมได้ หลังรอบสอง mutation ทุกจุดแดง ยกเว้น 1 จุดที่เป็น contract test เก่าค้าง (ส่งกลับแก้) และ `TrayPolicy.ShouldShowTray`
ที่เป็น identity ของ `ShowTrayIcon` จึงไม่มีพฤติกรรมให้แยก (ยอมรับ)

## Triage

| # | ผู้พบ | ข้อค้นพบ | ผลตรวจ | การตัดสินใจ |
|---|---|---|---|---|
| 1 | ทั้งสอง | ปิด tray จาก Settings ตอนทุกหน้าต่างซ่อนอยู่ → ไม่มี UI และไม่มี tray | จริง (ไล่ขั้นตอนจากโค้ด) | แก้: `LiveSettingsApplier` รับ `isAnyWindowVisible`/`showDashboard`; test พฤติกรรม |
| 2 | Codex | refresh ล้มทั้งหมดเขียนทับ cache ที่ใช้ replay ตอนสร้าง tray ใหม่ | จริง | แก้: เก็บ cache เฉพาะเมื่อมีอย่างน้อยหนึ่ง reading ที่ `HasData`; test ตรวจเนื้อหา |
| 3 | Codex | ปิด tray แล้วปิด dashboard → `_mainWindow` ชี้หน้าต่างที่ปิดแล้ว, launch ซ้ำ `Show()` ล้ม | จริง | แก้: `Closed` เคลียร์ field; **ไม่มี automated test** (ต้องใช้ WPF window จริง) |
| 4 | Codex | OnExit dispose tray โดยไม่ detach | จริง (ผลเบา) | แก้: `LiveSettingsApplier.Shutdown()`; test พฤติกรรมของ Shutdown แต่ **การเรียกใน OnExit ไม่มี test** |
| 5 | Fable | ทุกการเปลี่ยน settings (รวม geometry) รีเซ็ต countdown ของ timer | จริง (เดิมมีอยู่) | แก้: ตั้ง interval เมื่อค่าเปลี่ยนเท่านั้น; test |
| 6 | Fable | `ContextMenuStrip` รั่วทุกครั้งที่สร้าง tray ใหม่ | จริง | แก้: Dispose; ไม่มี test (ผล native handle) |
| 7 | ทั้งสอง | test แบบอ่านซอร์ส/นับครั้งไม่ตรวจพฤติกรรมจริง (หลายตัว) | จริง | แก้: ลบตัวไร้ค่า, เสริม contract (Changed subscription, InvokeAsync, ลำดับ `ShouldHideOnClose` → `e.Cancel` → `Hide()` โดยตัดคอมเมนต์), replay ตรวจเนื้อหา, วงจร true→false→true |

## Mutation หลังแก้ (ทุกจุดแดงยกเว้นที่ระบุ)

ลบ `showDashboard` · ตั้ง interval ทุกครั้ง · เก็บ cache ทุกครั้ง · `Shutdown` ไม่ detach · ลบ `Changed +=` · ลบ `Hide()` ใน `OnClosing` · (เฟสแรก) ลบ ApplySettings/AttachTray/สลับลำดับ/ลบ Apply ตอนเริ่ม/ลบ ShouldStartHidden
**เขียว (ยอมรับ ต้องตรวจมือ):** ลบ `Shutdown()` ใน `OnExit`, ลบ `Closed` handler, ไม่ Dispose `ContextMenuStrip`

## ผลรวม

build 0 warning · 997 → 1003 tests ผ่าน · `dotnet format` สะอาด

## ตรวจมือบนแอปจริง (🧪 รอผู้ใช้)

1. เปลี่ยน opacity / always-on-top ใน Settings → Widget เปลี่ยนทันที
2. ติ๊ก/เลิกติ๊ก Show tray icon → ไอคอนโผล่/หายทันที เปิดใหม่แล้วค่าล่าสุดแสดงเลย
3. ปิด dashboard (ซ่อนลง tray) → เปิด Settings จาก tray → เลิกติ๊ก tray → Save → dashboard ต้องเด้งขึ้น
4. Exit จาก tray ปกติ ไม่ค้าง process
