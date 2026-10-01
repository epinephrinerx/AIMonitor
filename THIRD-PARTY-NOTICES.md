# Third-Party Notices — AI Usage Monitor 2.0

AI Usage Monitor 2.0 is built on top of the following open-source components.
Their licences are reproduced below as required by each licence's terms.

---

## .NET Runtime and Libraries

**© Microsoft Corporation**
<https://dotnet.microsoft.com/>

The application is built with .NET 10.0, Windows Presentation Foundation (WPF)
and Windows Forms (WinForms), published as a self-contained executable.

The .NET runtime, WPF, and WinForms are licenced under the
**MIT Licence** (SPDX: MIT).

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## xUnit.net (test-only dependency — not shipped in release binary)

**© .NET Foundation and Contributors**
<https://xunit.net/>

Used exclusively in the automated test suite (`AIMonitor.*.Tests` projects).
`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, and
`coverlet.collector` are not included in the published application binary.

Licensed under the **Apache License 2.0** (SPDX: Apache-2.0).
<https://www.apache.org/licenses/LICENSE-2.0>

---

## GDI+ / Windows Graphics Device Interface

The tray icon is rendered using GDI+ drawing routines available through
`System.Drawing` (part of the .NET runtime). GDI+ is a Windows operating
system component licensed by Microsoft under the terms that govern the
Windows operating system.

---

*This file was last reviewed: 2026-10-01.*
*If you discover an omission or inaccuracy, please open an issue on the project repository.*
