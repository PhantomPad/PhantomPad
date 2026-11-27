/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// PhantomPad: mando de Xbox (360, One, Series y compatibles XInput) como mando de entrada.
    /// Se lee por la interfaz XUSB de Windows (la misma que usa XInput por dentro), asi se
    /// obtienen los dos gatillos por separado y el boton Xbox (Guide). El HidDevice "IG_" del
    /// mando solo sirve de ancla para identificarlo (ruta, conexion, ocultarlo).
    /// </summary>
    public class XboxDevice : DS4Device
    {
        // Interfaz XUSB (GUID_DEVINTERFACE_XUSB) y sus IOCTL
        private static readonly Guid XUSB_INTERFACE = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
        private const uint IOCTL_XUSB_GET_STATE = 0x8000E00C;
        private const uint IOCTL_XUSB_SET_STATE = 0x8000A010;
        private const uint IOCTL_XUSB_GET_BATTERY = 0x8000E018;
        private const int STATE_SIZE = 29;

        // Botones XInput
        private const ushort DPAD_UP = 0x0001, DPAD_DOWN = 0x0002, DPAD_LEFT = 0x0004, DPAD_RIGHT = 0x0008;
        private const ushort START = 0x0010, BACK = 0x0020, LTHUMB = 0x0040, RTHUMB = 0x0080;
        private const ushort LSHOULDER = 0x0100, RSHOULDER = 0x0200, GUIDE = 0x0400;
        private const ushort BTN_A = 0x1000, BTN_B = 0x2000, BTN_X = 0x4000, BTN_Y = 0x8000;

        /// <summary>
        /// Solo para pruebas sin mando real: id de Windows (USB\VID_045E&PID_028E\NN) de un mando
        /// Xbox virtual que se acepta como si fuera fisico (variable de entorno PHANTOMPAD_TEST_XBOX).
        /// </summary>
        public static readonly string TestInstance = Environment.GetEnvironmentVariable("PHANTOMPAD_TEST_XBOX") ?? "";

        private readonly string xusbPath;
        private IntPtr xusbHandle = new IntPtr(-1);
        private byte lastLarge, lastSmall;
        private bool rumbleSent;
        private readonly Stopwatch batterySw = new Stopwatch();

        /// <summary>Dispositivo XUSB del mando (para ocultarlo tambien a XInput).</summary>
        public string XusbInstanceId { get; }

        public override event ReportHandler<EventArgs> Report = null;
        public override event EventHandler BatteryChanged;
        public override event EventHandler ChargingChanged;

        public XboxDevice(HidDevice hidDevice, string disName, string xusbPath, string xusbInstanceId) :
            base(hidDevice, disName, VidPidFeatureSet.DefaultDS4)
        {
            this.xusbPath = xusbPath;
            XusbInstanceId = xusbInstanceId;
            synced = true;
        }

        public override void PostInit()
        {
            conType = DetermineConnectionType(hDevice);
            Mac = hDevice.GenerateFakeHwSerial();
            deviceType = InputDeviceType.Xbox;
            gyroMouseSensSettings = new GyroMouseSens();
            inputReport = new byte[64];
            outputReport = new byte[64];
            warnInterval = WARN_INTERVAL_USB;
            battery = 100;
            charging = conType == ConnectionType.USB;

            xusbHandle = CreateFile(xusbPath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        /// <summary>Bluetooth si en la cadena de padres aparece el bus Bluetooth.</summary>
        public static ConnectionType DetermineConnectionType(HidDevice hidDevice)
        {
            try
            {
                string id = Global.GetInstanceIdFromDevicePath(hidDevice.DevicePath);
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(id); i++)
                {
                    string up = id.ToUpperInvariant();
                    if (up.StartsWith("BTHENUM") || up.StartsWith("BTHLEDEVICE") || up.Contains("{00001124-") || up.Contains("{00001812-"))
                        return ConnectionType.BT;
                    id = Global.GetStringDeviceProperty(id, NativeMethods.DEVPKEY_Device_Parent);
                }
            }
            catch { }
            return ConnectionType.USB;
        }

        /// <summary>
        /// Busca la interfaz XUSB que corresponde a un HID "IG_" (el propio dispositivo o uno de sus padres).
        /// Devuelve null si no hay.
        /// </summary>
        public static string FindXusbPath(string hidDevicePath, out string xusbInstanceId)
        {
            xusbInstanceId = null;
            Dictionary<string, string> map = EnumerateXusb();
            if (map.Count == 0) return null;

            string id = Global.GetInstanceIdFromDevicePath(hidDevicePath);
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(id); i++)
            {
                if (map.TryGetValue(id.ToUpperInvariant(), out string path))
                {
                    xusbInstanceId = id;
                    return path;
                }
                id = Global.GetStringDeviceProperty(id, NativeMethods.DEVPKEY_Device_Parent);
            }
            return null;
        }

        /// <summary>Interfaces XUSB presentes: id de instancia (mayusculas) → ruta.</summary>
        private static Dictionary<string, string> EnumerateXusb()
        {
            var result = new Dictionary<string, string>();
            Guid g = XUSB_INTERFACE;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) return result;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4);
                        string inst = Global.GetInstanceIdFromDevicePath(path);
                        if (!string.IsNullOrEmpty(inst)) result[inst.ToUpperInvariant()] = path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        private volatile bool forceRemove;

        /// <summary>Soltar el mando (interruptor "Usar mandos de Xbox" apagado): se quita como si se desconectara.</summary>
        public void ForceRemove() => forceRemove = true;

        public override bool DisconnectBT(bool callRemoval = false) => false;
        public override bool DisconnectDongle(bool remove = false) => false;
        public override bool DisconnectWireless(bool callRemoval = false) => false;
        public override bool IsAlive() => synced;

        public override void StartUpdate()
        {
            inputReportErrorCount = 0;
            if (ds4Input == null)
            {
                ds4Input = new Thread(ReadInput)
                {
                    Priority = ThreadPriority.AboveNormal,
                    Name = "Xbox Input thread: " + Mac,
                    IsBackground = true,
                };
                ds4Input.Start();
            }
        }

        private void ReadInput()
        {
            unchecked
            {
                Debouncer = SetupDebouncer();
                firstActive = DateTime.UtcNow;
                Queue<long> latencyQueue = new Queue<long>(21);
                int tempLatencyCount = 0;
                long latencySum = 0;
                long oldtime = Stopwatch.GetTimestamp();
                int failures = 0;
                byte[] request = { 0x01, 0x01, 0 }; // version 1.1 de XUSB, mando 0
                byte[] st = new byte[STATE_SIZE];
                timeoutEvent = false;
                ds4InactiveFrame = true;
                idleInput = true;

                sixAxis.ResetContinuousCalibration();
                standbySw.Start();
                batterySw.Start();
                ReadBattery();

                while (!exitInputThread)
                {
                    if (tempLatencyCount >= 20)
                    {
                        latencySum -= latencyQueue.Dequeue();
                        tempLatencyCount--;
                    }
                    latencySum += lastTimeElapsed;
                    latencyQueue.Enqueue(lastTimeElapsed);
                    tempLatencyCount++;
                    Latency = latencySum / (double)tempLatencyCount;

                    Thread.Sleep(1); // el estado se pide al momento; esto evita usar la CPU al 100 %

                    bool ok = !forceRemove && xusbHandle != new IntPtr(-1) &&
                        DeviceIoControl(xusbHandle, IOCTL_XUSB_GET_STATE, request, 3, st, STATE_SIZE, out int got, IntPtr.Zero) &&
                        got >= 23 && st[2] == 1;
                    if (!ok)
                    {
                        // Unos fallos seguidos (~0,3 s) = el mando se desconecto
                        if (!forceRemove && ++failures < 150) continue;
                        exitInputThread = true;
                        isDisconnecting = true;
                        StopOutputUpdate();
                        RunRemoval();
                        timeoutExecuted = true;
                        continue;
                    }
                    failures = 0;

                    long curtime = Stopwatch.GetTimestamp();
                    lastTimeElapsedDouble = (curtime - oldtime) * (1.0 / Stopwatch.Frequency) * 1000.0;
                    lastTimeElapsed = (long)lastTimeElapsedDouble;
                    oldtime = curtime;
                    utcNow = DateTime.UtcNow;

                    cState.PacketCounter = pState.PacketCounter + 1;
                    cState.ReportTimeStamp = utcNow;

                    ushort b = (ushort)(st[11] | (st[12] << 8));
                    cState.Cross = (b & BTN_A) != 0;
                    cState.Circle = (b & BTN_B) != 0;
                    cState.Square = (b & BTN_X) != 0;
                    cState.Triangle = (b & BTN_Y) != 0;
                    cState.L1 = (b & LSHOULDER) != 0;
                    cState.R1 = (b & RSHOULDER) != 0;
                    cState.L3 = (b & LTHUMB) != 0;
                    cState.R3 = (b & RTHUMB) != 0;
                    cState.Share = (b & BACK) != 0;
                    cState.Options = (b & START) != 0;
                    cState.PS = (b & GUIDE) != 0;
                    cState.DpadUp = (b & DPAD_UP) != 0;
                    cState.DpadDown = (b & DPAD_DOWN) != 0;
                    cState.DpadLeft = (b & DPAD_LEFT) != 0;
                    cState.DpadRight = (b & DPAD_RIGHT) != 0;

                    cState.L2 = cState.L2Raw = st[13];
                    cState.R2 = cState.R2Raw = st[14];
                    cState.L2Btn = cState.L2 > 0;
                    cState.R2Btn = cState.R2 > 0;

                    // XInput: -32768..32767 con Y hacia arriba positiva. DS4: 0..255 con Y hacia abajo.
                    cState.LX = AxisToByte(BitConverter.ToInt16(st, 15), false);
                    cState.LY = AxisToByte(BitConverter.ToInt16(st, 17), true);
                    cState.RX = AxisToByte(BitConverter.ToInt16(st, 19), false);
                    cState.RY = AxisToByte(BitConverter.ToInt16(st, 21), true);

                    // Bateria: cada 5 segundos
                    if (batterySw.ElapsedMilliseconds >= 5000)
                    {
                        batterySw.Restart();
                        ReadBattery();
                    }
                    cState.Battery = (byte)battery;

                    double elapsedDeltaTime = lastTimeElapsedDouble * .001;
                    cState.elapsedTime = elapsedDeltaTime;
                    cState.totalMicroSec = pState.totalMicroSec + (uint)(elapsedDeltaTime * 1000000);
                    cState.ds4Timestamp = (ushort)(cState.totalMicroSec / 16 % ushort.MaxValue);

                    if (idleTimeout == 0)
                    {
                        lastActive = utcNow;
                    }
                    else
                    {
                        idleInput = isDS4Idle();
                        if (!idleInput) lastActive = utcNow;
                    }

                    if (fireReport)
                    {
                        Report?.Invoke(this, EventArgs.Empty);
                    }

                    SendRumble();
                    currentHap.dirty = false;

                    cState.CopyTo(pState);

                    if (hasInputEvts)
                    {
                        lock (eventQueueLock)
                        {
                            for (int actInd = 0, actLen = eventQueue.Count; actInd < actLen; actInd++)
                            {
                                eventQueue.Dequeue().Invoke();
                            }
                            hasInputEvts = false;
                        }
                    }
                }
            }

            timeoutExecuted = true;
        }

        private static byte AxisToByte(short value, bool invert)
        {
            int v = (value + 32768) >> 8; // 0..255
            return (byte)(invert ? 255 - v : v);
        }

        /// <summary>Vibracion del juego → motores del mando (solo cuando cambia).</summary>
        private void SendRumble()
        {
            MergeStates();
            byte large = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
            byte small = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
            if (rumbleSent && large == lastLarge && small == lastSmall) return;
            if (WriteRumble(large, small))
            {
                lastLarge = large;
                lastSmall = small;
                rumbleSent = true;
            }
        }

        private bool WriteRumble(byte large, byte small)
        {
            if (xusbHandle == new IntPtr(-1)) return false;
            // mando 0, sin cambiar la luz, motor grande, motor pequeno, 0x02 = vibracion
            byte[] set = { 0, 0, large, small, 0x02 };
            return DeviceIoControl(xusbHandle, IOCTL_XUSB_SET_STATE, set, set.Length, null, 0, out _, IntPtr.Zero);
        }

        /// <summary>Bateria de los mandos inalambricos (por cable = 100 % cargando).</summary>
        private void ReadBattery()
        {
            if (xusbHandle == new IntPtr(-1)) return;
            byte[] req = { 0x02, 0x01, 0, 0 }; // version 1.2, mando 0, tipo mando
            byte[] res = new byte[2];
            if (!DeviceIoControl(xusbHandle, IOCTL_XUSB_GET_BATTERY, req, req.Length, res, res.Length, out int got, IntPtr.Zero) || got < 2)
                return;

            // Tipo: 0 desconectado, 1 por cable, 2 alcalinas, 3 recargable. Nivel: 0 vacio .. 3 lleno
            bool wired = res[0] == 1 || res[0] == 0;
            int level = wired ? 100 : res[1] switch { 0 => 5, 1 => 30, 2 => 70, _ => 100 };
            if (wired != charging)
            {
                charging = wired;
                ChargingChanged?.Invoke(this, EventArgs.Empty);
            }
            if (level != battery)
            {
                battery = level;
                BatteryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void StopOutputUpdate()
        {
            WriteRumble(0, 0);
            rumbleSent = false;
            if (exitInputThread && xusbHandle != new IntPtr(-1))
            {
                CloseHandle(xusbHandle);
                xusbHandle = new IntPtr(-1);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
    }
}
