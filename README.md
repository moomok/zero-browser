# Zero Browser

> Cross-platform Zero Browser desktop dengan dukungan banyak profil dan fingerprint berbeda-beda per profil.

Setiap profil = identitas browser terpisah dengan **fingerprint sendiri** (Canvas, WebGL, Audio, fonts, timezone, locale, hardware, dll), **storage terisolasi**, dan **proxy berbeda**. Satu PC bisa terlihat sebagai puluhan/ratusan device berbeda dari sisi server.

Inspirasi: Multilogin / GoLogin / AdsPower / Dolphin Anty / Kameleo — versi open-source pribadi.

> ⚠️ **Disclaimer**: tool ini ditujukan untuk privasi, QA testing, manajemen multi-akun yang sah, dan riset keamanan. **JANGAN** dipakai untuk fraud, ad-fraud, fake account scam, atau aktivitas yang melanggar hukum / Terms of Service platform. Tanggung jawab penggunaan ada di tangan pengguna.

---

## Status

**MVP (v0.1)** — fungsional inti sudah jalan:

- ✅ FingerprintGenerator deterministik (seed → fingerprint konsisten antar sesi)
- ✅ FingerprintInjector lengkap (Navigator, Screen, Canvas, WebGL, Audio, Intl/timezone, Geolocation, MediaDevices, WebRTC, Permissions, chrome.*) dengan hardening anti-detect (toString native, prototype patching, no global leaks)
- ✅ PuppeteerBrowserLauncher cross-platform (PuppeteerSharp + CDP)
- ✅ Per-profile user-data-dir, proxy server + auth, timezone emulation
- ✅ Storage SQLite + AES-256-GCM + Argon2id
- ✅ Avalonia UI 11 (cross-platform native, Fluent Design)
- ✅ 23 unit tests (semua green)

**Belum ada (roadmap):**

- [ ] Profile editor UI lengkap (saat ini auto-generate via "New profile")
- [ ] Proxy manager UI
- [ ] Cookie importer (JSON / Netscape / EditThisCookie)
- [ ] Bulk create profil dengan template
- [ ] Master password lock di app start
- [ ] JA3/TLS fingerprint diversification (butuh patched Chromium / mitm-impersonate)
- [ ] Automation runner (Playwright/Puppeteer script per profil)
- [ ] Code signing + auto-update

---

## Tech Stack

| Komponen | Pilihan | Alasan |
|---|---|---|
| UI | [Avalonia UI 12](https://avaloniaui.net/) | Cross-platform native (Win/Mac/Linux), XAML-based, dipakai JetBrains Rider |
| Browser engine | [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp) + Chromium-for-Testing | Spawn Chromium per-profile, kontrol via CDP. Cross-platform "for free". |
| Storage | SQLite (`Microsoft.Data.Sqlite`) + Dapper | File DB ringan, embed-friendly |
| Crypto | AES-256-GCM (.NET built-in) + Argon2id (`Konscious.Security.Cryptography.Argon2`) | Standar industri |
| MVVM | `CommunityToolkit.Mvvm` | Source-generator MVVM, minim boilerplate |
| Test | xUnit + FluentAssertions | Idiomatik di .NET |
| CI | GitHub Actions matrix Win/Mac/Linux | Build & test di semua target |

---

## Struktur Solution

```
ZeroBrowser.sln
├─ src/
│  ├─ ZeroBrowser.Core/         (model, fingerprint generator + injector — pure .NET)
│  ├─ ZeroBrowser.Storage/      (SQLite repo + crypto)
│  ├─ ZeroBrowser.Browser/      (PuppeteerSharp launcher + CDP injection)
│  └─ ZeroBrowser.App/          (Avalonia UI, MVVM, entry point)
└─ tests/
   └─ ZeroBrowser.Tests/        (xUnit, runs on Linux/Mac/Win)
```

---

## Build & Run

### Persyaratan

- **.NET 8 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Git**
- Internet (saat pertama kali run, PuppeteerSharp akan download Chromium-for-Testing ~150 MB ke `~/.local-chromium/` atau `%USERPROFILE%\.local-chromium\`)

### Clone & Build

```bash
git clone https://github.com/moomok/zero-browser.git
cd zero-browser
dotnet build
dotnet test
dotnet run --project src/ZeroBrowser.App
```

### Build standalone .exe / app bundle

#### Windows (single-file)

```powershell
dotnet publish src/ZeroBrowser.App `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -o publish/win-x64
```

#### macOS (.app bundle, butuh tooling tambahan)

```bash
dotnet publish src/ZeroBrowser.App \
    -c Release \
    -r osx-arm64 \
    --self-contained \
    -o publish/osx-arm64
# untuk .app bundle proper, gunakan tool seperti `Microsoft.NET.Sdk.macOS`
# atau bungkus binary ke .app secara manual
```

#### Linux

```bash
dotnet publish src/ZeroBrowser.App \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -o publish/linux-x64
```

---

## Cara Pakai (Quick Start)

1. Run app: `dotnet run --project src/ZeroBrowser.App`.
2. Klik **"New profile"** — fingerprint baru auto-generate (deterministic dari seed UUID).
3. Klik **"Launch"** di baris profil. Chromium akan dibuka dengan:
   - User-data-dir terisolasi di `%LOCALAPPDATA%\ZeroBrowser\profiles\<uuid>\`
   - Patch fingerprint di-inject sebelum page script jalan
   - URL default: https://abrahamjuliot.github.io/creepjs/ (untuk verifikasi)
4. Periksa skor di **creepjs**, **iphey**, **pixelscan**, **browserleaks** — tiap profil harus terlihat sebagai device berbeda dengan skor "trust" tinggi.

---

## Cara Verifikasi Fingerprint Berbeda Per Profil

### 1. creepjs ([abrahamjuliot.github.io/creepjs](https://abrahamjuliot.github.io/creepjs/))

- Buka 2 profil berbeda secara berurutan, kunjungi creepjs di masing-masing
- Bandingkan field: **Trust Score**, **Fingerprint Hash**, **Lies**, **Resistance**
- Target:
  - Hash fingerprint berbeda antar profil
  - "Lies" tidak menyebut Canvas / WebGL / Audio (artinya patch tidak ketahuan)
  - Trust score ≥ 60% (untuk MVP — production-grade target ≥ 80%)

### 2. browserleaks ([browserleaks.com](https://browserleaks.com))

Cek satu per satu:
- `/canvas` → hash berbeda antar profil ✓
- `/webgl` → vendor + renderer sesuai profil ✓
- `/webrtc` → IP public sesuai proxy, tidak ada local IP ✓
- `/timezone` → match dengan profil ✓
- `/javascript` → UA, platform, languages match ✓

### 3. pixelscan ([pixelscan.net](https://pixelscan.net))

- Cek "Trustworthy" indicator
- Cek konsistensi antara timezone, locale, IP

### 4. iphey ([iphey.com](https://iphey.com))

- Pastikan tidak ada warning "Suspicious"
- IP / browser / system info konsisten

---

## Arsitektur

```
┌─────────────────────────────────────────────────────────────┐
│              ZeroBrowser.App (Avalonia)               │
│  ┌──────────────┐  ┌────────────────────────────────────┐  │
│  │ MainWindow   │──│ MainWindowViewModel (MVVM)         │  │
│  │  • DataGrid  │  │  • NewProfile / Reload / Launch    │  │
│  └──────────────┘  └────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│   Core: FingerprintGenerator → FingerprintProfile (deterministic)
│   Core: FingerprintInjector  → JS patch script
│   Storage: ProfileRepository (SQLite)
│   Browser: PuppeteerBrowserLauncher (PuppeteerSharp)
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
                  Per-profile Chromium process
                  + --user-data-dir=<storage_path>
                  + --proxy-server=<proxy>
                  + Page.addScriptToEvaluateOnNewDocument(patch)
```

---

## Catatan Realistis (Penting!)

1. **JA3/TLS fingerprint**: Chromium standar punya TLS handshake yang sama untuk semua profil di mesin yang sama. Untuk diversifikasi, butuh:
   - Patch source Chromium (berat), atau
   - Tunnel via [`curl-impersonate`](https://github.com/lwthiker/curl-impersonate) atau `mitmproxy` dengan TLS fingerprint custom.
   Roadmap lanjutan, bukan MVP.

2. **Anti-bot enterprise**: jangan ekspektasi 100% lolos Cloudflare Bot Management / Datadome / PerimeterX / Akamai. Vendor anti-detect komersial pun kucing-tikusan dengan mereka. Target realistis: lolos creepjs / iphey / pixelscan / browserleaks.

3. **Maintenance**: teknik anti-detect rentan break setiap update Chrome major (~6 minggu sekali). Pastikan rajin update dataset di `FingerprintDataset.cs` dan logic injector saat Chrome ganti API.

4. **Legal**: tool ini legal untuk privasi & multi-account yang TOS-compliant. **Tidak** legal untuk fraud, fake review, ad-fraud, ban evasion, dll. Periksa TOS platform target sebelum pakai.

---

## Lisensi

MIT — bebas dipakai, dimodifikasi, didistribusi. Lihat [LICENSE](./LICENSE).

## Kredit & Referensi

- [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp) — CDP control library
- [Avalonia UI](https://avaloniaui.net/) — cross-platform XAML UI
- [creepjs](https://github.com/abrahamjuliot/creepjs) — gold-standard fingerprint detection
- [Puppeteer Stealth Plugin](https://github.com/berstend/puppeteer-extra/tree/master/packages/puppeteer-extra-plugin-stealth) — referensi teknik patch
- [Riset fingerprinting modern](https://hovav.net/ucsd/dist/canvas.pdf) — Mowery & Shacham 2012, makalah klasik canvas fingerprinting
