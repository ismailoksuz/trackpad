# Trackpad

Wireless trackpad for Windows using your Android tablet over local WiFi.

## Requirements

- Windows 10/11 (64-bit)
- [.NET 9 SDK](https://dotnet.microsoft.com/download) installed
- Android 7+ tablet or phone with Chrome
- Both devices on the same WiFi network
  
## Install (PC)

```
git clone https://github.com/ismailoksuz/trackpad.git
cd trackpad
dotnet run
```
A window shows an address like `https://...`. Leave it running.


## Install (Android tablet)

**One-time certificate setup:**

1. On the tablet (same WiFi as PC), open the address shown in the TRACKPAD window, followed by `/ca.crt`.
2. Accept the "not secure" warning → **Proceed anyway**.
3. The `ca.crt` file downloads.
4. Android **Settings → Security → Encryption & credentials → Install a certificate → CA certificate** → select `ca.crt`.

**Install the PWA:**

1. Open the same address (without `/ca.crt`) in the browser — the padlock should now be green.
2. Chrome menu → **Install app**.
3. An icon appears on your home screen. Tap it to launch full-screen.

## Optional: build a standalone EXE

If you don't want to install .NET on the target PC, build a single-file, self-contained EXE:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
Output: `bin\Release\net9.0-windows\win-x64\publish\TRACKPAD.exe`
