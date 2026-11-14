# Review — R-002 เฟส 4: Settings live preview

branch `agent/antigravity-settings-preview` · รอบ 4a `174c65e` · 4b `21a2e6f`, `7f07ee4` · แก้ตามรีวิว (1 commit)
Reviewer 1 = Claude Fable 5.1 (High) · Reviewer 2 = Codex gpt-6.1-sol (High, read-only) · ทำงานแยกกัน

## ที่ทำ
`AppearanceApplier`, `WindowSizeOption`, `DashboardPreviewResizer`; Settings dialog มี Default window size, slider + `%` + swatch "Widget preview"; Cancel/X/Esc เรียก `Discard()` (คืนเฉพาะ 4 ค่าที่พรีวิวบน `Current` ล่าสุด); Save เขียนค่าและปิดโดยไม่ revert

## Triage

| # | ผู้พบ | ข้อค้นพบ | ผล | การตัดสินใจ |
|---|---|---|---|---|
| 1 | ทั้งสอง | Cancel/X ระหว่าง Save ที่ยังเขียน → UI revert แต่ไฟล์เป็นค่าใหม่ | จริง | แก้: `IsSaving`; Discard no-op ขณะ save, ปิด dialog ถูกยกเลิกขณะ save; save ล้มแล้ว Discard ยังทำงาน |
| 2 | Codex | entry = "Remember last size" (0,0) → Discard คืนขนาดหลังพรีวิวไม่ได้ | จริง | แก้: applier ส่ง (0,0) ต่อ, `DashboardPreviewResizer` จำขนาดจริงตอนพรีวิวแรกแล้วคืน |
| 3 | Codex | update อื่น (เช่น geometry) ระหว่างเปิด dialog รีเซ็ต opacity/on-top ที่พรีวิวอยู่ | จริง | แก้: `LiveSettingsApplier` apply widget เฉพาะเมื่อสองค่านี้เปลี่ยน |
| 4 | ทั้งสอง | contract test ของ App ไม่คุม resize callback; test "every call" ไม่ครอบ | จริง | แก้: ย้าย callback เป็นคลาสที่ test ได้ + test ครอบ; "every call" ใช้ค่าเดิมซ้ำ |
| 5 | Fable | ขนาด Wide บนจอเล็กเกินหน้าจอ (1.3.3 ใช้ `_fit_to_screen`) | จริง ผลเบา | **ไม่แก้ตอนนี้**: เก็บเข้าเฟส 5/8 (restore ถูก fit อยู่แล้ว) |
| 6 | Fable | ขนาดที่บันทึกไม่ถูกใช้ตอนเปิดแอป (มีมาก่อน) | จริง นอกขอบเขตเฟส | เก็บเข้าเฟส 5/8 |
| 7 | Fable | พรีวิวแรก apply theme เดิมซ้ำ | จริง ผลเบา | ไม่แก้ (ตามสเปก handoff) |

## Mutation (แดงทุกจุด)
ถอด Discard ใน Closing · ไม่ส่ง applier.Apply · ถอด IsCancel · ถอด binding SelectedItem ของ combo · ถอด `IsSaving` ใน Discard · ไม่ยกเลิกปิดขณะ save · ไม่ใช้ resizer · widget apply ทุกครั้ง/ไม่ apply เมื่อ opacity เปลี่ยน

## ผลรวม
build 0 warning · test: Domain 218, Application 85, Presentation 193, Infrastructure 661 · `dotnet format` สะอาด
**ต้องตรวจมือ:** พรีวิวบนจอจริง (theme/opacity/ขนาด), Esc/X แล้วคืนค่า, Save แล้วปิดไม่ revert, Wide บนจอเล็ก
