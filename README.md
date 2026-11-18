<div align="center">

![Logo](https://raw.githubusercontent.com/txrbo-raccoon/Virtualized/refs/heads/master/Repository/logo.png)

**A clean desktop interface for creating and running virtual machines on top of QEMU.**

![.NET](https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white)
![Blazor](https://img.shields.io/badge/Blazor-Photino-512BD4?logo=blazor&logoColor=white)
![QEMU](https://img.shields.io/badge/QEMU-8.0%2B-FF6600?logo=qemu&logoColor=white)
![Platform](https://img.shields.io/badge/Windows-10%2B%20x64-0078D4?logo=windows&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green)

[Features](#-features) · [Requirements](#-requirements) · [Installation](#-installation) · [Getting started](#-getting-started) · [Building](#-building-from-source) · [Architecture](#-architecture-overview)

</div>

---

Virtualized does not bundle an emulator: it drives the [QEMU](https://www.qemu.org/) you already have installed, and gives you a clean interface to define VMs, boot them, interact with their screen and take snapshots.

It is built with .NET 11 and Blazor, hosted in a native window through [Photino](https://www.tryphotino.io/), so there is **no browser and no Electron runtime** involved.

<!-- Add a screenshot here:
<p align="center"><img src="docs/screenshot.png" alt="Virtualized" width="800"></p>
-->

## ✨ Features

### 🧰 Management

- **VM management.** Create, edit, start, pause, resume, reset, shut down (ACPI) and force stop VMs. Every VM lives in its own folder with a `vm.json` definition, its disks and its runtime files.
- **x86_64 and aarch64 guests.** Machine types `q35` and `virt`, with sensible defaults for CPU cores, memory and disk size.
- **Guest presets** for Linux, Windows 10, Windows 11 and other systems. Presets choose firmware, devices and drivers that work out of the box (virtio for Linux, SATA and e1000e for Windows, which has no virtio drivers in its installer).
- **Automatic acceleration.** Picks the best usable accelerator on the host: KVM, HVF, WHPX, with TCG (software emulation) as the fallback.

### 🔐 Firmware and security

- **UEFI and BIOS firmware.** UEFI firmware is discovered from QEMU's own firmware descriptor files, the same metadata libvirt uses. Secure Boot is offered when the host can provide it.
- **TPM 2.0** through [swtpm](https://github.com/stefanberger/swtpm). The TPM state is stored in the VM folder, so it survives restarts.
- **Windows 11 compatibility mode.** When the host cannot provide a TPM or Secure Boot, an answer file is presented to Windows Setup on a virtual USB drive so the installer skips its hardware checks. The rest of the installation stays interactive.

### 🎮 Everyday use

- **Built-in console.** The guest display is served over VNC and rendered with [noVNC](https://github.com/novnc/noVNC) inside the app. Includes Ctrl+Alt+Del and other key combinations, and a fullscreen mode.
- **Removable media at runtime.** Insert or eject an ISO in the virtual CD drive while the VM is running.
- **Snapshots.** Internal qcow2 snapshots of the disks and UEFI variables. Running VMs get a snapshot that includes memory where the accelerator allows it, and a disk-only snapshot otherwise. Stopped VMs get disk-only snapshots.
- **Networking.** User-mode (NAT) networking, with optional TCP and UDP port forwards. Forwards listen on the host's loopback interface only, so services inside a VM are never exposed to your network.
- **Crash visibility.** A `pvpanic` device pauses the VM when the guest reports a crash, so the crash screen stays visible instead of the VM silently powering off.

## 📋 Requirements

| Requirement | Notes |
| --- | --- |
| **QEMU 8.0 or later** | Needs `qemu-system-x86_64` and/or `qemu-system-aarch64`, plus `qemu-img` to create disks. |
| **Windows 10 or later, x64** | Platform of the provided installer. |
| **WebView2 runtime** | Used by Photino to render the interface. Included with current Windows versions. |
| Hypervisor support *(optional)* | Enable *Windows Hypervisor Platform* on Windows for WHPX. Without it, VMs run with TCG, which is much slower. |
| swtpm *(optional)* | Only needed for TPM 2.0. QEMU has no TPM backend on Windows, so TPM is available on Linux and macOS hosts only. |

## 📦 Installation

1. Download the Windows installer, `virtualized_setup_w64_1-0.exe`, and run it.
2. Install QEMU. On Windows, the installers from [qemu.weilnetz.de](https://qemu.weilnetz.de/w64/) install to `C:\Program Files\qemu`, which Virtualized checks automatically.

<details>
<summary><b>🔎 Verify the download (SHA-256)</b></summary>

<br>

A SHA-256 checksum is published next to the installer so you can verify it:

```powershell
(Get-FileHash .\virtualized_setup_w64_1-0.exe -Algorithm SHA256).Hash
```

Compare the result with the contents of `virtualized_setup_w64_1-0.exe.sha256`.

</details>

## 🚀 Getting started

1. Open **Settings**. Virtualized looks for QEMU on your `PATH` and in the usual install locations. If yours is elsewhere, set the QEMU folder and choose *Save and detect*.
2. Check what the detection found: QEMU version, available architectures, accelerators in use, UEFI firmware, Secure Boot and TPM support.
3. Create a VM, pick a guest OS preset, attach an installation ISO and start it.
4. Use the console to install the system. Take a snapshot whenever you want a restore point.

## 🗂️ Where data is stored

| What | Location |
| --- | --- |
| Settings | `%AppData%\Virtualized\settings.json` (Windows)<br>`~/.config/Virtualized` (Linux)<br>`~/Library/Application Support/Virtualized` (macOS) |
| Virtual machines | `vms\` inside that folder, one subfolder per VM |

> [!WARNING]
> Deleting a VM from the app removes its folder, **disks included**.

## 🔨 Building from source

You need the **.NET 11 SDK**. The repository is a single solution:

```text
Virtualized.sln
Application/
  Virtualized/                The desktop app (Blazor + Photino)
  Installer/                  Inno Setup script and build script
Libraries/
  Virtualized.Assets/         .vpak archive format: packing, AES-256-GCM encryption, reading
  Virtualized.Assets.Packer/  Command line tool that packs a folder into a .vpak
Tests/
  Virtualized.Tests/          xUnit test project
```

Build and run:

```bash
dotnet build Virtualized.sln
dotnet run --project Application/Virtualized
```

Run the tests:

```bash
dotnet test Tests/Virtualized.Tests
```

> [!NOTE]
> Some tests exercise a real QEMU installation, so they need QEMU available on the machine.

<details>
<summary><b>⚙️ How the build works</b></summary>

<br>

- `wwwroot` (CSS, JS and the bundled noVNC) is packed at build time into an encrypted `.vpak` archive and embedded in the executable. A fresh AES-256 key is generated on first build and compiled into the app, and `ArchiveFileProvider` serves the files to the web view from that archive.
- Release publishes are obfuscated with [Obfuscar](https://github.com/obfuscar/obfuscar), restored through `dotnet-tools.json`.

</details>

<details>
<summary><b>📀 Building the installer</b></summary>

<br>

Install [Inno Setup](https://jrsoftware.org/isinfo.php) 6 or 7, then run:

```powershell
powershell -ExecutionPolicy Bypass -File Application\Installer\build-installer.ps1
```

The script publishes a self-contained single-file `win-x64` build, compiles `setup.iss` with Inno Setup and writes the installer plus its `.sha256` file to `Application\Installer\Output`. Use `-SkipPublish` to reuse an existing publish folder, or `-Iscc <path>` to point at a specific `ISCC.exe`.

</details>

## 🏗️ Architecture overview

| Area | Responsibility |
| --- | --- |
| `Qemu/` | Finding and inspecting QEMU, accelerator probing, firmware catalog, swtpm, QMP client, `qemu-img` wrapper and the command line builder. The builder is a pure function from a VM definition to arguments, which is what makes it easy to unit test. |
| `Vms/` | VM definitions, validation, persistence, lifecycle management, snapshots and the Windows answer file. |
| `Pages/`, `Shared/` | Blazor UI built with Cerberus.UI, including the noVNC console component. |
| `Settings/` | Persisted app settings. |

At runtime each VM runs as its own `qemu-system-*` process, with its display exposed on a loopback VNC websocket and its control channel on a loopback QMP port. Virtualized can reattach to VMs that are still running after the app restarts.

## 🙏 Third-party components

- [QEMU](https://www.qemu.org/) and [swtpm](https://github.com/stefanberger/swtpm), used as external programs
- [Photino](https://www.tryphotino.io/) and Photino.Blazor for the native window
- [noVNC](https://github.com/novnc/noVNC) (MPL 2.0) and pako for the in-app console
- [Obfuscar](https://github.com/obfuscar/obfuscar) for release obfuscation

## 📄 License

Released under the [MIT License](LICENCE.txt). Copyright © 2026 txrbo-raccoon.
