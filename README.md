# SETT Memory Cleaner ⚡

Portable RAM optimizer. Uses **native Windows APIs** to safely clear memory. Dark UI, single executable, no installation required.

> ⚠️ **Requirement:** Administrator privileges required.

---

## ✨ Features

- **Auto-Optimization:** Time-based or usage threshold.
- **Global Hotkey:** `CTRL + SHIFT + M` for quick cleanup.
- **Compact Mode:** Minimalist UI for background monitoring.
- **Run on Startup:** System-level configuration.
- **Process Exclusion:** List to ignore specific processes.
- **System Tray:** Monitor from the tray.
- **Bilingual:** EN + PT-BR.

---

## 🧬 How It Works

Uses documented **Windows API** calls. No tricks.

| Area | Description |
| :--- | :--- |
| Combined Page List | Merged page blocks |
| Modified File Cache | Disk cache flush |
| Modified Page List | Unsaved pages |
| Registry Cache | Registry hives |
| Standby List | Closed app cache |
| System File Cache | System file cache |
| Working Set | Process RAM |

---

## 🔎 Verification

Test via **Resource Monitor** (`resmon.exe`):
1. Open `resmon.exe` → **Memory** tab.
2. Check the **Standby** bar.
3. In SETT, click **Optimize**.
4. Watch **Standby** drop, **Free** rise.

---

## ⚠️ Common Issues

**Antivirus flagged as malware?**
Common false positive. The app accesses low-level APIs and creates scheduled tasks. Code is 100% open source and safe. Submit for analysis: [Microsoft WDSI](https://www.microsoft.com/en-us/wdsi/filesubmission).

---

## 🔒 Security

- **Open Source:** GPL-3.0.
- **No Dependencies:** Portable.
- **Transparency:** Official calls only.

---

## 📄 License

GPL-3.0. See [LICENSE](/LICENSE).
