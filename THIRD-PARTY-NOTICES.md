# Third-party notices

AI Usage Monitor is distributed under the **GNU General Public License v3.0**
(see [LICENSE](LICENSE)). This file lists the third-party components that are
redistributed inside the packaged application, and the terms each arrives under.

Versions are the ones actually pinned in `requirements.txt` and present in the
build; the shipped libraries were read from `dist/AIUsageMonitor/_internal/`.

---

## Qt for Python — PySide6 and shiboken6

| | |
|---|---|
| Version | 6.11.2 |
| Licence | LGPL-3.0-only **OR** GPL-2.0-only **OR** GPL-3.0 (also available commercially) |
| Taken here under | **GPL-3.0** |
| Source | <https://download.qt.io/official_releases/QtForPython/> · <https://code.qt.io/cgit/pyside/pyside-setup.git/> |
| Upstream Qt source | <https://download.qt.io/official_releases/qt/> |

Qt for Python is offered under a choice of licences. Because this application
is itself GPL-3.0, Qt is taken under its **GPL-3.0** option, and the whole
distributed work is consistently GPL-3.0. No separate LGPL relinking provision
is relied upon.

The following Qt libraries are redistributed in the packaged application:

```
Qt6Core.dll   Qt6Gui.dll     Qt6Network.dll  Qt6OpenGL.dll   Qt6Pdf.dll
Qt6Qml.dll    Qt6QmlMeta.dll Qt6QmlModels.dll               Qt6QmlWorkerScript.dll
Qt6Quick.dll  Qt6Svg.dll     Qt6VirtualKeyboard.dll          Qt6Widgets.dll
opengl32sw.dll               pyside6.abi3.dll
```

Several of these are pulled in as dependencies of the modules the app imports
rather than used directly; `AIUsageMonitor.spec` excludes the Qt modules the
app has no use for.

---

## PyInstaller

| | |
|---|---|
| Version | 6.22.2 |
| Licence | GPL-2.0-or-later **with a special exception** |
| Source | <https://github.com/pyinstaller/pyinstaller> |

PyInstaller is a build tool, but its **bootloader is compiled into the shipped
executable**, so it is redistributed and belongs in this list. PyInstaller's
licence carries an explicit exception permitting the bootloader to be combined
with programs under any licence, so it imposes no additional condition on this
application.

---

## rsa

| | |
|---|---|
| Version | 4.9.1 |
| Licence | Apache-2.0 |
| Copyright | Copyright 2011 Sybren A. Stüvel \<sybren@stuvel.eu\> |
| Source | <https://github.com/sybrenstuvel/python-rsa> |

Used only by the Gemini provider, to sign a service-account JWT. Imported
lazily, so it is loaded only when a service account is configured.

The Apache License 2.0 is available at
<https://www.apache.org/licenses/LICENSE-2.0>. No modifications were made.

---

## pyasn1

| | |
|---|---|
| Version | 0.6.4 |
| Licence | BSD-2-Clause |
| Copyright | Copyright (c) 2005-2020, Ilya Etingof \<etingof@gmail.com\>. All rights reserved. |
| Source | <https://github.com/pyasn1/pyasn1> |

A dependency of `rsa`. Redistribution in binary form must reproduce the above
copyright notice, this list of conditions and the disclaimer, which this file
does.

---

## Microsoft Visual C++ runtime

| | |
|---|---|
| Files | `MSVCP140.dll`, `MSVCP140_1.dll`, `MSVCP140_2.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` |
| Terms | Microsoft Visual C++ Redistributable terms |

Pulled in by the Qt binaries. Redistributed under Microsoft's redistributable
terms for the Visual C++ runtime, not under the GPL.

---

## Python

The application bundles a CPython runtime, distributed under the
**Python Software Foundation License 2.0** —
<https://docs.python.org/3/license.html>.

---

## Not redistributed

The app reads credentials that other tools wrote, and calls the providers' own
HTTP endpoints. It bundles no part of Claude Code, the Codex CLI, the Gemini
CLI or gcloud, and copies none of their code. It never writes to their files.

## Obtaining the source

The Corresponding Source for this application, including these build scripts
and the exact `requirements.txt` pinning every version above, is the repository
this file came from. Sources for the components listed here are linked in each
section.
