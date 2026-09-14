# แนวทางทำงานร่วมกันในโปรเจกต์ AI Usage Monitor

อ่าน [README.md](README.md) สำหรับพฤติกรรมโปรแกรม สถาปัตยกรรม และวิธี build
ไฟล์นี้บันทึกข้อกำหนดและบริบทสำหรับ Claude Code และผู้พัฒนาที่รับงานต่อ

## โปรเจกต์ที่ต้องแก้

- แอป Windows ใช้ Python + PySide6 มี Dashboard, Widget และ System tray
- ซอร์สหลักคือ `ai_usage_monitor/`; entry point คือ `run_ai_monitor.py`
- `claude_monitor/` และ `run_app.py` เป็นโปรแกรมรุ่นเก่า อย่าใช้เป็น entry point ของ AI Usage Monitor
- ใช้ `.venv\Scripts\python.exe` และ `AIUsageMonitor.spec` สำหรับ portable build
- ตรวจ `git status` ก่อนแก้ และรักษางานที่ยังไม่ได้ commit ของผู้ใช้หรือผู้ช่วยอื่น

## ข้อกำหนด OpenAI ที่ผู้ใช้ต้องการ

แสดงโควตาจากการล็อกอิน Codex ให้คล้าย Claude: เปอร์เซ็นต์การใช้และเวลารีเซ็ต
พร้อมประวัติ token เท่าที่บริการส่งกลับมา

1. `OpenAIProvider.detect()` ต้องเลือก Codex ChatGPT OAuth ก่อน Admin key ที่บันทึกไว้หรืออยู่ใน environment
2. หากพบ OAuth แต่หมดอายุหรือไม่สมบูรณ์ ให้แสดงคำแนะนำแก้การล็อกอิน ห้ามสลับไปแสดงค่าใช้จ่าย API โดยเงียบ ๆ
3. หากไม่พบ Codex ChatGPT OAuth จึงใช้ลำดับเดิม: saved key → environment key → CLI API key
4. เก็บ Admin key เดิมไว้ ไม่ต้องลบ key หรือรีเซ็ต Registry เพื่อเปิดใช้โควตา Codex
5. Ordinary project keys ใช้อ่าน organization spend ไม่ได้ ต้องแสดงสถานะ Limited

`providers/sources.py` เก็บรายการแหล่งข้อมูลตามลำดับทั่วไป ส่วนข้อยกเว้น Codex-first
อยู่ใน `OpenAIProvider.detect()` โดยตั้งใจ อย่าย้ายกลับไปใช้ `resolve()` ทั่วไปอย่างเดียว

## การดึงข้อมูลและข้อจำกัด

- `providers/sources.py`: อ่าน `CODEX_HOME/auth.json` หรือ `~/.codex/auth.json`
- `codex_usage.py`: เรียก Codex App Server ผ่าน stdin/stdout ในโปรเซสซ่อนหน้าต่าง
- `account/rateLimits/read`: อ่านเปอร์เซ็นต์ ช่วงเวลา และเวลารีเซ็ตจริง รวม limit groups เพิ่มเติม
- `account/usage/read`: อ่านสรุปและ daily total tokens ที่มีให้
- ไม่สมมติว่าทุกบัญชีมีช่วงเวลา 5 ชั่วโมง/7 วัน ต้องใช้ค่าจากเซิร์ฟเวอร์
- ประวัติในเส้นทางนี้ยังไม่มี output-token, model/project หรือ dollar breakdown; กราฟรองรับ Total tokens
- ห้ามสร้างเปอร์เซ็นต์จาก token เอง ห้ามเติมวันขาดหายเป็นศูนย์ และห้ามทำโควตาหายเมื่อ history ล้มเหลว
- ใช้ temporary `CODEX_HOME` แยกจากโปรไฟล์จริง ไม่คัดลอก config หรือ refresh token
- ส่ง access token ผ่าน stdin เท่านั้น ไม่ใส่ใน command arguments, log, exception หรือเอกสาร
- ไม่แก้ไฟล์ล็อกอินและไม่รีเฟรช token ของ CLI; ให้ผู้ใช้เปิด Codex เพื่อทำวงจรนี้เอง
- ปิดโปรเซสและ pipe หลังดึงข้อมูลเสร็จหรือผิดพลาด จำกัดเวลารอ และไม่สร้าง thread/turn หรือส่ง prompt
- การยืนยันตัวตนแบบ externally supplied tokens ยังเป็น experimental ตรวจเอกสารและ schema ของ Codex ก่อนเปลี่ยน protocol
- เอกสารอ้างอิง: [Codex App Server](https://learn.chatgpt.com/docs/app-server)

## บันทึกการแก้ไข 2026-09-14

หลังเพิ่ม Codex quota ผู้ใช้รายงานว่าไม่มีข้อมูล หน้าจอ Connections ของโปรแกรมจริง
แสดง `Detected from Admin key saved in this app`: key ที่บันทึกไว้มีลำดับสูงกว่า Codex
จึงเข้าโหมด API spend การทดสอบ provider โดยไม่โหลดค่าตั้งเดิมไม่ครอบคลุมกรณีนี้

แก้ด้วย Codex-first detection และเพิ่ม regression tests สำหรับ saved/environment Admin key,
OAuth หมดอายุ และ Admin fallback เมื่อไม่มี OAuth โดยไม่ลบค่าตั้งเดิม

ผลตรวจ ณ วันดังกล่าว:

- ชุดทดสอบ 14 กรณีผ่าน
- เปิด **ไฟล์ .exe ที่ build จริง** และยืนยันบนแท็บ OpenAI ว่าเกจ Session/Weekly เวลารีเซ็ต
  และกราฟรายวันขึ้นแล้ว ไม่ได้ตรวจเฉพาะการรัน Python จากซอร์ส
- ไฟล์ที่ตรวจผ่านคือ `dist/codex-first/AIUsageMonitor.exe`
- `dist/openai-quota/AIUsageMonitor.exe` เป็น build ก่อนแก้ลำดับบัญชี
- ตัวติดตั้งและทางลัดเดิมยังไม่ได้อัปเดตในการแก้ครั้งนี้ อย่าถือว่าเปิด Start Menu แล้วจะเป็น build ล่าสุด
- เปอร์เซ็นต์ที่เห็นตอนทดสอบเป็นข้อมูลชั่วขณะ ไม่ใช่ค่าคาดหวังสำหรับ tests

## ทดสอบและ build

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
.\.venv\Scripts\python.exe -m ai_usage_monitor
.\.venv\Scripts\python.exe -m PyInstaller AIUsageMonitor.spec --noconfirm --distpath dist/codex-first --workpath build/openai-quota
```

ทดสอบกรณีมี Admin key เดิมควบคู่กับ Codex login ทุกครั้งที่เปลี่ยน detection
ใช้ข้อมูลจำลองสำหรับ automated tests และอย่าแสดงข้อมูลลับเมื่อวินิจฉัยเครื่องจริง

ก่อนลองไฟล์ build ใหม่ ให้ Exit ตัวเดิมจาก Tray และตรวจ executable path ของโปรเซส
ระบบ single-instance อาจเปิดหน้าต่างของรุ่นเก่าแทนไฟล์ที่เพิ่งเรียก โปรแกรม portable แบบ one-file
อาจมี parent/child สองโปรเซสจาก PyInstaller ซึ่งไม่ได้แปลว่ามี monitor สองหน้าต่าง

การ build portable ไม่ได้อัปเดตตัวติดตั้ง หากต้องออกรุ่นติดตั้ง ให้ทำตาม README
และตรวจ `build_ai_installer.ps1` ก่อนใช้ เพราะสคริปต์ปิด monitor และล้าง build outputs
เมื่อเปลี่ยนเวอร์ชันต้องรักษา installer `AppId` เดิมไว้
