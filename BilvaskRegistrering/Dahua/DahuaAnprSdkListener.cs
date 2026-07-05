using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BilvaskRegistrering.Dahua
{
    // STEP 1: dump all SDK alarm callbacks to dahua_raw.log in the app folder.
    public sealed class DahuaAnprSdkListener
    {
        public event EventHandler<string>? Debug;
        public event EventHandler<string>? PlateDetected;

        private readonly string _host;
        private readonly int _port;
        private readonly string _user;
        private readonly string _pass;

        private readonly string _logPath;

        private int _loginId = 0;
        private int _alarmHandle = 0;
        private bool _running;

        // Keep delegates alive
        private static fDisConnect? _disconnectCb;
        private static fHaveReConnect? _reconnectCb;
        private static fMessCallBack? _alarmCb;

        public DahuaAnprSdkListener(string host, int port, string username, string password)
        {
            _host = host ?? "";
            _port = port <= 0 ? 37777 : port;
            _user = username ?? "";
            _pass = password ?? "";
            _logPath = Path.Combine(AppContext.BaseDirectory, "dahua_raw.log");
        }

        public bool IsRunning => _running;

        public void Start()
        {
            if (_running) return;
            _running = true;

            Log("=== START Dahua SDK listener ===");

            _disconnectCb = new fDisConnect(OnDisconnect);
            _reconnectCb = new fHaveReConnect(OnReconnect);
            _alarmCb = new fMessCallBack(OnAlarm);

            if (!CLIENT_Init(_disconnectCb, IntPtr.Zero))
            {
                Log("CLIENT_Init FAILED");
                _running = false;
                return;
            }
            Log("CLIENT_Init OK");

            CLIENT_SetAutoReconnect(_reconnectCb, IntPtr.Zero);

            NET_DEVICEINFO_Ex devInfo = new NET_DEVICEINFO_Ex();
            int error = 0;
            _loginId = CLIENT_LoginEx2(_host, (ushort)_port, _user, _pass, 0, IntPtr.Zero, ref devInfo, ref error);
            if (_loginId == 0)
            {
                // `error` here is the SDK's login result code.
                // Additionally we fetch CLIENT_GetLastError() which often contains a more precise reason.
                var lastErr = CLIENT_GetLastError();
                var msg = $"LOGIN FAILED. loginError={error} (0x{error:X8}), lastError={lastErr} (0x{lastErr:X8}). " +
                          $"Host={_host}, Port={_port}, User={_user}. " +
                          "Hint: For Dahua NetSDK the default TCP port is usually 37777 (NOT HTTP 80).";

                Log(msg);
                try { Debug?.Invoke(this, msg); } catch { }

                _running = false;
                return;
            }
            Log("LOGIN OK. ChannelCount=" + devInfo.byChanNum);

            if (!CLIENT_SetDVRMessCallBack(_alarmCb, IntPtr.Zero))
                Log("CLIENT_SetDVRMessCallBack FAILED");
            else
                Log("CLIENT_SetDVRMessCallBack OK");

            _alarmHandle = CLIENT_StartListenEx(_loginId);
            if (_alarmHandle == 0)
            {
                var lastErr = CLIENT_GetLastError();
                Log($"CLIENT_StartListenEx FAILED. lastError={lastErr} (0x{lastErr:X8}).");
                Log("Tips: 1) In the camera web UI, enable IVS/ANPR events and event push. " +
                    "2) Some Dahua models require configuring an 'Alarm Server' (your PC IP + port) under Network. " +
                    "3) Ensure Windows Firewall allows inbound connections from the camera.");
            }
            else
            {
                Log("CLIENT_StartListenEx OK. Waiting for events...");
                RaiseDebug("ANPR (SDK): lytter etter hendelser...");
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            Log("=== STOP Dahua SDK listener ===");

            try
            {
                if (_alarmHandle != 0)
                {
                    CLIENT_StopListen(_loginId);
                    _alarmHandle = 0;
                    Log("CLIENT_StopListen OK");
                }
            }
            catch { }

            try
            {
                if (_loginId != 0)
                {
                    CLIENT_Logout(_loginId);
                    _loginId = 0;
                    Log("CLIENT_Logout OK");
                }
            }
            catch { }

            try
            {
                CLIENT_Cleanup();
                Log("CLIENT_Cleanup OK");
            }
            catch { }
        }

        private void OnDisconnect(int lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            Log("DISCONNECT: " + pchDVRIP + ":" + nDVRPort);
            RaiseDebug("ANPR (SDK): frakoblet");
        }

        private void OnReconnect(int lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            Log("RECONNECT: " + pchDVRIP + ":" + nDVRPort);
            RaiseDebug("ANPR (SDK): tilkoblet igjen");
        }

        private bool OnAlarm(int lCommand, int lLoginID, IntPtr pStuEvent, uint dwBufLen, string strDeviceIP, int nDevicePort, IntPtr dwUser)
        {
            try
            {
                Log($"ALARM cmd=0x{lCommand:X} ({lCommand}) ip={strDeviceIP}:{nDevicePort} len={dwBufLen}");

                if (pStuEvent != IntPtr.Zero && dwBufLen > 0)
                {
                    int n = (int)Math.Min(dwBufLen, 2048);
                    byte[] buf = new byte[n];
                    Marshal.Copy(pStuEvent, buf, 0, n);
                    Log("RAW HEX=" + BitConverter.ToString(buf));
                }

                string plate = TryFindPlateInMemory(pStuEvent, dwBufLen);
                if (!string.IsNullOrWhiteSpace(plate))
                {
                    Log("PLATE(best-effort)=" + plate);
                    RaisePlate(plate);
                }

                RaiseDebug($"SDK event cmd=0x{lCommand:X} len={dwBufLen}");
                return true;
            }
            catch (Exception ex)
            {
                Log("OnAlarm exception: " + ex);
                return true;
            }
        }

        private string TryFindPlateInMemory(IntPtr p, uint len)
        {
            if (p == IntPtr.Zero || len == 0) return "";
            try
            {
                int n = (int)Math.Min(len, 8192);
                byte[] buf = new byte[n];
                Marshal.Copy(p, buf, 0, n);

                var sb = new StringBuilder(n);
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b >= 32 && b <= 126) sb.Append((char)b);
                    else sb.Append(' ');
                }
                var s = sb.ToString();

                string[] markers = new[] { "PlateNumber", "PlateNo", "plateNumber", "TrafficCar" };
                foreach (var m in markers)
                {
                    int idx = s.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int start = Math.Max(0, idx - 50);
                        int end = Math.Min(s.Length, idx + 200);
                        var win = s.Substring(start, end - start).ToUpperInvariant();

                        // tolerant plate token
                        var rx = new System.Text.RegularExpressions.Regex("([A-Z0-9]{4,10})");
                        var matches = rx.Matches(win);
                        foreach (System.Text.RegularExpressions.Match mm in matches)
                        {
                            var token = mm.Groups[1].Value;
                            if (token.Contains("PLATE")) continue;
                            if (token.Contains("TRAFFIC")) continue;
                            return token;
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private void RaiseDebug(string msg)
        {
            try { Debug?.Invoke(this, msg); } catch { }
        }

        private void RaisePlate(string plate)
        {
            try { PlateDetected?.Invoke(this, plate); } catch { }
        }

        private void Log(string line)
        {
            try
            {
                var msg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine;
                File.AppendAllText(_logPath, msg, Encoding.UTF8);
                RaiseDebug(line);
            }
            catch { }
        }

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool CLIENT_Init(fDisConnect cbDisConnect, IntPtr dwUser);

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern void CLIENT_Cleanup();

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern void CLIENT_SetAutoReconnect(fHaveReConnect cbAutoConnect, IntPtr dwUser);

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int CLIENT_LoginEx2(string pchDVRIP, ushort wDVRPort, string pchUserName, string pchPassword,
            int nSpecCap, IntPtr pCapParam, ref NET_DEVICEINFO_Ex lpDeviceInfo, ref int error);

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool CLIENT_Logout(int lLoginID);

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool CLIENT_SetDVRMessCallBack(fMessCallBack cbMessage, IntPtr dwUser);

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int CLIENT_StartListenEx(int lLoginID);

        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool CLIENT_StopListen(int lLoginID);

        // Get last SDK error code (useful for diagnosing login failures etc.).
        [DllImport("dhnetsdk.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int CLIENT_GetLastError();

        // Explicit ANSI marshaling for string callback args.
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public delegate void fDisConnect(int lLoginID, [MarshalAs(UnmanagedType.LPStr)] string pchDVRIP, int nDVRPort, IntPtr dwUser);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public delegate void fHaveReConnect(int lLoginID, [MarshalAs(UnmanagedType.LPStr)] string pchDVRIP, int nDVRPort, IntPtr dwUser);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public delegate bool fMessCallBack(int lCommand, int lLoginID, IntPtr pStuEvent, uint dwBufLen, [MarshalAs(UnmanagedType.LPStr)] string strDeviceIP, int nDevicePort, IntPtr dwUser);

        [StructLayout(LayoutKind.Sequential)]
        public struct NET_DEVICEINFO_Ex
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] sSerialNumber;
            public byte byAlarmInPortNum;
            public byte byAlarmOutPortNum;
            public byte byDiskNum;
            public byte byDVRType;
            public byte byChanNum;
            public byte byLimitLoginTime;
            public byte byLeftLogTimes;
            public byte byLockLeftTime;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
            public byte[] byReserved;
        }
    }
}
