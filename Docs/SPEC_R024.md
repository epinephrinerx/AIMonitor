# SPEC R-024 — Linux adapters (single-instance · autostart · secret protector)

Requirement: `Requirements.md` R-024 (✅ ตกลงแล้ว 2026-11-20, คำตัดสิน secret store 2026-11-21 "ใช้ libsecret พร้อม fallback ไฟล์")
· เกี่ยวกับ R-022 · Base: branch `linux` ที่ `a0f40ea` · ผู้เขียน spec: Coding Owner (Claude Opus 5.5, High)
· สถานะ: **ร่าง รอบ 9** — Inspector ขั้น A รอบ 1–6 ตัดสินครบแล้ว (§11, §11b, §11c, §11d, §11f, §11g)
· Q-6, Q-7 ผู้ใช้ตอบแล้ว (U7, U8)

spec นี้เขียนให้ Writing Owner และ Test Author ทำงานขนานกันได้จาก spec อย่างเดียว ทุกชื่อ type, signature, path,
ข้อความในไฟล์ และค่าที่คาดถูกกำหนดไว้ตรงนี้ ถ้าเจอสิ่งที่ spec ไม่ได้บอก **ห้ามเดา** ให้ส่งขึ้น Coding Owner

**คำตัดสินของผู้ใช้ (2026-11-21, final — เป็นข้อกำหนด ไม่ใช่คำถามเปิดอีกต่อไป)**
- U1 libsecret เรียกด้วย P/Invoke ตรง ไม่ใช้ `secret-tool` และไม่เพิ่ม NuGet (§3.1)
- U2 ไม่ย้ายกุญแจระหว่าง key file กับ keyring อัตโนมัติในรอบนี้ (S26)
- U3 พฤติกรรม tray/menu bar บน Linux **อยู่นอก R-024** ผู้ใช้จะเปิด **R-025** แยก · `App.axaml.cs` ไม่ถูกแตะ (N2)
- U4 secret store: libsecret เป็นหลัก, key file 0600 เป็น fallback, เทสต์ต้องครอบทั้งสองทาง
- U5 แก้ข้อความ `PlatformHost.cs:47` ("runs on Windows and macOS") ใน R-024 เฟส 3 — เป็นการแก้ Presentation **บรรทัดเดียว** ที่อนุญาต (W3e, N8) · `Requirements.md` R-024 "แตะส่วนไหน" รวมไว้แล้ว (`a832085`)
- U6 race ของ single-instance บน macOS คง non-goal (N9) ไม่เสนอ requirement
- U7 (Q-6 = ก) เพิ่ม `CryptographicException` ใน catch ที่ `ConnectionsViewModel.cs:327` ใน R-024 เฟส 3 (W3f) พร้อมเทสต์ (T3e) · `Requirements.md` R-024 รวมไว้แล้ว (`5f4b066`)
- U8 (Q-7 = ข) ช่องว่างของสวิตช์ "start at login" ใน Settings (ไม่เรียก registrar ทั้ง macOS และ Linux) เป็น requirement ใหม่แยก **นอก R-024** (N11)

**กฎ G-ARG (รอบ 6 — ใช้กับทุก type ใหม่ใน `Linux/` ทั้ง §1–§3):** argument ที่เป็น `string` และเป็น **null → `ArgumentNullException`**; ว่าง/whitespace/ผิดรูป → `ArgumentException` (ชนิดตรงตัว ไม่ใช่ subclass) · `ParamName` = ชื่อพารามิเตอร์ · วิธีที่แนะนำ: `ArgumentException.ThrowIfNullOrWhiteSpace(x)` ซึ่งให้ผลแบบนี้อยู่แล้ว · argument ที่ไม่ใช่ string และเป็น null → `ArgumentNullException` · เทสต์ใช้ `Assert.Throws<T>` (ตรงชนิด) ตามนี้ · ถ้าแถวใดใน spec เขียนแค่ "null/whitespace → `ArgumentException`" ให้อ่านตามกฎนี้

---

## 0. ข้อเท็จจริงที่ตรวจแล้ว (2026-11-21 บนเครื่องนี้ ไม่ได้แตะ keyring จริงของผู้ใช้)

| # | ข้อเท็จจริง | หลักฐาน |
|---|---|---|
| F1 | บน Linux `dotnet test` ของ `PlatformAdaptersTests` ล้ม 4 ข้อ: `CreateStartupRegistrar_PicksTheRunKeyOnWindowsAndALaunchAgentOnMacOS`, `CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOS`, `CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS`, `CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms` (ผ่าน 12, skip 2) | รันจริง; สาเหตุ `throw Unsupported(...)` ที่ `PlatformAdapters.cs:84`, `:103`, `:152` (ข้อสุดท้ายล้มเพราะ `CreateSecretStore` เรียก `CreateSecretProtector` ที่ `:64`) |
| F2 | `IsSupportedPlatform_IsTrueOnTheTwoPlatformsWithAFullSetOfAdapters` (`PlatformAdaptersTests.cs:21-26`) **ผ่านอยู่** บน Linux เพราะทั้งสองข้าง = false แต่จะล้มทันทีเมื่อ `IsSupportedPlatform` (`PlatformAdapters.cs:50`) รวม Linux — ต้องแก้คู่กันในเฟส 3 | อ่านโค้ด |
| F3 | `ISecretProtector` เป็น `internal` อยู่ใน **Infrastructure** (`src/AIMonitor.Infrastructure/Storage/ISecretProtector.cs:3`) ไม่ใช่ Application · `IStartupRegistrar` (`src/AIMonitor.Application/Windows/IStartupRegistrar.cs:8`) และ `ISingleInstanceCoordinator` (`.../ISingleInstanceCoordinator.cs:8`) อยู่ใน Application | อ่านโค้ด |
| F4 | `DpapiSecretStore` **อ่านก่อนเขียน**: `SetAsync`/`RemoveAsync` เรียก `ReadAllAsync` (ซึ่งเรียก `Unprotect` เมื่อ `File.Exists` เป็นจริง) ก่อน `WriteAllAsync` (`DpapiSecretStore.cs:49-51`, `:65-68`, `:79-87`) จึงถ้า `Unprotect` throw ไฟล์เดิมไม่ถูกเขียนทับ · **ขอบเขตของ invariant:** ใช้ได้เมื่อ (ก) มีแอปรันอยู่ **ตัวเดียว** ต่อ scope ต่อผู้ใช้ และ (ข) ไม่มีใครอื่นแก้/ลบ `secrets.dat` ระหว่าง `File.Exists` กับการเขียน · single-instance รับประกัน **เจ้าของ socket ไม่เกินหนึ่งตัวเสมอ** (L8) แต่ **ไม่** รับประกันว่ารันตัวเดียว: เส้นทาง fail-open ใน L9 ปล่อยให้มีตัวที่สองที่รันโดยไม่มี guard ได้ → สองผู้เขียน `secrets.dat` (semaphore ต่อ instance `DpapiSecretStore.cs:12`) → การเขียนล่าสุดชนะ (atomic writer จึงไม่เสียไฟล์ แต่การแก้ของอีกตัวหายได้, R12) · การ **สร้างกุญแจ** ยังกันได้ด้วย key lock (S24) · นอกสมมติฐานนี้ spec รับประกันแค่ว่า protector ไม่ทับ/ลบกุญแจเพราะอ่านไม่สำเร็จ (S24, S25) | อ่านโค้ด |
| F5 | `Environment.GetFolderPath(LocalApplicationData)` บน Linux = `$XDG_DATA_HOME` หรือ `~/.local/share` แต่ **คืน `""` ถ้าโฟลเดอร์นั้นยังไม่มี** ทำให้ `AppDataDirectory` (`PlatformAdapters.cs:53-55`) เป็น path สัมพัทธ์ `AIUsageMonitor` · `SpecialFolderOption.DoNotVerify` คืน path เต็มเสมอ · ค่า env ถูกอ่านใหม่ทุกครั้งที่เรียก · `XDG_DATA_HOME` สัมพัทธ์ถูก .NET ไม่สนใจ | probe app ใน scratchpad, .NET 10 |
| F6 | `libsecret-1.so.0`, `libglib-2.0.so.0`, `libgobject-2.0.so.0`, `libgio-2.0.so.0` มีอยู่ และ export ครบ: `secret_password_lookupv_sync`, `secret_password_storev_sync`, `secret_password_clearv_sync`, `secret_password_free`, `secret_schema_newv`, `secret_schema_unref`, `g_hash_table_new`, `g_hash_table_insert`, `g_hash_table_unref`, `g_str_hash`, `g_str_equal`, `g_error_free`, `g_quark_to_string`, `g_cancellable_new`, `g_cancellable_cancel`, `g_object_unref` (gobject) | `nm -D` |
| F7 | **`secret-tool` ไม่ได้ติดตั้ง** บนเครื่องผู้ใช้ (Ubuntu 26.04, KDE) ทั้งที่ `libsecret-1-0` ติดตั้งอยู่ · `org.freedesktop.secrets` ถูกถือโดย `gnome-keyring-daemon` และมี `ksecretd` ด้วย | `which`, `apt-cache policy`, `busctl --user list` |
| F8 | ค่า enum: quark string `g-dbus-error-quark`, `g-io-error-quark`, `secret-error` · GIOError: NOT_FOUND=1, CANCELLED=19, TIMED_OUT=24, DBUS_ERROR=36 · GDBusError: SERVICE_UNKNOWN=2, NAME_HAS_NO_OWNER=3, NO_REPLY=4, IO_ERROR=5, BAD_ADDRESS=6, NOT_SUPPORTED=7, ACCESS_DENIED=9, NO_SERVER=11, TIMEOUT=12, NO_NETWORK=13, DISCONNECTED=15, UNKNOWN_METHOD=19, TIMED_OUT=20, SPAWN_EXEC_FAILED=23 … SPAWN_NO_MEMORY=34 · SecretError: PROTOCOL=1, IS_LOCKED=2, NO_SUCH_OBJECT=3 … EMPTY_TABLE=9 | python3 `gi` |
| F9 | libsecret บน bus ที่ไม่มีจริง → GError `g-io-error-quark` 1 ทันที · บน bus ส่วนตัวที่ activate `org.freedesktop.secrets` ไม่สำเร็จ → **ค้าง 25 วินาที** แล้วได้ `g-io-error-quark` 24 — จึงต้องมี timeout ของเราเอง | probe ด้วย `dbus-run-session` + HOME/XDG ปลอม (daemon จริงไม่ถูกแตะ) |
| F10 | `.NET` ลบไฟล์ socket เองเมื่อ `Dispose` socket ที่ bind ไว้ · socket ค้างจำลองได้โดย `File.Move` ไฟล์ socket ไปชื่อใหม่ก่อน `Dispose` → `File.Exists`=true, connect ได้ `ConnectionRefused` · symlink ที่ปลายทางไม่มี: `FileInfo.Exists`=true, `File.Exists`=true, `LinkTarget`≠null | probe app |
| F11 | `XDG_RUNTIME_DIR=/run/user/1000` โหมด 700 เจ้าของคือผู้ใช้ | `stat` |
| F12 | ไม่มี `.editorconfig`; CA1416 ทำงานและ `TreatWarningsAsErrors=true` — type `[SupportedOSPlatform("windows")]` ถูกเรียกหลัง guard (`PlatformAdapters.cs:70-72`, `:155-162`) และเทสต์ใส่ attribute เดียวกันที่ class (`tests/.../Windows/WindowsStartupRegistrarTests.cs:11`) | อ่านโค้ด |
| F13 | ผู้เรียก `Register` ใน Avalonia มีที่เดียว: `StartupSettingsReconciler.ReconcileAsync` (`.../StartupSettingsReconciler.cs:26`) ส่ง `Environment.ProcessPath!` (`PlatformHost.cs:111`) ไม่มี arguments · `PlatformHost.StartAsync` จับเฉพาะ `InvalidOperationException`, `IOException`, `UnauthorizedAccessException` (`PlatformHost.cs:115`) | อ่านโค้ด |
| F14 | `LocalSocketSingleInstance` ไม่ผูกกับ macOS: สร้างโฟลเดอร์และ chmod 0700 (`:177-186`), **ลบ path ของ socket โดยไม่มีเงื่อนไขก่อน bind** (`:191`), chmod socket 0600 (`:197-200`), probe ด้วย connect (`:150-171`), ไม่ได้ทั้ง connect และ bind → คืน `true` โดยไม่มี ownership (`:114-117`), จำกัด path 103 byte (`:31`, `:53-60` → `ArgumentException`), ชื่อไฟล์ `si-<12 hex>.sock` (`:73-81`) · **มี race:** สอง launch ที่ probe เสร็จก่อนกันทั้งคู่ ตัวหลังจะลบ socket ที่ตัวแรกเพิ่ง bind แล้ว bind ทับ → สองตัวเป็นเจ้าของ (Inspector ข้อ 1) — แก้ด้วย L8 โดยไม่แก้ไฟล์นี้ · เทสต์เดิมเป็น `[Fact]` รันบน Linux อยู่แล้ว | อ่านโค้ด |
| F15 | `new FileStream(path, …, FileShare.None)` บน Linux ถือ lock แบบ exclusive (flock) ข้าม process และข้าม handle ใน process เดียวกัน: เปิดครั้งที่สองได้ `IOException` (HResult 11) ทั้งใน process เดียวกันและจาก process ลูก · ปิดได้ด้วย env `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` (R9) · `Environment.IsPrivilegedProcess` ใช้ได้ · `DirectoryInfo.LinkTarget` ของ symlink โฟลเดอร์ ≠ null และ `File.GetUnixFileMode` ผ่าน symlink อ่านโหมดของปลายทาง | probe app |
| F16 | AF_UNIX บนเครื่องนี้ (Inspector รอบ 3 ข้อ 2): listener ที่ `listen(1)` และไม่ `accept` → connect แบบ non-blocking ครั้งที่ 3 ได้ **`EAGAIN`** (listener ยังมีชีวิต แต่ต่อไม่ได้) · ไฟล์ socket ค้างที่ไม่มีใครฟัง → **`ECONNREFUSED`** · ไฟล์ธรรมดาที่ path นั้น → `ECONNREFUSED` · ไม่มีไฟล์ → `ENOENT` · ดังนั้น "ไม่มีเจ้าของแน่นอน" แยกจาก "ต่อไม่ได้ด้วยเหตุอื่น" ได้ด้วย `ConnectionRefused` เท่านั้น · เจ้าของไฟล์ `chmod` ไฟล์ของตัวเองจาก 0000 เป็น 0600 ได้โดยไม่ต้องเปิดไฟล์ | probe python ใน `/tmp` (ลบแล้ว) |

---

## 1. Single-instance บน Linux (เฟส 1)

`LocalSocketSingleInstance` ใช้ต่อ **ไม่แก้ไฟล์นั้น** · เพิ่ม decorator `LinuxSingleInstance` ที่ทำให้ "probe + bind" ของหลาย launch
ต่อกันทีละตัวด้วย file lock (ปิด race ใน F14) และตรวจโฟลเดอร์ก่อนส่งต่อ · ทั้งหมดอยู่ใน "adapter ใหม่ของ Linux" ตาม R-024 จึงไม่ขยาย scope

| Id | ที่ไหน | ข้อกำหนด |
|---|---|---|
| L1 | ใหม่ `src/AIMonitor.Infrastructure/Linux/LinuxRuntimeDirectory.cs` | `internal static class LinuxRuntimeDirectory` มี `public const string EnvironmentVariable = "XDG_RUNTIME_DIR";`, `public const int MaxSocketPathBytes = 103;`, `public const int SocketFileNameBytes = 20;` (`"si-"` + 12 hex + `".sock"`), และ `[UnsupportedOSPlatform("windows")] public static string ResolveSocketDirectory(string? xdgRuntimeDir, string fallbackDirectory, string scopedAppId)` — ไม่อ่าน env เอง ไม่สร้าง/ไม่ chmod อะไร |
| L2 | same | `fallbackDirectory` หรือ `scopedAppId` เป็น null/ว่าง/whitespace → `ArgumentException` (null → `ArgumentNullException`) |
| L3 | same | `xdgRuntimeDir` **ใช้ได้** เมื่อครบทุกข้อ (ไม่ trim): (ก) ไม่ null/ว่าง/whitespace และไม่มี `\0` (ข) `Path.IsPathFullyQualified` (ค) `Directory.Exists` (ง) **ไม่ใช่ symlink** (`new DirectoryInfo(x).LinkTarget == null`) (จ) `File.GetUnixFileMode(x)` มีบิต owner ครบ `UserRead\|UserWrite\|UserExecute` และไม่มีบิต group/other ใดเลย (บิตพิเศษ setuid/setgid/sticky ไม่สนใจ) — เช่น `0700` ✓, `1700` ✓, `0750` ✗, `0500` ✗, `0000` ✗ (ฉ) แจกแจงเนื้อหาได้: เรียก `Directory.EnumerateFileSystemEntries(x).GetEnumerator().MoveNext()` ไม่ throw (เมื่อไม่ใช่ root แปลว่าเป็นของผู้ใช้นี้ เพราะโหมด 0700) (ช) ความยาว: `Encoding.UTF8.GetByteCount(Path.Combine(x, scopedAppId)) + 1 + SocketFileNameBytes <= MaxSocketPathBytes` · exception `IOException`/`UnauthorizedAccessException`/`ArgumentException` ระหว่างตรวจ → ใช้ไม่ได้ · ไม่ตรวจเจ้าของด้วย `stat` (ข้อ (ฉ) แทน); root ที่ส่ง `XDG_RUNTIME_DIR` ของคนอื่นมาถือว่ายอมรับ (root ข้ามสิทธิ์ได้อยู่แล้ว) |
| L4 | same | ใช้ได้ → `Path.Combine(xdgRuntimeDir, scopedAppId)` · ใช้ไม่ได้ → `fallbackDirectory` ตามเดิม (ไม่ต่อ scopedAppId, ไม่ตรวจความยาว) · ถ้า fallback ยาวเกิน ctor ของ `LocalSocketSingleInstance` throw `ArgumentException` (`:53-60`) และ `PlatformHost.Create` ล้ม — พฤติกรรมเดียวกับ macOS วันนี้ (R10) |
| L5 | ใหม่ `src/AIMonitor.Infrastructure/Linux/LinuxFileLock.cs` (**เจ้าของ: เฟส 1**; เฟส 3 เรียกใช้อย่างเดียว) | `[SupportedOSPlatform("linux")] internal static class LinuxFileLock` · `public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);` · `public static FileStream? TryAcquire(string lockPath, TimeSpan timeout)` · lockPath null/whitespace/ไม่ fully qualified → `ArgumentException`; timeout ติดลบ → `ArgumentOutOfRangeException` · ลำดับ (Inspector รอบ 3 ข้อ 1 — normalize **ก่อน** เปิด เพราะ 0400/0000 เปิดแบบ ReadWrite ไม่ได้ แต่เจ้าของ chmod ได้, F16): (1) `lockPath` เป็น symlink (`new FileInfo(lockPath).LinkTarget != null`) → `IOException` ข้อความมี `"symbolic link"` ไม่ chmod ไม่เปิด (2) **pre-normalize:** ถ้า `File.Exists(lockPath)` และ `(File.GetUnixFileMode(lockPath) & 0o7777) != 0o600` → `File.SetUnixFileMode(lockPath, 0600)`; ล้ม (`UnauthorizedAccessException`/`IOException` เช่น ไฟล์ของผู้ใช้อื่น) → ส่งต่อให้ผู้เรียก ไม่เปิด · ไฟล์หายระหว่างนี้ (`FileNotFoundException`) → ข้ามไปข้อ 3 · chmod ไฟล์ที่ process อื่นถือ lock อยู่ไม่กระทบ lock นั้น (flock ไม่ขึ้นกับโหมด) (3) เปิดด้วย `FileStreamOptions { Mode = OpenOrCreate, Access = ReadWrite, Share = None, UnixCreateMode = UserRead\|UserWrite }` (4) `IOException` ที่ไม่ใช่ `FileNotFoundException`/`DirectoryNotFoundException`/`PathTooLongException` = ถูกถืออยู่ → รอ `PollInterval` แล้วกลับไปข้อ 1 จนเวลารวม ≥ timeout → คืน `null` (`Zero` = ลองครั้งเดียว) · `UnauthorizedAccessException` จากการเปิด (โหมดถูกเปลี่ยนจากภายนอกหลังข้อ 2) → ส่งต่อ ไม่ลองซ้ำ (5) **post-normalize:** ได้ lock แล้ว ถ้าโหมด ≠ 0600 (เช่นไฟล์ใหม่ที่ umask ตัดบิต) → `SetUnixFileMode(lockPath, 0600)`; ล้ม → dispose stream (ปล่อย lock) แล้วโยน exception เดิมต่อ (6) คืน stream ที่ถือ lock · `DirectoryNotFoundException` ส่งต่อให้ผู้เรียก · ปล่อย lock = `Dispose` · **ไม่ลบไฟล์ lock** · ไม่สร้างโฟลเดอร์แม่ · หลังคืน stream สำเร็จ ไฟล์ lock มีโหมด 0600 พอดีเสมอ · พฤติกรรมเหมือนกันทั้ง root และ non-root สำหรับไฟล์ของผู้ใช้เอง และใช้เหมือนกันกับ key lock ของเฟส 3 (S24(2)) |
| L6 | ใหม่ `src/AIMonitor.Infrastructure/Linux/LinuxSingleInstance.cs` | `[SupportedOSPlatform("linux")] internal sealed class LinuxSingleInstance : ISingleInstanceCoordinator` · `public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);` · `public static readonly TimeSpan DefaultDisposeLockTimeout = TimeSpan.FromSeconds(10);` (> สองเท่าของ connect timeout 3 s ที่ผู้ acquire ถือ lock ได้นานสุด: LP + probe ของ inner) · ctor `public LinuxSingleInstance(string socketDirectory, string scopedAppId, TimeSpan? connectTimeout = null, TimeSpan? lockTimeout = null, TimeSpan? disposeLockTimeout = null)` (ค่าติดลบ → `ArgumentOutOfRangeException`) สร้าง inner = `new LocalSocketSingleInstance(socketDirectory, scopedAppId, connectTimeout)` (ctor สาธารณะเดิม `:41`) · socketDirectory/scopedAppId null → `ArgumentNullException`, ว่าง/whitespace → `ArgumentException` (G-ARG); path ยาวเกิน → `ArgumentException` จาก inner · properties: `SocketDirectory` (ตามที่รับมา), `SocketPath` (= inner), `LockPath` = `Path.ChangeExtension(SocketPath, ".lock")`, `HasOwnership` (= `!_closed && inner.HasOwnership`) · `Activated` add/remove ส่งต่อ inner · `NotifyExistingInstanceAsync` ส่งต่อ inner · `DisposeAsync` ตาม **L7a** (ไม่ส่งต่อตรง) · field `private readonly SemaphoreSlim _gate = new(1, 1);` ให้ `TryAcquireAsync` กับ `DisposeAsync` ของ instance เดียวกัน **ทำทีละตัว** (Inspector รอบ 5 ข้อ 2; ไม่ dispose `_gate`) · `private volatile bool _closed;` และ `internal bool IsClosing => _closed;` (สำหรับเทสต์ P1-26d) · **ctor ไม่แตะดิสก์** |
| L7 | `LinuxSingleInstance.TryAcquireAsync` (Inspector รอบ 2 ข้อ 1) | (0) `await _gate.WaitAsync(ct)`; ปล่อยใน `finally` ครอบทุกข้อด้านล่าง (1) `_closed` → `ObjectDisposedException` (ตรวจ **หลัง** ได้ `_gate`); เป็นเจ้าของอยู่แล้ว → `true` (2) `SocketDirectory` มีอยู่และเป็น symlink หรือเป็นไฟล์ → **probe-only (PO)** โดยไม่ chmod ไม่สร้าง ไม่ลบ (3) `Directory.CreateDirectory(SocketDirectory, UserRead\|UserWrite\|UserExecute)` แล้ว `File.SetUnixFileMode(SocketDirectory, 0700)`; ล้มด้วย `IOException`/`UnauthorizedAccessException` → **PO** (4) `lock = LinuxFileLock.TryAcquire(LockPath, lockTimeout ?? DefaultLockTimeout)`; ได้ `null` (timeout) หรือ throw `IOException`/`UnauthorizedAccessException` → **PO** — **ห้ามเรียก `inner.TryAcquireAsync` โดยไม่ถือ lock** (5) ถือ lock — **locked probe (LP)** (Inspector รอบ 3 ข้อ 2; inner ตีความ timeout ว่าไม่มีเจ้าของแล้วลบ socket `:165`, `:191` ซึ่งไม่แก้): (ก) `!File.Exists(SocketPath)` → ไปข้อ 6 (ข) ไม่งั้น connect `SocketPath` ภายใน connect timeout: ต่อได้ → ปิด socket, **ไม่เรียก inner**, คืน `false` · `SocketException` ที่ `SocketErrorCode == SocketError.ConnectionRefused` **หรือ** หลังล้มแล้ว `!File.Exists(SocketPath)` → ไม่มีเจ้าของแน่นอน (F16) → ไปข้อ 6 · อย่างอื่นทั้งหมด (timeout/`OperationCanceledException` ที่ไม่ใช่ของผู้เรียก, `WouldBlock`/`TryAgain`, `AccessDenied`, error อื่น) = **inconclusive** → **ไม่เรียก inner**, ไม่ลบ ไม่ bind, คืน `true` โดย `HasOwnership=false` (fail-open, L9) (6) `result = await inner.TryAcquireAsync(ct)` (7) dispose lock ใน `finally` ของข้อ 5–6; คืนผล · `OperationCanceledException` จาก `ct` ของผู้เรียกส่งต่อตามเดิม · **PO** = เปิด `Socket(Unix, Stream)` แล้ว `ConnectAsync(new UnixDomainSocketEndPoint(SocketPath))` ภายใน connect timeout (ค่าเดียวกับที่ส่งให้ inner, default 3 s): ต่อได้ → ปิด socket แล้วคืน `false` (มีเจ้าของอยู่ ผู้เรียกจะ `NotifyExistingInstanceAsync`) · ต่อไม่ได้ (`SocketException`/timeout) → คืน `true` โดย `HasOwnership=false` · PO **ไม่ bind ไม่ลบไฟล์ใด ไม่ chmod** |
| L7a | `LinuxSingleInstance.DisposeAsync` (Inspector รอบ 4 ข้อ 1 — inner ปิด listener ซึ่ง .NET unlink path (F10, `:300`) แล้ว `File.Delete(_socketPath)` อีกครั้ง (`:322`) โดยไม่มีเงื่อนไข; ถ้าทำนอก lock ผู้ bind รายใหม่ที่เข้ามาระหว่างสองจังหวะนี้จะเสีย path) | (1) **คำสั่งแรก ทำแบบ synchronous:** ตั้ง `_closed = true` (ต่อจากนี้ `HasOwnership` = false และ `TryAcquireAsync` ที่ยังไม่ได้ `_gate` → `ObjectDisposedException`) (1b) `await _gate.WaitAsync()` ไม่มี timeout — acquire ที่กำลังทำอยู่ทำจนจบก่อน (มีขอบเขต ≤ lockTimeout + 2 × connect timeout) แล้วคืนผลของมันตามปกติ (อาจเป็น `true`) จากนั้น dispose ทำต่อด้วยสถานะ inner หลัง acquire นั้น · ปล่อย `_gate` ใน `finally` · inner ถูก dispose ไปแล้ว → return (no-op) (2) `inner.HasOwnership` เป็น false (ไม่เคยเป็นเจ้าของ — ไม่มี listener และ inner จะไม่ลบอะไร) → `await inner.DisposeAsync()` **โดยไม่ต้องใช้ lock**; จบ (3) เป็นเจ้าของ: `lock = LinuxFileLock.TryAcquire(LockPath, disposeLockTimeout ?? DefaultDisposeLockTimeout)` — **ต้องได้ lock ก่อนเรียก inner** (4) ได้ lock → `await inner.DisposeAsync()` ทั้งหมด (ปิด listener, รอ server loop ≤ 2 s, ลบไฟล์) **ภายใต้ lock**; ปล่อย lock ใน `finally` (5) ได้ `null` หรือ throw `IOException`/`UnauthorizedAccessException`/`DirectoryNotFoundException` → **ไม่เรียก inner** (listener ยังเปิดอยู่และยังเป็นเจ้าของ path จนกว่า process จะจบ — process ที่จบโดยไม่ dispose ทิ้งไฟล์ socket ค้าง ซึ่ง launch ต่อไปได้ `ConnectionRefused` แล้วแทนที่ใต้ lock ตามปกติ) แล้ว return ปกติ ไม่ throw · `DisposeAsync` ครั้งต่อไปลองข้อ 3–5 ใหม่ (ใช้ใน cleanup ของเทสต์) · ระหว่างค้างแบบข้อ 5 `Activated` อาจยังยิงได้ — ยอมรับ (R15) |
| L8 | ผลที่ต้องได้ | **ทุกกรณี: เจ้าของ socket (`HasOwnership=true`) ไม่เกินหนึ่งตัวต่อ `SocketPath`** — ไม่มีเงื่อนไขว่าเจ้าของต้องตอบได้ เพราะ (ก) inner ถูกเรียกเฉพาะใต้ lock (ข) ใต้ lock inner ถูกเรียกเฉพาะเมื่อไม่มีไฟล์หรือได้ `ConnectionRefused` ซึ่งเกิดได้เฉพาะเมื่อไม่มีใครฟัง (F16) และไม่มีใครอื่น bind ได้ระหว่างนั้นเพราะทุกผู้ bind ถือ lock (ค) PO/inconclusive ไม่ bind/ไม่ลบ (ง) **ใน dispose ปกติ** listener ของเจ้าของถูกปิดและไฟล์ถูกลบเฉพาะใน L7a(4) ภายใต้ lock; ไม่ได้ lock ก็ไม่เรียก inner (L7a(5)); acquire กับ dispose ของ instance เดียวกันทำทีละตัว (`_gate`) — Inspector รอบ 4 ข้อ 1, รอบ 5 ข้อ 2 · **ขอบเขตของการรับประกัน (Inspector รอบ 5 ข้อ 1):** ข้อความ "เจ้าของไม่เกินหนึ่ง" ด้านบน **พิสูจน์ได้เฉพาะเมื่อไม่มีเส้นทาง E1** · **E1 (ยกเว้น, ไม่ได้พิสูจน์, ยอมรับ R15):** ใน `LocalSocketSingleInstance.TryListen` (private) **ทุก** exception หลัง `listener.Bind` สำเร็จและก่อน `_listener = listener` — คือ `Listen(backlog: 8)` ล้ม หรือ `File.SetUnixFileMode(_socketPath, …)` ล้ม (`LocalSocketSingleInstance.cs:193-202`) — ถูกจับที่ catch เดียวกัน → **`TryListen` คืน false** โดย socket ที่ bind แล้วไม่มีใครอ้างถึง · จากนั้น `inner.TryAcquireAsync` probe อีกครั้งแล้วคืน `false` (ถ้า socket นั้น listen อยู่) หรือ `true` แบบ fail-open; ทั้งสองกรณี inner ไม่ได้ ownership · socket ที่ถูกทิ้งอาจ unlink path ภายหลังนอก lock เมื่อถูก GC (ไม่ได้ตรวจ) จึงหลัง E1 ไม่มีการรับประกันเจ้าของไม่เกินหนึ่ง · E1 ต้องให้ `Listen` หรือ chmod ไฟล์ใหม่ของผู้ใช้เองในโฟลเดอร์ 0700 ล้ม · ไม่แก้ไฟล์ macOS (U6) · **เจ้าของที่ยังมีชีวิตแต่ต่อไม่ได้** (accept queue เต็ม, ค้าง) ไม่ถูกแทนที่: ตัวที่มาใหม่ได้ `true` ไม่มี ownership (รันแบบไม่มี guard, L9) · ข้อนี้อาศัยว่าทุก process บน Linux ที่ bind `SocketPath` ผ่าน `LinuxSingleInstance` (ไม่มีใครใช้ `LocalSocketSingleInstance` ตรงบน Linux หลัง W1b) · launch พร้อมกันเมื่อ lock ทำงานและไม่มีใครค้างถือ lock เกิน `DefaultLockTimeout`: ตัวเดียวได้ `true` + ownership ที่เหลือได้ `false` · ตัวที่ได้ `false` ส่ง args ถึงเจ้าของผ่าน `Activated` · โฟลเดอร์ socket 0700, ไฟล์ socket 0600, ไฟล์ lock 0600 · ไฟล์ค้างที่ไม่มีใครฟังถูกแทนที่ (ใต้ lock เท่านั้น) · `DisposeAsync` ของเจ้าของลบไฟล์ socket (ไฟล์ lock คงอยู่) |
| L9 | fail-open ที่ยอมรับ | PO ที่ต่อไม่ได้ (L7 ข้อ 2/3/4), LP ที่ inconclusive (L7 ข้อ 5ข — เจ้าของมีชีวิตแต่ต่อไม่ได้; `PlatformHost.TryAcquireSingleInstance` จะเปิดหน้าต่างที่สองแทนการออกเงียบ ๆ `PlatformHost.cs:62-66`) และกรณี inner ไม่ได้ทั้ง connect/bind (`:114-117`): `TryAcquireAsync` = `true`, `HasOwnership` = `false`, `Activated` ไม่ถูกยิง, `NotifyExistingInstanceAsync` ของตัวอื่นที่มาหาตัวนี้ = `false` → อาจมีแอป **รัน** สองตัว แต่เจ้าของไม่เกินหนึ่ง (R12) · กรณีเฉพาะ: ผู้ถือ lock ค้างระหว่าง probe กับ bind นานกว่า `DefaultLockTimeout` → ตัวที่รอ PO ไม่เจอใคร → รันแบบไม่มี guard; ผู้ถือ lock bind สำเร็จตามปกติภายหลังและเป็นเจ้าของตัวเดียว · session ที่ resolve โฟลเดอร์ต่างกันเห็นกันไม่ได้ (R3) |
| L10 | path อื่น ๆ (Inspector ข้อ 8) | ไฟล์/ของอื่นที่ path `si-*.sock` ภายในโฟลเดอร์ของเราถูก inner ลบ (`:191`) — ยอมรับ เพราะเป็นชื่อ hash ในโฟลเดอร์ 0700 ของแอปเอง · บรรพบุรุษของโฟลเดอร์ (เช่น `$HOME` เป็น symlink) ไม่ตรวจ — ยอมรับ |
| L11 | `PlatformAdapters.CreateSingleInstanceCoordinator` | สาขา Linux (W1b): `new Linux.LinuxSingleInstance(Linux.LinuxRuntimeDirectory.ResolveSocketDirectory(Environment.GetEnvironmentVariable(Linux.LinuxRuntimeDirectory.EnvironmentVariable), AppDataDirectory, Scoped(AppId)), Scoped(AppId))` |
| L12 | ขอบเขต | ล็อกต่อ **ผู้ใช้** ไม่ใช่ต่อ session (systemd ให้ `/run/user/<uid>` เดียวกันทุก session) — เหมือน macOS ต่างจาก Windows (R3) · race ของ macOS แบบ F14 ไม่แก้ในรอบนี้ (N9) |

---

## 2. Startup registrar: XDG autostart (เฟส 2)

**กฎ path ที่ไม่ขึ้นกับ host (Inspector ข้อ 3):** ใน §2 "path เต็ม" หมายถึงสตริงที่ขึ้นต้นด้วย `/` และไม่มี `\0` (ไม่ใช้
`Path.IsPathFullyQualified`) และการต่อ path ใช้ `"/"` เป็นตัวคั่นตรง ๆ (ไม่ใช้ `Path.Combine`) จึงได้ผลเดียวกันทุก OS
และเทสต์ pure รันได้บน Windows/macOS/Linux · เทสต์ที่แตะดิสก์จริงเป็น Linux-only

| Id | ที่ไหน | ข้อกำหนด |
|---|---|---|
| A1 | ใหม่ `src/AIMonitor.Infrastructure/Linux/XdgDesktopEntry.cs` | `internal static class XdgDesktopEntry` (pure, ไม่มี platform attribute): `public const string ApplicationName = "AI Usage Monitor";` · `public static string Build(string executablePath, string? arguments = null)` · `public static string FormatExec(string executablePath, string? arguments = null)` · `public static bool? ReadEnabledState(string contents)` |
| A2 | `FormatExec` — ลำดับตรวจ (Inspector ข้อ 12) | (1) `executablePath` null/ว่าง/whitespace → `ArgumentException` (null → `ArgumentNullException`) (2) มีอักขระ `< 0x20` หรือ `0x7F` **ที่ใดก็ได้** ใน `executablePath` หรือ `arguments` (รวมหัว/ท้าย เช่น tab, newline) → `InvalidOperationException` — ตรวจก่อน trim/split (3) `e = executablePath.Trim().Trim('"')` (กฎเดียวกับ `LaunchAgentPlist.Build`, `MacOS/LaunchAgentPlist.cs:56`: quote ตัวท้ายที่เป็นตัวอักษรจริงจึงหายไปด้วย — ยอมรับ) (4) `e` ไม่ขึ้นต้นด้วย `/` → `InvalidOperationException` (F13) (5) tokens = `[e]` ตามด้วย `LaunchAgentPlist.SplitArguments(arguments)` (`:44-47`; ไม่มี quoting dialect — `"a b"` แยกเป็น `"a` กับ `b"`) — เรียกใช้ตรง ๆ ห้ามแก้ไฟล์นั้น |
| A3 | ต่อ token `t` | (1) ต้อง quote เมื่อ `t` มีอักขระใดใน 17 ตัว: ช่องว่าง `"` `'` `\` `>` `<` `~` `\|` `&` `;` `$` `*` `?` `#` `(` `)` `` ` `` (`=` และ `%` ไม่อยู่ในรายการ) (2) ถ้าต้อง quote: ใส่ `\` หน้าทุก `"` `` ` `` `$` `\` แล้วครอบด้วย `"` (3) แทน `%` ทุกตัวด้วย `%%` |
| A4 | ขั้นสุดท้าย | ต่อ token ด้วยช่องว่างหนึ่งตัว แล้วแทน `\` ทุกตัวด้วย `\\` (escape ระดับ string ของ Desktop Entry) · ผลคือค่าหลัง `Exec=` · spec รับประกัน **การ serialize ตามกฎนี้เท่านั้น** ไม่ตรวจว่าไฟล์ที่ชี้ไปรันได้ (Inspector ข้อ 13) |
| A5 | ตัวอย่าง | ตาราง A5 |
| A6 | `Build` | คืนทุกตัวอักษร (LF, LF ปิดท้าย): `"[Desktop Entry]\nType=Application\nName=AI Usage Monitor\nComment=Start AI Usage Monitor when you log in\nExec=" + FormatExec(executablePath, arguments) + "\nTerminal=false\nX-GNOME-Autostart-enabled=true\n"` |
| A7 | `ReadEnabledState(contents)` — การแยก (Inspector ข้อ 14) | `contents` null → `ArgumentNullException` · แยกบรรทัดด้วย `\n`, ตัด `\r` ท้าย, trim · ข้ามบรรทัดว่างและขึ้นต้น `#` · บรรทัดที่ขึ้นต้น `[` **และ** ลงท้าย `]` = หัว group ชื่อ = ข้อความข้างใน (case-sensitive) · บรรทัดขึ้นต้น `[` ที่ไม่ลงท้าย `]` = บรรทัดเสีย ข้ามไป (group ปัจจุบันไม่เปลี่ยน) · บรรทัดอื่นที่มี `=`: key = ก่อน `=` ตัวแรก trim, value = หลัง trim; **ชื่อ key เทียบแบบ ordinal case-sensitive** (`Exec`, `Hidden`, `X-GNOME-Autostart-enabled` เท่านั้น — `hidden`, `EXEC` ฯลฯ เป็น key อื่นที่ไม่นับ; ตาม Desktop Entry spec และเหมือนชื่อ group, รอบ 9); ไม่มี `=` → ข้าม · นับเฉพาะ entry ใน group `Desktop Entry` (ทุกครั้งที่ปรากฏ) · บรรทัด `key=value` ก่อนหัว group แรก ไม่อยู่ใน group ใด → ไม่นับ (D13) |
| A8 | `ReadEnabledState` — ค่า | key ซ้ำ: **ค่าดิบตัวสุดท้ายชนะก่อน แล้วค่อยตีความ** (ค่าก่อนหน้าไม่ "รอด") · boolean: `true`/`1` = จริง, `false`/`0` = เท็จ (case-sensitive) ค่าอื่น = ถือว่าไม่มี key · ไม่มี group `Desktop Entry` → `null` · `Exec` (ตัวสุดท้าย) ไม่มีหรือว่าง → `null` · `Hidden` จริง → `false` · `X-GNOME-Autostart-enabled` เท็จ → `false` · นอกนั้น → `true` · ไม่ตรวจว่า `Exec` ชี้มาที่แอปนี้ |
| A9 | ใหม่ `src/AIMonitor.Infrastructure/Linux/XdgAutostartStartupRegistrar.cs` | `internal sealed class XdgAutostartStartupRegistrar : IStartupRegistrar` (ไม่มี platform attribute) · `public const string DefaultFileId = "net.apichart.aiusagemonitor";` · ctor `public XdgAutostartStartupRegistrar(string autostartDirectory, string fileId)` · `public static string ResolveAutostartDirectory(string? xdgConfigHome, string homeDirectory)` · properties `AutostartDirectory` (ตามที่รับ), `FileId`, `DesktopFilePath` = `autostartDirectory.TrimEnd('/') + "/" + fileId + ".desktop"` |
| A10 | ctor | (กฎ G-ARG) `autostartDirectory` null → `ArgumentNullException`, ว่าง/whitespace → `ArgumentException`; ไม่ขึ้นต้น `/` หรือมี `\0` → `ArgumentException` · `fileId` null → `ArgumentNullException`, ว่าง/whitespace → `ArgumentException`; มี `/` หรือ `\0` → `ArgumentException` · ไม่แตะดิสก์ |
| A11 | `ResolveAutostartDirectory` | (กฎ G-ARG) `homeDirectory` null → `ArgumentNullException`, ว่าง/whitespace → `ArgumentException`; ไม่ขึ้นต้น `/` หรือมี `\0` → `ArgumentException` (Inspector ข้อ 11) · `xdgConfigHome` ขึ้นต้น `/` และไม่มี `\0` (ไม่ trim) → `xdgConfigHome.TrimEnd('/') + "/autostart"` · นอกนั้น (null, ว่าง, whitespace, สัมพัทธ์, มี `\0`) → `homeDirectory.TrimEnd('/') + "/.config/autostart"` · ไม่แตะดิสก์ |
| A12 | `GetRegistrationState()` | `!File.Exists(DesktopFilePath)` → `false` (รวม: โฟลเดอร์ไม่มี, มี **โฟลเดอร์** อยู่ที่ path นั้น, ตรวจการมีอยู่ไม่ได้ — `File.Exists` คืน false ในทุกกรณีนี้ ยอมรับ) · อ่าน `File.ReadAllText` (UTF-8; symlink ถูกตามไป) · ถอดแบบ **ไม่ strict** (`File.ReadAllText` ค่าเริ่มต้น): byte UTF-8 ที่เสียกลายเป็น U+FFFD แล้วแยกตาม A7/A8 ตามปกติ — ไม่ใช่ `null` (ไฟล์ที่แอปเขียนเป็น UTF-8 ถูกต้องเสมอ; ไฟล์ที่ถูกแก้จากภายนอกอ่านแบบ best effort และ `Register` เขียนทับเป็นเนื้อหา P2-25 ตาม A13 เหมือนไฟล์อื่น, รอบ 9) · `FileNotFoundException`/`DirectoryNotFoundException` (เช่น symlink ที่ปลายทางไม่มี — `File.Exists` เป็นจริงแต่อ่านไม่ได้, รอบ 8) → **`false`** (จับก่อน `IOException`) · `IOException` อื่น/`UnauthorizedAccessException` → `null` · นอกนั้น → `ReadEnabledState` · `IsRegistered()` = `GetRegistrationState() ?? false` |
| A13 | `Register(executablePath, arguments = "")` (Inspector ข้อ 15) | (1) `executablePath` null → `ArgumentNullException`, ว่าง/whitespace → `ArgumentException` (G-ARG) (2) `content = XdgDesktopEntry.Build(...)` ก่อนแตะดิสก์ (throw → ไม่มีอะไรถูกสร้าง/แก้) (3) `Directory.CreateDirectory(AutostartDirectory)` (4) เขียนลง temp `AutostartDirectory.TrimEnd('/') + "/." + FileId + ".desktop." + Guid.NewGuid().ToString("N") + ".tmp"` (UTF-8 ไม่มี BOM; ชื่อ temp เป็นข้อกำหนดของ implementation ให้ Reviewer ตรวจ — **เทสต์ไม่ต้อง assert ชื่อ** assert แค่ว่าไม่เหลือไฟล์ `*.tmp` และมีไฟล์เดียวตาม P2-58/59/66) (5) `File.Move(tmp, DesktopFilePath, overwrite: true)` — atomic: ไฟล์เดิมคงอยู่ครบจนวินาทีที่ถูกแทน · symlink ที่ `DesktopFilePath` ถูกแทนด้วยไฟล์ธรรมดา (ปลายทางของ link ไม่ถูกแตะ) (6) ข้อ 3–5 ล้มด้วย `IOException`/`UnauthorizedAccessException` → ลบ tmp (best effort) แล้ว `InvalidOperationException($"Cannot write the autostart entry at '{DesktopFilePath}'.", inner)`; ไฟล์/โฟลเดอร์เดิมที่ `DesktopFilePath` ไม่เปลี่ยน · idempotent: input เดิม → ไบต์เดิม ไฟล์เดียว (ไม่เหลือ tmp เมื่อทำงานปกติ) · ไม่กำหนดโหมดไฟล์ (umask) |
| A14 | `Unregister()` | `File.Delete(DesktopFilePath)` อย่างเดียว (symlink → ลบตัว link ไม่ใช่ปลายทาง) · ไม่มีไฟล์ หรือ `DirectoryNotFoundException` → ไม่ทำอะไร · มีโฟลเดอร์ที่ path นั้น หรือ `IOException`/`UnauthorizedAccessException` อื่น → `InvalidOperationException($"Cannot remove the autostart entry at '{DesktopFilePath}'.", inner)` และของเดิมไม่เปลี่ยน · ห้ามเขียน `Hidden=true` แทนการลบ ห้ามแตะไฟล์อื่น ห้ามลบโฟลเดอร์ |
| A15 | per-user | เขียนเฉพาะโฟลเดอร์ของผู้ใช้ (A11) ไม่แตะ `$XDG_CONFIG_DIRS`/`/etc/xdg/autostart` |
| A16 | `PlatformAdapters.CreateStartupRegistrar` | สาขา Linux (W2): `new Linux.XdgAutostartStartupRegistrar(Linux.XdgAutostartStartupRegistrar.ResolveAutostartDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify)), Scoped(Linux.XdgAutostartStartupRegistrar.DefaultFileId))` (รอบ 8: `DoNotVerify` แบบเดียวกับ W1a — ถ้า `$HOME` ชี้โฟลเดอร์ที่ยังไม่มี ค่าเดิมคืน `""` แล้ว A11 throw และ `PlatformHost.StartAsync` ล้มเพราะเรียกนอก try (`PlatformHost.cs:82`); A11 ไม่เปลี่ยน) → ไม่มี scope: `~/.config/autostart/net.apichart.aiusagemonitor.desktop` · scope `smoke`: `…/net.apichart.aiusagemonitor.smoke.desktop` |
| A17 | โดเมนของ scope (Inspector ข้อ 11) | scope ที่รองรับ = หลัง trim ตรงกับ `^[A-Za-z0-9._-]{0,64}$` (ค่าที่เครื่องมือ smoke/เทสต์ใช้) · ค่าอื่นไม่รองรับ: ไม่เพิ่มการตรวจใน `Scoped` (จะเปลี่ยน macOS) · ผลที่กำหนดไว้มีแค่: scope ที่มี `/` หรือ `\0` ทำให้ `CreateStartupRegistrar` บน Linux throw `ArgumentException` (A10) |

**ตาราง A5 — `FormatExec`** (คอลัมน์ input เป็น C# literal; คอลัมน์ผลเป็นข้อความดิบในไฟล์)

| # | executablePath (C#) | arguments (C#) | ผล (ดิบ) |
|---|---|---|---|
| E1 | `"/home/u/apps/aim/AIMonitor.Presentation.Avalonia"` | `""` | `/home/u/apps/aim/AIMonitor.Presentation.Avalonia` |
| E2 | `"/opt/AI Monitor/aim"` | `"--minimized"` | `"/opt/AI Monitor/aim" --minimized` |
| E3 | `" \"/opt/AI Monitor/aim\" "` | `null` | `"/opt/AI Monitor/aim"` |
| E4 | `"/opt/a$b/aim"` | `""` | `"/opt/a\\$b/aim"` |
| E5 | `"/opt/back\\slash/aim"` (backslash หนึ่งตัว) | `""` | `"/opt/back\\\\slash/aim"` (สี่ตัว) |
| E6 | `"/opt/q\"uote/aim"` | `""` | `"/opt/q\\"uote/aim"` |
| E7 | `"/opt/100%/aim"` | `""` | `/opt/100%%/aim` |
| E8 | `"/opt/it's/aim"` | `""` | `"/opt/it's/aim"` |
| E9 | `"/opt/50% off/aim"` | `""` | `"/opt/50%% off/aim"` |
| E10 | `"/opt/x/aim"` | `"  --tray   --minimized "` | `/opt/x/aim --tray --minimized` |
| E11 | `"/opt/x~y/aim"` | `""` | `"/opt/x~y/aim"` |
| E12 | `"/opt/x/aim"` | `"--open=a=b"` | `/opt/x/aim --open=a=b` |
| E13 | `"/opt/x/aim"` | `"--name=$HOME"` | `/opt/x/aim "--name=\\$HOME"` |
| E14 | `` "/opt/back`tick/aim" `` | `""` | `` "/opt/back\\`tick/aim" `` |
| E15 | `"relative/aim"` | `""` | `InvalidOperationException` |
| E16 | `"/opt/a\tb/aim"` | `""` | `InvalidOperationException` |
| E17 | `"   "` / `""` / `null` | `""` | `ArgumentException` (null → `ArgumentNullException`) |
| E18 | `"/opt/a=b/aim"` | `""` | `/opt/a=b/aim` |
| E19 | `"/opt/x/aim\n"` | `""` | `InvalidOperationException` (newline ท้าย ตรวจก่อน trim) |
| E20 | `"/opt/x/aim"` | `"--tray\t--minimized"` | `InvalidOperationException` |
| E21 | `"/opt/x/aim"` | `"--x \"a b\""` | `/opt/x/aim --x "\\"a" "b\\""` |
| E22 | `"/opt/x/aim"` | `"\"\""` | `/opt/x/aim "\\"\\""` |
| E23 | `"/opt/x/aim\""` (quote ท้ายเป็นตัวอักษร) | `""` | `/opt/x/aim` |
| E24 | `"C:\\aim.exe"` | `""` | `InvalidOperationException` (ไม่ขึ้นต้น `/` ทุก OS) |

**ตาราง D — `ReadEnabledState`** (`B` = ผล `Build("/opt/x/aim")`)

| # | contents | ผล |
|---|---|---|
| D1 | `B` | `true` |
| D2 | `B + "Hidden=true\n"` | `false` |
| D3 | `B` ที่แทน `X-GNOME-Autostart-enabled=true` ด้วย `X-GNOME-Autostart-enabled=false` | `false` |
| D4 | `"[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled = false\n"` | `false` |
| D5 | `"[Desktop Entry]\nExec=/x\nHidden=True\n"` | `true` |
| D6 | `"[Desktop Entry]\nExec=/x\nHidden=1\n"` | `false` |
| D7 | `""` | `null` |
| D8 | `"[Other]\nExec=/x\n"` | `null` |
| D9 | `"[Desktop Entry]\nType=Application\n"` | `null` |
| D10 | `"[Desktop Entry]\nExec=\n"` | `null` |
| D11 | `B` ที่แทน `\n` ทุกตัวด้วย `\r\n` | `true` |
| D12 | `"# c\n[Desktop Entry]\nExec=/x\n[Desktop Action new]\nHidden=true\n"` | `true` |
| D13 | `"Hidden=true\n[Desktop Entry]\nExec=/x\n"` | `true` |
| D14 | `"[Desktop Entry]\nExec=/x\nHidden=true\nHidden=false\n"` | `true` |
| D15 | `"[Desktop Entry]\nExec=/x\nX-GNOME-Autostart-enabled=0\n"` | `false` |
| D16 | `"  [Desktop Entry]  \n  Exec = /x  \n"` | `true` |
| D17 | `"[Desktop Entry]\nExec=/x\nHidden=true\nHidden=maybe\n"` | `true` (ตัวสุดท้ายไม่ใช่ boolean → ไม่มี key) |
| D18 | `"[Desktop Entry]\nExec=/x\nExec=\n"` | `null` |
| D19 | `"[Desktop Entry]\nExec=\nExec=/x\n"` | `true` |
| D20 | `"[Desktop Entry]\nExec=/x\n[Broken\nHidden=true\n"` | `false` (`[Broken` ข้าม, ยังอยู่ใน group เดิม) |
| D21 | `null` | `ArgumentNullException` |
| D22 | `"[Desktop Entry]\nExec=/x\njunk line\n"` | `true` |
| D23 | `"[Desktop Entry]\nExec=/x\nhidden=true\n"` | `true` (`hidden` ไม่ใช่ `Hidden`) |
| D24 | `"[Desktop Entry]\nEXEC=/x\n"` | `null` (ไม่มี `Exec`) |
| D25 | `"[Desktop Entry]\nExec=/x\nx-gnome-autostart-enabled=false\n"` | `true` |

---

## 3. Secret protector: libsecret + key-file fallback (เฟส 3)

### 3.1 การตัดสินใจ: P/Invoke เข้า libsecret (U1 — final)

เรียก C API ของ `libsecret-1.so.0` ตรงด้วย `[DllImport]` ที่รับ/คืน `IntPtr` ล้วน ไม่เพิ่ม NuGet เหตุผล: เครื่องผู้ใช้ไม่มี
`secret-tool` (F7) แต่มี library (F6) · C API แยก "ไม่พบ" กับ "ผิดพลาด" ได้ · key ไม่ผ่าน process อื่น · binding สำเร็จรูป
(Tmds.DBus) เพิ่ม NuGet และ `busctl`/`gdbus` จัดการ Prompt ของ keyring ที่ล็อกไม่ได้ — ตัดทิ้ง · ราคาที่จ่าย: shim ที่ไม่มี
unit test ตรง พิสูจน์ด้วย P3-SMOKE และ manual M1–M6 (§6.4)

### 3.2 รูปแบบไฟล์ sealed

| Id | ข้อกำหนด |
|---|---|
| S1 | `[0]` = `0x01` (format version) · `[1]` = แหล่งกุญแจ: `0x01` Secret Service, `0x02` key file · `[2..14)` nonce สุ่มใหม่ทุกครั้ง · `[14..30)` tag · `[30..)` ciphertext · ยาว 30 + len(plaintext) |
| S2 | AES-GCM, key 32 byte, tag 16 byte, **associated data = `[0..2)`** |
| S3 | `internal enum LinuxKeySource : byte { SecretService = 1, KeyFile = 2 }` ใน `LinuxSecretProtector.cs` |

### 3.3 Seam ที่ทดสอบได้

| Id | ที่ไหน | ข้อกำหนด |
|---|---|---|
| S4 | ใหม่ `src/AIMonitor.Infrastructure/Linux/ISecretServiceKeyring.cs` | `internal enum SecretServiceStatus { Found, NotFound, Done, Unavailable, Failed }` · `internal readonly record struct SecretServiceResult(SecretServiceStatus Status, string? Secret = null, string? Message = null)` override `ToString()` = `$"{Status}: {Message}"` (**ไม่มี `Secret`**; `Message` ไม่ถูกเชื่อถือ: protector ส่งผ่าน `SafeDetail` เสมอ — S8a, S16a) · `internal interface ISecretServiceKeyring { SecretServiceResult Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes); SecretServiceResult Store(string schemaName, IReadOnlyDictionary<string, string> attributes, string label, string secret); SecretServiceResult Clear(string schemaName, IReadOnlyDictionary<string, string> attributes); }` |
| S5 | ความหมายผล | `Lookup`: `Found` (Secret ≠ null) / `NotFound` / `Unavailable` / `Failed` · `Store`: `Done` / `Unavailable` / `Failed` · `Clear`: `Done` / `NotFound` / `Unavailable` / `Failed` · `Unavailable` = ไม่มี Secret Service ให้คุยใน session นี้ · `Failed` = มี service แต่ไม่สำเร็จ (ล็อกแล้วปิด prompt, ถูกปฏิเสธ, protocol error) · exception ที่ implementation อนุญาตให้ throw: ดู S9a |
| S6 | ใหม่ `src/AIMonitor.Infrastructure/Linux/SecretServiceErrors.cs` | `internal static class SecretServiceErrors` (pure, ไม่มี platform attribute): `public static SecretServiceStatus Classify(string? domain, int code, bool timedOut)` · `public static bool HasSessionBus(string? dbusSessionBusAddress, string? xdgRuntimeDir, Func<string, bool> fileExists)` · `public static string SafeDetail(string? message)` · ค่าคงที่ข้อความของ shim: `NoSessionBusMessage`, `LibraryMissingMessage`, `TimeoutMessage`, `StoreFalseMessage` (ข้อความตาม S10, S13, S15) |
| S7 | `Classify` (คืนแค่ `Unavailable`/`Failed`) | `timedOut` → `Unavailable` · `"g-io-error-quark"`: 1, 24 → `Unavailable`; อื่น (รวม 19 CANCELLED) → `Failed` · `"g-dbus-error-quark"`: {2,3,4,5,6,11,12,13,15,20} หรือ 23–34 → `Unavailable`; อื่น → `Failed` · domain อื่นทั้งหมด (`"secret-error"`, `null`) → `Failed` · ordinal case-sensitive |
| S8 | `HasSessionBus` (host-independent) | `fileExists` null → `ArgumentNullException` · `dbusSessionBusAddress` ไม่ null/whitespace → `true` (ไม่เรียก `fileExists`) · นอกนั้น ถ้า `xdgRuntimeDir` ขึ้นต้น `/` และไม่มี `\0` และ `fileExists(xdgRuntimeDir.TrimEnd('/') + "/bus")` → `true` · นอกนั้น `false` |
| S8a | `SafeDetail` — whitelist แทน redaction (Inspector รอบ 2 ข้อ 5) | คืน `message` ตามเดิม **เฉพาะ** เมื่อเท่ากับค่าคงที่ข้อความของ shim ตัวใดตัวหนึ่ง (ordinal) หรือตรงกับ regex `\A(?:g-io-error-quark\|g-dbus-error-quark\|secret-error\|other) -?[0-9]{1,10}\z` ด้วย `RegexOptions.CultureInvariant` เท่านั้น (ไม่มี `IgnoreCase`/`Multiline`; Inspector รอบ 3 ข้อ 5 — `\z` ไม่ยอมให้มีขึ้นบรรทัดใหม่ท้ายสตริง ต่างจาก `$`) · นอกนั้นทุกกรณี (รวม null, ว่าง, ข้อความ native, ข้อความที่มี plaintext/secret ใด ๆ) → `"details withheld"` · ไม่มีการ "ลบคำ" แบบ blacklist อีก — ข้อความที่ไม่รู้จักไม่ถูกส่งต่อเลย จึงครอบ plaintext, secret ที่ผู้เรียกส่งมา, `Secret` ที่ไม่ผ่าน S24a และ secret ที่ไม่รู้จัก |

### 3.4 Native shim `LibSecretKeyring`

| Id | หัวข้อ | ข้อกำหนด |
|---|---|---|
| S9 | — | ใหม่ `src/AIMonitor.Infrastructure/Linux/LibSecretKeyring.cs`: `[SupportedOSPlatform("linux")] internal sealed class LibSecretKeyring : ISecretServiceKeyring` · `public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);` · ctor ไม่มีพารามิเตอร์ ไม่โหลด library ไม่แตะ D-Bus ไม่แตะดิสก์ |
| S9a | ลำดับและสัญญา argument (Inspector ข้อ 20) | (1) ตรวจ argument: `schemaName` null → `ArgumentNullException`, whitespace หรือมี `\0` → `ArgumentException` · `attributes` null → `ArgumentNullException`; จำนวน 0 หรือ > 32 → `ArgumentException`; key/value null → `ArgumentNullException`; key ว่าง หรือ key/value มี `\0` → `ArgumentException` · Store: `label` null → `ArgumentNullException`, whitespace/`\0` → `ArgumentException`; `secret` null → `ArgumentNullException`, ว่าง/`\0` → `ArgumentException` (2) session bus (S10 ข้อ 1) (3) โหลด library (S10 ข้อ 2) (4) เรียก native · exception ที่ shim throw ได้: เฉพาะจากข้อ 1 · `DllNotFoundException`/`EntryPointNotFoundException` → `Unavailable` · exception อื่นจาก runtime (เช่น `OutOfMemoryException`) ส่งต่อตามปกติ |
| S10 | ก่อนเรียก native | (1) `HasSessionBus(env DBUS_SESSION_BUS_ADDRESS, env XDG_RUNTIME_DIR, File.Exists)` false → `Unavailable`, Message = `NoSessionBusMessage` = `"No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set and $XDG_RUNTIME_DIR/bus does not exist)."` (2) `NativeLibrary.TryLoad` ของ `libsecret-1.so.0`, `libglib-2.0.so.0`, `libgobject-2.0.so.0`, `libgio-2.0.so.0` ไม่สำเร็จตัวใด → `Unavailable`, Message = `LibraryMissingMessage` = `"libsecret is not installed (libsecret-1.so.0 could not be loaded)."` (จำผลการโหลดทั้ง process) |
| S11 | signatures | glib: `g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc) → IntPtr`, `g_hash_table_insert(IntPtr, IntPtr, IntPtr) → int`, `g_hash_table_unref(IntPtr)`, `g_error_free(IntPtr)`, `g_quark_to_string(uint) → IntPtr`; ตัวชี้ `g_str_hash`/`g_str_equal` จาก `NativeLibrary.GetExport(<handle libglib>, …)` · gio: `g_cancellable_new() → IntPtr`, `g_cancellable_cancel(IntPtr)` · gobject: `g_object_unref(IntPtr)` · libsecret: `secret_schema_newv(IntPtr name, int flags, IntPtr attributeNamesAndTypes) → IntPtr`, `secret_schema_unref(IntPtr)`, `secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error) → IntPtr`, `secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error) → int`, `secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error) → int`, `secret_password_free(IntPtr)` · string ส่งเป็น UTF-8 จาก `Marshal.StringToCoTaskMemUTF8` · ห้ามใช้ฟังก์ชัน variadic |
| S12 | schema/attributes | `secret_schema_newv(schemaName, 0, table)` โดย table = `g_hash_table_new(g_str_hash, g_str_equal)` key = ชื่อ attribute, value = `IntPtr.Zero` (STRING = 0) · attributes เป็น hash table แบบเดียวกัน · collection = `IntPtr.Zero` (default) |
| S13 | timeout | `GCancellable` ต่อ call + `System.Threading.Timer` ที่ครบ `CallTimeout` แล้วตั้ง `timedOut` (Volatile) และเรียก `g_cancellable_cancel` · dispose timer แบบรอ callback จบ ก่อน `g_object_unref(cancellable)` · `TimeoutMessage` = `"The Secret Service did not answer within 30 seconds."` |
| S14 | GError | `domain = (uint)Marshal.ReadInt32(err, 0)`, `code = Marshal.ReadInt32(err, 4)` · domain string = `PtrToStringUTF8(g_quark_to_string(domain))` · **ข้อความ native (`err->message`) ไม่ถูกอ่านและไม่ถูกเก็บ** · `g_error_free` เสมอ · Message ของผล = `$"{d} {code}"` โดย `d` = domain string ถ้าเป็นหนึ่งใน `g-io-error-quark`, `g-dbus-error-quark`, `secret-error` ไม่งั้น `"other"` (ข้อความเดียวที่อนุญาตจาก native) |
| S15 | ลำดับความสำคัญของผล | (1) มี GError → `Classify(domain, code, timedOut)`; Message = timedOut ? `TimeoutMessage` : ตาม S14; ค่าคืนใด ๆ ถูกทิ้ง (pointer จาก lookup ถูก `secret_password_free`) (2) ไม่มี GError และสำเร็จ (lookup ≠ 0 / store, clear ≠ 0) → สำเร็จ **แม้ timer ยิงแล้ว** (3) ไม่มี GError, ไม่สำเร็จ และ `timedOut` → `Unavailable`, `TimeoutMessage` (4) ไม่มี GError, ไม่สำเร็จ: lookup → `NotFound`; store → `Failed`, `StoreFalseMessage` = `"secret_password_storev_sync returned FALSE without an error."`; clear → `NotFound` |
| S16 | หน่วยความจำ | unmanaged string ทุกตัวมีชีวิตจนจบ call แล้ว free ใน `finally`; สำเนา UTF-8 ของ `secret` เขียนทับด้วยศูนย์ก่อน free · unref schema และ hash table ทั้งสองใน `finally` · ห้าม log/format `secret` |
| S16a | ข้อความของ shim | Message ของทุกผลจาก `LibSecretKeyring` เป็นค่าคงที่ใน S6 หรือรูป S14 เท่านั้น (จึงผ่าน `SafeDetail` ได้เสมอ) |

### 3.5 ไฟล์กุญแจ (fallback)

| Id | หัวข้อ | ข้อกำหนด |
|---|---|---|
| S17 | — | ใหม่ `src/AIMonitor.Infrastructure/Linux/SecretKeyFile.cs`: `[SupportedOSPlatform("linux")] internal static class SecretKeyFile` · `public const int KeyBytes = 32;` · `public static byte[]? TryRead(string path)` · `public static byte[] ReadOrCreate(string path)` · path null/whitespace/ไม่ fully qualified → `ArgumentException` · เนื้อหา = key ดิบ 32 byte · **โหมดที่บังคับแน่นอน** (Inspector ข้อ 16): หลังทุก `TryRead` ที่คืนค่า และทุก `ReadOrCreate` ที่สำเร็จ ไฟล์มีโหมด `(mode & 0o7777) == 0o600` และโฟลเดอร์แม่ `== 0o700` |
| S18 | `TryRead(path)` ตามลำดับ | (1) ไฟล์เป็น symlink (`new FileInfo(path).LinkTarget != null`, รวมปลายทางหาย) → `CryptographicException` (2) โฟลเดอร์แม่มีอยู่และเป็น symlink (`new DirectoryInfo(dir).LinkTarget != null`) → `CryptographicException` (3) path เป็นโฟลเดอร์ → `CryptographicException` (4) ไม่มีไฟล์ → `null` **ไม่สร้าง ไม่ chmod อะไร** (5) โหมดโฟลเดอร์แม่ `!= 0o700` → `SetUnixFileMode(dir, 0700)` (6) โหมดไฟล์ `!= 0o600` (รวม `0400`, `0000`, บิตพิเศษ) → `SetUnixFileMode(path, 0600)` (7) chmod ในข้อ 5/6 ล้ม → `CryptographicException` (8) อ่าน; ความยาว ≠ 32 → `CryptographicException` **ไม่แก้ ไม่ลบ ไม่เขียนทับเนื้อหา** (9) คืน 32 byte · IO error อื่นห่อเป็น `CryptographicException` |
| S19 | `ReadOrCreate(path)` (Inspector ข้อ 17) | (1) `TryRead` ได้ค่า → คืน (2) `Directory.CreateDirectory(dir, 0700)` แล้ว `SetUnixFileMode(dir, 0700)` (3) key = 32 byte สุ่ม (4) temp = `dir + "/." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp"` เปิดด้วย `FileStreamOptions { Mode = CreateNew, Access = Write, Share = None, UnixCreateMode = UserRead\|UserWrite }`, เขียน, `Flush(true)`, `SetUnixFileMode(tmp, 0600)` (5) `File.Move(tmp, path, overwrite: false)` (6) Move ล้มด้วย `IOException` → ลบ tmp (best effort), ล้าง key, แล้วคืน `TryRead(path)` ซึ่งใช้กฎ S18 ทุกข้อ (ปลายทางที่แข่งเข้ามาเป็นโฟลเดอร์/symlink/ยาวผิด → `CryptographicException` ตาม S18); ถ้า `TryRead` ได้ `null` (หายไประหว่างนั้น) → `CryptographicException` ข้อความมี `"changed while"` (7) ล้มแบบอื่นในข้อ 2–5 → ลบ tmp (best effort), ล้าง key, `CryptographicException` ห่อ inner (8) temp: เมื่อทำงานปกติและลบสำเร็จ ไม่มี `.tmp` เหลือ · process ตาย/ไฟดับอาจทิ้ง `.secrets.key.<guid>.tmp` ไว้ — ไม่ถูกอ่าน ไม่ถูกเก็บกวาดอัตโนมัติ และเป็นกุญแจที่ไม่เคย seal อะไร (ยอมรับ, R11) |

### 3.6 `LinuxSecretProtector`

| Id | หัวข้อ | ข้อกำหนด |
|---|---|---|
| S20 | — | ใหม่ `src/AIMonitor.Infrastructure/Linux/LinuxSecretProtector.cs`: `[SupportedOSPlatform("linux")] internal sealed class LinuxSecretProtector : ISecretProtector` · `public const string KeyFileName = "secrets.key";` · `public const string LockFileName = "secrets.key.lock";` · `public const string SchemaName = "net.apichart.aiusagemonitor.SecretStoreKey";` · `public static readonly TimeSpan DefaultKeyLockTimeout = TimeSpan.FromSeconds(65);` (> Lookup 30 s + Store 30 s ของผู้ถือ lock — Inspector รอบ 2 ข้อ 3) · ctor `internal LinuxSecretProtector(string service, string account, ISecretServiceKeyring keyring, string keyFilePath, TimeSpan? keyLockTimeout = null)` (เทสต์ส่งค่าสั้น; ติดลบ → `ArgumentOutOfRangeException`) · properties `Service`, `Account`, `KeyFilePath`, `LockFilePath` (= `Path.Combine(Path.GetDirectoryName(KeyFilePath)!, LockFileName)`) · `internal static IReadOnlyDictionary<string, string> KeyAttributes(string service, string account)` = `{ "service": service, "account": account }` · `internal static string KeyLabel(string service)` = `$"AI Usage Monitor secret store key ({service})"` |
| S21 | ctor | service/account/keyFilePath null → `ArgumentNullException`, ว่าง/whitespace → `ArgumentException` (G-ARG); keyFilePath ไม่ fully qualified → `ArgumentException`; keyring null → `ArgumentNullException` · ไม่เรียก keyring ไม่แตะดิสก์ |
| S22 | สถานะ (Inspector ข้อ 18) | **ไม่ cache กุญแจข้าม call** — ทุก `Protect`/`Unprotect` ดึงกุญแจจากแหล่งใหม่ (กุญแจที่ถูกแทน/ลบ/ล็อกจากภายนอกจึงเห็นผลทันที; ไม่มี state ของกุญแจให้ต้องล้าง) · เก็บแค่ `_lastSource` (`LinuxKeySource?`) = แหล่งของ `Unprotect` หรือ `Protect` ที่สำเร็จล่าสุด; call ที่ล้มไม่เปลี่ยนค่า · instance ไม่ thread-safe: ผู้เรียกต้องเรียกทีละ call (`DpapiSecretStore` ทำอยู่แล้วด้วย semaphore) · กุญแจในหน่วยความจำถูกล้างเป็นศูนย์หลังใช้ |
| S23 | `Protect(plaintext)` | `_lastSource` = KeyFile → key = `SecretKeyFile.ReadOrCreate(KeyFilePath)` (ไฟล์ถูกลบไปแล้ว → สร้างใหม่ได้ เพราะ store เขียนทั้งไฟล์ใหม่) · `_lastSource` = SecretService → **ตาม S24 แต่ห้าม fallback**: Lookup/Store ที่ได้ `Unavailable` → `CryptographicException` (ไม่ย้ายไปไฟล์ — U2) · `_lastSource` = null → S24 เต็ม · seal ตาม S1/S2 แล้วตั้ง `_lastSource` |
| S24 | เลือก/สร้างกุญแจ Secret Service | ใช้กับ `Protect` ทุกครั้งที่ `_lastSource` ≠ KeyFile · (0) **ตรวจ symlink ก่อนแตะดิสก์ใด ๆ** (Inspector รอบ 2 ข้อ 4): `dir` = โฟลเดอร์ของ `KeyFilePath`; ถ้า `dir` มีอยู่และ `new DirectoryInfo(dir).LinkTarget != null` → CE `E-PARENT-LINK` (ไม่สร้าง ไม่ chmod ไม่เปิด lock ไม่เรียก keyring; โหมดและเนื้อหาของปลายทาง link ไม่เปลี่ยน) (1) `Directory.CreateDirectory(dir, 0700)` + `SetUnixFileMode(dir, 0700)`; ล้มด้วย `IOException`/`UnauthorizedAccessException` → CE `E-DIR` (inner = exception นั้น) (2) `lock = LinuxFileLock.TryAcquire(LockFilePath, keyLockTimeout ?? DefaultKeyLockTimeout)`; `null` → CE `E-LOCK-BUSY`; throw `IOException`/`UnauthorizedAccessException`/`DirectoryNotFoundException` → CE `E-LOCK-ERROR` (inner = exception นั้น) — ทั้งสามกรณี keyring ถูกเรียก **0 ครั้ง**; สิ่งที่อาจเหลือ: `dir` (0700) และไฟล์ lock ว่าง (3) ภายใต้ lock: `r = Lookup(SchemaName, KeyAttributes)`: **Found** → canonical (S24a); ไม่ผ่าน → CE `E-SS-INVALID`, **ห้าม Store ทับ** · **NotFound** → key ใหม่, `Store(SchemaName, KeyAttributes, KeyLabel(service), Convert.ToBase64String(key))`: `Done` → ใช้ key; `Unavailable` → ถ้า `_lastSource` = null ใช้ (KeyFile, `ReadOrCreate`) ไม่งั้น CE `E-NO-MIGRATE`; `Failed` → CE `E-STORE-FAILED` · **Unavailable** → ถ้า `_lastSource` = null (KeyFile, `ReadOrCreate`) ไม่งั้น CE `E-NO-MIGRATE` · **Failed** → CE `E-LOOKUP-FAILED`, ไม่ fallback, ไม่สร้างไฟล์กุญแจ (4) ปล่อย lock ใน `finally` · **สัญญาความพร้อมกัน (Inspector รอบ 3 ข้อ 3)** — สำหรับ protector ที่ `Protect` พร้อมกันบน `KeyFilePath` เดียวกัน: (ก) ทุกตัวเรียก keyring **เฉพาะใต้ key lock** จึงไม่มีสอง call ของ keyring ซ้อนกัน (ข) **Store สำเร็จได้ไม่เกินหนึ่งครั้ง** เมื่อ Store ครั้งแรกได้ `Done` และ Lookup หลังจากนั้นได้ `Found` (keyring จำค่าได้) และทุกตัวได้ lock ภายใน `keyLockTimeout` — ตัวหลังได้ `Found` และใช้กุญแจเดียวกัน (ค) **Store=`Unavailable`** (กับ `_lastSource` = null): แต่ละตัวที่ Lookup ได้ `NotFound` เรียก Store **หนึ่งครั้งพอดี** แล้ว fallback ไป `ReadOrCreate` ใต้ lock เดียวกัน → `secrets.key` ถูกสร้างครั้งเดียว ทุกตัวใช้กุญแจไฟล์เดียวกัน (จำนวน Store = จำนวนตัวที่ Lookup ได้ `NotFound` — ไม่ใช่ ≤ 1) (ง) **Store=`Failed`**: แต่ละตัวเรียก Store หนึ่งครั้งพอดีแล้วได้ CE `E-STORE-FAILED`; ไม่มี `secrets.key` (จ) ไม่ได้ lock ทันเวลา → CE `E-LOCK-BUSY`, ไม่เรียก keyring · ค่า default 65 s ครอบ Lookup+Store ของผู้ถือ (2 × `CallTimeout`) |
| S24a | canonical key (Inspector ข้อ 19) | `Secret` ที่ `Found` ใช้ได้ก็ต่อเมื่อ `Convert.TryFromBase64String` สำเร็จ, ได้ 32 byte **และ** `Convert.ToBase64String(bytes) == Secret` (ordinal) — จึงต้องยาว 44 ตัว ลงท้าย `=` หนึ่งตัว ไม่มีช่องว่าง/newline และบิตท้ายเป็นศูนย์ · ตัวอย่าง: `"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="` ✓ · ตัวเดิม + `"\n"` ✗ · ตัวเดิมที่มีช่องว่างตรงกลาง ✗ · ไม่มี `=` ✗ · `"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB="` ✗ (non-canonical) · base64 ของ 16 byte ✗ |
| S25 | `Unprotect(data)` | (1) `data.Length < 30` → CE `E-SHORT` (2) `data[0] != 0x01` → CE `E-VERSION` (3) `data[1]` ไม่ใช่ 1/2 → CE `E-SOURCE` — ข้อ 1–3 ไม่เรียก keyring ไม่แตะดิสก์ (4) SecretService → `Lookup` (ไม่ใช้ lock ไม่แตะดิสก์): Found → canonical (S24a) ไม่ผ่าน → `E-SS-INVALID`; NotFound → `E-SS-NOTFOUND`; Unavailable → `E-SS-UNAVAILABLE`; Failed → `E-SS-FAILED` · KeyFile → `SecretKeyFile.TryRead` (ซึ่งปฏิเสธ parent symlink ก่อน chmod ตาม S18); null → CE `E-KF-MISSING` (5) ถอด AES-GCM (AAD `[0..2)`); tag ผิด → `AuthenticationTagMismatchException` (6) สำเร็จ → `_lastSource` = แหล่งนั้น · **ห้าม Store/Clear และห้ามสร้างไฟล์กุญแจ** · ไม่ fallback ข้ามแหล่ง (CE = `CryptographicException`) |
| S26 | ความสอดคล้อง | ไฟล์ที่ seal แล้วถูกอ่านด้วยแหล่งใน header เท่านั้น และ `Protect` หลัง `Unprotect` ใช้แหล่งเดิม (S23) · ไฟล์ใหม่เลือก Secret Service ก่อน · แหล่งเดิมใช้ไม่ได้ → throw → `DpapiSecretStore` ไม่เขียนทับ (F4) → อ่านได้อีกเมื่อแหล่งกลับมา · ไม่ migrate (U2) · กุญแจค้างไม่ถูกลบ |
| S27 | ข้อความ exception — ตรงตัว (Inspector รอบ 2 ข้อ 5) | Message ของ CE ที่ protector สร้าง = ข้อความคงที่ในตาราง S27 ทุกตัวอักษร · ถ้ากรณีนั้นมาจากผล keyring ให้ต่อท้าย `" (" + r.Status + ": " + SecretServiceErrors.SafeDetail(r.Message) + ")"` (Status เป็นชื่อ enum) · CE ที่มาจากผล keyring มี `InnerException == null` · `E-DIR`, `E-LOCK-ERROR` มี inner เป็น IO exception (ข้อความ .NET ของ IO มีแค่ path) · CE จาก `SecretKeyFile` ส่งต่อตามเดิม (ข้อความของ S18/S19 มีแค่ path) · **ไม่มีข้อความใดมาจาก `r.Secret`, plaintext, กุญแจ หรือข้อความ native** |
| S28 | ไม่มี `Forget()` บน Linux | |
| S29 | ไม่มีความลับเป็นข้อความธรรมดาบนดิสก์ | บนดิสก์: `secrets.dat` (ciphertext), `secrets.key.lock` (ว่าง), และในโหมด fallback `secrets.key` (กุญแจ) · โหมด fallback ป้องกันด้วยสิทธิ์ไฟล์เท่านั้น (R5) |
| S30 | `PlatformAdapters.CreateSecretProtector` | สาขา Linux (W3b): `new Linux.LinuxSecretProtector(Scoped(KeychainSecretProtector.DefaultService), KeychainSecretProtector.DefaultAccount, new Linux.LibSecretKeyring(), Path.Combine(AppDataDirectory, Linux.LinuxSecretProtector.KeyFileName))` |

**ตาราง S27 — ข้อความคงที่** (`*` = ต่อท้ายด้วย suffix ของผล keyring)

| รหัส | ข้อความ |
|---|---|
| E-SHORT | `The sealed secret store is too short to be valid.` |
| E-VERSION | `The sealed secret store has format version {data[0]}, which this build cannot read.` (เลขฐานสิบ) |
| E-SOURCE | `The sealed secret store names an unknown key source {data[1]}.` (เลขฐานสิบ) |
| E-SS-INVALID | `The Secret Service item for the secret store key was not written by this app.` (ไม่มี suffix) |
| E-SS-NOTFOUND* | `The secret store was sealed with a key from the Secret Service, but the Secret Service holds no such key.` |
| E-SS-UNAVAILABLE* | `The secret store was sealed with a key from the Secret Service, which is not available in this session; the stored secrets were left unchanged.` |
| E-SS-FAILED* | `The Secret Service could not provide the secret store key; the stored secrets were left unchanged.` |
| E-KF-MISSING | `The secret store was sealed with a key file that no longer exists.` |
| E-LOOKUP-FAILED* | `The Secret Service could not be asked for the secret store key.` |
| E-STORE-FAILED* | `The Secret Service refused to store the secret store key.` |
| E-NO-MIGRATE* | `The secret store uses the Secret Service, which is not available in this session; nothing was saved.` |
| E-LOCK-BUSY | `Another copy of AI Usage Monitor is creating the secret store key; try again.` |
| E-LOCK-ERROR | `Cannot lock the secret store key.` |
| E-DIR | `Cannot prepare the secret store folder.` |
| E-PARENT-LINK | `The secret store folder is a symbolic link; refusing to use it.` |

---

## 4. การแก้ `PlatformAdapters.cs`

กฎร่วม: **ห้ามเพิ่ม `using`** — ใช้ `Linux.<Type>` · สาขา Linux วางหลังสาขา macOS ก่อน `throw Unsupported(...)` · ห้ามแตะสาขา Windows/macOS ·
type ที่มี platform attribute เรียกได้ใน `if (OperatingSystem.IsLinux())` เท่านั้น (CA1416) · ห้ามเพิ่ม private helper **ในไฟล์นี้** (ใน
ไฟล์ใหม่ของ `Linux/` เพิ่มได้) · **เฟส 1 และ 2 แทรกเฉพาะบรรทัดโค้ด; doc comment ทุกจุดแก้ในเฟส 3 (W3d)** เพื่อลด context ที่ชิดกัน (Inspector ข้อ 23)

| Hunk | เฟส | ตำแหน่งที่ `a0f40ea` | การแก้ |
|---|---|---|---|
| W1a | 1 | `AppDataDirectory` `:53-55` | `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, OperatingSystem.IsLinux() ? Environment.SpecialFolderOption.DoNotVerify : Environment.SpecialFolderOption.None)` |
| W1b | 1 | `CreateSingleInstanceCoordinator` | แทรกสาขา Linux ตาม L11 ระหว่าง `:101` กับ `:103` |
| W2 | 2 | `CreateStartupRegistrar` | แทรกสาขา Linux ตาม A16 ระหว่าง `:82` กับ `:84` |
| W3a | 3 | `IsSupportedPlatform` `:50` | `OperatingSystem.IsWindows() \|\| OperatingSystem.IsMacOS() \|\| OperatingSystem.IsLinux()` |
| W3b | 3 | `CreateSecretProtector` | แทรกสาขา Linux ตาม S30 ระหว่าง `:150` กับ `:152` |
| W3c | 3 | `Unsupported` `:165` | `$"{port} has no adapter for this operating system; AI Usage Monitor supports Windows, macOS and Linux."` |
| W3d | 3 | doc comments | class summary `:14-17`: "so Windows, macOS and Linux make the same choices" · `AppDataDirectory`: เพิ่มว่าบน Linux ไม่ตรวจว่าโฟลเดอร์มีอยู่ · `CreateSecretStore` `:57-60`: "and on Linux with a key from the Secret Service, or from a 0600 key file when there is none" · `CreateStartupRegistrar` `:67`: "or an XDG autostart entry" · `CreateSingleInstanceCoordinator` `:87-90`: "and on Linux a socket in `$XDG_RUNTIME_DIR` when it is usable, guarded by a lock file" |
| W3e | 3 | `src/AIMonitor.Presentation.Avalonia/PlatformHost.cs:47` (U5) | สตริงเดียว: `"AI Usage Monitor runs on Windows and macOS; this operating system has no adapters."` → `"AI Usage Monitor runs on Windows, macOS and Linux; this operating system has no adapters."` · ห้ามแก้บรรทัดอื่นในไฟล์นั้น · ไม่มีเทสต์อัตโนมัติ (เห็นได้เฉพาะบน OS ที่ไม่รองรับ) — Reviewer ตรวจจาก diff ว่าเปลี่ยนแค่สตริงนี้ |
| W3f | 3 | `src/AIMonitor.Presentation.Avalonia/ViewModels/ConnectionsViewModel.cs:327` (U7) | บรรทัดเดียว: `            is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)` → `            is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException)` · ใช้ชื่อเต็ม **ไม่เพิ่ม `using`** เพื่อให้ diff เป็นบรรทัดเดียว (ถ้า `dotnet format` บังคับ `using` ให้หยุดและส่งขึ้น Coding Owner) · ข้อความที่แสดงคือ `$"Could not save the key for {card.DisplayName}: {exception.Message}"` เดิม (`:329`) ซึ่งปลอดภัยเพราะ Message ของ CE เป็นข้อความคงที่ S27 · มีผลกับ macOS ด้วย (Keychain ที่ throw CE จะแสดงข้อความแทนการเงียบ) — ผู้ใช้อนุมัติ (U7) · ทดสอบด้วย T3e |

ลำดับ merge **1 → 2 → 3** (แต่ละเฟส rebase บนเฟสก่อน) · W3a ลงหลังสุดเสมอ · **conflict (Inspector ข้อ 23):** conflict ใน
`PlatformAdapters.cs`/`PlatformAdaptersTests.cs` ที่แก้ได้โดย "เก็บทั้งสอง hunk ตามที่ spec เขียน" → Integrator แก้แบบ mechanical
แล้วรัน gate ของเฟสนั้น (§7) · conflict ที่ต้องเขียนโค้ดต่างจาก spec หรือ Writing Owner/Test Author ต้องการแตะไฟล์นอกตาราง §7 →
หยุด ส่ง Coding Owner ซึ่งต้องแก้ spec (เพิ่มแถวใน §12 พร้อมวันที่) **ก่อน** ทำต่อ

---

## 5. การแก้ `PlatformAdaptersTests.cs` (Test Author) — ห้ามลดทอน Windows/macOS

หลัก: assertion ของ Windows/macOS คงเดิมทุกตัว · `else` ที่หมายถึง macOS → `else if (OperatingSystem.IsMacOS())` + `else if (OperatingSystem.IsLinux())`
+ `else` สุดท้าย `Assert.Throws<PlatformNotSupportedException>` · รูปที่ยอมรับ (T2a และตัวอื่น): ย้ายการเรียก factory เข้าไปในแต่ละสาขา เพื่อให้สาขาสุดท้ายห่อการเรียกด้วย `Assert.Throws` ได้ — assertion ของ Windows/macOS ต้องเหมือนเดิมทุกตัว · `using AIMonitor.Infrastructure.Linux;` ถูกเพิ่มโดย hunk T1 หรือ T2 **ตัวที่ลงก่อน** (ตัวหลังไม่เพิ่มซ้ำ; ใส่ใน T0 ไม่ได้เพราะ namespace ยังไม่มีจนกว่า production ของเฟส 1 หรือ 2 จะลง) · method ที่เรียก type
`[SupportedOSPlatform("linux")]` ใส่ `[SupportedOSPlatform("linux")]` ที่ method (ไม่ใส่ที่ class) · **การแยก env (Inspector ข้อ 22):** **T0** ใส่
`[Collection(ProcessEnvironmentCollection.Name)]` ที่ class นี้ (S-test ไฟล์ใหม่ §6) — collection ปิด parallel จึงไม่มีเทสต์อื่นรันพร้อม

| Hunk | เฟส | ข้อกำหนด |
|---|---|---|
| T0 | T0 (ก่อนเฟสแรกที่ส่ง — รอบ 4: ไปกับเฟส 2) | `[Collection(ProcessEnvironmentCollection.Name)]` บน class `PlatformAdaptersTests` — บรรทัดเดียว ไม่แตะ method ใด |
| T1a | 1 | `CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOS` (`:59-74`) → ชื่อใหม่ `CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOSAndLinux` · Linux: `IsType<LinuxSingleInstance>` และ `SocketPath == LocalSocketSingleInstance.ResolveSocketPath(LinuxRuntimeDirectory.ResolveSocketDirectory(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), PlatformAdapters.AppDataDirectory, PlatformAdapters.Scoped("AIUsageMonitor")), PlatformAdapters.Scoped("AIUsageMonitor"))` |
| T1b | 1 | ใหม่ `[LinuxOnlyFact("the socket coordinator is the Linux adapter")] CreateSingleInstanceCoordinator_OnLinux_UnderAScope_UsesItsOwnSocket` — scope `smoke`: socket path ต่างจากไม่มี scope · ถ้า `ResolveSocketDirectory(xdg, …)` ≠ fallback แล้ว path ขึ้นต้นด้วย `Path.Combine(xdg, "AIUsageMonitor.smoke") + "/"` |
| T1c | 1 | ใหม่ `[LinuxOnlyFact("activation goes through a real Unix socket")] CreateSingleInstanceCoordinator_OnLinux_ASecondLaunchActivatesTheFirst` — scope = `"t" + Guid.NewGuid().ToString("N")[..8]`; A `TryAcquireAsync` true + `HasOwnership`; B false; `B.NotifyExistingInstanceAsync(["--show"])` true; A.`Activated` ได้ `["--show"]` ภายใน 10 s · cleanup: dispose ทั้งคู่ ลบ `Path.GetDirectoryName(A.SocketPath)` (โฟลเดอร์ของ scope สุ่ม) |
| T1d | 1 | ใหม่ `[LinuxOnlyFact("the data folder rule is Linux-only")] AppDataDirectory_OnLinux_IsFullyQualifiedEvenWhenTheDataFolderDoesNotExistYet` — ไม่มี scope, `XDG_DATA_HOME = Path.Combine(Path.GetTempPath(), "aim-nodata-" + Guid.NewGuid().ToString("N"))` (ไม่สร้าง) → `AppDataDirectory == Path.Combine(thatDir, "AIUsageMonitor")` และ `Directory.Exists(thatDir)` false · คืน env |
| T2a | 2 | `CreateStartupRegistrar_PicksTheRunKeyOnWindowsAndALaunchAgentOnMacOS` → `CreateStartupRegistrar_PicksTheRunKeyOnWindowsALaunchAgentOnMacOSAndAnAutostartEntryOnLinux` · Linux: `IsType<XdgAutostartStartupRegistrar>`, `FileId == PlatformAdapters.Scoped("net.apichart.aiusagemonitor")`, `DesktopFilePath == XdgAutostartStartupRegistrar.ResolveAutostartDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) + "/" + FileId + ".desktop"` · ไม่เรียก Register/Unregister |
| T2b | 2 | ใหม่ `[LinuxOnlyFact("the autostart registrar is the Linux adapter")] CreateStartupRegistrar_OnLinux_UnderAScope_TargetsItsOwnEntry_NotTheInstalledOne` — scope `smoke`: `FileId == "net.apichart.aiusagemonitor.smoke"`, `DesktopFilePath` ลงท้าย `"/autostart/net.apichart.aiusagemonitor.smoke.desktop"` |
| T3a | 3 | `IsSupportedPlatform_IsTrueOnTheTwoPlatformsWithAFullSetOfAdapters` → `IsSupportedPlatform_IsTrueOnThePlatformsWithAFullSetOfAdapters` ค่าที่คาด `IsWindows() \|\| IsMacOS() \|\| IsLinux()` |
| T3b | 3 | `CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS` → `CreateSecretProtector_PicksDpapiOnWindowsTheKeychainOnMacOSAndTheSecretServiceOnLinux` · Linux: `IsType<LinuxSecretProtector>`, `Service == PlatformAdapters.Scoped("AIMonitor2.secrets.v1")`, `Account == "secret-store-key"`, `KeyFilePath == Path.Combine(PlatformAdapters.AppDataDirectory, "secrets.key")` · ไม่เรียก Protect/Unprotect |
| T3c | 3 | `CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms` **ไม่แก้** (ผ่านเองเมื่อ W3b ลง) · ห้ามเพิ่ม assertion เรื่องไฟล์ใน `AppDataDirectory` จริง |
| T3d | 3 | ใหม่ `[LinuxOnlyFact("the secret protector is the Linux adapter")] CreateSecretProtector_OnLinux_UnderAScope_UsesItsOwnKeyringItemAndKeyFile` — scope `smoke`: `Service == "AIMonitor2.secrets.v1.smoke"`, `Path.GetFileName(Path.GetDirectoryName(KeyFilePath)) == "AIUsageMonitor.smoke"` |
| T3e | 3 | **ไฟล์อื่น:** `tests/AIMonitor.Presentation.Avalonia.Tests/ViewModels/ConnectionsViewModelTests.cs` เพิ่ม `[Fact] WhenTheSecretStoreCannotSealTheKey_TheReasonIsShownInsteadOfThrowing` (U7, W3f) ใช้ `BuildAsync()` เดิม · `secrets.FailOnSet = new CryptographicException("The Secret Service could not be asked for the secret store key. (Failed: details withheld)")` · card `openai`, `Key = "sk-admin-typed"` · `await page.SaveAsync(card)` · **คาด:** ไม่ throw · `page.Message == "Could not save the key for " + card.DisplayName + ": The Secret Service could not be asked for the secret store key. (Failed: details withheld)"` ตรงตัว · `card.Key == "sk-admin-typed"` (ไม่ถูกล้าง) · เพิ่ม `using System.Security.Cryptography;` ในไฟล์เทสต์ได้ · ก่อน W3f เทสต์นี้ **ล้ม** (exception หลุด) |

---

## 6. แผนทดสอบต่อ adapter (Test Author — ค่าที่คาดมาจาก spec นี้เท่านั้น)

**ไฟล์สนับสนุน — ขั้น T0 (Test Author; รอบ 4 ย้ายออกจากเฟส 1 เพราะเทสต์เฟส 2 ใช้ `LinuxOnlyFact` (T2b, P2 ที่แตะดิสก์), `LinuxNonRootOnlyFact` (P2-66, P2-68) และ collection (T2a, T2b อ่าน env ผ่าน `PlatformAdapters`))** ·
T0 ไม่ขึ้นกับ production ใด (ใช้แค่ xUnit + `OperatingSystem`/`Environment`) · ส่งเป็น commit แรกของ branch เทสต์ของเฟสที่ส่งก่อน (ตอนนี้เฟส 2) · เฟสอื่น rebase ทับและ **ห้ามสร้างซ้ำ/แก้** ไฟล์เหล่านี้:
- `tests/AIMonitor.Infrastructure.Tests/LinuxOnlyFactAttribute.cs` — `public sealed class LinuxOnlyFactAttribute : FactAttribute` ctor `(string because)`; whitespace → `ArgumentException`; ไม่ใช่ Linux → `Skip = $"Linux-only: {because}"` (แบบเดียวกับ `MacOSOnlyFactAttribute.cs`) · **ทุกการใช้ต้องส่งเหตุผล**
- `tests/AIMonitor.Infrastructure.Tests/LinuxNonRootOnlyFactAttribute.cs` — ctor `(string because)`; ไม่ใช่ Linux → `Skip = $"Linux-only: {because}"`; `Environment.IsPrivilegedProcess` → `Skip = $"Linux non-root only: {because}"`
- `tests/AIMonitor.Infrastructure.Tests/ProcessEnvironmentCollection.cs` — `[CollectionDefinition(Name, DisableParallelization = true)] public sealed class ProcessEnvironmentCollection { public const string Name = "Process environment"; }`

**กฎแยก env:** เทสต์ที่ตั้ง env ของ process หรือเรียก member ของ `PlatformAdapters`/`LibSecretKeyring` ที่อ่าน env ต้องอยู่ใน collection นี้ ·
คลาสเทสต์ใหม่อื่นทุกตัวต้องส่งค่าเป็นพารามิเตอร์ และห้ามอ่าน/ตั้ง env · temp dir ต่อเทสต์ ลบใน `Dispose` (คืนสิทธิ์ 0700 ก่อนลบถ้าเทสต์ chmod ไว้) ·
ห้ามแตะ keyring จริง, `~/.config/autostart` จริง · namespace `AIMonitor.Infrastructure.Tests.Linux` + `using AIMonitor.Infrastructure.Linux;` ·
**ชนิดเทสต์:** pure (`XdgDesktopEntry`, `XdgAutostartStartupRegistrar` ที่ไม่แตะดิสก์, `ResolveAutostartDirectory`, `SecretServiceErrors`) =
`[Fact]`/`[Theory]` ทุก OS โดยค่าที่คาดเป็นสตริง `/` ตามตาราง · แตะดิสก์หรือใช้ type ที่มี `[SupportedOSPlatform("linux")]`/`[UnsupportedOSPlatform("windows")]`
= `[LinuxOnlyFact(...)]` และ `[SupportedOSPlatform("linux")]` ที่ class

### 6.1 เฟส 1 — `Linux/LinuxRuntimeDirectoryTests.cs`, `Linux/LinuxFileLockTests.cs`, `Linux/LinuxSingleInstanceTests.cs` (Linux-only ทั้งหมด)

| # | input | ค่าที่คาด | ที่มา |
|---|---|---|---|
| P1-1 | dir 0700, fallback `"/fb"`, appId `"AIUsageMonitor"` | `Path.Combine(dir, "AIUsageMonitor")` | L3, L4 |
| P1-2 | dir 0755 / 0710 / 0701 / 0500 / 0000 / 0300 | `"/fb"` ทุกตัว | L3(จ) |
| P1-3 | dir 01700 (sticky) | `Path.Combine(dir, "AIUsageMonitor")` | L3(จ) |
| P1-4 | path ไม่มีจริง / `"run/user/1000"` / `null` / `""` / `"   "` / ไฟล์ธรรมดา / `"/tmp/a\0b"` | `"/fb"` | L3 |
| P1-5 | symlink ที่ชี้ไป dir 0700 | `"/fb"` | L3(ง) |
| P1-6 | dir 0700 + appId `"AIUsageMonitor.smoke"` | `Path.Combine(dir, "AIUsageMonitor.smoke")` | L4 |
| P1-7 | ความยาว: dir 0700 ที่ `UTF8(Path.Combine(dir, appId))` = 82 byte (รวม 82+1+20 = 103) | ใช้ dir | L3(ช) |
| P1-8 | เหมือน P1-7 แต่ 83 byte (= 104) | `"/fb"` | L3(ช) |
| P1-9 | appId มีอักษรไทย (3 byte/ตัว) เช่น `"AIUsageMonitor.ทด"` ที่รวมได้ 103 / 104 byte | ใช้ dir / `"/fb"` (นับ byte ไม่ใช่ char) | L3(ช) |
| P1-10 | fallback หรือ appId null/""/"  " | `ArgumentException` (null → `ArgumentNullException`) | L2 |
| P1-11 | หลัง P1-1 | `Directory.Exists(Path.Combine(dir,"AIUsageMonitor"))` false และโหมด dir ไม่เปลี่ยน | L1 |
| P1-12 | `LinuxFileLock.TryAcquire(p, 0)` บนไฟล์ใหม่ | ไม่ null; ไฟล์มีโหมด 0600 | L5 |
| P1-13 | ถือ lock ไว้ แล้ว `TryAcquire(p, 200ms)` | `null` และเวลาที่ใช้ ≥ 150 ms | L5 |
| P1-14 | dispose ตัวแรกแล้ว `TryAcquire(p, 0)` | ไม่ null; ไฟล์ lock ยังอยู่หลัง dispose | L5 |
| P1-14a (`LinuxOnlyFact` — ค่าที่คาดเหมือนกันทั้ง root และ non-root) | ไฟล์ lock ของผู้ใช้เองมีอยู่แล้ว (theory) โหมด 0644 / 0400 / 0200 / 0000 / 04600 / 0700 → `TryAcquire(p, 0)` | ไม่ null; `File.GetUnixFileMode(p)` = 0600; ไฟล์ lock เดิม (เนื้อหาเดิมไม่ถูกตัด: เขียน `"x"` ไว้ก่อน → ยังยาว 1 byte) | L5(2) |
| P1-14b | `p` เป็น symlink ไปไฟล์ 0644 | `IOException` ข้อความมี `"symbolic link"`; ปลายทางยัง 0644 | L5(1) |
| P1-14c | ไฟล์ lock ที่ chmod ไม่ได้ (ของผู้ใช้อื่น) | **ไม่ทดสอบอัตโนมัติ** (จำลองไม่ได้โดยไม่มีผู้ใช้อื่น) — กฎ L5(2) ส่วน chmod ล้มตรวจด้วย code review | L5(2) |
| P1-14d | ถือ lock บนไฟล์ 0600 ไว้ (stream S1) แล้ว chmod ไฟล์เป็น 0000 จากเทสต์ แล้ว `TryAcquire(p, 200ms)` | `null` (ยังถูกถือ) และโหมดเป็น 0600 แล้ว; dispose S1 แล้ว `TryAcquire(p, 0)` ไม่ null | L5(2), L5(4) |
| P1-15 | `TryAcquire` ในโฟลเดอร์ที่ไม่มี | `DirectoryNotFoundException` | L5 |
| P1-16 | `TryAcquire("rel.lock", 0)` / timeout −1 ms | `ArgumentException` / `ArgumentOutOfRangeException` | L5 |
| P1-17 | `new LinuxSingleInstance(<tmp>/s, "app")` | ไม่ throw, `Directory.Exists(<tmp>/s)` false; `LockPath == SocketPath` ที่ลงท้าย `.sock` เปลี่ยนเป็น `.lock` | L6 |
| P1-18 | `TryAcquireAsync` ตัวแรก | `true`, `HasOwnership`; socket 0600; `<tmp>/s` 0700; lock file 0600 | L8 |
| P1-19 | `<tmp>/s` สร้างไว้ 0755 ก่อน | หลัง acquire เป็น 0700 | L7(3) |
| P1-20 | socket ค้างจริง (F10: bind/listen ที่ `x.sock`, `File.Move` ไป `SocketPath`, dispose) | `true` + `HasOwnership` | L8 |
| P1-21 | **race:** 20 รอบ; แต่ละรอบสร้าง 2 instance บน dir ใหม่ แล้ว `Task.WhenAll` ของ `TryAcquireAsync` ทั้งคู่ (เริ่มด้วย `Task.Run` พร้อมกัน) | ทุกรอบ: ผล `{true,false}` พอดี และ `HasOwnership` จริงตัวเดียว | L8 |
| P1-22a | **lock ถูกถือ ไม่มีเจ้าของ (จำลองผู้ถือที่ค้างระหว่าง probe กับ bind):** เทสต์ถือ `LinuxFileLock.TryAcquire(LockPath, 0)` ไว้; instance B (lockTimeout 200 ms) `TryAcquireAsync` | `true`, `HasOwnership` false, ใช้เวลา ≥ 150 ms; **ไม่มีไฟล์ที่ `SocketPath`**; แล้วเทสต์ปล่อย lock และ instance A `TryAcquireAsync` → `true` + `HasOwnership`; B ยัง `HasOwnership` false; socket ของ A อยู่และต่อได้ | L7(4), L8, L9 |
| P1-22b | **lock ถูกถือ มีเจ้าของอยู่:** A acquire สำเร็จ (เจ้าของ) แล้วเทสต์ถือ lock ไว้; B (lockTimeout 200 ms) `TryAcquireAsync` แล้ว `NotifyExistingInstanceAsync(["--show"])` | B ได้ `false`; A ได้ `["--show"]` ภายใน 10 s; socket ของ A ยังเป็นไฟล์เดิม (inode เดิม: `File.GetLastWriteTimeUtc` ไม่เปลี่ยน และยังต่อได้) | L7(4), L8 |
| P1-22d | **เจ้าของมีชีวิตแต่ต่อไม่ได้ (Inspector รอบ 3 ข้อ 2):** เทสต์สร้างโฟลเดอร์ `SocketDirectory` 0700 แล้ว bind `Socket(Unix)` ของเทสต์เองที่ `SocketPath` ด้วย `Listen(1)` และ **ไม่ accept** · เติม queue: เปิด client `Blocking = false` แล้ว `Connect` วนซ้ำ (สูงสุด 64 ครั้ง) จนได้ `SocketException` ครั้งแรก (ถ้าครบ 64 แล้วยังต่อได้ → เทสต์ล้มด้วยข้อความ `"could not fill the accept queue"`) · client เก่าแต่ละตัวที่ต่อสำเร็จ **ส่ง 1 byte `0x01` ทันที** (non-blocking `Send`; throw → เทสต์ล้ม `"could not mark an old client"`) แล้วเก็บใน `old` · instance B (connectTimeout 300 ms, lockTimeout 1 s) `TryAcquireAsync` (await ≤ 10 s) · **ตรวจว่า listener ของเทสต์ยังเป็นเจ้าของ path (Inspector รอบ 4 ข้อ 2 — ไม่สมมติจำนวน client ที่ค้างใน queue):** (1) `File.Exists(SocketPath)` (2) client ใหม่ N (blocking) เริ่ม `ConnectAsync(SocketPath)` แล้ว `Send` 1 byte `0xFF` ใน task · **socket ของ N ถูกเก็บไว้ไม่ปิดจนจบเทสต์** (3) กำหนด deadline รวม 15 s นับจากข้อ 2 · เทสต์วน: `AcceptAsync` ด้วยเวลาที่เหลือ → `ReceiveTimeout` = เวลาที่เหลือ แล้ว `Receive` 1 byte: `0x01` = client เก่า → ปิด วนต่อ · 0 byte (EOF) = probe ของ B ที่ปิดไปแล้ว (B ถูก await จบก่อนข้อ 2) → ปิด วนต่อ · `0xFF` = พบ N หยุด · byte อื่น → เทสต์ล้ม · ไม่มีการทิ้ง connection เพราะ timeout ต่อครั้ง (N ยังเปิดอยู่จึงจะส่ง marker แน่นอน) · deadline หมด → เทสต์ล้ม `"N was not accepted by the test listener"` | B ได้ `true`, `HasOwnership` false, `Activated` ไม่ยิง · (1) จริง · (3) พบ `0xFF` ภายในขอบเขต (ถ้า B ลบ/bind ทับ N จะต่อไปที่อื่นหรือ listener ของเทสต์จะไม่ได้ `0xFF` → เทสต์ล้ม) · task ของ N จบสำเร็จ · ไฟล์ lock ไม่ถูกถือหลัง B คืน (`TryAcquire(LockPath, 0)` ไม่ null) · cleanup: ปิด client ทั้งหมดและ listener | L7(5ข), L8, L9 |
| P1-22e | ไฟล์ socket ค้าง (ทำแบบ F10) ที่ `SocketPath` → instance A `TryAcquireAsync` | `true` + `HasOwnership`; ไฟล์ใหม่ต่อได้ (LP ได้ `ConnectionRefused` → inner แทนที่) | L7(5ข), L8 |
| P1-22c | **ไม่มีการลบ socket ของคนอื่นโดยไม่มี lock:** A เป็นเจ้าของ; เทสต์ถือ lock; B, C (lockTimeout 200 ms) `TryAcquireAsync` พร้อมกัน 20 รอบ | ทุกรอบ B, C ได้ `false`; A ยังเป็นเจ้าของตัวเดียวและต่อได้ทุกรอบ | L8 |
| P1-23 | ตัวที่สองแจ้ง `["--tray","a b"]` | ตัวแรกได้ `["--tray","a b"]` ภายใน 10 s | L8 |
| P1-24 | `SocketDirectory` เป็น symlink ไป dir 0755 | `TryAcquireAsync` true, `HasOwnership` false, โหมดของปลายทางยัง 0755, ไม่มีไฟล์ใหม่ในปลายทาง | L7(2) |
| P1-25 (`LinuxNonRootOnlyFact`) | parent ของ `SocketDirectory` โหมด 0500 (สร้างโฟลเดอร์ไม่ได้) | `true`, `HasOwnership` false; ตัวที่สอง `NotifyExistingInstanceAsync` = false | L7(3), L9 |
| P1-26 | `DisposeAsync` ของเจ้าของ | socket หาย; lock file ยังอยู่; dispose ซ้ำไม่ throw; `TryAcquireAsync` หลัง dispose → `ObjectDisposedException` | L6, L7a, L8 |
| P1-26a | **dispose รอ lock (Inspector รอบ 4 ข้อ 1, รอบ 5 ข้อ 3):** A เป็นเจ้าของ สร้างด้วย `disposeLockTimeout` 10 s · เทสต์ถือ `TryAcquire(LockPath, 0)` · `d = Task.Run(() => A.DisposeAsync().AsTask())` · **สัญญาณเข้า:** poll ทุก 10 ms จน `A.HasOwnership == false` (L7a(1) ตั้งเป็นคำสั่งแรก; ≤ 5 s ไม่งั้นล้ม) · แล้วรอ 300 ms | `d` ยังไม่จบ; `SocketPath` ยังมีและ connect ได้ (listener ยังไม่ถูกปิด) · **สัญญาณปล่อย:** เทสต์ dispose lock → `d` จบภายใน 10 s; `File.Exists(SocketPath)` false · B `TryAcquireAsync` → `true` + ownership; C → `false`; `C.NotifyExistingInstanceAsync(["x"])` → B ได้ `["x"]` ภายใน 10 s | L7a(1–4) |
| P1-26b | **stress (ไม่ใช่หลักฐานเชิงกำหนด — Inspector รอบ 5 ข้อ 3):** 30 รอบ (โฟลเดอร์ใหม่ทุกรอบ): X เป็นเจ้าของ · `Task.WhenAll(Task.Run(X.DisposeAsync), Task.Run(Y.TryAcquireAsync))` (≤ 10 s) · แล้ว Z `TryAcquireAsync` | ทุกรอบ: Y และ Z ไม่เป็นเจ้าของพร้อมกัน · มีเจ้าของพอดีหนึ่งตัวใน {Y, Z} · W `NotifyExistingInstanceAsync(["r"])` ถึงเจ้าของนั้นภายใน 10 s · หลักฐานเชิงกำหนดของ "close → bind ใหม่ → delete" ไม่มี seam ให้หยุดกลาง inner dispose โดยไม่แก้ไฟล์ macOS — ส่วนนี้พิสูจน์ด้วย P1-26a (dispose ไม่ปิด listener ก่อนได้ lock) + Reviewer ตรวจว่า `await inner.DisposeAsync()` อยู่ภายในช่วงที่ถือ lock ทั้งหมด | L7a, L8 |
| P1-26c | **dispose ไม่ได้ lock:** A เป็นเจ้าของ สร้างด้วย `disposeLockTimeout` 200 ms · เทสต์ถือ lock · `sw = Stopwatch.StartNew()` แล้ว `await Task.Run(() => A.DisposeAsync().AsTask())` (≤ 5 s) แล้วหยุด `sw` (นับรวมช่วง synchronous) | ไม่ throw; `sw` ≥ 150 ms; `A.HasOwnership` false; `SocketPath` ยังมีและ connect ได้; `A.TryAcquireAsync` → `ObjectDisposedException` · แล้วเทสต์ปล่อย lock และ `await A.DisposeAsync()` อีกครั้ง → `File.Exists(SocketPath)` false | L7a(5) |
| P1-26d | **dispose ระหว่าง acquire ของ instance เดียวกัน (Inspector รอบ 5 ข้อ 2, รอบ 6 ข้อ 1):** สร้าง `SocketDirectory` เองด้วยโหมด **0755** · เทสต์ถือ `TryAcquire(LockPath, 0)` · A (lockTimeout 5 s) `a = Task.Run(() => A.TryAcquireAsync().AsTask())` · **สัญญาณเข้าของ acquire:** poll ทุก 10 ms จนโหมดของ `SocketDirectory` เป็น 0700 (L7(3) ทำหลังได้ `_gate` และก่อนรอ lock; ≤ 5 s ไม่งั้นล้ม) · `d = Task.Run(() => A.DisposeAsync().AsTask())` · **สัญญาณเข้าของ dispose:** poll ทุก 10 ms จน `A.IsClosing == true` (L7a(1) ตั้งก่อนรอ `_gate`; ≤ 5 s ไม่งั้นล้ม) · แล้วรอ 300 ms · **ปล่อย:** เทสต์ dispose lock | ก่อนปล่อย: `a.IsCompleted` false และ `d.IsCompleted` false (`d` รอ `_gate` ที่ acquire ถืออยู่) · หลังปล่อย: `a` ได้ `true` ภายใน 10 s (acquire ที่เริ่มแล้วทำจนจบ); `d` จบภายใน 10 s หลัง `a` · หลังทั้งคู่จบ: `A.HasOwnership` false; `File.Exists(SocketPath)` false (ความเป็นเจ้าของที่เพิ่งได้ถูกปล่อยใต้ lock) · B `TryAcquireAsync` → `true` + ownership | L6, L7(0), L7a(1), L7a(1b) |

socket path ในเทสต์ต้องสั้น (`LocalSocketSingleInstanceTests.cs:10-17`)

### 6.2 เฟส 2 — `Linux/XdgDesktopEntryTests.cs` (ทุก OS), `Linux/XdgAutostartStartupRegistrarTests.cs` (pure ทุก OS + filesystem Linux-only)

| # | input | ค่าที่คาด | ที่มา |
|---|---|---|---|
| P2-1…24 | ตาราง A5 E1–E24 | ตามตาราง | A2–A4 |
| P2-25 | `Build("/opt/x/aim")` | `"[Desktop Entry]\nType=Application\nName=AI Usage Monitor\nComment=Start AI Usage Monitor when you log in\nExec=/opt/x/aim\nTerminal=false\nX-GNOME-Autostart-enabled=true\n"` | A6 |
| P2-26 | `Build("/opt/AI Monitor/aim", "--minimized")` | เหมือน P2-25 แต่ `Exec="/opt/AI Monitor/aim" --minimized` | A6 |
| P2-27 | `Build("relative")` | `InvalidOperationException` | A6 |
| P2-28…49 | ตาราง D1–D25 (D23–D25 รอบ 9) | ตามตาราง | A7, A8 |
| P2-50 | `ResolveAutostartDirectory(null, "/home/u")`, `("", …)`, `("   ", …)`, `("cfg", …)`, `("/x\0y", …)` | `"/home/u/.config/autostart"` | A11 |
| P2-51 | `("/xdg/cfg", "/home/u")` / `("/xdg/cfg/", "/home/u")` / `("/", "/home/u")` | `"/xdg/cfg/autostart"` / `"/xdg/cfg/autostart"` / `"/autostart"` | A11 |
| P2-52 | `(null, "/home/u/")` | `"/home/u/.config/autostart"` | A11 |
| P2-53 | home `null` / `""` / `" "` / `"home/u"` / `"C:\\Users\\u"` | null → `ArgumentNullException` (ParamName `homeDirectory`); ที่เหลือ `ArgumentException` ชนิดตรงตัว | A11, G-ARG |
| P2-54 | ctor `("autostart", "id")`, `("/a", "a/b")`, `("/a", " ")`, `("/a", "")`, `(" ", "id")`, `("/a", "a\0b")`, `("/a\0", "id")` | `ArgumentException` ทุกตัว (ชนิดตรงตัว) | A10 |
| P2-54a | ctor `(null, "id")` / `("/a", null)` | `ArgumentNullException` ทั้งคู่ (ParamName `autostartDirectory` / `fileId`) | A10, G-ARG |
| P2-55 | ctor `("/home/u/.config/autostart", "net.x")` / `("/home/u/.config/autostart/", "net.x")` | `DesktopFilePath` = `"/home/u/.config/autostart/net.x.desktop"` ทั้งคู่; `AutostartDirectory` = ค่าที่ส่งมา | A9 |
| P2-56 (Linux) | ctor ด้วย `<tmp>/cfg/autostart` ที่ไม่มี | ไม่ throw, `<tmp>/cfg` ไม่ถูกสร้าง | A10 |
| P2-57 (Linux) | ไม่มีโฟลเดอร์ → state / IsRegistered | `false` / `false` | A12 |
| P2-58 (Linux) | `Register("/opt/x/aim")` เมื่อโฟลเดอร์ซ้อนสองชั้นไม่มี | โฟลเดอร์ถูกสร้าง; ไบต์ไฟล์ == UTF-8 ของ P2-25 (ไบต์แรก `0x5B`); state `true`; ในโฟลเดอร์มีไฟล์เดียว (ไม่มี `.tmp`) | A13 |
| P2-59 (Linux) | `Register` สองครั้ง | ไบต์เท่ากัน; ไฟล์เดียว | A13 |
| P2-60 (Linux) | ต่อท้าย `"Hidden=true\n"` | state `false`; แล้ว `Register("/opt/x/aim")` → ไฟล์ == P2-25, state `true` | A12, A13 |
| P2-61 (Linux) | `Unregister()` | ไฟล์หาย; โฟลเดอร์อยู่; state `false`; ซ้ำ / ไม่มีโฟลเดอร์ → ไม่ throw | A14 |
| P2-62 (Linux) | มี `other.desktop` → Register, Unregister | `other.desktop` ไบต์เดิม | A14 |
| P2-63 (Linux) | `Register("  ")` / `Register("relative/aim")` | `ArgumentException` / `InvalidOperationException`; ไม่มีโฟลเดอร์/ไฟล์ถูกสร้าง | A13 |
| P2-64 (Linux) | มีโฟลเดอร์ที่ `DesktopFilePath` | state `false`; `Register` → `InvalidOperationException` และโฟลเดอร์ยังอยู่ ไม่มี `.tmp` เหลือ; `Unregister` → `InvalidOperationException` และโฟลเดอร์ยังอยู่ | A12–A14 |
| P2-65 (Linux) | `DesktopFilePath` เป็น symlink ไปไฟล์ `target.desktop` ที่มี `B` | state `true`; `Register` → path กลายเป็นไฟล์ธรรมดา (`LinkTarget` null) เนื้อหา P2-25, `target.desktop` ไบต์เดิม; (setup ใหม่) `Unregister` → link หาย, `target.desktop` ยังอยู่ | A12–A14 |
| P2-66 (Linux) | ไฟล์เดิมเนื้อหา `B`; โฟลเดอร์ autostart ถูก chmod 0500 (non-root, `LinuxNonRootOnlyFact`) แล้ว `Register("/opt/y/aim")` | `InvalidOperationException`; ไฟล์เดิมยังเป็น `B` ครบทุกไบต์ | A13 |
| P2-67 (Linux) | ไฟล์เนื้อหา `"garbage"` | state `null`, IsRegistered `false` | A8 |
| P2-68 (`LinuxNonRootOnlyFact`) | ไฟล์โหมด 000 | state `null` | A12 |
| P2-70 (Linux, รอบ 9) | ไฟล์ byte `"[Desktop Entry]\nExec=/x"` + `0xFF 0xFE` + `"\n"` / ไฟล์ byte `"[Desktop Entry]\nExec=/x\nHidden="` + `0xC3` + `"true\n"` / ไฟล์ byte `"[Desktop Entry]\nExec=/x\nHidden=true\n"` + `0xFF` | state `true` / `true` (ค่า `\uFFFDtrue` ไม่ใช่ boolean) / `false` · ไม่ throw ทุกตัว · แล้ว `Register("/opt/x/aim")` บนไฟล์แรก → ไบต์ไฟล์ == UTF-8 ของ P2-25, state `true` | A12, A13 |
| P2-69 (Linux, รอบ 8) | `DesktopFilePath` เป็น symlink ไป `<dir>/missing.desktop` ที่ไม่มี | state `false`, IsRegistered `false` · แล้ว `Register("/opt/x/aim")` → path เป็นไฟล์ธรรมดา (`LinkTarget` null) เนื้อหา P2-25, state `true`; `missing.desktop` ยังไม่มี | A12, A13 |

### 6.3 เฟส 3 — `Linux/SecretServiceErrorsTests.cs` (ทุก OS), `Linux/SecretKeyFileTests.cs`, `Linux/LinuxSecretProtectorTests.cs` (Linux-only), smoke

**FakeKeyring:** implement `ISecretServiceKeyring`, thread-safe (lock ภายใน) · ตั้งได้ว่า Lookup/Store คืนอะไร · `Store` ที่ `Done` ทำให้ Lookup ต่อไปคืน `Found` ด้วย secret นั้น ·
ตั้ง delay ใน Lookup ได้ (ใช้ใน P3-46) · **สัญญาณสำหรับ P3-46a (Inspector รอบ 3 ข้อ 4):** `Task LookupEntered` (`TaskCompletionSource`, `RunContinuationsAsynchronously`) ถูก set ตอนเข้า Lookup ครั้งแรกหลังตั้ง `HoldLookup = true`; Lookup นั้นรอ `ManualResetEventSlim LookupGate` สูงสุด 10 s (เกิน → throw `TimeoutException` ซึ่งทำให้เทสต์ล้มชัดเจน) · Lookup ครั้งต่อไปไม่รอ · บันทึกทุกการเรียก (method, schemaName, attributes, label, secret) · ค่าคงที่: service `"AIMonitor2.secrets.test"`,
account `"secret-store-key"`, plaintext = UTF-8 ของ `"{\"openai\":\"sk-test-0123456789abcdef\"}"` · key file = `<tmp>/d/secrets.key` (ต่อเทสต์)

| # | input | ค่าที่คาด | ที่มา |
|---|---|---|---|
| P3-1 | `Classify("g-io-error-quark", 1, false)`, `(…, 24, false)` | `Unavailable` | S7 |
| P3-2 | `Classify("g-io-error-quark", c, false)` c ∈ {19, 0, 14} | `Failed` | S7 |
| P3-3 | `Classify("g-io-error-quark", 19, true)`, `Classify("secret-error", 2, true)` | `Unavailable` | S7 |
| P3-4 | `Classify("g-dbus-error-quark", c, false)` c ∈ {2,3,4,5,6,11,12,13,15,20,23,27,34} | `Unavailable` | S7 |
| P3-5 | c ∈ {0,1,7,9,10,19,21,22,35} | `Failed` | S7 |
| P3-6 | `("secret-error", 2, false)`, `(null, 1, false)`, `("G-IO-ERROR-QUARK", 1, false)` | `Failed` | S7 |
| P3-7 | `HasSessionBus("unix:path=/x", null, f)` | `true`, `f` ไม่ถูกเรียก | S8 |
| P3-8 | `HasSessionBus(null, "/run/user/1", p => p == "/run/user/1/bus")` และ `(null, "/run/user/1/", p => p == "/run/user/1/bus")` | `true` | S8 |
| P3-9 | `("  ", "/run/user/1", _ => false)`, `(null, "run/user/1", _ => true)`, `(null, null, _ => true)`, `(null, "", _ => true)`, `(null, "/r\0", _ => true)` | `false` | S8 |
| P3-10 | `HasSessionBus(null, null, null!)` | `ArgumentNullException` | S8 |
| P3-11 | `SafeDetail` ด้วย: `"g-io-error-quark 19"`, `"g-dbus-error-quark 2"`, `"secret-error 2"`, `"other -5"`, ค่าคงที่ทั้งสี่ของ shim | คืนค่าเดิมทุกตัว | S8a |
| P3-11a | `SafeDetail` ด้วย: `null`, `""`, `"refused s3cr3t"`, `"{\"openai\":\"sk-test-0123456789abcdef\"}"`, `"g-io-error-quark 19 extra"`, `"G-IO-ERROR-QUARK 1"`, `"g-io-error-quark 12345678901"`, `"Could not connect: No such file or directory"`, **`"g-io-error-quark 19\n"`, `"g-io-error-quark 19\r\n"`, `"\ng-io-error-quark 19"`, `"g-io-error-quark 19\nsk-test-0123456789abcdef"`, `TimeoutMessage + "\n"`, `"g-io-error-quark ١٩"` (เลขอารบิก-อินดิก)** | `"details withheld"` ทุกตัว | S8a |
| P3-12 | `new SecretServiceResult(Found, "s3cr3t", "m").ToString()` | `"Found: m"` | S4 |
| P3-13 | `ReadOrCreate(<tmp>/a/b/secrets.key)` | ยาว 32; ไฟล์ 0600; `<tmp>/a/b` 0700; ไม่มี `.tmp` | S17, S19 |
| P3-14 | เรียกซ้ำ | ไบต์เท่าเดิม | S19 |
| P3-15 | โฟลเดอร์มีอยู่ 0755 / 0500 (เจ้าของคือเรา) → `ReadOrCreate` | โฟลเดอร์ 0700 | S19(2) |
| P3-16 | `TryRead` ไม่มีไฟล์ (โฟลเดอร์ 0755) | `null`; ไม่มีไฟล์ใหม่; โฟลเดอร์ยัง 0755 | S18(4) |
| P3-17 | ไฟล์ 32 byte โหมด 0644 / 0400 / 0000 / 04600 ในโฟลเดอร์ 0755 → `TryRead` | ไบต์เดิม; ไฟล์ 0600; โฟลเดอร์ 0700 | S18(5–6) |
| P3-18 | ไฟล์ 31 byte → `TryRead` และ `ReadOrCreate` | `CryptographicException` ทั้งคู่; ไฟล์ยัง 31 ไบต์เดิม | S18(8) |
| P3-19 | ไฟล์เป็น symlink ไปไฟล์ 32 byte / symlink ปลายทางหาย / โฟลเดอร์แม่เป็น symlink | `CryptographicException` ทั้งสาม | S18(1–2) |
| P3-20 | path เป็นโฟลเดอร์ | `CryptographicException` | S18(3) |
| P3-21 | ctor | fake ไม่ถูกเรียก; ไม่มีไฟล์/โฟลเดอร์ใหม่ใด ๆ | S21 |
| P3-22 | ctor: service/account/keyFilePath `" "`; keyFilePath `"rel/secrets.key"`; keyring `null` | `ArgumentException` ×4, `ArgumentNullException` | S21 |
| P3-23 | Lookup=`NotFound`, Store=`Done` → `Protect(pt)` | Store 1 ครั้ง: schema `"net.apichart.aiusagemonitor.SecretStoreKey"`, attributes `{service:"AIMonitor2.secrets.test", account:"secret-store-key"}` (2 คู่), label `"AI Usage Monitor secret store key (AIMonitor2.secrets.test)"`, secret ยาว 44 ผ่าน S24a = K · ผล: ยาว 30+len(pt), `[0]=0x01`, `[1]=0x01`, `AesGcm(K,16).Decrypt(nonce=[2..14), ct=[30..), tag=[14..30), aad=[0x01,0x01])` = pt · ไม่มี `secrets.key`; มี `secrets.key.lock` | S1, S2, S24 |
| P3-24 | P3-23 แล้ว `Protect` อีกครั้ง | Lookup รวม 2, Store รวม 1; ผลสองครั้งต่างกันแต่ถอดด้วย K ได้ทั้งคู่ | S22, S24 |
| P3-25 | Lookup=`Found(base64(K))` → `Protect` | Store 0; `[1]=0x01`; ถอดด้วย K | S24 |
| P3-26 | Lookup=`Unavailable` → `Protect` | Store 0; `[1]=0x02`; `secrets.key` 0600 ยาว 32; ถอดด้วยไบต์ในไฟล์ (aad `[0x01,0x02]`) | S24 |
| P3-27 | Lookup=`NotFound`, Store=`Unavailable` | `[1]=0x02`; `secrets.key` ถูกสร้าง | S24 |
| P3-28 | Lookup=`NotFound`, Store=`Failed` | CE; ไม่มี `secrets.key` | S24 |
| P3-29 | Lookup=`Failed("secret-error 2: locked")` | CE; Store 0; ไม่มี `secrets.key` | S24 |
| P3-30 | Lookup=`Found(x)` ด้วย x แต่ละตัวที่ ✗ ใน S24a | CE; Store 0 | S24a |
| P3-31 | seal ด้วย A (Found K) → B ใหม่ (Found K) `Unprotect` แล้ว `Protect` | ได้ pt; B: Lookup 2, Store 0; `[1]=0x01` | S23, S25 |
| P3-32 | blob SecretService → B ที่ Lookup=`Unavailable` → `Unprotect` | CE ข้อความมี `"Secret Service"` และ `"not available"`; ไม่มี `secrets.key`; Store 0 | S25 |
| P3-33 | blob SecretService → Lookup=`NotFound` / `Failed` → `Unprotect` | CE (`NotFound`: ข้อความมี `"Secret Service"`); Store 0 | S25 |
| P3-34 | **ไม่ migrate:** B `Unprotect` blob SecretService สำเร็จ (Found K) แล้วตั้ง fake เป็น Lookup=`Unavailable` แล้ว `Protect` | CE; ไม่มี `secrets.key`; Store 0 | S23, U2 |
| P3-35 | blob KeyFile → B ใหม่ที่ Lookup=`Found(base64(K2))` → `Unprotect` แล้ว `Protect` | ได้ pt; **Lookup 0, Store 0**; `[1]=0x02` | S23, S25, S26 |
| P3-36 | blob KeyFile แต่ลบ `secrets.key` → `Unprotect` | CE ข้อความมี `"key file"`; ไม่มี `secrets.key` ถูกสร้าง | S25 |
| P3-37 | B `Unprotect` blob KeyFile สำเร็จ แล้วลบ `secrets.key` แล้ว `Protect` | สำเร็จ; `[1]=0x02`; `secrets.key` ใหม่ (ไบต์ต่างจากเดิม) ถอดผลได้; Lookup 0 | S23 |
| P3-38 | กุญแจถูกแทนภายนอก: seal ด้วย Found K; ตั้ง fake เป็น Found K3 (≠K) → `Unprotect` | `AuthenticationTagMismatchException`; ไม่มีการ cache: ตั้งกลับเป็น Found K แล้ว `Unprotect` ได้ pt | S22, S25 |
| P3-39 | blob ยาว 29; `[0]=0x02`; `[1]=0x00`; `[1]=0x03` | CE ทุกตัว; fake ไม่ถูกเรียก; ไม่มีไฟล์ใหม่ | S25(1–3) |
| P3-40 | แก้บิตใน ciphertext | CE (tag) | S25(5) |
| P3-41 | blob KeyFile ที่เปลี่ยน `[1]` เป็น `0x01` และ Lookup=`Found(base64(<key ในไฟล์>))` | CE (AAD) | S2 |
| P3-42 | plaintext ว่าง | `Protect` ยาว 30; `Unprotect` ได้อาร์เรย์ว่าง | S1 |
| P3-43 | **adversarial (Inspector รอบ 2 ข้อ 5)** — ทุกแถวตรวจ `Message` ตรงตัว และ `InnerException == null`: (a) Lookup=`Failed(Message = <plaintext เป็นสตริง>)` → `Protect(pt)` (b) Lookup=`NotFound`, Store=`Failed(Message = "refused " + <secret ที่ fake ได้รับ>)` (c) Lookup=`Found("sk-test-0123456789abcdef")` → `Protect` (d) blob SecretService, Lookup=`Unavailable(Message = <plaintext>)` → `Unprotect` (e) blob SecretService, Lookup=`Failed(Message = "g-io-error-quark 19")` → `Unprotect` | (a) `The Secret Service could not be asked for the secret store key. (Failed: details withheld)` (b) `The Secret Service refused to store the secret store key. (Failed: details withheld)` (c) `The Secret Service item for the secret store key was not written by this app.` (d) `The secret store was sealed with a key from the Secret Service, which is not available in this session; the stored secrets were left unchanged. (Unavailable: details withheld)` (e) `The Secret Service could not provide the secret store key; the stored secrets were left unchanged. (Failed: g-io-error-quark 19)` | S8a, S27 |
| P3-44 | ข้อความ (รวม `ToString()` ของ exception และ inner ทุกชั้น) ของทุก CE ใน P3-28…43 | ไม่มี base64 ของกุญแจใด, ไม่มี `"sk-test-0123456789abcdef"`, ไม่มีสตริงที่ fake ได้รับเป็น secret | S27 |
| P3-45 | lock ถูกถือไว้ภายนอก (`LinuxFileLock.TryAcquire(LockFilePath, 0)`); protector ที่ `keyLockTimeout` = 300 ms → `Protect` | CE ข้อความตรงตัว `Another copy of AI Usage Monitor is creating the secret store key; try again.`; ใช้เวลา ≥ 250 ms; fake ถูกเรียก 0 ครั้ง; ไม่มี `secrets.key` | S24(2) |
| P3-46 | **race (Inspector ข้อ 1):** fake ตัวเดียวกัน (Lookup=`NotFound`, delay 100 ms), สอง protector instance บน key file path เดียวกัน, `Protect` พร้อมกันด้วย `Task.Run` × 10 รอบ (ล้าง fake และโฟลเดอร์ทุกรอบ) | ทุกรอบ Store = 1 ครั้งพอดี และผลของทั้งคู่ถอดด้วยกุญแจที่ Store ได้ | S24 |
| P3-46a | **ผู้ถือช้ากว่า timeout (ไม่ขึ้นกับ scheduling):** fake ร่วม Lookup=`NotFound`, `HoldLookup = true` · (1) A (`keyLockTimeout` 5 s) `Protect` ใน `Task.Run` (2) `await LookupEntered.WaitAsync(10 s)` — ณ จุดนี้ A ถือ key lock แน่นอน (Lookup ถูกเรียกใต้ lock เท่านั้น, S24(3)) (3) B (`keyLockTimeout` 200 ms) `Protect` ใน `Task.Run`; `await` สูงสุด 10 s (4) `LookupGate.Set()`; `await` A สูงสุด 10 s (5) B `Protect` อีกครั้ง | (3) B ได้ CE `Another copy of AI Usage Monitor is creating the secret store key; try again.` ตรงตัว, ใช้เวลา ≥ 150 ms, ณ ตอนนั้น Store รวม 0 และ Lookup รวม 1 (ของ A) (4) A สำเร็จ; Store รวม 1 (5) สำเร็จด้วย Found; Store ยังรวม 1; ผลของ A และ B ถอดได้ด้วยกุญแจเดียวกัน · ทุก wait มีขอบเขต — เกิน → เทสต์ล้ม ไม่ค้าง | S24(2), (ข), (จ) |
| P3-46b | **Store=`Unavailable` พร้อมกัน:** fake ร่วม Lookup=`NotFound` (delay 100 ms), Store=`Unavailable`; protector ใหม่สองตัว `Protect` พร้อมกัน × 10 รอบ (ล้างทุกรอบ) | ทุกรอบ: ทั้งคู่สำเร็จ; Store รวม **2** (ตัวละ 1); `secrets.key` หนึ่งไฟล์ โหมด 0600 ขนาด 32; blob ของทั้งคู่ header `01 02` และแต่ละตัว `Unprotect` blob ของอีกตัวได้ | S24 (ค) |
| P3-46c | **Store=`Failed` พร้อมกัน:** เหมือน P3-46b แต่ Store=`Failed(Message = "g-dbus-error-quark 9")` | ทุกรอบ: ทั้งคู่ CE `The Secret Service refused to store the secret store key. (Failed: g-dbus-error-quark 9)`; Store รวม 2; ไม่มี `secrets.key` | S24 (ง) |
| P3-50 | **parent symlink (Inspector รอบ 2 ข้อ 4):** โฟลเดอร์ของ `KeyFilePath` เป็น symlink ไปโฟลเดอร์ T โหมด 0755 (ว่าง) → `Protect` ด้วย Lookup=`Found(base64(K))` และอีกรอบด้วย Lookup=`Unavailable` | CE `The secret store folder is a symbolic link; refusing to use it.` ทั้งสองรอบ; fake ถูกเรียก 0 ครั้ง; T ยัง 0755 และว่าง (ไม่มี lock, ไม่มี `secrets.key`) | S24(0) |
| P3-51 | blob KeyFile และโฟลเดอร์ของ `KeyFilePath` เป็น symlink ไป T (มี `secrets.key` 0644 อยู่) → `Unprotect` | CE (จาก S18(2)); `T/secrets.key` ยัง 0644 | S18, S25 |
| P3-52 (`LinuxNonRootOnlyFact`) | โฟลเดอร์แม่ของ `dir` โหมด 0500 (สร้าง `dir` ไม่ได้) → `Protect` | CE `Cannot prepare the secret store folder.`, inner เป็น `UnauthorizedAccessException`/`IOException`; fake 0 ครั้ง | S24(1) |
| P3-53 | `LockFilePath` เป็น symlink (ไปไฟล์ใดก็ได้) → `Protect` | CE `Cannot lock the secret store key.`, inner `IOException`; fake 0 ครั้ง | S24(2), L5(1) |
| P3-54 (`LinuxOnlyFact`, root และ non-root เหมือนกัน) | `dir` 0700 มีอยู่แล้ว และ `secrets.key.lock` ของผู้ใช้เองโหมด 0000 (theory: 0000, 0400) → `Protect` ด้วย Lookup=`Found(base64(K))` | สำเร็จ; ไฟล์ lock โหมด 0600; Lookup 1 ครั้ง | S24(2), L5(2) |
| P3-47 | **end-to-end ไม่มี Secret Service:** `new DpapiSecretStore(<tmp>/d/secrets.dat, protector(Lookup=Unavailable, keyFile=<tmp>/d/secrets.key), new AtomicFileWriter())` → `SetAsync("openai","sk-test-0123456789abcdef")` → `GetAsync` | ค่าเดิม; ไม่มีไฟล์ใดใน `<tmp>` (recursive) ที่มีไบต์ UTF-8 ของ `"sk-test-0123456789abcdef"`; `secrets.dat` ขึ้นต้น `01 02`; `secrets.key` 0600 ยาว 32; `<tmp>/d` 0700 | S29 |
| P3-48 | **end-to-end มี Secret Service (fake):** เหมือน P3-47 แต่ Lookup=`NotFound`, Store=`Done` | ค่าเดิม; ไม่มี plaintext ในไฟล์ใด; `secrets.dat` ขึ้นต้น `01 01`; **ไม่มี** `secrets.key` | S24 |
| P3-49 | **ไม่อ่านไม่ได้แบบเงียบ:** จาก P3-48 store ใหม่บนไฟล์เดิมด้วย Lookup=`Unavailable` → `SetAsync("x","y")` | CE; ไบต์ `secrets.dat` เท่าเดิม; ไม่มี `secrets.key`; store ที่สามด้วย Found(K เดิม) → `GetAsync("openai")` ได้ค่าเดิม | S26, F4 |
| P3-SMOKE | `Linux/LibSecretKeyringSmokeTests.cs` ใน `[Collection(ProcessEnvironmentCollection.Name)]` ใช้ attribute ใหม่ `tests/.../LinuxSecretServiceSmokeFactAttribute.cs` ctor `(string because)`: ไม่ใช่ Linux → `Skip = $"Linux-only: {because}"`; env `AIM_SECRET_SERVICE_SMOKE` ≠ `"1"` → `Skip = $"Secret Service smoke: {because}"` · attributes `{service: "AIMonitor2.secrets.smoke-" + guid8, account: "secret-store-key"}`, schema `SchemaName` | Lookup → `NotFound`; Store(`"c21va2U="`) → `Done`; Lookup → `Found("c21va2U=")`; Clear → `Done`; Lookup → `NotFound` · Clear ใน `finally` | S9–S16 — ด้วยมือบนเครื่องจริง |
| P3-SMOKE-M4 | **กลไก skip (xUnit 2 ไม่มี skip ระหว่างรัน):** `LinuxSecretServiceSmokeFactAttribute` มี property `public string? RequiredEnvironment { get; init; }` รูป `"NAME"` (ต้องไม่ว่าง) หรือ `"NAME=VALUE"` (ต้องเท่ากัน ordinal) ตรวจ **หลัง** เงื่อนไข Linux และ `AIM_SECRET_SERVICE_SMOKE == "1"`; ไม่ผ่าน → `Skip = $"Secret Service smoke: {because}"` · สาม method ในไฟล์เดียวกัน (`RequiredEnvironment = "AIM_SECRET_SERVICE_SMOKE_M4=store"` / `=lookup` / `=clear`): `Smoke_M4_StoreProbeItem` (`AIM_SECRET_SERVICE_SMOKE_M4=store`) Store attributes `{service:"AIMonitor2.secrets.smoke-m4", account:"secret-store-key"}` ค่า `"bTQ="` แล้ว assert `Done` · `Smoke_M4_LookupProbeItem` (`…=lookup`) Lookup แล้วเขียน `$"M4 outcome: {r.Status} ({SafeDetail(r.Message)})"` ลง `ITestOutputHelper` และ **ผ่านเสมอ** · `Smoke_M4_ClearProbeItem` (`…=clear`) Clear แล้ว assert `Done` หรือ `NotFound` | ใช้กับ M4 | S15 |
| P3-SMOKE-CLEAR | method `Smoke_ClearManualScopeKeyringItem` ในไฟล์เดียวกัน, `RequiredEnvironment = "AIM_SECRET_SERVICE_SMOKE_CLEAR_SCOPE"` (ไม่ตั้ง → skip) · ค่าไม่ตรง `\Ar024m[2-6]-[0-9]{1,12}\z` → `Assert.Fail("refusing to clear a scope that M2-M6 did not create")` **โดยไม่เรียก keyring** · ตรง → `Clear(SchemaName, {service: "AIMonitor2.secrets.v1." + scope, account: "secret-store-key"})` | `Done` หรือ `NotFound` | M-cleanup |

### 6.4 ตรวจด้วยมือ (ผู้ใช้/Integrator บันทึกผลก่อนตรวจรับ — Inspector ข้อ 24)

**เตรียม shell (Inspector รอบ 3 ข้อ 6, รอบ 4 ข้อ 3, รอบ 5 ข้อ 5–7, รอบ 6 ข้อ 3–5)** — bash, ทำ M2–M-cleanup ใน shell เดียวกันที่ root ของ repo · ห้ามเปลี่ยน `HOME`/`XDG_DATA_HOME`/`XDG_CONFIG_HOME`/`XDG_RUNTIME_DIR` ของ shell ระหว่างนั้น (M5 ตั้งเฉพาะในคำสั่งของมันเอง) · เริ่มด้วย `r024_init` (ล้ม = หยุด) ·
**กติกา:** (ก) **ฟังก์ชันทั้งหมดไม่ลบอะไร** — สิ่งที่เขียนลงดิสก์มีแค่: โฟลเดอร์ `$HOME/.cache/aimonitor-r024/{tmp,manual}` (`r024_init`), โฟลเดอร์บันทึกใหม่ต่อ scope (`r024_new`), ไฟล์ `runtime` ในนั้น (`r024_after`) และไฟล์สคริปต์ใหม่ที่ `mktemp` สร้างใน M-cleanup · การลบเกิดเฉพาะเมื่อคนอ่านสคริปต์นั้นแล้วรันเอง (ข) ต่อ scope มีโฟลเดอร์บันทึก `$R024_REC/<scope>/` ไฟล์ละ **หนึ่ง path** (`printf '%s'`) (ค) ทุก path ถูกตรวจ **ก่อน** เขียนดิสก์ใด ๆ: ไม่ขึ้นต้น `/` หรือมี tab, newline, `'`, `"`, `\`, `*`, `?`, `[`, `]` → ปฏิเสธ (ง) ทุกเป้าหมายต้องไม่มีอยู่ตอน setup (จ) runtime ที่บันทึกคือที่แอปเลือกจริง (L3/L4) ตรวจแบบ literal ด้วย `find <dir> -maxdepth 1 -name 'si-*.lock'` (ฉ) บันทึกถูกอ่านแบบ raw (`IFS= read -r -d ''`) ไม่ตัด newline ท้าย จึง newline ใด ๆ ทำให้ถูกปฏิเสธ

```bash
r024_bad() {   # 0 = refuse
  case "$1" in /*) ;; *) return 0;; esac
  case "$1" in *$'\t'*|*$'\n'*|*\'*|*\"*|*\\*|*\**|*\?*|*\[*|*\]*) return 0;; esac
  return 1
}
r024_init() {
  if r024_bad "$HOME"; then echo "FAILED: refusing HOME"; return 1; fi
  export TMPDIR="$HOME/.cache/aimonitor-r024/tmp"; R024_REC="$HOME/.cache/aimonitor-r024/manual"
  mkdir -p -m 700 "$TMPDIR" "$R024_REC"
}
r024_scope_ok() { case "$1" in r024m[2-6]-[0-9]*) ;; *) return 1;; esac; case "${1#r024m?-}" in *[!0-9]*) return 1;; esac; return 0; }
r024_has_lock() { [ -n "$(find "$1" -maxdepth 1 -type f -name 'si-*.lock' -print -quit 2>/dev/null)" ]; }
r024_read() {   # $1 = record file · raw content (no newline stripping) into R024_V
  R024_V=; [ -f "$1" ] && [ ! -L "$1" ] || return 1
  IFS= read -r -d '' R024_V < "$1"; return 0
}
r024_new() {   # $1 = m2|m5|m6 ; $2 (ไม่บังคับ) = XDG_RUNTIME_DIR ที่จะใช้รันแอป
  local s d c r v
  [ -n "$R024_REC" ] || { echo "FAILED: run r024_init first"; return 1; }
  s="r024$1-$(date +%s)"; r024_scope_ok "$s" || { echo "FAILED: bad label $1"; return 1; }
  case "$XDG_DATA_HOME" in /*) d="$XDG_DATA_HOME";; *) d="$HOME/.local/share";; esac
  case "$XDG_CONFIG_HOME" in /*) c="$XDG_CONFIG_HOME";; *) c="$HOME/.config";; esac
  r="${2-$XDG_RUNTIME_DIR}"
  for v in "$d" "$c"; do if r024_bad "$v"; then echo "FAILED: refusing '$v'"; return 1; fi; done
  if [ -n "$r" ] && r024_bad "$r"; then echo "FAILED: refusing runtime dir"; return 1; fi
  d="${d%/}/AIUsageMonitor.$s"; c="${c%/}/autostart/net.apichart.aiusagemonitor.$s.desktop"
  if [ -n "$r" ]; then r="${r%/}/AIUsageMonitor.$s"; fi
  for v in "$d" "$c" "$R024_REC/$s" ${r:+"$r"}; do if [ -e "$v" ] || [ -L "$v" ]; then echo "FAILED: exists already: $v"; return 1; fi; done
  mkdir -m 700 "$R024_REC/$s" || return 1
  printf '%s' "$d" > "$R024_REC/$s/data"; printf '%s' "$c" > "$R024_REC/$s/autostart"; printf '%s' "$r" > "$R024_REC/$s/runtime-candidate"
  S="$s"; DATA="$d"; echo "scope=$S"
}
r024_m5_new() {   # M5 เท่านั้น · หยุดถ้าโฟลเดอร์ runtime ที่ต้อง "ไม่มี" มีอยู่
  local r="$TMPDIR/r024-absent-runtime"
  if [ -e "$r" ] || [ -L "$r" ]; then echo "FAILED: $r exists; M5 stopped"; return 1; fi
  R5="$r"; r024_new m5 "$r"
}
r024_after() {   # $1 = scope · หลังแอปของ scope นั้นปิดครั้งแรก · บันทึกโฟลเดอร์ socket ที่แอปเลือกจริง
  local rec="$R024_REC/$1" d r
  r024_scope_ok "$1" && [ -d "$rec" ] || { echo "FAILED: unknown scope $1"; return 1; }
  r024_read "$rec/data" && d="$R024_V" && ! r024_bad "$d" || { echo "FAILED: bad data record"; return 1; }
  r024_read "$rec/runtime-candidate"; r="$R024_V"
  if [ -n "$r" ] && ! r024_bad "$r" && [ -d "$r" ] && [ ! -L "$r" ] && r024_has_lock "$r"; then
    printf '%s' "$r" > "$rec/runtime"; echo "runtime: XDG_RUNTIME_DIR ($r)"
  elif r024_has_lock "$d"; then
    printf '' > "$rec/runtime"; echo "runtime: fallback inside the data folder"
  else echo "FAILED: no si-*.lock in either place"; return 1; fi
}
r024_cleanup_print() {   # เขียนลง stdout เท่านั้น · ไม่ลบ ไม่สร้างไฟล์
  local rec s f v want n=0
  for rec in "$R024_REC"/r024m*; do
    [ -d "$rec" ] && [ ! -L "$rec" ] || continue
    s="${rec##*/}"
    if ! r024_scope_ok "$s" || r024_bad "$rec"; then echo "# REFUSED: odd record folder (inspect by hand)"; continue; fi
    n=$((n+1)); echo "# scope $s"
    for f in data autostart runtime; do
      [ -e "$rec/$f" ] || [ -L "$rec/$f" ] || continue
      if ! r024_read "$rec/$f"; then echo "# REFUSED: $f record in $rec is not a regular file"; continue; fi
      v="$R024_V"; [ -n "$v" ] || continue
      case "$f" in autostart) want="net.apichart.aiusagemonitor.$s.desktop";; *) want="AIUsageMonitor.$s";; esac
      if r024_bad "$v" || [ "${v##*/}" != "$want" ]; then echo "# REFUSED: odd $f record in $rec"; continue; fi
      if [ -e "$v" ] || [ -L "$v" ]; then printf "rm -rf -- '%s'\n" "$v"; fi
    done
    printf "AIM_SECRET_SERVICE_SMOKE=1 AIM_SECRET_SERVICE_SMOKE_CLEAR_SCOPE='%s' dotnet test tests/AIMonitor.Infrastructure.Tests/AIMonitor.Infrastructure.Tests.csproj --filter Smoke_ClearManualScopeKeyringItem\n" "$s"
    printf "rm -rf -- '%s'\n" "$rec"
  done
  [ "$n" -gt 0 ] || echo "# nothing recorded"
}
```

**`# REFUSED` เกิดเมื่อ:** ชื่อโฟลเดอร์บันทึกไม่ตรง `r024m[2-6]-<ตัวเลข>` หรือ path ของมันผิดกติกา (ค) · ไฟล์บันทึกไม่ใช่ไฟล์ธรรมดา (symlink/โฟลเดอร์) · เนื้อหา raw ผิดกติกา (ค) (รวม newline ท้าย) · basename ไม่ใช่ `AIUsageMonitor.<scope>` (data, runtime) หรือ `net.apichart.aiusagemonitor.<scope>.desktop` (autostart) · **ข้อจำกัดที่ยอมรับ:** บันทึกที่ถูกแก้ให้ชี้ path ที่มีอยู่จริงและผ่านกติกาทั้งหมด (เช่น basename ตรงแต่อยู่ในโฟลเดอร์อื่น) จะถูกพิมพ์เป็นคำสั่ง `rm` ได้ — ขั้นตอนนี้ **ไม่พิสูจน์ว่าใครสร้าง path** · การป้องกันคือคนอ่านทุกบรรทัดก่อนรัน (M-cleanup ข้อ 3) · `'` ถูกปฏิเสธจึงคำสั่งใน `'…'` อ่านตรงตัว · `<app>` = คำสั่งรันแอปของ build ที่ตรวจ

| # | ทำ | ผลที่ต้องบันทึก |
|---|---|---|
| M1 | `AIM_SECRET_SERVICE_SMOKE=1 dotnet test … --filter LibSecretKeyringSmokeTests` | ผ่าน 1 |
| M2 | **เตรียม:** `r024_new m2 && M2_S="$S" M2_DATA="$DATA"` (ล้ม = รันใหม่) · desktop session ที่ keyring ปลดล็อกอยู่ · **ทำ:** `AI_USAGE_MONITOR_SETTINGS_SCOPE=$S <app>` → หน้า **Connections** → การ์ด OpenAI → ใส่ key ปลอม `sk-r024-manual-not-a-real-key` → Save → ปิดแอป → `r024_after "$M2_S"` → เปิดใหม่ด้วย scope เดิม → Connections | `r024_after` พิมพ์บรรทัด `runtime:` (ไม่ใช่ `FAILED`); ข้อความ "Saved the key for OpenAI."; หลังเปิดใหม่การ์ดแสดงว่ามี key บันทึกไว้; `xxd -p -l 2 "$M2_DATA/secrets.dat"` = `0101`; `test ! -e "$M2_DATA/secrets.key"`; KWallet/Seahorse มี item "AI Usage Monitor secret store key (AIMonitor2.secrets.v1.$M2_S)" · M3/M4 ใช้ `$M2_S`/`$M2_DATA` |
| M3 | **เตรียม:** ต้องผ่าน M2 ก่อน (store เดิมที่ header `0101`) · ล็อก keyring · **ทำ:** `AI_USAGE_MONITOR_SETTINGS_SCOPE="$M2_S" <app>` → Connections → ปลดล็อกใน prompt | การ์ด OpenAI ยังแสดง key ที่บันทึกไว้ |
| M4 | **เตรียม:** store เดิมจาก M2 (header `0101` — แยกจาก M5 ที่เป็นการเขียนครั้งแรก) · `H=$(sha256sum < "$M2_DATA/secrets.dat")` · ล็อก keyring · **ทำ:** `AI_USAGE_MONITOR_SETTINGS_SCOPE="$M2_S" <app>` → Connections → ใส่ key ใหม่ `sk-r024-m4` → Save → **ปิด** prompt · แล้ว (เก็บผล native) `AIM_SECRET_SERVICE_SMOKE=1 AIM_SECRET_SERVICE_SMOKE_M4=store dotnet test … --filter Smoke_M4_StoreProbeItem` → ล็อก keyring → `…=lookup … --filter Smoke_M4_LookupProbeItem --logger "console;verbosity=detailed"` ปิด prompt → ปลดล็อก → `…=clear … --filter Smoke_M4_ClearProbeItem` | **ตรวจรับ (ทุกข้อบังคับ):** หน้า Connections แสดง `Could not save the key for OpenAI: ` ตามด้วยข้อความคงที่ของตาราง S27 (เช่น `The Secret Service could not be asked for the secret store key. (Failed: …)`) และไม่มีส่วนใดของ `sk-r024-m4` ในข้อความ (U7, W3f) · `[ "$(sha256sum < "$M2_DATA/secrets.dat")" = "$H" ]`; `test ! -e "$M2_DATA/secrets.key"`; ปิดเปิดแอปแล้วปลดล็อก → การ์ดยังมี key เดิม (ไม่ใช่ `sk-r024-m4`) · **บันทึก:** บรรทัด `M4 outcome: …` (ปิด R1) |
| M5 | **เตรียม + เปิดแอปในคำสั่งเดียว (หยุดเมื่อเงื่อนไขไม่ครบ):** `r024_m5_new && env -u DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR="$R5" AI_USAGE_MONITOR_SETTINGS_SCOPE="$S" <app>` — `r024_m5_new` คืนค่าไม่สำเร็จและไม่สร้าง scope ถ้า `$TMPDIR/r024-absent-runtime` มีอยู่ (โฟลเดอร์ที่ไม่มีจริงใช้ไม่ได้ตาม L3(ค) → fallback L4) · พิมพ์ `FAILED` = M5 หยุด ห้ามเปิดแอปแยก · **ทำ:** (ในแอปที่เปิดจากคำสั่งข้างบน) → Connections → OpenAI → `sk-r024-manual-not-a-real-key` → Save → ปิดแอป → `r024_after "$S"` | ไม่มี `FAILED`; `r024_after` พิมพ์ `runtime: fallback inside the data folder` (L4); `[ ! -e "$R5" ]` ยังจริง; "Saved the key for OpenAI."; `xxd -p -l 2 "$DATA/secrets.dat"` = `0102`; `stat -c '%a %s' "$DATA/secrets.key"` = `600 32`; `stat -c %a "$DATA"` = `700` |
| M6 | **เตรียม:** `r024_new m6` (profile ใหม่ → ค่า default `StartWithWindows = true`, `AppSettings.cs:9`) · `E=$(cat "$R024_REC/$S/autostart")` · **ทำ:** `AI_USAGE_MONITOR_SETTINGS_SCOPE="$S" <app>` แล้วปิด → `r024_after "$S"` → `cat -- "$E"` และดูหน้า Autostart ของ KDE → ปิด entry นั้นในหน้า Autostart ของ KDE → เปิดแอปด้วย `$S` → Settings · ขณะแอปรัน เปิดแอปซ้ำด้วย `$S` | ไฟล์ `.desktop` ถูกสร้างตอนเปิดครั้งแรก (reconcile ของ profile ใหม่ `StartupSettingsReconciler.cs:24-26`) และขึ้นในหน้า Autostart; หลังปิดใน KDE สวิตช์ "start at login" ใน Settings เป็นปิด; การเปิดซ้ำนำหน้าต่างเดิมขึ้นมา · **นอก R-024 (U8, N11):** การเปิด/ปิดสวิตช์ใน Settings แล้วไฟล์ถูกสร้าง/ลบ — ไม่ตรวจ |
| M-cleanup | **print-only (Inspector รอบ 5 ข้อ 5–7):** (1) ปิดแอปทุกตัวของ M2–M6 (รวม tray) แล้ว `pgrep -fa AIMonitor.Presentation.Avalonia` ต้องว่าง (2) `out=$(mktemp "$TMPDIR/r024-cleanup.XXXXXXXX") && r024_cleanup_print > "$out" && echo "$out"` — `mktemp` สร้างไฟล์ใหม่แบบ exclusive (ไม่ทับไฟล์เดิม ไม่ตาม symlink) · ขั้นนี้ไม่ลบอะไร (3) ผู้ทดสอบอ่านทุกบรรทัด: ทุก `rm -rf -- '<path>'` ต้องเป็น path ที่ตัวเองจำได้ว่ามาจาก M2–M6 (basename `AIUsageMonitor.r024m…`, `net.apichart.aiusagemonitor.r024m….desktop` หรือโฟลเดอร์บันทึกใต้ `$HOME/.cache/aimonitor-r024/manual/`) · ลบบรรทัดที่ไม่แน่ใจออก (4) ปลดล็อก keyring แล้วรันไฟล์นั้นเองด้วย `bash -e -x "$out"` (`-e` หยุดที่คำสั่งแรกที่ล้ม จึงบรรทัดลบโฟลเดอร์บันทึกของ scope นั้นไม่ถูกรัน) (การล้าง keyring ใช้ P3-SMOKE-CLEAR ซึ่งปฏิเสธชื่อที่ไม่ตรง `r024m[2-6]-<ตัวเลข>`) | (2) ไม่มีบรรทัด `# REFUSED`; ถ้ามี → บันทึกและจัดการด้วยมือ (3) ผู้ทดสอบยืนยัน (4) ไม่มี error; `r024_cleanup_print` รอบใหม่พิมพ์แค่หัว `# nothing recorded`; KWallet/Seahorse ไม่มี item `AIMonitor2.secrets.v1.r024m*` · **ห้ามเขียน cleanup ที่ลบเองอัตโนมัติ** |
| — | timeout race และ error branch อื่นของ native ตรวจด้วยมือไม่ได้ — ยอมรับเป็นความเสี่ยง R1/R2 | |

---

## 7. ความเป็นเจ้าของไฟล์และ gate ต่อเฟส

เสนอผู้เขียน: เฟส 1 → Writing Owner 1, เฟส 2 → Writing Owner 2, เฟส 3 → Writing Owner 3 (Coding Owner ยืนยันใน handoff §5.3) ·
Test Author (รอบ 4): **T0 ก่อนเสมอ** แล้วเฟสใดก็ได้ที่ spec พร้อม — ลำดับปัจจุบัน T0 → 2 → 1 → 3 (session เดียวหรือหลาย session ก็ได้ แต่ไฟล์หนึ่งมีผู้เขียนหนึ่งคนต่อเวลา; `PlatformAdaptersTests.cs` ถูกแก้ทีละเฟสตามลำดับ merge) · เทสต์เฟส 3 compile ได้เมื่อ production เฟส 1 (`LinuxFileLock`) ลงแล้ว · ห้ามแก้ไฟล์นอกตาราง (เปลี่ยนตารางได้ตามขั้นตอน §4)

| เฟส | production (Writing Owner) | เทสต์ (Test Author) |
|---|---|---|
| T0 | — | ใหม่ `tests/AIMonitor.Infrastructure.Tests/LinuxOnlyFactAttribute.cs`, `tests/AIMonitor.Infrastructure.Tests/LinuxNonRootOnlyFactAttribute.cs`, `tests/AIMonitor.Infrastructure.Tests/ProcessEnvironmentCollection.cs` · `PlatformAdaptersTests.cs` T0 (บรรทัด `[Collection]`) |
| 1 | ใหม่ `src/AIMonitor.Infrastructure/Linux/LinuxRuntimeDirectory.cs`, `src/AIMonitor.Infrastructure/Linux/LinuxFileLock.cs`, `src/AIMonitor.Infrastructure/Linux/LinuxSingleInstance.cs` · `PlatformAdapters.cs` W1a, W1b | ใหม่ `tests/AIMonitor.Infrastructure.Tests/Linux/LinuxRuntimeDirectoryTests.cs`, `tests/AIMonitor.Infrastructure.Tests/Linux/LinuxFileLockTests.cs`, `tests/AIMonitor.Infrastructure.Tests/Linux/LinuxSingleInstanceTests.cs` · `PlatformAdaptersTests.cs` T1a–T1d + `using` (ถ้า T2 ยังไม่ได้ใส่) |
| 2 | ใหม่ `src/AIMonitor.Infrastructure/Linux/XdgDesktopEntry.cs`, `src/AIMonitor.Infrastructure/Linux/XdgAutostartStartupRegistrar.cs` · `PlatformAdapters.cs` W2 | ใหม่ `tests/AIMonitor.Infrastructure.Tests/Linux/XdgDesktopEntryTests.cs`, `tests/AIMonitor.Infrastructure.Tests/Linux/XdgAutostartStartupRegistrarTests.cs` · `PlatformAdaptersTests.cs` T2a, T2b |
| 3 | ใหม่ `src/AIMonitor.Infrastructure/Linux/ISecretServiceKeyring.cs`, `src/AIMonitor.Infrastructure/Linux/SecretServiceErrors.cs`, `src/AIMonitor.Infrastructure/Linux/LibSecretKeyring.cs`, `src/AIMonitor.Infrastructure/Linux/SecretKeyFile.cs`, `src/AIMonitor.Infrastructure/Linux/LinuxSecretProtector.cs` · `PlatformAdapters.cs` W3a–W3d · `src/AIMonitor.Presentation.Avalonia/PlatformHost.cs` W3e (สตริงเดียว) · `src/AIMonitor.Presentation.Avalonia/ViewModels/ConnectionsViewModel.cs` W3f (บรรทัดเดียว) · ใช้ `LinuxFileLock` ของเฟส 1 โดยไม่แก้ | ใหม่ `tests/AIMonitor.Infrastructure.Tests/Linux/SecretServiceErrorsTests.cs`, `tests/AIMonitor.Infrastructure.Tests/Linux/SecretKeyFileTests.cs`, `tests/AIMonitor.Infrastructure.Tests/Linux/LinuxSecretProtectorTests.cs`, `tests/AIMonitor.Infrastructure.Tests/Linux/LibSecretKeyringSmokeTests.cs`, `tests/AIMonitor.Infrastructure.Tests/LinuxSecretServiceSmokeFactAttribute.cs` · `PlatformAdaptersTests.cs` T3a, T3b, T3d · `tests/AIMonitor.Presentation.Avalonia.Tests/ViewModels/ConnectionsViewModelTests.cs` T3e (เพิ่ม method เดียว + `using`) |
| หลังเฟส 3 (Coding Owner/Integrator) | `Docs/ARCHITECTURE.md`, `Docs/TESTING.md`, `Requirements.md` R-024 | — |

**กติกา comment ในโค้ด (รอบ 8, ทุกเฟส):** comment ในไฟล์ production และเทสต์ใหม่เป็น **ภาษาอังกฤษ** แบบเดียวกับ `src/AIMonitor.Infrastructure/MacOS/*.cs` · อ้างได้เฉพาะ requirement id (`R-024`, `R-022`) · ห้ามอ้าง id ของ spec/Inspector (`A2(1)`, `F13`, "Inspector …") และห้ามอ้างเลขบรรทัดของไฟล์อื่น · เฟส 2 แก้ comment ที่มีอยู่ในรอบแก้ของ WO2

**แยกเฟส 3 (รอบ 8):** **3a** เขียนและ compile ได้ทันทีโดยไม่ต้องมี `LinuxFileLock`: `ISecretServiceKeyring.cs`, `SecretServiceErrors.cs`, `LibSecretKeyring.cs`, `SecretKeyFile.cs`, W3e, W3f · เทสต์: `SecretServiceErrorsTests.cs`, `SecretKeyFileTests.cs`, `LibSecretKeyringSmokeTests.cs` + `LinuxSecretServiceSmokeFactAttribute.cs`, T3e · **3b** หลังเฟส 1 ลง: `LinuxSecretProtector.cs` (ใช้ `LinuxFileLock`), `PlatformAdapters.cs` W3a–W3d · เทสต์ `LinuxSecretProtectorTests.cs`, T3a, T3b, T3d · gate ของ 3a: ชุดที่ต้องล้มบน Linux เท่ากับชุดของเฟสก่อนหน้า (3a ไม่แตะ `PlatformAdapters.cs`) และ T3e ต้องผ่าน · ownership ตามตารางเดิม (WO3 / Test Author)

ห้ามแตะทุกเฟส: `src/AIMonitor.Infrastructure/MacOS/*` (เรียกใช้ได้), `Windows/`, `Storage/`, `src/AIMonitor.Presentation.*` (รวม `App.axaml.cs`, `SettingsViewModel.cs`) — **ข้อยกเว้นมีสองจุดพอดี ทั้งคู่ในเฟส 3:** W3e `PlatformHost.cs:47` และ W3f `ConnectionsViewModel.cs:327`, `src/AIMonitor.Application/`, `*.csproj`, `Directory.Build.props`, เทสต์ของ macOS/Windows · เทสต์ Presentation แตะได้เฉพาะ T3e

**Gate ต่อเฟส (Inspector ข้อ 2)** — ประเมินบนผลรวมของโค้ด + เทสต์ของเฟสนั้นหลัง rebase · ห้ามใส่ skip/ignore เพื่อให้ผ่าน gate:

```
export PATH=$HOME/.dotnet:$PATH
dotnet build AIMonitor.CrossPlatform.slnf                       # 0 warning 0 error ทุกเฟส
dotnet format AIMonitor.CrossPlatform.slnf --verify-no-changes  # สะอาดทุกเฟส
dotnet test tests/AIMonitor.Infrastructure.Tests/AIMonitor.Infrastructure.Tests.csproj
```

| หลังเฟส | บน **Linux**: ชุดเทสต์ที่ **ต้องล้ม** (ชื่อตรงตัว ไม่มากไม่น้อย) — Inspector รอบ 2 ข้อ 9 | บน Linux: ที่เหลือ · บน **Windows/macOS** (ทุกเฟส): **ไม่มีข้อล้ม** — ตรวจด้วย CI macOS หลัง push / รอบ Windows ของ Integrator ก่อน merge |
|---|---|---|
| 1 | `CreateStartupRegistrar_PicksTheRunKeyOnWindowsAndALaunchAgentOnMacOS`, `CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS`, `CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms` | ผ่านหรือ skip ที่มีเหตุผล |
| T0 อย่างเดียว | ชุดเดิมก่อน R-024 ไม่เปลี่ยน: `CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOS`, `CreateStartupRegistrar_PicksTheRunKeyOnWindowsAndALaunchAgentOnMacOS`, `CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS`, `CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms` | ผ่านหรือ skip ที่มีเหตุผล |
| 2 ก่อน 1 (T0 + เฟส 2; ลำดับปัจจุบัน) | `CreateSingleInstanceCoordinator_PicksTheMutexOnWindowsAndASocketOnMacOS`, `CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS`, `CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms` | ผ่านหรือ skip ที่มีเหตุผล |
| 1 และ 2 (ลำดับใดก็ได้) | `CreateSecretProtector_PicksDpapiOnWindowsAndTheKeychainOnMacOS`, `CreateSecretStore_IsTheSameSealedFileStoreOnBothPlatforms` | ผ่านหรือ skip ที่มีเหตุผล |
| 3 | ไม่มี — และ `dotnet test AIMonitor.CrossPlatform.slnf` ต้องไม่มีข้อล้ม (รวม `AIMonitor.Presentation.Avalonia.Tests` ที่มี T3e) | ผ่านหรือ skip ที่มีเหตุผล (P3-SMOKE skip ด้วย `Secret Service smoke:`) |

---

## 8. Non-goals

- N1 ไม่แก้ WPF และไม่เปลี่ยนพฤติกรรม Windows/macOS
- N2 **tray/menu bar บน Linux อยู่นอก R-024 (U3)** — `App.axaml.cs` ไม่ถูกแตะ แม้ `ApplyMenuBar` (`App.axaml.cs:127-145`) จะสร้าง `MenuBarHost` บนทุก OS เมื่อเปิด tray · การปิด/ปรับ tray บน Linux และผลของ "minimize to tray" เป็นงานของ **R-025** ที่ผู้ใช้จะเปิด · ระหว่างนี้หน้าต่างที่ถูกซ่อนเรียกกลับได้ด้วยการเปิดแอปซ้ำ (L8)
- N3 ไม่ทำแพ็กเกจ และไม่เพิ่ม CI ของ Linux
- N4 ไม่แตะเรื่อง update ของ Linux edition (R-023)
- N5 ไม่ migrate กุญแจ (U2) · ไม่มี `Forget()`
- N6 ไม่นำเข้า profile 1.x · ไม่แตะ `CreateClaudeCredentialReader`
- N7 ไม่จัดการ autostart ระดับระบบ, `OnlyShowIn`/`NotShowIn`, systemd user unit
- N8 Presentation แก้ได้สองจุดพอดี: สตริง `PlatformHost.cs:47` (U5, W3e) และบรรทัด catch `ConnectionsViewModel.cs:327` (U7, W3f) · ส่วนอื่นของ Presentation ทั้งหมดไม่แตะ
- N9 ไม่แก้ race ของ `LocalSocketSingleInstance` บน macOS (F14) และไม่เสนอ requirement (U6)
- N10 ไม่ตรวจ/validate scope เพิ่ม (A17)
- N11 **(U8)** สวิตช์ "start at login" ใน Settings ไม่เรียก registrar (`SettingsViewModel.cs:200-203`) — ช่องว่างเดิมทั้ง macOS และ Linux เป็น requirement ใหม่แยก นอก R-024 · `SettingsViewModel.cs` ไม่ถูกแตะ · M6 ตรวจ autostart ด้วย profile ใหม่ + การปิดใน KDE เท่านั้น
- N12 ไม่เพิ่ม deadline ต่อ connection ให้ server ของ `LocalSocketSingleInstance` (ต้นเหตุที่เจ้าของต่อไม่ได้เมื่อ queue เต็ม, Inspector รอบ 3 ข้อ 2) — L7(5ข) กันการแทนที่แทน; แอปที่ค้างแบบนั้นต้องปิดเอง

## 9. คำถามเปิดและความเสี่ยง

**คำถามที่ block เฟส:** ไม่มี (U1–U8 final; ข้ออื่น Coding Owner ตัดสินใน §11, §11b, §11c)

**คำถามขอบเขตที่ตอบแล้ว (เก็บไว้เป็นประวัติ — Q-6 = (ก) → U7/W3f/T3e; Q-7 = (ข) → U8/N11)**
- Q-6 (Inspector รอบ 2 ข้อ 6) เมื่อบันทึก key ไม่ได้เพราะ keyring ล็อก/ปิด prompt protector จะ throw `CryptographicException` แต่ `ConnectionsViewModel.SaveAsync` (`ConnectionsViewModel.cs:326-327`) จับเฉพาะ IO/Unauthorized/InvalidOperation/Argument และ view ทิ้ง task (`ConnectionsWindow.axaml.cs:31`) → ผู้ใช้ไม่เห็นอะไรเลย (ข้อมูลไม่เสีย) · ทางเลือก (ก) เพิ่ม `CryptographicException` ใน `when` ของบรรทัด 327 — แก้หนึ่งบรรทัดใน Presentation, ข้อความที่แสดงคือข้อความคงที่ S27 ซึ่งปลอดภัยแล้ว, กระทบ macOS ด้วย (Keychain ล้มก็จะเห็นข้อความแทนเงียบ — ดีขึ้น), ต้องมีเทสต์ใน `AIMonitor.Presentation.Avalonia.Tests` · (ข) เลื่อนไป requirement อื่น — ไม่เพิ่ม scope แต่ผู้ใช้กด Save แล้วไม่มี feedback · **Coding Owner แนะนำ (ก)** ในเฟส 3
- Q-7 (Inspector รอบ 2 ข้อ 7) สวิตช์ "start at login" ใน Settings บันทึกแค่ `StartWithWindows` (`SettingsViewModel.cs:200-203`) ไม่เรียก registrar · registrar ถูกเรียกเฉพาะตอนเริ่มแอปของ profile ใหม่ (`StartupSettingsReconciler.cs:24-26`) และครั้งต่อไปค่าจะถูก reconcile กลับเป็น false ถ้าไม่มี entry — **เป็นช่องว่างเดิมของแอป Avalonia บน macOS ด้วย** (R-022) ไม่ใช่ของ Linux · ทางเลือก (ก) ทำใน R-024: แก้ Application/Presentation ให้ Save เรียก `Register`/`Unregister` — เปลี่ยนพฤติกรรม macOS ซึ่ง R-024 ห้าม · (ข) แยกเป็น requirement ใหม่ที่ครอบทั้ง macOS และ Linux · **Coding Owner แนะนำ (ข)**; R-024 ตรวจ autostart ด้วย M6 แบบ profile ใหม่ + การปิดใน KDE แทน

**ความเสี่ยง**
- R1 พฤติกรรม libsecret ตอนปิด prompt ยังไม่ได้พิสูจน์ (ปิดด้วย M4) · layout ของ GError/`secret_schema_newv` พิสูจน์ด้วย M1 เท่านั้น
- R2 call ของ libsecret บล็อกได้ถึง 30 s (อาจบน UI thread) · prompt ที่ช้ากว่า 30 s นับเป็น Unavailable → ไฟล์ใหม่ fallback เป็น key file
- R3 single-instance ต่อผู้ใช้ข้าม session; session ที่ resolve โฟลเดอร์ต่างกันรันได้สองตัว (L9)
- R4 เปิดด้วย `dotnet x.dll` → autostart รัน `dotnet` เปล่า
- R5 โหมด fallback ป้องกันด้วยสิทธิ์ไฟล์เท่านั้น
- R6 ไม่มี CI Linux
- R7 T1c ใช้ `XDG_RUNTIME_DIR` จริงด้วย scope สุ่ม และลบโฟลเดอร์ของตัวเอง
- R8 เกณฑ์ "ผู้ใช้รันแอปจริง" และ M1–M6 ตรวจอัตโนมัติไม่ได้
- R9 lock (L5) พึ่ง flock ของ .NET; ถ้าตั้ง `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` lock จะไม่มีผล → race ของ F14 กลับมา และ S24 อาจ Store สองครั้ง
- R10 home ที่ยาวมากจน fallback socket path > 103 byte → แอปเปิดไม่ได้ (เหมือน macOS)
- R11 process ตายระหว่างสร้างกุญแจอาจทิ้ง `.secrets.key.<guid>.tmp`
- R13 เจ้าของที่มีชีวิตแต่ต่อไม่ได้ (queue เต็ม/ค้าง) ทำให้ launch ใหม่เปิดหน้าต่างที่สองแบบไม่มี guard แทนการแทนที่ (L7(5ข), N12)
- R14 W3f กระทบ macOS: Keychain ที่ throw `CryptographicException` ตอน Save จะแสดงข้อความ `Could not save the key …: <Message ของ Keychain>` แทนการเงียบ (U7 อนุมัติ) · ข้อความของ `KeychainSecretProtector` ไม่อยู่ใต้ whitelist S27
- R15 L7a(5): ถ้า dispose ไม่ได้ lock ภายใน 10 s listener ของเจ้าของยังเปิดจน process จบ (`Activated` อาจยังยิง) · **E1 (L8):** exception ใดใน `TryListen` หลัง `Bind` สำเร็จและก่อน `_listener = listener` (`Listen` หรือ chmod ล้ม, `LocalSocketSingleInstance.cs:193-202`) → socket ที่ไม่มีใครถือ อาจ unlink นอก lock ภายหลัง → หลัง E1 "เจ้าของไม่เกินหนึ่ง" ไม่ได้รับประกัน · ยอมรับ ไม่แก้ไฟล์ macOS (U6)
- R12 เส้นทาง fail-open (L9) ทำให้รันสองตัวได้ (เจ้าของไม่เกินหนึ่ง) → สองผู้เขียน `secrets.dat`: การเขียนล่าสุดชนะ ไฟล์ไม่เสีย แต่การแก้ของอีกตัวอาจหาย · การสร้างกุญแจยังกันด้วย key lock

## 10. เทียบเกณฑ์ตรวจรับของ R-024

| เกณฑ์ | พิสูจน์ด้วย |
|---|---|
| build บน Linux ไม่มี warning | gate §7 |
| `PlatformAdaptersTests` 4 ข้อผ่าน ไม่ลดทอน Windows/macOS | T1a, T2a, T3b, T3c (+ T3a) · กฎ §5 |
| เทสต์ใหม่ต่อ adapter: autostart เขียน/ลบ · seal/unseal · เปิดซ้ำ activate | P2-58…P2-66 · P3-23…P3-49 · T1c, P1-21, P1-23 |
| ไม่มีความลับเป็นข้อความธรรมดา และไฟล์กุญแจ 0600 | P3-47, P3-48, P3-13, P3-17 |
| ทดสอบทั้งมี/ไม่มี Secret Service | P3-48 · P3-47 · P3-49 · P3-SMOKE/M1–M5 |
| `dotnet test AIMonitor.CrossPlatform.slnf` ไม่มีข้อล้ม | gate เฟส 3 |
| ผู้ใช้รันแอปจริงบน Linux | M2–M6 |
| บันทึก key ไม่ได้ → ผู้ใช้เห็นเหตุผล (U7) | T3e · M4 |

## 11. Inspector A รอบ 1 — คำตัดสิน (Coding Owner, 2026-11-21)

| # | ระดับ | คำตัดสิน | อยู่ที่ |
|---|---|---|---|
| 1 | blocker | **adopt** — race ของ single-instance ปิดด้วย decorator `LinuxSingleInstance` + `LinuxFileLock` ในเฟส 1 (ไฟล์ใหม่ของ Linux; `LocalSocketSingleInstance` ไม่ถูกแก้ จึงไม่ขยาย scope) · race ของการสร้างกุญแจปิดด้วย lock `secrets.key.lock` ภายใต้ Lookup+Store ในเฟส 3 (fail-closed เมื่อรอ lock เกิน 10 s) · เทสต์ P1-21, P1-22, P3-45, P3-46 | F14, L5–L8, S24, §6 |
| 2 | blocker | **adopt** — gate ต่อเฟสระบุชื่อเทสต์ที่ต้องล้มตรงตัว | §7 |
| 3 | blocker | **adopt** — §2 และ `HasSessionBus` ใช้กฎ `/` ที่ไม่ขึ้นกับ host; เทสต์ pure รันทุก OS, เทสต์ดิสก์เป็น Linux-only | §2 หัว, A2, A9–A11, S8, §6 |
| 4 | blocker | **adopt** — U1–U4 final ที่หัวเอกสาร; Q เดิม 4–6 ตัดสินแล้ว (N8, L3(ฉ), `LinuxNonRootOnlyFact`); เหลือแค่คำถามที่ไม่ block | หัว, §9 |
| 5 | blocker | **adopt** (คำตัดสินผู้ใช้ U3) — tray อยู่นอก R-024 ไป R-025; ไม่แตะ `App.axaml.cs` | N2 |
| 6 | should-fix | **adopt** — F4 จำกัดเป็นผู้เขียน process เดียวต่อ scope + ไม่มีการแก้ภายนอก | F4 |
| 7 | should-fix | **adopt** — ต้องมีบิต owner rwx, ไม่มี group/other, บิตพิเศษไม่สนใจ, ตรวจการเข้าถึงแทนเจ้าของ, root ยอมรับ | L3 |
| 8 | should-fix | **adopt** — `XDG_RUNTIME_DIR` ที่เป็น symlink ใช้ไม่ได้; `SocketDirectory` symlink → fail-open ไม่ chmod; parent ของ key file ที่เป็น symlink → CE · **reject** การตรวจบรรพบุรุษ (โครง home ของผู้ใช้ที่ถูกต้องอาจเป็น symlink) | L3, L7, L10, S18 |
| 9 | should-fix | **adopt** — fail-open ระบุผลชัด และยอมรับ mixed-session | L9, P1-24, P1-25 |
| 10 | should-fix | **adopt** — การเลือกโฟลเดอร์นับความยาวเป็น byte; boundary 103/104 รวมหลาย byte; fallback ที่ยาวเกินคงพฤติกรรมเดิม | L1, L3(ช), L4, P1-7…9, R10 |
| 11 | should-fix | **adopt บางส่วน** — home/XDG ที่สัมพัทธ์หรือมี `\0` กำหนดแล้ว · **reject** การ validate scope ร่วมกันทุก adapter (ต้องแก้ `Scoped` ที่ macOS ใช้; scope เป็นเครื่องมือ dev) — กำหนดโดเมนที่รองรับไว้แทน | A10, A11, A17, N10 |
| 12 | should-fix | **adopt** — ตรวจ control char ก่อน trim/split; ตัวอย่าง E19–E23 | A2, A5 |
| 13 | should-fix | **adopt** — รับประกันแค่ serialization; E18 (`=` ใน executable) | A4, E18 |
| 14 | should-fix | **adopt** — ค่าดิบตัวสุดท้ายชนะก่อนตีความ, null, header เสีย; D17–D22 | A7, A8 |
| 15 | should-fix | **adopt** — เขียนแบบ temp + rename; symlink/โฟลเดอร์ที่ path กำหนดผลครบ | A12–A14, P2-64…66 |
| 16 | should-fix | **adopt** — โหมด 0600/0700 แน่นอน (ไม่ใช่แค่ขีดบน) | S17, S18, P3-15, P3-17 |
| 17 | should-fix | **adopt** — race ของ Move ใช้กฎ `TryRead`; temp guarantee จำกัดที่การทำงานปกติ; crash ทิ้ง temp ได้ | S19, R11 |
| 18 | should-fix | **adopt** — เลิก cache กุญแจ เก็บแค่ `_lastSource`; ไม่ thread-safe ต่อ instance | S22, S23, P3-24, P3-37, P3-38 |
| 19 | should-fix | **adopt** — canonical base64 (encode กลับต้องเท่าเดิม) พร้อมตัวอย่าง | S24a, P3-30 |
| 20 | should-fix | **adopt** — ลำดับตรวจ argument, ขอบเขต exception, ลำดับความสำคัญ GError/สำเร็จ/timeout | S9a, S15 |
| 21 | should-fix | **adopt** — `Redact` แบบ pure ทั้งใน shim และ protector, inner = null, fixture P3-43 | S8a, S16a, S27 |
| 22 | should-fix | **adopt** — signature ของ attribute, ใช้ `Environment.IsPrivilegedProcess`, collection ปิด parallel | §5, §6 หัว |
| 23 | should-fix | **adopt** — doc comment ย้ายไปเฟส 3; Integrator แก้ conflict แบบ mechanical; การแก้ตารางต้องผ่าน Coding Owner ก่อน | §4 |
| 24 | nit | **adopt** — ใช้ `P3-SMOKE`; เพิ่ม manual M1–M6 | §3.1, §6.3, §6.4 |

## 11b. Inspector A รอบ 2 — คำตัดสิน (Coding Owner, 2026-11-21)

| # | ระดับ | คำตัดสิน | อยู่ที่ |
|---|---|---|---|
| 1 | blocker | **adopt** — lock timeout/error ไม่เรียก inner อีกต่อไป ใช้ probe-only (ต่อได้ → `false`; ต่อไม่ได้ → `true` ไม่มี ownership ไม่ bind ไม่ลบ) จึงเจ้าของไม่เกินหนึ่งเสมอ · F4 แก้เป็น "เจ้าของไม่เกินหนึ่ง แต่รันได้สองตัวใน fail-open" (R12) · เทสต์ผู้ถือ lock ค้างระหว่าง probe กับ bind: P1-22a/b/c (จำลองด้วย lock ที่เทสต์ถือ เพราะ inner เป็น sealed และไม่แก้) | F4, L7–L9, P1-22a–c, R12 |
| 2 | should-fix | **adopt** — ไฟล์ lock เดิมถูก normalize เป็น 0600 หลังได้ lock; chmod ล้ม → ปล่อย lock แล้วโยนต่อ; lock path ที่เป็น symlink ถูกปฏิเสธก่อนเปิด | L5, P1-14a–c |
| 3 | should-fix | **adopt** — key lock: ข้อผิดพลาดของโฟลเดอร์/lock เป็น CE ที่มีข้อความตรงตัว (`E-DIR`, `E-LOCK-ERROR`, `E-LOCK-BUSY`), keyring 0 ครั้ง, ของที่อาจเหลือระบุไว้ · timeout default เพิ่มเป็น 65 s (> 2 × 30 s) และฉีดได้ทาง ctor · สัญญาความพร้อมกันระบุเงื่อนไข · เทสต์ P3-45, P3-46a, P3-52, P3-53 | S20, S24, §6.3 |
| 4 | should-fix | **adopt** — ตรวจ parent symlink เป็นขั้นแรกของ `Protect` ก่อนแตะดิสก์ใด ๆ ทั้งทาง keyring และ fallback; ปลายทางไม่ถูกแตะ · `Unprotect` ทาง KeyFile ใช้ S18 (ปฏิเสธก่อน chmod) · เทสต์ P3-50, P3-51 | S24(0), S25 |
| 5 | blocker | **adopt** — เปลี่ยนจาก blacklist redaction เป็น whitelist (`SafeDetail`): shim ไม่อ่านข้อความ native เลย ส่งได้แค่ค่าคงที่และ `"{domain} {code}"`; protector ใช้ข้อความคงที่ตรงตัว (ตาราง S27) + suffix ที่ผ่าน whitelist; inner = null · fixture adversarial ห้ากรณีพร้อมข้อความที่คาดตรงตัว | S6, S8a, S14–S16a, S27, P3-11/11a, P3-43, P3-44 |
| 6 | should-fix (scope) | **ผู้ใช้ตอบ (ก) (U7, รอบ 4)** — W3f แก้บรรทัด `ConnectionsViewModel.cs:327` ในเฟส 3 + เทสต์ T3e · M4: ข้อความ UI เป็นเกณฑ์ตรวจรับ **บังคับ** | §4 W3f, §5 T3e, §7, N8, M4 |
| 7 | should-fix (scope) | **ผู้ใช้ตอบ (ข) (U8, รอบ 4)** — requirement ใหม่แยก นอก R-024 · M6 คงวิธี profile ใหม่ + ปิดใน KDE; ส่วนสวิตช์ใน Settings นอก scope | N11, M6 |
| 8 | should-fix | **adopt** — M2–M6 ใช้ scope ใหม่ทุกครั้ง, ใส่ key ปลอมผ่าน Connections, แยก store เดิม (M3/M4 ต้องมี header `0101` จาก M2) กับการเขียนครั้งแรก (M5), M-cleanup, และเก็บผล native ของ M4 ด้วย P3-SMOKE-M4 (whitelist detail) | §6.3, §6.4 |
| 9 | nit | **adopt** — ชุดเทสต์ที่ต้องล้มเป็นของ Linux เท่านั้น; Windows/macOS ต้องไม่มีข้อล้มทุกเฟส | §7 |
| U5/U6 | ผู้ใช้ | ข้อความ `PlatformHost.cs:47` แก้ในเฟส 3 (W3e) เป็นการแก้ Presentation เดียวที่อนุญาต · race ของ macOS คง non-goal | หัว, §4, §7, N8, N9 |

## 11c. Inspector A รอบ 3 — คำตัดสิน (Coding Owner, 2026-11-21)

| # | ระดับ | คำตัดสิน | อยู่ที่ |
|---|---|---|---|
| 1 | blocker | **adopt — ซ่อมได้ ไม่ใช่ access failure:** chmod เป็น 0600 **ก่อน** เปิด (เจ้าของ chmod ไฟล์ตัวเองได้แม้โหมด 0000, F16) แล้ว normalize ซ้ำหลังได้ lock (กรณี umask) · chmod ล้ม (ไฟล์ของผู้ใช้อื่น) → ส่งต่อ exception · ค่าที่คาดเหมือนกันทั้ง root/non-root จึง P1-14a เป็น `LinuxOnlyFact` · key lock เฟส 3 ใช้ helper เดียวกัน (P3-54) | L5, F16, P1-14a–d, P3-54 |
| 2 | blocker | **adopt — wrapper กันการแทนที่; exclusivity ไม่มีเงื่อนไข:** ใต้ lock wrapper probe เองก่อนเรียก inner; เรียก inner เฉพาะเมื่อไม่มีไฟล์หรือได้ `ConnectionRefused` (ซึ่งเกิดเฉพาะเมื่อไม่มีใครฟัง, F16); ผลอื่น (timeout, `EAGAIN` ของ queue เต็ม ฯลฯ) = inconclusive → `true` ไม่มี ownership ไม่ลบ ไม่ bind · ไม่แก้ไฟล์ macOS, ไม่เปิด U6 · fixture เจ้าของมีชีวิตแต่ queue เต็ม P1-22d + stale P1-22e | L7(5), L8, L9, N12, R13, P1-22d/e |
| 3 | should-fix | **adopt** — สัญญาความพร้อมกันแยกกรณี: ≤ 1 Store สำเร็จเมื่อ Store ได้ `Done` และ Lookup ต่อมาได้ `Found`; Store=`Unavailable` → Store หนึ่งครั้งต่อตัวที่เห็น `NotFound` แต่ `secrets.key` ถูกสร้างครั้งเดียวและใช้ร่วมกัน; Store=`Failed` → Store หนึ่งครั้งต่อตัว ทุกตัว CE ไม่มีไฟล์ · P3-46b/c | S24, P3-46b, P3-46c |
| 4 | should-fix | **adopt** — FakeKeyring มี `LookupEntered` + `LookupGate`; B เริ่มหลังสัญญาณเท่านั้น; ทุก wait ≤ 10 s | FakeKeyring, P3-46a |
| 5 | nit | **adopt** — `\A…\z` + `CultureInvariant` ไม่มี `IgnoreCase`/`Multiline`; fixture ขึ้นบรรทัดใหม่ท้าย/หน้า, CRLF, ตัวเลขที่ไม่ใช่ ASCII | S8a, P3-11a |
| 6 | should-fix | **adopt** — `r024_new` บันทึก path ที่ resolve แล้ว (XDG override, runtime/fallback) ลง `~/r024-manual-scopes.tsv`; M-cleanup เป็นคำสั่งที่รันได้จริงหลังยืนยันว่าไม่มี process เหลือ; ลบ keyring item ด้วย P3-SMOKE-CLEAR (ปฏิเสธ scope ที่ไม่ใช่ `r024m[2-6]-…`) · กลไก skip ของ smoke ระบุชัด (`RequiredEnvironment`) | §6.4, P3-SMOKE-M4, P3-SMOKE-CLEAR |
| E14 | — | Inspector ยืนยันว่าเป็น Markdown ถูกต้อง — คงไว้ | §2 |
| ลำดับเทสต์ | coordinator | **re-split:** ไฟล์สนับสนุนสามไฟล์ + บรรทัด `[Collection]` ของ `PlatformAdaptersTests` ย้ายจากเฟส 1 ไปขั้น **T0** ที่ไม่ขึ้นกับ production · `using …Linux;` ใส่โดย T1/T2 ตัวที่ลงก่อน · Test Author ทำ T0 → 2 ได้ทันที · เพิ่มแถว gate "T0 อย่างเดียว" และ "2 ก่อน 1" · **§2 และ W2 ไม่เปลี่ยน** | §5, §6 หัว, §7 |
| U7/U8 | ผู้ใช้ | Q-6 = (ก), Q-7 = (ข) | หัว, W3f, T3e, N8, N11, M4, M6 |

## 11d. Inspector A รอบ 4 — คำตัดสิน (Coding Owner, 2026-11-21)

| # | ระดับ | คำตัดสิน | อยู่ที่ |
|---|---|---|---|
| 1 | blocker | **adopt — dispose ของเจ้าของทำทั้งหมดใต้ lock เดียวกับ acquire:** wrapper ไม่ส่ง `DisposeAsync` ตรงอีก; เจ้าของต้องได้ lock (`DefaultDisposeLockTimeout` 10 s, ฉีดได้) ก่อนเรียก `inner.DisposeAsync` ซึ่งปิด listener และลบไฟล์ใต้ lock · ไม่ได้ lock → ไม่เรียก inner เลย (listener เปิดจน process จบ; ไฟล์ค้างถูกแทนที่ใต้ lock ครั้งหน้า) และ dispose ซ้ำลองใหม่ได้ · ไม่ใช่เจ้าของ → dispose inner โดยไม่ต้อง lock · ไม่แก้ไฟล์ macOS (U6) · L8 ตรวจใหม่: ทุก bind และทุก unlink เกิดใต้ lock จึงเจ้าของไม่เกินหนึ่ง เหลือกรณี chmod socket ของตัวเองล้มที่ระบุเป็น R15 · fixture P1-26a/b/c | L6, L7a, L8, R15, P1-26a–c |
| 2 | should-fix | **adopt** — P1-22d ไม่สมมติจำนวน client เก่า: client ใหม่ส่ง `0xFF`; เทสต์ accept และอ่าน 1 byte (500 ms) ทิ้งตัวที่ไม่ใช่ `0xFF` จนพบ; ขอบเขต `old.Count + 8` ครั้ง / 15 s | P1-22d |
| 3 | should-fix | **adopt** — เลิกใช้ TSV: ต่อ scope มีโฟลเดอร์บันทึก ไฟล์ละหนึ่ง path · ปฏิเสธ path ฐานที่ไม่ absolute หรือมี tab/newline · ทุกเป้าหมายต้องไม่มีอยู่ตอน setup · cleanup ลบเฉพาะเป้าหมายที่ basename ตรงกับชื่อที่ได้จาก scope, ไม่ใช่ symlink, ชนิดถูก · runtime ที่บันทึกคือที่แอปเลือกจริง (`r024_after` ดู `si-*.lock`) · scratch ใช้ `$HOME/.cache/aimonitor-r024/tmp` ไม่ใช้ `/tmp` | §6.4, M2, M5, M6, M-cleanup |

## 11e. คำถามจากการรวมเฟส 2 (Coding Owner, 2026-11-21)

| # | คำถาม | คำตัดสิน | ฝั่งที่ต้องแก้ |
|---|---|---|---|
| 1 | P2-53: `ResolveAutostartDirectory(null, …)` | `ArgumentNullException` (กฎ G-ARG; ตรงกับ P2-53 เดิม, L2/P1-10, A2/E17, S9a) · A11 เขียนใหม่ให้ชัด | **โค้ด (WO2)** — เช่น `ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory)` |
| 2 | ctor `XdgAutostartStartupRegistrar` null | `ArgumentNullException` (G-ARG) · A10 เขียนใหม่ · เพิ่ม P2-54a · `Constructor_RefusesNullArguments` ของ Test Author ถูกต้อง | **โค้ด (WO2)**; เทสต์คงไว้ (เพิ่ม ParamName ได้) · `Register(null)` ก็เป็น `ArgumentNullException` ตาม A13(1) |
| a | `=` ก่อนหัว group แรกถูกข้าม | เห็นด้วย (D13) · เพิ่มใน A7 | — |
| b | T2a ย้าย factory เข้าแต่ละสาขา | เห็นด้วย ถ้า assertion Windows/macOS ไม่เปลี่ยน · เพิ่มใน §5 | — |
| c | ชื่อ temp ของ A13 | ไม่ต้อง assert ชื่อ; assert แค่ไม่เหลือ `*.tmp` / ไฟล์เดียว · ชื่อเป็นเรื่องของ Reviewer | — (ถ้าเทสต์ assert ชื่ออยู่ ไม่ผิด แต่ไม่บังคับ) |

## 11f. Inspector A รอบ 5 — คำตัดสิน (Coding Owner, 2026-11-21)

| # | ระดับ | คำตัดสิน | อยู่ที่ |
|---|---|---|---|
| 1 | should-fix | **adopt — แยกขอบเขต:** "เจ้าของไม่เกินหนึ่ง" พิสูจน์ได้เมื่อไม่มีเส้นทาง E1; E1 (chmod socket ของตัวเองล้มหลัง bind ใน inner) เป็นข้อยกเว้นที่ยอมรับและไม่ได้พิสูจน์ (R15) · ไม่แก้ไฟล์ macOS | L8(ง), R15 |
| 2 | should-fix | **adopt — serialize:** `_gate` (`SemaphoreSlim(1,1)`) ครอบ `TryAcquireAsync` และ `DisposeAsync` ของ instance เดียวกัน · dispose ตั้ง `_closed` ก่อนแล้วรอ acquire ที่กำลังทำจนจบ (acquire นั้นคืนผลปกติ) แล้วปล่อยความเป็นเจ้าของใต้ lock · fixture P1-26d | L6, L7(0), L7a(1b), P1-26d |
| 3 | should-fix | **adopt** — P1-26a: เรียกผ่าน `Task.Run`, สัญญาณเข้า = `HasOwnership` เป็น false, สัญญาณปล่อย = เทสต์ปล่อย lock · P1-26c: จับเวลารวมช่วง synchronous · P1-26b: **จัดเป็น stress coverage** (ไม่มี seam หยุดกลาง inner dispose โดยไม่แก้ไฟล์ macOS) · หลักฐานเชิงกำหนด = P1-26a + Reviewer ตรวจว่า inner dispose ทั้งหมดอยู่ใต้ lock | P1-26a–c |
| 4 | should-fix | **adopt** — client เก่าส่ง `0x01`, probe ของ B ที่ปิดแล้วให้ EOF, N ส่ง `0xFF` และถูกเก็บไว้ไม่ปิด · ไม่มี timeout ต่อ connection; deadline รวม 15 s | P1-22d |
| 5 | blocker | **adopt คำแนะนำของ coordinator — print-only:** cleanup ไม่ลบอะไรเอง พิมพ์ `rm -rf -- '<path>'` ให้คนตรวจและรัน · keyring ล้างด้วย P3-SMOKE-CLEAR เท่านั้น · ปัญหาขอบเขตการลบ (บันทึกถูกแก้, symlink ของบรรพบุรุษ, โฟลเดอร์บันทึกที่ถูกวาง) จึงไม่มีอีก — บันทึกแปลก ๆ ถูกรายงาน `# REFUSED` | §6.4, M-cleanup |
| 6 | should-fix | **adopt** — setup ปฏิเสธ glob/quote/backslash; ตรวจ lock ด้วย `find -maxdepth 1 -name` (literal) · M5 ใช้ `$TMPDIR/r024-absent-runtime` และตรวจว่าไม่มีอยู่ก่อนรัน | §6.4, M5 |
| 7 | should-fix | **หมดไป** — cleanup ไม่ลบบันทึกเอง; คำสั่งลบบันทึกเป็นบรรทัดสุดท้ายของแต่ละ scope ในไฟล์ที่คนตรวจก่อนรัน · รันด้วย `bash -e` จึงคำสั่งใดล้ม → หยุดทันที บันทึกยังอยู่ | M-cleanup |

## 11g. Inspector A รอบ 6 และ Reviewer1 เฟส 2 — คำตัดสิน (Coding Owner, 2026-11-21)

| # | ระดับ | คำตัดสิน | อยู่ที่ |
|---|---|---|---|
| 1 | should-fix | **adopt** — `internal bool IsClosing` (ตั้งเป็นคำสั่งแรกของ dispose) เป็นสัญญาณเข้าของ dispose; poll ≤ 5 s ทั้งสองสัญญาณ; ปล่อย = เทสต์ dispose lock; ลำดับ a แล้ว d | L6, P1-26d |
| 2 | should-fix | **adopt** — E1 = ทุก exception ใน `TryListen` หลัง `Bind` และก่อน `_listener = listener` (`Listen` หรือ chmod); ค่า false ที่พูดถึงคือของ `TryListen`; `inner.TryAcquireAsync` ต่อจากนั้นคืน false หรือ fail-open true โดยไม่มี ownership | L8(ง), R15 |
| 3 | should-fix | **adopt** — `r024_init` ตรวจ `HOME` ก่อน `mkdir`; `r024_new` ตรวจทุก path ก่อนเขียน; สคริปต์ cleanup เขียนลงไฟล์ใหม่จาก `mktemp`; ระบุสิ่งที่ฟังก์ชันเขียนลงดิสก์ทั้งหมด | §6.4, M-cleanup |
| 4 | should-fix | **adopt** — อ่านบันทึกแบบ raw (`IFS= read -r -d ''`) และปฏิเสธไฟล์บันทึกที่ไม่ใช่ไฟล์ธรรมดา; เพิ่มการตรวจ basename; ระบุชัดว่า `# REFUSED` เกิดเมื่อใด และ **ยอมรับว่า** ขั้นตอนไม่พิสูจน์ว่าใครสร้าง path — คนต้องอ่านก่อนรัน | §6.4 |
| 5 | should-fix | **adopt** — `r024_m5_new` คืนไม่สำเร็จและไม่สร้าง scope ถ้า runtime ที่ต้องไม่มีมีอยู่; เปิดแอปด้วย `&&` ในคำสั่งเดียวกัน | §6.4, M5 |
| R1-A | Reviewer1 | **เปลี่ยน spec (i) แบบแคบ:** A16 ใช้ `SpecialFolderOption.DoNotVerify` เหมือน W1a · A11 และ P2-50…54 ไม่เปลี่ยน · **ฝั่งโค้ด (WO2):** หนึ่ง argument ใน `PlatformAdapters.cs` สาขา Linux ของ `CreateStartupRegistrar` · ฝั่งเทสต์: ไม่บังคับ (T2a ให้ค่าเท่ากันเมื่อ `$HOME` มีอยู่; Test Author จะเปลี่ยน T2a ให้ใช้ `DoNotVerify` ด้วยก็ได้) · กรณี `HOME` ว่าง/สัมพัทธ์จริง ๆ ยังทำให้ `StartAsync` ล้ม — ยอมรับ (นอก scope; Presentation ไม่แก้) | A16 |
| R1-B | Reviewer1 | **เปลี่ยน spec:** A12 แปลง `FileNotFoundException`/`DirectoryNotFoundException` เป็น `false` (symlink ที่ปลายทางไม่มี = ไม่มี entry → reconcile ของ profile ใหม่ลงทะเบียนได้ และ `Register` แทน link ด้วยไฟล์ตาม A13) · **ฝั่งโค้ด (WO2)** + **ฝั่งเทสต์ (Test Author):** P2-69 ใหม่ | A12, P2-69 |
| R1-nit | Reviewer1 | **adopt** — กติกา comment: อังกฤษ, อ้างได้แค่ requirement id, ห้ามอ้าง spec/Inspector id และเลขบรรทัดข้ามไฟล์ · WO2 แก้ในรอบแก้เดียวกัน | §7 |
| แยกเฟส 3 | coordinator | **3a** (4 ไฟล์ที่ไม่ใช้ `LinuxFileLock` + W3e, W3f และเทสต์ของมัน) เริ่มและ compile ได้ทันที · **3b** (`LinuxSecretProtector`, W3a–W3d และเทสต์) หลังเฟส 1 | §7 |

## 11h. Inspector ขั้น B เฟส 2 — คำตัดสิน (Coding Owner, 2026-11-21)

| mutant | คำตัดสิน | อยู่ที่ | ฝั่งที่ต้องแก้ |
|---|---|---|---|
| M02, M15 | ชื่อ key เป็น ordinal case-sensitive (ตาม Desktop Entry spec) · เพิ่ม D23–D25 | A7, D23–D25 | **เทสต์เท่านั้น** — โค้ด WO2 ใช้ `switch` บนสตริงตรงตัวอยู่แล้ว (`XdgDesktopEntry.cs`) |
| M30 | อ่านแบบไม่ strict: byte เสีย → U+FFFD แล้วแยกตามปกติ (ไม่ใช่ `null`); `Register` เขียนทับเป็นเนื้อหามาตรฐาน · เพิ่ม P2-70 | A12, P2-70 | **เทสต์เท่านั้น** — โค้ดใช้ `File.ReadAllText(path)` ค่าเริ่มต้นอยู่แล้ว |

## 12. บันทึกการเปลี่ยน spec

| วันที่ | การเปลี่ยน |
|---|---|
| 2026-11-21 | ร่างแรก |
| 2026-11-21 | รอบ 2: คำตัดสินผู้ใช้ U1–U4 และคำตัดสินของ Inspector A รอบ 1 ทั้ง 24 ข้อ (§11) |
| 2026-11-21 | รอบ 3: Inspector A รอบ 2 (§11b), U5/U6 ของผู้ใช้; Q-6/Q-7 รอผู้ใช้ |
| 2026-11-21 | รอบ 4: Inspector A รอบ 3 (§11c), U7/U8 ของผู้ใช้, ขั้น T0 ของเทสต์ · §2/W2 ไม่เปลี่ยน |
| 2026-11-21 | รอบ 5: Inspector A รอบ 4 (§11d) — L6/L7a/L8 (dispose ใต้ lock), P1-22d, P1-26a–c, §6.4 · §2, §3, §4, §5 ไม่เปลี่ยน |
| 2026-11-21 | รอบ 6: กฎ G-ARG (null → `ArgumentNullException`), A7/A10/A11/A13, P2-53/54/54a, L6, S21, §5 รูป T2a, §11e |
| 2026-11-21 | รอบ 7: Inspector A รอบ 5 (§11f) — L6/L7/L7a (`_gate`), L8 ขอบเขต E1, R15, P1-22d, P1-26a–d, §6.4 แบบ print-only · §2, §3, §4, §5 ไม่เปลี่ยน |
| 2026-11-21 | รอบ 8: Inspector A รอบ 6 (§11g) — L6 `IsClosing`, L8/R15 ขอบเขต E1, P1-26d, §6.4 · Reviewer1: A12 + P2-69, A16 `DoNotVerify`, กติกา comment · แยกเฟส 3a/3b |
| 2026-11-21 | รอบ 9: Inspector ขั้น B เฟส 2 (§11h) — A7 key case-sensitive + D23–D25; A12 ถอด UTF-8 แบบไม่ strict + P2-70 |
