# Repository instructions for Codex and compatible agents

เอกสารกลางของโครงการอยู่ที่:

- `Docs/AI_WORKFLOW.md` — workflow, authority, handoff และ Definition of Done
- `Docs/ARCHITECTURE.md` — system boundaries, dependency rules และ data flow
- `Docs/TESTING.md` — commands และ verification policy
- `Requirements.md` — สิ่งที่ตกลงกันไว้

อ่านเฉพาะเอกสารที่เกี่ยวข้องกับงาน: ใช้ `Docs/ARCHITECTURE.md` เมื่อเปลี่ยน boundaries, interfaces, storage หรือ data flow และใช้ `Docs/TESTING.md` เมื่อแก้ behavior, tests, build หรือ CI

กติกาหลัก:

- รักษาการเปลี่ยนแปลงให้ตรง scope และอย่าเขียนทับงานที่มีอยู่
- ตรวจ Git diff ก่อนและหลังแก้ไข
- รัน verification ตามความเสี่ยงและแก้ failure ที่เกิดจากงานนี้
- อย่าอ้างว่า test ผ่านหากไม่ได้รันจริง
- ห้าม deploy, publish, ลบข้อมูล, เปลี่ยน production หรือเปิดเผย secrets โดยไม่มีคำสั่งชัดเจน
- ห้ามใส่ `Co-Authored-By:` ที่ชี้ไปหา agent ใน commit ใช้ `Assisted-by: <เครื่องมือ> (<ผู้ให้บริการ>)` ตามข้อ 5.4 ของ `Docs/AI_WORKFLOW.md`
- เมื่อส่งมอบ ให้สรุปผล ไฟล์ที่เปลี่ยน คำสั่งที่รัน ผลลัพธ์ และความเสี่ยงที่เหลือ

Local tests ใช้ disposable fixtures และไม่มี production access: `YES` — ใช้เฉพาะ synthetic data และห้ามใช้ credentials จริง

หากงานเกี่ยวข้องกับ OpenAI API, ChatGPT หรือ Codex ให้ใช้ official OpenAI documentation ที่เป็นปัจจุบันก่อนตัดสินใจด้าน API หรือผลิตภัณฑ์
