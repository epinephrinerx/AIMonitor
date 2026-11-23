# AIMonitor 2.0.5

- The program is now called **AIMonitor** everywhere you see its name: window title bars, the tray tooltip and balloon, About, the usage log, Task Manager and file Properties, the Start menu and desktop shortcuts, Apps & features and the installer. Upgrading removes the old "AI Usage Monitor" shortcuts. The file names (`AIUsageMonitor.exe`, the Setup file), the install folder, the settings folder and the start-with-Windows entry are unchanged, so settings, stored keys and start-up carry over.
- New **ESP32 desk display** option in Settings. When it is switched on, the app sends the same quota readings that the tray shows, over USB serial, to an ESP32 board running the AIMonitor display firmware (ESP32-2432S028, 2.8" touch screen). The readings are sent on every refresh and repeated every 30 seconds.
- Pick the board's COM port, or let the app find it. Find automatically asks each port for the display and uses the one that answers.
- Only service names, window names, percentages, severity and reset times are sent. Accounts, keys and tokens are never sent.
- An unplugged board or a port in use by another program does not stop or slow the app. The app tries the port again every 15 seconds. The port is opened with DTR and RTS off, so the board does not reset each time the app connects.
- The option is off by default. While it is off, the app opens no serial port.
- Adds the `System.IO.Ports` package (Microsoft, MIT), listed in THIRD-PARTY-NOTICES.

Windows x64 downloads: use **AIUsageMonitor Setup 2.0.5.exe** to install, or **AIUsageMonitor 2.0.5.exe** for the self-contained portable app. Settings carry over from 2.0.4. Keep the previous installer for rollback.

Validation: build with 0 warnings/errors, all 3,258 synthetic .NET tests (including the renamed strings) and format verification pass, and the NuGet vulnerability audit reports no vulnerable packages. The serial link was tried against a real board with a test harness outside the app. The owner accepted R-022 on a real board. The rename (R-024) is not yet accepted by the owner. Both changes were written without the independent review roles used for earlier releases. This build replaces the first 2.0.5 build published earlier the same day. Real-desktop DPI/tray and clean-machine upgrade smoke matrices were not repeated. Executables are unsigned.
