# Review — R-002 เฟส 1 (settings แหล่งเดียว)

Writing Owner: Antigravity (Gemini 3.8 Flash, High) · Reviewer 1: Claude Code (Fable 5.1, High) · Reviewer 2: Codex (gpt-6.1-sol, High)
ช่วง commit: `5dca791..96b334a` แล้วแก้ปิด findings ใน commit ถัดมา · Reviewer ตรวจแยกกัน ไม่เห็นรายงานของกัน (static review)

## การตรวจของ Coding Owner ก่อนส่ง Reviewer

ทดลองย้อนโค้ด (mutation) ทีละจุดแล้วดูว่า test แดงจริงหรือไม่ พบ 2 จุดที่เขียวทั้งที่ย้อนแล้ว:
`SettingsViewModel` กลับไปใช้สำเนาเก่า และ `MainWindow`/`WidgetWindow` เขียนสำเนาเก่าเองแทนผ่าน recorder
(Writing Owner อ้างว่า test แดง ซึ่งไม่จริง) ส่งกลับแก้ก่อนเข้ารีวิว

## Findings และคำตัดสิน

| # | ที่มา | ข้อหา | คำตัดสิน |
|---|---|---|---|
| A | Fable 1 | Save ล้ม → exception ใน async void ปิดโปรเซส | ยืนยัน แก้: ตัวห่อคำสั่งจับ IOException/UnauthorizedAccessException, dialog ค้างไว้, เหตุการณ์ `SaveFailed` |
| B | Codex 2 | `OnExit` flush หลังปล่อย mutex ทำให้ instance ใหม่โหลดค่าเก่า | ยืนยัน แก้: flush ก่อนปล่อยทรัพยากรอื่น |
| C | Codex 1 | flush หมดเวลา 2 วินาทีแล้ว Dispose ทิ้งงานค้าง | ยืนยันบางส่วน ลดจาก High เป็น Low (ต้องเขียน JSON เล็กค้างเกิน 2 วินาที) แก้: ไม่ Dispose เมื่อหมดเวลา |
| D | Codex 3 | `Changed` ยิงขณะถือ gate: ผู้ฟังที่รอ session จะ deadlock | จริงเชิงทฤษฎี ไม่แก้โค้ด (ย้ายออกนอก gate ทำให้ลำดับ event สลับ) เขียนข้อกำหนดใน XML doc; ผู้ฟังปัจจุบันใช้ `InvokeAsync` |
| E | Fable 2, Codex 4 | AC4 เขียวแม้ `FlushAsync` ว่าง | ยืนยัน (ตรงกับ mutation) เขียนใหม่ด้วย store ที่บล็อกได้ |
| F | Codex 5, Fable 5, W1/W2 | test ไม่สร้างสภาวะแย่งกัน / test โครงสร้างผ่านแม้ข้าม recorder | ยืนยัน เขียน contention test และ contract ใหม่ |
| G | Codex 6 | test concurrency เขียวแม้ถอด serialization | **ไม่เห็นด้วย** mutation ถอด gate ทำให้ test แดง 6 ตัว; เพิ่ม test ที่บังคับให้ซ้อนกันจริงอยู่แล้ว |
| H | Fable 3, 4 | AC5 พึ่งเวลา, AC3 ไม่ใช้ store จริง | ยืนยัน แก้ในชุดเดียวกับ E |

## ผลหลังแก้ (Integrator: Coding Owner)

- `dotnet build`: 0 warning / 0 error · `dotnet format --verify-no-changes`: ผ่าน
- test ทั้งหมด 966 กรณีผ่าน (Domain 218 · Application 62 · Presentation 42 · Infrastructure 644) เดิม 939
- mutation ที่แดงตามต้องการ: ถอด gate · assign ก่อน save · `FlushAsync` ว่าง · Release ไม่ดัก · recorder เขียน snapshot เก่า · recorder จับ `Current` ก่อนเข้าคิว · `MainWindow`/`WidgetWindow` ข้าม recorder · `SettingsViewModel` สำเนาเก่า · ฟิลด์ `AppSettings` ในหน้าต่าง

## ความเสี่ยงที่เหลือ

- ลำดับใน `App.OnExit` (flush ก่อนปล่อย mutex, ข้าม Dispose เมื่อหมดเวลา) ทดสอบอัตโนมัติไม่ได้ เพราะอยู่ใน `App`
- Reviewer ตรวจจากโค้ดอย่างเดียว ไม่ได้รันแอป ต้องให้ผู้ใช้ตรวจรับบนแอปจริง
