# Repository instructions for Claude Code

ใช้เอกสารเดียวกับเครื่องมืออื่นเพื่อป้องกันกฎขัดกัน:

- `Docs/AI_WORKFLOW.md` — workflow, authority, handoff และ Definition of Done
- `Docs/ARCHITECTURE.md` — system boundaries, dependency rules และ data flow
- `Docs/TESTING.md` — commands และ verification policy
- `Requirements.md` — สิ่งที่ตกลงกันไว้

อ่านเฉพาะเอกสารที่เกี่ยวข้องกับงาน ไม่ต้องอ่านทุกไฟล์สำหรับการแก้ไขเล็กน้อย

กติกาหลัก:

- รักษา diff ให้เล็ก ตรง scope และสอดคล้องกับรูปแบบเดิม
- ตรวจ Git status/diff และรักษา uncommitted work ของผู้อื่น
- อย่าแก้ generated files โดยตรงหรือเพิ่ม dependency โดยไม่มีเหตุผล
- รัน targeted checks ก่อน แล้วขยายตามความเสี่ยงตาม `Docs/TESTING.md`
- อย่าอ้างว่า test ผ่านหากไม่ได้รันจริง
- ห้าม deploy, publish, ลบข้อมูล, เปลี่ยน production หรือเปิดเผย secrets โดยไม่มีคำสั่งชัดเจน
- เมื่อทำหน้าที่ reviewer อย่าแก้โค้ด ให้รายงานเฉพาะ findings ที่พิสูจน์ได้ พร้อมไฟล์/บรรทัด ผลกระทบ และวิธี reproduce
- เมื่อส่งมอบ ให้สรุปผล ไฟล์ที่เปลี่ยน คำสั่งที่รัน ผลลัพธ์ และความเสี่ยงที่เหลือ

เมื่อทำงานร่วมกับ Antigravity หรือเครื่องมืออื่น ให้มี writing owner หนึ่งตัวต่อ task/branch และใช้ commit SHA หรือ diff พร้อม handoff contract ใน `Docs/AI_WORKFLOW.md`

Commit: **ห้ามใส่ `Co-Authored-By:` ที่ชี้ไปหา agent** ใช้ `Assisted-by: <เครื่องมือ> (<ผู้ให้บริการ>)` ตามข้อ 5.4 ของ `Docs/AI_WORKFLOW.md`

