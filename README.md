# Trackpad

Wireless trackpad for Windows using your Android tablet over local WiFi.

## Install (PC)

1. Download `TRACKPAD.exe` from [Releases](../../releases).
2. Double-click it. A window shows an address like `https://...`.
3. Leave it running in the system tray. Closing the window doesn't stop the server.

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

Done. Next time, just tap the home-screen icon.