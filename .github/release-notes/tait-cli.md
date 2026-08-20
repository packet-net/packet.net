Standalone Tait TM8100/TM8200 codeplug CLI - read, decode, edit and program a codeplug over the serial interface without the Windows CPS, and apply the Packet.NET (`pdn-basic` / `pdn-extra`) upgrade profiles.

Each binary is **self-contained** (the .NET runtime and the native serial library are embedded) and **single-file** - no .NET install needed. Download the one for your platform and run.

**Linux / macOS**
```
curl -LO https://github.com/__REPO__/releases/download/tait-cli-v__VER__/tait-codeplug-__VER__-linux-x64   # or linux-arm64 / linux-arm / osx-x64 / osx-arm64
chmod +x tait-codeplug-__VER__-*
./tait-codeplug-__VER__-linux-x64 --help
```

**Windows**: download `tait-codeplug-__VER__-win-x64.exe` and run it.

Assets: `linux-x64`, `linux-arm64`, `linux-arm` (armv7 / 32-bit Pi), `win-x64`, `osx-x64` (Intel), `osx-arm64` (Apple Silicon). `SHA256SUMS` covers every asset - verify with `sha256sum -c SHA256SUMS` (or `shasum -a 256 -c` on macOS).

Common commands: `dump <file.m8p>` (decode every field), `get` / `set <file.m8p> <field> <value>`, `set <file.m8p> profile pdn-basic|pdn-extra`, and the hardware verbs `version` / `read` / `patch <port> ...` (power-cycle the radio into programming mode as you trigger). The write path is version-pinned and backs up before writing; it never touches firmware.
