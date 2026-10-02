# Review — R-002 เฟส 3: Connections page, ConnectDialog และเส้นทางเขียน key

branch `agent/antigravity-connections` · รอบ 3a `0148147` · 3b-1 `e95d79c` · 3b-2 `bdd55db` · แก้ตามรีวิว (2 commit)
Reviewer 1 = Claude Fable 5.1 (High) · Reviewer 2 = Codex gpt-6.1-sol (High, read-only) · ทำงานแยกกัน

## ปัญหาที่เฟสนี้แก้
- key/budget/project ถูกอ่านครั้งเดียวตอนเริ่มแอป → ใส่ key ใหม่แล้วไม่มีผลจนรีสตาร์ท และไม่มีโค้ดเขียน `secrets.dat` หลัง migration
- ไม่มีหน้า Connections / Re-detect / ConnectDialog

## ที่ทำ
`ProviderConnectionStore` (secret ก่อน แล้วค่อย settings ผ่าน `SettingsSession`; แทนที่/ลบ/ไม่แตะ) · `ReconfigurableQuotaClient` (สร้าง client ใหม่เมื่อ key/extra เปลี่ยน) · view models ของการ์ด/หน้า/dialog · `ConnectionsPage`, `ConnectDialog`, ปุ่ม Connections บน header

## Triage ของรีวิว

| # | ผู้พบ | ข้อค้นพบ | ผล | การตัดสินใจ |
|---|---|---|---|---|
| 1 | Fable (high) | Save จับ exception แคบ; store จริงโยน Win32Exception/InvalidDataException/JsonException → โปรเซสล้ม | จริง | แก้: จับทุกอย่างยกเว้น cancel, ข้อความทั่วไปไม่มีค่า key; test ใช้ exception จริงที่ฝัง key ในข้อความ |
| 2 | ทั้งสอง | Save/Re-detect ระหว่าง refresh ถูกทิ้ง | จริง | แก้: pending-refresh รันอีกรอบเดียวเมื่อรอบปัจจุบันจบ |
| 3 | Fable | provider ล้ม → การ์ดค้าง "Checking..." | จริง | แก้: คง detection เดิม, ถ้าไม่เคยมี → "Unavailable" + error |
| 4 | Fable | อ่าน secrets.dat ทุก poll; ไฟล์เสีย → OpenAI/Gemini ล้มทุกรอบ | จริง ผลเบา | **ไม่แก้**: ก่อนหน้านี้พังตอนเปิดแอปอยู่แล้ว; ข้อความผิดพลาดปลอดภัย; บันทึกเป็นข้อจำกัด |
| 5 | ทั้งสอง | dispose inner client ระหว่างที่ request ยังวิ่ง | จริง (latent) | แก้: lease counting + test ด้วย inner ที่บล็อก |
| 6 | ทั้งสอง | ShowAtStartup บันทึกแบบ fire-and-forget | จริง | แก้: revert ค่า + event SaveFailed + MessageBox |
| 7 | Codex (high) | ปิดแอประหว่าง Save ที่ยังเขียน secret → settings ถูก dispose | จริงแต่แคบ (dialog เป็น modal, DPAPI เร็ว) | **ไม่แก้**: บันทึกเป็นความเสี่ยงที่ยอมรับ |
| 8 | ทั้งสอง | tests ไม่ไวพอ: PasswordBox sync ไม่มี test, redaction test ใช้ exception ไม่มี key, token cancel เฉพาะก่อนเริ่ม, concurrency test รันเรียง, contract test หา occurrence แรก | จริงทั้งหมด | แก้ทั้งหมด; ยืนยันด้วย mutation (stale-snapshot ของทั้งสองจุดแดงจริงหลังจัดลำดับใหม่) |

## Mutation (แดงทุกจุด)
Clear ลบทันที · Cancel บันทึก · Save ไม่สน Clear · extra ส่งทุกครั้ง · ปิด dialog ตอน save ล้ม · mask เต็ม · ข้อความปุ่ม · ShowAtStartup ไม่บันทึก/ไม่ revert · ไม่เรียก Connections.Update · catch แคบ · ไม่มี pending refresh · reset detection · dispose ทันที · token ไม่ส่งต่อ (inner/store) · ไม่ผูก PasswordChanged/TextChanged · refresh ก่อน ShowDialog · stale snapshot (ConnectionsViewModel, ProviderConnectionStore) · แถบ tab ไม่ซ่อน · Save โชว์กับ Claude · ไม่ผูก RequestConnect
**เขียว (ต้องตรวจมือ):** Exit ระหว่าง Save (ข้อ 7)

## ผลรวม
build 0 warning · tests ผ่านทั้งโซลูชัน (Domain 218, Application 84, Presentation 148, Infrastructure 661) · `dotnet format` สะอาด

## ตรวจมือบนแอปจริง (🧪 รอผู้ใช้)
1. เปิดปุ่ม Connections บน header → เห็น 3 การ์ด สถานะมีสัญลักษณ์+คำ; Re-detect all ทำงาน; ติ๊ก "Show this page at startup" แล้วเปิดแอปใหม่ตรงตามที่เลือก
2. Connect OpenAI: พิมพ์ key → Save → การ์ดอัปเดตทันทีโดยไม่ต้องรีสตาร์ท; เปิดใหม่เห็น key แบบ mask
3. Clear แล้ว **Cancel** → key ต้องยังอยู่; Clear แล้ว Save → ลบ
4. Gemini: Browse เลือกไฟล์ JSON → Save; Claude: เห็นเฉพาะปุ่ม Close และคำแนะนำ
