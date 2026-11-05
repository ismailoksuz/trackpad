using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Forms;

class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx; public int dy;
        public uint mouseData; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan;
        public uint dwFlags; public uint time;
        public IntPtr dwExtraInfo;
    }

    const int INPUT_MOUSE = 0;
    const int INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE      = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
    const uint MOUSEEVENTF_LEFTUP    = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
    const uint MOUSEEVENTF_WHEEL     = 0x0800;

    const uint KEYEVENTF_KEYDOWN = 0x0000;
    const uint KEYEVENTF_KEYUP   = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    const string PASSWORD = "Trackpad123!";
    const string PfxPath  = "server.pfx";
    const string CrtPath  = "ca.crt";

    static X509Certificate2 ServerCert;
    static TcpListener Listener;
    static byte[] _icon192, _icon512;
    static byte[] Icon192 => _icon192 ??= LoadEmbeddedPng("icon-192.png");
    static byte[] Icon512 => _icon512 ??= LoadEmbeddedPng("icon-512.png");

    static byte[] LoadEmbeddedPng(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return CreateIconPng(512); // yedek: eski üretim
        using var s = asm.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [STAThread]
    static void Main()
    {
        ServerCert = LoadCertificate();

        Listener = new TcpListener(IPAddress.Any, 8080);
        try { Listener.Start(); }
        catch (SocketException)
        {
            MessageBox.Show(
                "Sunucu zaten çalışıyor.\n\nGörev Yöneticisi'nden eski TRACKPAD.exe'yi kapatıp tekrar dene.",
                "UzakFare", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _ = Task.Run(AcceptLoop);

        var urls = GetLocalIPv4s().Select(ip => $"https://{ip}:8080").ToArray();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(urls));
    }

    static async Task AcceptLoop()
    {
        while (true)
        {
            try
            {
                var client = await Listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
            catch { break; }
        }
    }

    static IEnumerable<string> GetLocalIPv4s()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(a.Address.ToString());
        }
        if (list.Count == 0) list.Add("127.0.0.1");
        return list;
    }

    static X509Certificate2 LoadCertificate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var pfxName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("server.pfx", StringComparison.OrdinalIgnoreCase));

        if (pfxName != null)
        {
            using var s = asm.GetManifestResourceStream(pfxName);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return X509CertificateLoader.LoadPkcs12(ms.ToArray(), PASSWORD, X509KeyStorageFlags.Exportable);
        }

        if (File.Exists(PfxPath) && File.Exists(CrtPath))
            return new X509Certificate2(PfxPath, PASSWORD, X509KeyStorageFlags.Exportable);

        return GenerateCertificate();
    }

    static X509Certificate2 GenerateCertificate()
    {
        using var caRsa = RSA.Create(4096);
        var caReq = new CertificateRequest("CN=UzakFare Root CA, O=UzakFare",
            caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        using var srvRsa = RSA.Create(2048);
        var srvReq = new CertificateRequest("CN=UzakFare", srvRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                    san.AddIpAddress(a.Address);
        }
        srvReq.CertificateExtensions.Add(san.Build());
        srvReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        srvReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        srvReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        srvReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(srvReq.PublicKey, false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        var srvCert = srvReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5), serial);
        var srvCertWithKey = srvCert.CopyWithPrivateKey(srvRsa);
        var srvPfx = srvCertWithKey.Export(X509ContentType.Pfx, PASSWORD);

        File.WriteAllBytes(PfxPath, srvPfx);
        File.WriteAllBytes(CrtPath, caCert.Export(X509ContentType.Cert));
        return X509CertificateLoader.LoadPkcs12(srvPfx, PASSWORD, X509KeyStorageFlags.Exportable);
    }

    static byte[] GetCaCrtBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ca.crt", StringComparison.OrdinalIgnoreCase));
        if (name != null)
        {
            using var s = asm.GetManifestResourceStream(name);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        return File.Exists(CrtPath) ? File.ReadAllBytes(CrtPath) : Array.Empty<byte>();
    }

    static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var networkStream = client.GetStream())
            using (var ssl = new SslStream(networkStream, false))
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = ServerCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                });

                var buffer = new byte[16384];
                int bytesRead = await ssl.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) return;
                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (request.StartsWith("GET / HTTP/1.1"))
                {
                    string response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                        + Encoding.UTF8.GetByteCount(Html) + "\r\nConnection: close\r\n\r\n" + Html;
                    await ssl.WriteAsync(Encoding.UTF8.GetBytes(response));
                }
                else if (request.StartsWith("GET /manifest.json"))
                {
                    string response = "HTTP/1.1 200 OK\r\nContent-Type: application/manifest+json\r\nContent-Length: "
                        + Encoding.UTF8.GetByteCount(Manifest) + "\r\nConnection: close\r\n\r\n" + Manifest;
                    await ssl.WriteAsync(Encoding.UTF8.GetBytes(response));
                }
                else if (request.StartsWith("GET /ca.crt"))
                {
                    byte[] crt = GetCaCrtBytes();
                    string header = "HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\nContent-Length: "
                        + crt.Length + "\r\nContent-Disposition: attachment; filename=ca.crt\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.UTF8.GetBytes(header));
                    await ssl.WriteAsync(crt);
                }
                else if (request.StartsWith("GET /icon-192.png"))
                {
                    await ServePng(ssl, Icon192);
                }
                else if (request.StartsWith("GET /icon-512.png"))
                {
                    await ServePng(ssl, Icon512);
                }
                else if (request.StartsWith("POST / HTTP/1.1"))
                {
                    int bodyStart = request.IndexOf("\r\n\r\n");
                    if (bodyStart > 0) ProcessInput(request.Substring(bodyStart + 4).Trim());
                    string ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.UTF8.GetBytes(ok));
                }
            }
        }
        catch { }
    }

    static async Task ServePng(Stream stream, byte[] png)
    {
        string header = "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: " + png.Length
            + "\r\nCache-Control: max-age=86400\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header));
        await stream.WriteAsync(png);
    }

    static byte[] CreateIconPng(int size)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        WriteChunk(ms, "IHDR", w =>
        {
            WriteUInt32BE(w, (uint)size);
            WriteUInt32BE(w, (uint)size);
            w.WriteByte(8); w.WriteByte(2); w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
        });

        int rowBytes = 1 + size * 3;
        byte[] raw = new byte[size * rowBytes];
        int cx = size / 2, cy = size / 2;
        int outer = size * 38 / 100, inner = size * 32 / 100;

        for (int y = 0; y < size; y++)
        {
            int rowStart = y * rowBytes;
            raw[rowStart] = 0;
            int dy = y - cy;
            for (int x = 0; x < size; x++)
            {
                int px = rowStart + 1 + x * 3;
                int dx = x - cx;
                int distSq = dx * dx + dy * dy;
                byte r, g, b;
                if (distSq <= outer * outer && distSq >= inner * inner) { r = 200; g = 200; b = 200; }
                else if (distSq < inner * inner) { r = 74; g = 124; b = 255; }
                else { r = 26; g = 26; b = 26; }
                raw[px] = r; raw[px + 1] = g; raw[px + 2] = b;
            }
        }

        byte[] compressed;
        using (var tmp = new MemoryStream())
        {
            using (var zlib = new ZLibStream(tmp, CompressionLevel.Optimal, true))
                zlib.Write(raw, 0, raw.Length);
            compressed = tmp.ToArray();
        }

        WriteChunk(ms, "IDAT", w => w.Write(compressed, 0, compressed.Length));
        WriteChunk(ms, "IEND", _ => { });
        return ms.ToArray();
    }

    static void WriteChunk(Stream s, string type, Action<Stream> writeData)
    {
        using var dataMs = new MemoryStream();
        writeData(dataMs);
        byte[] data = dataMs.ToArray();
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        WriteUInt32BE(s, (uint)data.Length);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        WriteUInt32BE(s, Crc32(typeBytes, data));
    }

    static void WriteUInt32BE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    static uint[] _crcTable;
    static uint Crc32(byte[] type, byte[] data)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                _crcTable[i] = c;
            }
        }
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    static void ProcessInput(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == null) return;

            switch (type)
            {
                case "move":
                    MoveMouse((int)doc.RootElement.GetProperty("x").GetDouble(),
                              (int)doc.RootElement.GetProperty("y").GetDouble());
                    break;
                case "scroll":
                    Scroll((int)doc.RootElement.GetProperty("y").GetDouble());
                    break;
                case "tap":
                    Click(doc.RootElement.GetProperty("button").GetString() == "right");
                    break;
                case "char":
                    var ch = doc.RootElement.GetProperty("value").GetString();
                    if (!string.IsNullOrEmpty(ch)) SendChar(ch[0]);
                    break;
                case "vk":
                    ushort vk = (ushort)doc.RootElement.GetProperty("value").GetInt32();
                    bool shift = false, ctrl = false, alt = false, win = false;
                    if (doc.RootElement.TryGetProperty("mods", out var mods))
                    {
                        if (mods.TryGetProperty("shift", out var s)) shift = s.GetBoolean();
                        if (mods.TryGetProperty("ctrl", out var c)) ctrl = c.GetBoolean();
                        if (mods.TryGetProperty("alt", out var a)) alt = a.GetBoolean();
                        if (mods.TryGetProperty("cmd", out var w)) win = w.GetBoolean();
                    }
                    SendVK(vk, shift, ctrl, alt, win);
                    break;
            }
        }
        catch { }
    }

    static void MoveMouse(int dx, int dy)
    {
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } } }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    static void Click(bool right)
    {
        uint down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = down } } },
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = up } } }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    static void Scroll(int pixels)
    {
        int clicks = pixels / 30;
        if (clicks == 0) clicks = pixels > 0 ? 1 : -1;
        int delta = clicks * 120;
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)delta, dwFlags = MOUSEEVENTF_WHEEL } } }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    static void SendChar(char c)
    {
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    static void SendVK(ushort vk, bool shift, bool ctrl, bool alt, bool win)
    {
        var list = new List<INPUT>();
        void add(ushort code, bool down)
        {
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = code, dwFlags = down ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP } }
            });
        }
        const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_LWIN = 0x5B;

        if (shift) add(VK_LSHIFT, true);
        if (ctrl) add(VK_LCONTROL, true);
        if (alt) add(VK_LMENU, true);
        if (win) add(VK_LWIN, true);
        add(vk, true);
        add(vk, false);
        if (shift) add(VK_LSHIFT, false);
        if (ctrl) add(VK_LCONTROL, false);
        if (alt) add(VK_LMENU, false);
        if (win) add(VK_LWIN, false);

        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    const string Manifest = @"{
  ""name"": ""Uzak Fare"",
  ""short_name"": ""UzakFare"",
  ""start_url"": ""/"",
  ""scope"": ""/"",
  ""display"": ""standalone"",
  ""background_color"": ""#111111"",
  ""theme_color"": ""#111111"",
  ""orientation"": ""portrait"",
  ""icons"": [
    { ""src"": ""/icon-192.png"", ""sizes"": ""192x192"", ""type"": ""image/png"", ""purpose"": ""any"" },
    { ""src"": ""/icon-512.png"", ""sizes"": ""512x512"", ""type"": ""image/png"", ""purpose"": ""any"" }
  ]
}";

    const string Html = @"<!DOCTYPE html>
<html>
<head>
<meta charset='UTF-8'>
<meta name='theme-color' content='#111111'>
<link rel='manifest' href='/manifest.json'>
<link rel='icon' type='image/png' href='/icon-192.png'>
<link rel='apple-touch-icon' href='/icon-192.png'>
<meta name='mobile-web-app-capable' content='yes'>
<meta name='apple-mobile-web-app-capable' content='yes'>
<meta name='apple-mobile-web-app-status-bar-style' content='black-translucent'>
<meta name='apple-mobile-web-app-title' content='UzakFare'>
<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover'>
<style>
* { margin: 0; padding: 0; box-sizing: border-box; }
html, body {
    background: #111; overflow: hidden; height: 100vh;
    touch-action: none;
    -webkit-user-select: none; user-select: none;
    -webkit-touch-callout: none;
    font-family: -apple-system, BlinkMacSystemFont, sans-serif;
}
#trackpad {
    position: absolute; inset: 0;
    display: flex; align-items: center; justify-content: center;
    color: #444; font-size: 18px; text-align: center;
    line-height: 1.6;
}
</style>
</head>
<body>

<div id='trackpad'>Bağlandı<br>Dokunmaya başlayın</div>

<script>
const NATURAL_SCROLL = false;
let startX = 0, startY = 0, lastX = 0, lastY = 0;
let startTime = 0, maxFingers = 0;
let moved = false, consumed = false, scrolled = false;
let scrollAccum = 0, longPressTimer = null;

const url = 'https://' + location.host + '/';
function send(d) {
    fetch(url, { method: 'POST', body: JSON.stringify(d), keepalive: true }).catch(() => {});
}

document.addEventListener('touchstart', e => {
    e.preventDefault();
    const n = e.touches.length;
    if (n > maxFingers) {
        maxFingers = n;
        if (n === 1) {
            startX = lastX = e.touches[0].clientX;
            startY = lastY = e.touches[0].clientY;
            startTime = Date.now();
            moved = false; consumed = false; scrollAccum = 0;
            if (longPressTimer) clearTimeout(longPressTimer);
            longPressTimer = setTimeout(() => {
                if (!moved && !consumed) { send({ type: 'tap', button: 'right' }); consumed = true; }
                longPressTimer = null;
            }, 500);
        } else if (n === 2) {
            lastX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
            lastY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
            scrollAccum = 0; scrolled = false;
            if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
            moved = true;
        }
    }
}, { passive: false });

document.addEventListener('touchmove', e => {
    e.preventDefault();
    const n = e.touches.length;
    if (n === 1) {
        const cx = e.touches[0].clientX, cy = e.touches[0].clientY;
        if (!moved && Math.hypot(cx - startX, cy - startY) > 8) {
            moved = true;
            if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
            lastX = cx; lastY = cy;
        }
        if (moved) {
            const dx = Math.round((cx - lastX) * 2.0);
            const dy = Math.round((cy - lastY) * 2.0);
            if (dx !== 0 || dy !== 0) send({ type: 'move', x: dx, y: dy });
            lastX = cx; lastY = cy;
        }
    } else if (n === 2) {
        const ax = (e.touches[0].clientX + e.touches[1].clientX) / 2;
        const ay = (e.touches[0].clientY + e.touches[1].clientY) / 2;
        scrollAccum += NATURAL_SCROLL ? (ay - lastY) : (lastY - ay);
        lastX = ax; lastY = ay;
        if (Math.abs(scrollAccum) >= 30) {
            const clicks = scrollAccum > 0 ? Math.floor(scrollAccum / 30) : Math.ceil(scrollAccum / 30);
            if (clicks !== 0) {
                send({ type: 'scroll', y: clicks * 30 });
                scrollAccum -= clicks * 30;
                scrolled = true;
            }
        }
    }
}, { passive: false });

document.addEventListener('touchend', e => {
    if (e.touches.length > 0) {
        if (e.touches.length === 1) { lastX = e.touches[0].clientX; lastY = e.touches[0].clientY; }
        return;
    }
    if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
    const duration = Date.now() - startTime;
    const endX = e.changedTouches[0].clientX;
    const endY = e.changedTouches[0].clientY;
    const totalDist = Math.hypot(endX - startX, endY - startY);
    if (!consumed) {
        if (maxFingers === 1 && !moved && duration < 500) send({ type: 'tap', button: 'left' });
        else if (maxFingers === 2 && !scrolled && totalDist < 15 && duration < 500) send({ type: 'tap', button: 'right' });
    }
    maxFingers = 0; moved = false; consumed = false; scrolled = false; scrollAccum = 0;
}, { passive: false });
</script>
</body>
</html>";

static Icon _appIcon;
public static Icon AppIcon => _appIcon ??= LoadAppIcon();

static Icon LoadAppIcon()
{
    var asm = Assembly.GetExecutingAssembly();
    var name = asm.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
    if (name != null)
    {
        using var s = asm.GetManifestResourceStream(name);
        return new Icon(s);
    }
    return SystemIcons.Application;
}
}
// ============================================================
// TRAY + ANA PENCERE
// ============================================================
class MainForm : Form
{
    NotifyIcon _tray;
    readonly string[] _urls;

    public MainForm(string[] urls)
    {
        _urls = urls;

        Text = "UzakFare";
        Icon = Program.AppIcon;
        ClientSize = new Size(540, 270);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = Color.FromArgb(17, 17, 17);
        ForeColor = Color.White;

        var title = new Label
        {
            Text = "Tabletinden bu adrese gir:",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.White,
            Left = 20, Top = 18, Width = 500, Height = 28
        };

        var urlBox = new TextBox
        {
            Text = urls.Length > 0 ? urls[0] : "",
            Font = new Font("Consolas", 15f, FontStyle.Bold),
            Left = 20, Top = 56, Width = 500, Height = 40,
            ReadOnly = true,
            TextAlign = HorizontalAlignment.Center,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(120, 170, 255),
            BorderStyle = BorderStyle.FixedSingle
        };
        urlBox.Click += (s, e) => urlBox.SelectAll();
        urlBox.Enter += (s, e) => urlBox.SelectAll();

        var btnCopy = new Button
        {
            Text = "Adresi Kopyala",
            Left = 20, Top = 112, Width = 160, Height = 44,
            Font = new Font("Segoe UI", 10f)
        };
        btnCopy.Click += (s, e) =>
        {
            try
            {
                Clipboard.SetText(urlBox.Text);
                _tray.ShowBalloonTip(2000, "Kopyalandı", urlBox.Text, ToolTipIcon.Info);
            }
            catch { }
        };

        var btnHide = new Button
        {
            Text = "Tepsiye Küçült",
            Left = 190, Top = 112, Width = 160, Height = 44,
            Font = new Font("Segoe UI", 10f)
        };
        btnHide.Click += (s, e) => Hide();

        var btnQuit = new Button
        {
            Text = "Çıkış",
            Left = 360, Top = 112, Width = 160, Height = 44,
            Font = new Font("Segoe UI", 10f)
        };
        btnQuit.Click += (s, e) =>
        {
            if (_tray != null) _tray.Visible = false;
            Application.Exit();
        };

        var info = new Label
        {
            Text = "Pencereyi kapatsan bile sunucu arka planda çalışmaya devam eder.\n" +
                   "Sağ alttaki simgeden tekrar açabilir veya çıkabilirsin.",
            Left = 20, Top = 175, Width = 500, Height = 60,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 9f)
        };

        Controls.Add(title);
        Controls.Add(urlBox);
        Controls.Add(btnCopy);
        Controls.Add(btnHide);
        Controls.Add(btnQuit);
        Controls.Add(info);

        // ---- Tray ikonu ----
        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem("UzakFare çalışıyor") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        foreach (var u in urls)
        {
            var localUrl = u;
            var item = new ToolStripMenuItem("Kopyala: " + localUrl);
            item.Click += (s, e) =>
            {
                try { Clipboard.SetText(localUrl); } catch { }
                if (_tray != null) _tray.ShowBalloonTip(2000, "Kopyalandı", localUrl, ToolTipIcon.Info);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pencereyi Göster", null, (s, e) => ShowForm());
        menu.Items.Add("Çıkış", null, (s, e) =>
        {
            if (_tray != null) _tray.Visible = false;
            Application.Exit();
        });

        _tray = new NotifyIcon
        {
            // Icon = SystemIcons.Application,
            Icon = Program.AppIcon,
            Visible = true,
            Text = "UzakFare (sağ tıkla)",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (s, e) => ShowForm();

        // İlk açılışta URL'yi balloon olarak da göster
        _tray.ShowBalloonTip(4000, "UzakFare hazır",
            urls.Length > 0 ? urls[0] : "Sunucu çalışıyor", ToolTipIcon.Info);

        Resize += (s, e) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
        };

        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(2000, "UzakFare",
                    "Arka planda çalışıyor. Çıkmak için sağ alttaki simgeye sağ tıkla.",
                    ToolTipIcon.Info);
            }
        };
    }

    void ShowForm()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}