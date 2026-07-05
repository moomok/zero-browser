# Zero Browser

> Cross-platform multi-profile browser desktop dengan fingerprint berbeda per profil.

Setiap profil = identitas browser terpisah dengan **fingerprint sendiri** (Canvas, WebGL, Audio, fonts, timezone, locale, hardware, dll), **storage terisolasi**, dan **proxy berbeda**. Satu PC bisa terlihat sebagai puluhan/ratusan device berbeda dari sisi server.

Inspirasi: Multilogin / GoLogin / AdsPower / Dolphin Anty / Kameleo — versi open-source pribadi.

> ⚠️ **Disclaimer**: tool ini ditujukan untuk privasi, QA testing, manajemen multi-akun yang sah, dan riset keamanan. **JANGAN** dipakai untuk fraud, ad-fraud, fake account scam, atau aktivitas yang melanggar hukum / Terms of Service platform. Tanggung jawab penggunaan ada di tangan pengguna.

---

## Status

**v0.3** — fungsional sudah lengkap untuk daily use, plus fitur multi-fingerprint level enterprise:

### Core
- ✅ FingerprintGenerator deterministik (seed → fingerprint konsisten antar sesi)
- ✅ FingerprintInjector lengkap (Navigator, Screen, Canvas, WebGL, Audio, Intl/timezone, Geolocation, MediaDevices, WebRTC, Permissions, chrome.*) dengan hardening anti-detect (toString native, prototype patching, no global leaks)
- ✅ PuppeteerBrowserLauncher cross-platform (PuppeteerSharp + CDP)
- ✅ Per-profile user-data-dir, proxy server + auth, timezone emulation
- ✅ Storage SQLite + AES-256-GCM + Argon2id
- ✅ Avalonia UI 12 (cross-platform native, Fluent Design)
- ✅ **68 unit tests passing** (Win/Mac/Linux)

### Identity & Security
- ✅ **TLS / JA3 fingerprint diversion** — in-process sidecar proxy per profile, derives TLS ClientHello (cipher suite order) from seed-based pool (Chrome/Firefox/Safari/Edge/Randomized). User-Agent and TLS mode are auto-normalized for consistency.
- ✅ **Master password lock di app start** (Argon2id-derived SecretBox; sensitive data di disk dienkripsi)
- ✅ **Encrypted fingerprint token** — format portable `base64_key|base64_payload|base64_iv|flags|ver` (AES-256-GCM)
- ✅ **Proxy manager + bulk import** + **connectivity test** (Test / Test All via httpbin.org/ip)
- ✅ **SOCKS5 auth fix** + multi-tab proxy auth
- ✅ **Command-injection-proof proxy args**, zip-slip-proof CRX importer, cookie file permission restriction
- ✅ **Cookie exporter** — JSON (Playwright/Puppeteer format) + Netscape (curl/wget format) via Export buttons

### Profile Management
- ✅ **Profile editor lengkap** (nama, OS pin, proxy, regenerate seed, rotation, token import/export, notes; live preview)
- ✅ **Fingerprint preview dialog**
- ✅ **Fingerprint rotation** — auto-rotate tiap X hari, seed lama masuk history
- ✅ **Seed history per profil** — switch kembali ke fingerprint lama
- ✅ **Batch create** — 1 klik = 10 profil sekaligus
- ✅ **Tag filter & search** — search by name/notes/seed + filter by tag, Clear button
- ✅ **Cookie importer** (JSON Puppeteer/Playwright/EditThisCookie + Netscape/curl)

### Performance
- ✅ **Non-blocking UI** — semua DB query, fingerprint generation, dan filesystem scan (BrowserDetector.Detect) dipindah ke background thread; UI tetap responsif saat buat/list/launch profil

### Dataset
- ✅ **Chrome 145-150** (current stable Juli 2026: 150.0.7871)
- ✅ **GPU combos komplet** — RTX 40/50 (4070/4090/5070/5090), RX 7600/7800 XT/9070 XT, Apple M4/M4 Pro/M4 Max, Intel Arc A770
- ✅ **Sec-CH-UA grease brand** fix per milestone range (126-130, 131-134, 135-144, 145+)

### Belum (roadmap)
- [ ] Automation runner UI (Runner + Node.js CDP attach sudah dibuild; tinggal bind UI file picker)
- [ ] Code signing + auto-update
- [ ] TLS cert-pinning bypass list per domain

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
│  ├─ ZeroBrowser.Core/         (model, fingerprint generator + injector, token codec — pure .NET)
│  ├─ ZeroBrowser.Storage/      (SQLite repo + crypto: SecretBox, MasterKey)
│  ├─ ZeroBrowser.Browser/      (PuppeteerSharp launcher + CDP injection + browser detection)
│  └─ ZeroBrowser.App/          (Avalonia UI, MVVM, entry point)
└─ tests/
   └─ ZeroBrowser.Tests/        (xUnit, 68 tests, runs on Linux/Mac/Win)
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
dotnet test                      # 68 tests
dotnet run --project src/ZeroBrowser.App
```

### Build standalone executable / installer

Cara cepat: pakai script bantu di `scripts/`.

#### Windows MSIX + portable zip
```powershell
./scripts/build-msix.ps1 -Version "0.3.0.0"
```

#### Linux / macOS portable archive
```bash
./scripts/build-portable.sh linux-x64  0.3.0  artifacts
./scripts/build-portable.sh osx-arm64  0.3.0  artifacts   # Apple Silicon
./scripts/build-portable.sh osx-x64    0.3.0  artifacts   # Intel
```

Atau langsung `dotnet publish` (manual, tanpa archive):

```bash
dotnet publish src/ZeroBrowser.App -c Release -r linux-x64 --self-contained
```

`-r` bisa diganti ke `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`.

---

## Install dari GitHub Release

Pipeline `.github/workflows/release.yml` otomatis bikin **draft Release** setiap kali tag `v*.*.*` di-push.

### Windows MSIX (recommended)
1. Download `ZeroBrowser-x64-*.cer` dan `ZeroBrowser-x64-*.msix`.
2. Klik kanan `.cer` → **Install Certificate** → **Local Machine** → **Place all certificates in the following store** → **Trusted Root Certification Authorities** → OK.
3. Dobel-klik `.msix` → **Install**.
4. Cari **Zero Browser** di Start Menu.

### Windows portable
1. Download `ZeroBrowser-x64-*-portable.zip` → extract → run `ZeroBrowser.App.exe`.

### macOS portable
1. Download `ZeroBrowser-osx-x64-*.tar.gz` (Intel) atau `ZeroBrowser-osx-arm64-*.tar.gz` (Apple Silicon).
2. Extract, run `./ZeroBrowser.App`. Pertama kali mungkin perlu klik kanan → **Open** karena binary belum ditandatangani Apple.

### Linux portable
1. Download `ZeroBrowser-linux-x64-*.tar.gz` → extract → run `./ZeroBrowser.App`. Library: `libicu`, `libssl`, `libsecret-1`, `libfontconfig`.

---

## Cara Pakai (Quick Start)

1. Run app: `dotnet run --project src/ZeroBrowser.App`.
2. Master password di-prompt pertama kali → set password (min 8 chars). Password ini encrypt proxy secrets di disk.
3. **New profile** — fingerprint baru auto-generate (deterministic dari seed UUID).
4. Atau klik **Batch create** untuk spawn 10 profil sekaligus.
5. Klik **Launch** di baris profil. Chromium akan dibuka dengan:
   - User-data-dir terisolasi di `%LOCALAPPDATA%\ZeroBrowser\profiles\<uuid>\`
   - Patch fingerprint di-inject sebelum page script jalan
   - URL default: https://abrahamjuliot.github.io/creepjs/ (untuk verifikasi)
6. Periksa skor di **creepjs**, **iphey**, **pixelscan**, **browserleaks** — tiap profil harus terlihat sebagai device berbeda dengan skor "trust" tinggi.

---

## Fingerprint Token (Clone Identity antar Instance)

Setiap profil punya token portable yang bisa di-share:

```
ThMFnidaOXJ7cXMURGAGIw==|BY3C...==|aDwCyoV3c9TwPZud|08|1
       |                    |              |         |    |
       |                    |              |         |    └─ version
       |                    |              |         └─ flags (os, rotation bits)
       |                    |              └─ AES IV (12B)
       |                    └─ encrypted payload (AES-256-GCM)
       └─ AES key (32B)
```

**Cara pakai:**
- Profile Editor → section **Fingerprint token**:
  - **Copy token** — clipboard, paste ke colleague / simpan note
  - **Regenerate** — buat token baru (key + IV baru, payload ter-encrypt ulang)
  - **Import token** — paste token orang lain → seed, OS, rotation settings auto-applied

Use case: maintain identity consistency across machines, share tested profiles dengan tim, rollback ke known-good fingerprint.

---

## Fingerprint Rotation

Auto-rotate seed profil saat launch agar fingerprint tidak statis (anti-pattern detector detaktil).

**Profile Editor → Fingerprint rotation:**
- Off (manual only) — default
- Every day / 3 days / weekly / 2 weeks / monthly

Saat rotasi, **seed lama otomatis di-archive ke seed history** — bisa switch kembali kapanpun.

---

## Seed History (per Profile)

Setiap profile menyimpan history of seeds. Seed baru otomatis di-archive setiap kali:
- Tombol **Regenerate** diklik
- Token di-import
- Auto-rotation triggered
- Switch manual ke seed dari history

Di Profile Editor → **Seed history** panel → **Use** (switch ke seed ini) / **Remove**.

---

## Extensions (per profile)

| Mode | Web Store | Determinism | Cara |
|---|---|---|---|
| **Chromium for Testing** (default) | ❌ tidak bisa install dari Web Store | ✅ binary pinned | Sideload manual via folder atau CRX |
| **Browser ter-install** (Chrome / Brave / Edge / Vivaldi / Opera) | ✅ login + install Web Store extensions | ⚠️ binary auto-update bisa drift | Pilih dari dropdown "Engine" di Profile Editor |

**Cara pakai (Profile Editor → Extensions):**
- **Add folder** — browse ke folder unpacked extension (harus berisi `manifest.json`)
- **Import .crx** — pilih file `.crx`, otomatis di-extract ke `%LOCALAPPDATA%\ZeroBrowser\extensions\<profile-id>\<random>\`
- Toggle ✓ untuk enable/disable per launch
- **Remove** — hapus dari profil

**Implementation notes:**
- Mode "Chromium for Testing" pakai `--disable-extensions-except` untuk pin set extension → binary state deterministic.
- Mode browser ter-install guard dilepas → user tetap bisa install Web Store extensions interaktif.
- Folder picker **reject path dengan koma** (`,`) karena Chromium `--load-extension` flag pakai comma sebagai separator tanpa escape.

**Anti-detect note:** Extensions adalah vektor fingerprint besar (uBlock + Cookie Editor punya DOM signature). Untuk stealth maksimum: stay on Chromium-for-Testing tanpa extensions, atau install set extension yang **sama di semua profil**.

---

## Proxy Manager + Test

**Bulk import** (satu per baris):
```
1.2.3.4:8080
user:pass@1.2.3.4:8080
socks5://user:pass@10.0.0.1:1080
https://proxy.example.com:443
```

**Test connectivity:**
- Klik **Test** per proxy → HTTP GET ke `https://httpbin.org/ip` via proxy, tampil latency
- Klik **Test all** → test semua proxy berurutan
- Hasil: `OK (245ms)` / `HTTP 403` / `timeout` / `failed: <reason>` / `auth-error` (decrypt password rusak → re-add proxy)

**SOCKS5 auth:** credentials di-embed di URL `socks5://user:pass@host:port` (Chromium tidak handle SOCKS5 auth via 407 challenge).

---

## Cara Verifikasi Fingerprint Berbeda Per Profil

### 1. creepjs ([abrahamjuliot.github.io/creepjs](https://abrahamjuliot.github.io/creepjs/))
- Buka 2 profil berbeda, bandingkan field: Trust Score, Fingerprint Hash, Lies, Resistance
- Target: hash berbeda, "Lies" tidak sebut Canvas/WebGL/Audio, Trust ≥ 60% (production target ≥ 80%)

### 2. browserleaks ([browserleaks.com](https://browserleaks.com))
Cek satu per satu: `/canvas`, `/webgl`, `/webrtc`, `/timezone`, `/javascript` → masing-masing harus match profil.

### 3. pixelscan ([pixelscan.net](https://pixelscan.net))
Cek "Trustworthy" indicator + konsistensi TZ/locale/IP.

### 4. iphey ([iphey.com](https://iphey.com))
Pastikan tidak ada warning "Suspicious", IP/browser/system info konsisten.

---

## Arsitektur

```
┌─────────────────────────────────────────────────────────────┐
│   ZeroBrowser.App (Avalonia)                                │
│   • MainWindowViewModel: NewProfile / BatchCreate /         │
│     ReloadAsync / LaunchAsync / EditProfileAsync / Delete   │
│   • ProfileEditorViewModel: rotation, seed history,        │
│     token generate/import/copy                              │
│   • ProxyManagerViewModel: bulk import, test, delete        │
└─────────────────────────────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────┐
│   Core                                                      │
│ • FingerprintGenerator      → seed → FingerprintProfile    │
│ • FingerprintInjector       → JS patch script (CDP inject) │
│ • FingerprintTokenCodec     → portable encrypted token     │
│   SeededRandom (SHA-256 + xorshift32, deterministic)        │
│   Util: ProxyImporter, CookieImporter, CrxImporter          │
│                                                             │
│   Storage                                                   │
│ • ProfileRepository (SQLite + Dapper)                       │
│ • ProxyRepository (passwords encrypted via SecretBox)       │
│ • SecretBox (AES-256-GCM), MasterKey (Argon2id verifier)   │
│                                                             │
│   Browser                                                   │
│ • PuppeteerBrowserLauncher (PuppeteerSharp + CDP)           │
│ • BrowserDetector (auto-detect Chrome/Brave/Edge/...)       │
└─────────────────────────────────────────────────────────────┘
                             │
                             ▼
                   Per-profile Chromium process
                   + --user-data-dir=<storage_path>
                   + --proxy-server=<http(s)/socks5://...>
                   + Page.addScriptToEvaluateOnNewDocument(patch)
```

---

## Security Notes

- **Master password**: Argon2id-derived key (64MB memory, 3 iterations). Tidak ada recovery. Proxy passwords dienkripsi at-rest dengan SecretBox ini.
- **Fingerprint token**: payload AES-256-GCM. Token portable untuk share antar instance — **bukan security boundary**, hanya portability. Jangan share token yang isinya confidential.
- **SQLite**: file DB tidak encrypted at-rest. Pertimbangkan encrypt disk (BitLocker / FileVault / LUKS) kalau profil berisi secret sensitive.
- **Argon2id parameters** bisa di-tune di `SecretBox.cs` (saat ini ringan cukup untuk desktop app, bisa dinaikkan untuk paranoid).
- **Security audit report**: lihat commit history — issue command-injection, zip-slip, JS payload injection, cookie file permission, payload size limit, secret decrypt error surfacing sudah fix.

---

## Catatan Realistis (Penting!)

1. **JA3/TLS fingerprint**: Chromium standar punya TLS handshake yang sama untuk semua profil di mesin yang sama. Untuk diversifikasi, butuh patch source Chromium atau tunnel via `curl-impersonate` / `mitmproxy` dengan TLS fingerprint custom. **Roadmap**, bukan MVP.

2. **Anti-bot enterprise**: jangan ekspektasi 100% lolos Cloudflare Bot Management / Datadome / PerimeterX / Akamai. Vendor anti-detect komersial pun kucing-tikusan. Target realistis: lolos creepjs / iphey / pixelscan / browserleaks.

3. **Maintenance dataset**: teknik anti-detect rentan break setiap Chrome major (~6 minggu sekali). Update `FingerprintDataset.cs` setiap update Chrome. Sekarang adapter support Chrome 126-150; kalau sudah lewat 150 mungkin perlu aggiornare.

4. **Legal**: tool legal untuk privasi & multi-account TOS-compliant. **Tidak** legal untuk fraud, fake review, ad-fraud, ban evasion, dll. Periksa TOS platform target sebelum pakai.

---

## Lisensi

MIT — bebas dipakai, dimodifikasi, didistribusi. Lihat [LICENSE](./LICENSE).

## Kredit & Referensi

- [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp) — CDP control library
- [Avalonia UI](https://avaloniaui.net/) — cross-platform XAML UI
- [creepjs](https://github.com/abrahamjuliot/creepjs) — gold-standard fingerprint detection
- [Puppeteer Stealth Plugin](https://github.com/berstend/puppeteer-extra/tree/master/packages/puppeteer-extra-plugin-stealth) — referensi teknik patch
- [ChromiumDash](https://chromiumdash.appspot.com) — Chrome version data
- [Riset fingerprinting modern](https://hovav.net/ucsd/dist/canvas.pdf) — Mowery & Shacham 2012
