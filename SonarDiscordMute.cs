// Sonar Discord Mute
// SteelSeries Sonar - Microphone cihazinda Discord'un sesini surekli kapali tutar
// (ses duzeyi karistiricisinda elle kapatmanin otomatik hali).
//
// Kaynak harcamamak icin surekli tarama yapmaz: Windows ses olaylarini dinler
// (yeni oturum, ses acilmasi, cihaz takilip cikmasi). Yedek olarak 10 sn'de bir kontrol eder.
//
// Derleme: build.bat (Windows'ta yerlesik .NET Framework 4 derleyicisi yeterli)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Sonar Discord Mute")]
[assembly: System.Reflection.AssemblyProduct("Sonar Discord Mute")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace SonarDiscordMute
{
    // =====================================================================
    // Windows Core Audio COM tanimlari
    // =====================================================================

    public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }
    public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    // PROPVARIANT (x64'te 24 bayt). Sadece VT_LPWSTR okunuyor.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IMMNotificationClient client);
        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged(IntPtr deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded(IntPtr deviceId);
        [PreserveSig] int OnDeviceRemoved(IntPtr deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, IntPtr defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged(IntPtr deviceId, PropertyKey key);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, [MarshalAs(UnmanagedType.IUnknown)] out object control);
        void GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        void RegisterSessionNotification(IAudioSessionNotification notification);
        void UnregisterSessionNotification(IAudioSessionNotification notification);
        void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        void UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    // IAudioSessionControl + IAudioSessionControl2 tek vtable olarak
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        void GetState(out AudioSessionState state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        void GetGroupingParam(out Guid groupingParam);
        void SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        void RegisterAudioSessionNotification(IAudioSessionEvents client);
        void UnregisterAudioSessionNotification(IAudioSessionEvents client);
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid eventContext);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged(IntPtr newDisplayName, IntPtr eventContext);
        [PreserveSig] int OnIconPathChanged(IntPtr newIconPath, IntPtr eventContext);
        [PreserveSig] int OnSimpleVolumeChanged(float newVolume, [MarshalAs(UnmanagedType.Bool)] bool newMute, IntPtr eventContext);
        [PreserveSig] int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumes, uint changedChannel, IntPtr eventContext);
        [PreserveSig] int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext);
        [PreserveSig] int OnStateChanged(AudioSessionState newState);
        [PreserveSig] int OnSessionDisconnected(int disconnectReason);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IntPtr newSession);
    }

    // =====================================================================
    // Yardimcilar
    // =====================================================================

    public sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    public static class AudioDevices
    {
        public const uint DeviceStateActive = 1;
        static readonly PropertyKey FriendlyNameKey = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant value);

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        public static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                try { Marshal.ReleaseComObject(comObject); } catch { }
            }
        }

        public static string GetId(IMMDevice device)
        {
            string id;
            device.GetId(out id);
            return id ?? "";
        }

        public static string GetName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                device.OpenPropertyStore(0 /* STGM_READ */, out store);
                PropertyKey key = FriendlyNameKey;
                PropVariant value;
                store.GetValue(ref key, out value);
                try { return value.VarType == 31 /* VT_LPWSTR */ ? (Marshal.PtrToStringUni(value.Pointer) ?? "") : ""; }
                finally { PropVariantClear(ref value); }
            }
            catch { return ""; }
            finally { Release(store); }
        }

        public static List<AudioDeviceInfo> ListActiveOutputs()
        {
            var result = new List<AudioDeviceInfo>();
            IMMDeviceEnumerator enumerator = CreateEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out collection);
                uint count;
                collection.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        collection.Item(i, out device);
                        var info = new AudioDeviceInfo();
                        info.Id = GetId(device);
                        info.Name = GetName(device);
                        result.Add(info);
                    }
                    catch { }
                    finally { Release(device); }
                }
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
            return result;
        }

        // Oturum kimligi bicimi: "{cihaz}|\Device\HarddiskVolumeN\...\Discord.exe%b{...}"
        // Sadece exe dosya adini dondurur (klasor/kullanici adi eslesmesin diye).
        public static string ExeFromSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            int bar = sessionId.IndexOf('|');
            if (bar < 0) return null;
            int end = sessionId.IndexOf("%b", bar + 1, StringComparison.Ordinal);
            string path = end > bar ? sessionId.Substring(bar + 1, end - bar - 1) : sessionId.Substring(bar + 1);
            int slash = path.LastIndexOf('\\');
            if (slash < 0 || slash == path.Length - 1) return null;
            string exe = path.Substring(slash + 1);
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
        }

        // 2 = Sonar Microphone, 1 = baska bir Sonar cihazi, 0 = Sonar degil
        public static int SonarScore(string name)
        {
            if (string.IsNullOrEmpty(name) || !Contains(name, "sonar")) return 0;
            return (Contains(name, "microphone") || Contains(name, "mikrofon")) ? 2 : 1;
        }

        static bool Contains(string text, string part)
        {
            return text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    static class ProcessInfo
    {
        const uint QueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        public static string GetExeName(uint processId)
        {
            string path = GetExePath(processId);
            return path == null ? null : Path.GetFileName(path);
        }

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

        // Sadece Discord surecleri icin kullanilir (rolunu anlamak icin); oyunlara dokunulmaz.
        public static string GetCommandLine(uint processId)
        {
            IntPtr handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                int needed;
                NtQueryInformationProcess(handle, 60 /* ProcessCommandLineInformation */, IntPtr.Zero, 0, out needed);
                if (needed <= 0 || needed > 1 << 20) return null;
                IntPtr buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(handle, 60, buffer, needed, out needed) != 0) return null;
                    int length = (ushort)Marshal.ReadInt16(buffer); // UNICODE_STRING.Length (bayt)
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(handle); }
        }

        public static string GetExePath(uint processId)
        {
            IntPtr handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }
    }

    // Discord sesleri farkli ic sureclerden calar:
    //  - renderer: ses motoru (kanaldaki konusmalar)
    //  - Chromium ses servisi: arayuz sesleri (giris/cikis, bildirim)
    public enum DiscordRole { Unknown, Main, VoiceEngine, UiSounds, Other }

    public static class DiscordRoles
    {
        public static DiscordRole Classify(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return DiscordRole.Unknown;
            if (commandLine.IndexOf("--type=", StringComparison.Ordinal) < 0) return DiscordRole.Main;
            if (commandLine.IndexOf("audio.mojom.AudioService", StringComparison.Ordinal) >= 0) return DiscordRole.UiSounds;
            if (commandLine.IndexOf("--type=renderer", StringComparison.Ordinal) >= 0) return DiscordRole.VoiceEngine;
            return DiscordRole.Other;
        }

        public static DiscordRole OfProcess(uint processId)
        {
            return processId == 0 ? DiscordRole.Unknown : Classify(ProcessInfo.GetCommandLine(processId));
        }

        public static string Label(DiscordRole role)
        {
            switch (role)
            {
                case DiscordRole.Main: return "ana süreç";
                case DiscordRole.VoiceEngine: return "ses motoru (konuşmalar)";
                case DiscordRole.UiSounds: return "arayüz sesleri (giriş/çıkış, bildirim)";
                case DiscordRole.Other: return "diğer";
                default: return "bilinmiyor";
            }
        }
    }

    // "Tani bilgisini kopyala": hangi Discord oturumu hangi cihazda, ne durumda
    public static class Diagnostics
    {
        public static string BuildReport(AppConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Sonar Discord Mute " + typeof(Diagnostics).Assembly.GetName().Version.ToString(3) + " – tanı bilgisi");
            sb.AppendLine("Seçili cihaz: " + config.DeviceName);
            sb.AppendLine("Giriş/çıkış seslerini koru: " + (config.KeepUiSounds ? "açık" : "kapalı"));
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = AudioDevices.CreateEnumerator();
                sb.AppendLine("Windows varsayılan çıkış: " + DefaultName(enumerator, ERole.Multimedia));
                sb.AppendLine("Windows varsayılan iletişim çıkışı: " + DefaultName(enumerator, ERole.Communications));
                sb.AppendLine("Discord ses oturumları:");
                int count = 0;
                foreach (AudioDeviceInfo device in AudioDevices.ListActiveOutputs())
                    count += AppendDeviceSessions(sb, enumerator, device, config.ProcessMatch);
                if (count == 0) sb.AppendLine("  (yok – Discord şu an hiçbir cihazda ses açmamış)");
            }
            catch (Exception ex) { sb.AppendLine("Hata: " + ex.Message); }
            finally { AudioDevices.Release(enumerator); }
            return sb.ToString();
        }

        static string DefaultName(IMMDeviceEnumerator enumerator, ERole role)
        {
            IMMDevice device = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out device);
                return AudioDevices.GetName(device);
            }
            catch { return "-"; }
            finally { AudioDevices.Release(device); }
        }

        static int AppendDeviceSessions(StringBuilder sb, IMMDeviceEnumerator enumerator, AudioDeviceInfo info, string match)
        {
            int count = 0;
            IMMDevice device = null;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                enumerator.GetDevice(info.Id, out device);
                Guid iid = typeof(IAudioSessionManager2).GUID;
                object instance;
                device.Activate(ref iid, 23, IntPtr.Zero, out instance);
                manager = (IAudioSessionManager2)instance;
                manager.GetSessionEnumerator(out sessions);
                int n;
                sessions.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    object raw = null;
                    try
                    {
                        sessions.GetSession(i, out raw);
                        var control = (IAudioSessionControl2)raw;
                        string sessionId;
                        control.GetSessionIdentifier(out sessionId);
                        string exe = AudioDevices.ExeFromSessionId(sessionId);
                        if (exe == null || exe.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        uint pid;
                        control.GetProcessId(out pid);
                        AudioSessionState state;
                        control.GetState(out state);
                        bool muted;
                        ((ISimpleAudioVolume)raw).GetMute(out muted);
                        sb.AppendLine("  [" + info.Name + "] " + DiscordRoles.Label(DiscordRoles.OfProcess(pid)) +
                                      " | pid " + pid + " | " + (state == AudioSessionState.Active ? "çalıyor" : "beklemede") +
                                      " | " + (muted ? "susturulmuş" : "açık"));
                        count++;
                    }
                    catch { }
                    finally { AudioDevices.Release(raw); }
                }
            }
            catch { }
            finally
            {
                AudioDevices.Release(sessions);
                AudioDevices.Release(manager);
                AudioDevices.Release(device);
            }
            return count;
        }
    }

    static class Memory
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        // Bosta beklerken RAM'de gereksiz sayfa tutmamak icin
        public static void Trim()
        {
            try
            {
                GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }
    }

    // =====================================================================
    // Susturma motoru (tek bir arka plan thread'inde calisir)
    // =====================================================================

    public sealed class EngineStatus
    {
        public bool DeviceFound;
        public string DeviceName;
        public int Sessions;
        public int Muted;
        public int Kept;   // "giris/cikis seslerini koru" ile acik birakilan oturumlar
        public bool Paused;
        public string Error;

        internal string Key()
        {
            return string.Concat(new string[] {
                DeviceFound ? "1" : "0", "|", DeviceName, "|", Sessions.ToString(), "|",
                Muted.ToString(), "|", Kept.ToString(), "|", Paused ? "1" : "0", "|", Error });
        }
    }

    public sealed class MuteEngine
    {
        const uint ClsctxAll = 23;
        const int SafetyIntervalMs = 10000;
        static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly object targetLock = new object();
        readonly string processMatch;
        Guid ownContext = Guid.NewGuid(); // kendi yaptigimiz degisiklikleri tanimak icin
        string targetId, targetName;
        volatile bool paused, stopping, releasePending, retargetPending, devicesChanged, keepUiSounds;
        Thread worker;
        readonly Dictionary<uint, DiscordRole> roleCache = new Dictionary<uint, DiscordRole>();

        // Asagidakileri sadece motor thread'i kullanir
        IMMDeviceEnumerator enumerator;
        EndpointEvents endpointEvents;
        IAudioSessionManager2 manager;
        SessionCreatedEvents sessionCreatedEvents;
        string attachedId, attachedName;
        readonly Dictionary<string, TrackedSession> tracked = new Dictionary<string, TrackedSession>(StringComparer.Ordinal);
        int lastSessions, lastMuted, lastKept;
        string lastError, lastStatusKey;

        public event Action<EngineStatus> StatusChanged;

        public MuteEngine(string deviceId, string deviceName, string processMatch)
        {
            targetId = deviceId ?? "";
            targetName = deviceName ?? "";
            this.processMatch = string.IsNullOrEmpty(processMatch) ? "discord" : processMatch;
        }

        public bool Paused { get { return paused; } }

        public void Start()
        {
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "SonarDiscordMute";
            worker.SetApartmentState(ApartmentState.MTA); // ses oturumu bildirimleri MTA ister
            worker.Start();
        }

        // true: Discord'un arayuz seslerini (giris/cikis, bildirim) susturma
        public void SetKeepUiSounds(bool value)
        {
            keepUiSounds = value;
            wake.Set();
        }

        public void SetPaused(bool value)
        {
            paused = value;
            if (value) releasePending = true;
            wake.Set();
        }

        public void SetTarget(string deviceId, string deviceName)
        {
            lock (targetLock)
            {
                targetId = deviceId ?? "";
                targetName = deviceName ?? "";
            }
            releasePending = true;
            retargetPending = true;
            wake.Set();
        }

        // Discord'un sesini geri acar ve motoru durdurur
        public void Stop(int timeoutMs)
        {
            Stop(timeoutMs, true);
        }

        // restoreDiscord = false: guncelleme sirasinda Discord susturulmus kalsin
        public void Stop(int timeoutMs, bool restoreDiscord)
        {
            if (restoreDiscord) releasePending = true;
            stopping = true;
            wake.Set();
            if (worker != null) worker.Join(timeoutMs);
        }

        internal void Wake() { wake.Set(); }
        internal void DevicesChanged() { devicesChanged = true; wake.Set(); }
        internal bool Enforcing { get { return !paused && !stopping; } }

        internal bool IsOwnContext(IntPtr context)
        {
            if (context == IntPtr.Zero) return false;
            try { return (Guid)Marshal.PtrToStructure(context, typeof(Guid)) == ownContext; }
            catch { return false; }
        }

        void Run()
        {
            try
            {
                enumerator = AudioDevices.CreateEnumerator();
                endpointEvents = new EndpointEvents(this);
                enumerator.RegisterEndpointNotificationCallback(endpointEvents);
            }
            catch (Exception ex)
            {
                lastError = "Windows ses sistemine bağlanılamadı: " + ex.Message;
                Report();
                return;
            }

            while (true)
            {
                if (releasePending) { releasePending = false; Guarded(ReleaseMutes); }
                if (retargetPending) { retargetPending = false; Detach(); }
                if (stopping) break;
                if (devicesChanged) { devicesChanged = false; Guarded(CheckAttachedDevice); }
                if (!paused) Guarded(Enforce);
                Report();
                wake.WaitOne(SafetyIntervalMs);
            }

            if (releasePending) Guarded(ReleaseMutes);
            Detach();
            try { enumerator.UnregisterEndpointNotificationCallback(endpointEvents); } catch { }
            AudioDevices.Release(enumerator);
            enumerator = null;
        }

        void Guarded(Action action)
        {
            try
            {
                action();
                lastError = null;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Detach();
            }
        }

        void Enforce()
        {
            if (manager == null) Attach();
            if (manager != null) Scan(true);
        }

        void ReleaseMutes()
        {
            if (manager != null) Scan(false);
        }

        void CheckAttachedDevice()
        {
            if (manager != null && !IsActive(attachedId)) Detach();
        }

        bool IsActive(string id)
        {
            IMMDevice device = null;
            try
            {
                enumerator.GetDevice(id, out device);
                uint state;
                device.GetState(out state);
                return state == AudioDevices.DeviceStateActive;
            }
            catch { return false; }
            finally { AudioDevices.Release(device); }
        }

        void Attach()
        {
            string id, name;
            lock (targetLock) { id = targetId; name = targetName; }

            IMMDevice device = FindDevice(id, name);
            if (device == null) return;
            try
            {
                Guid iid = IID_IAudioSessionManager2;
                object instance;
                device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out instance);
                manager = (IAudioSessionManager2)instance;
                attachedId = AudioDevices.GetId(device);
                attachedName = AudioDevices.GetName(device);

                // Yeni oturum bildirimlerinin gelmesi icin once bir kez liste alinmali
                IAudioSessionEnumerator warmup;
                manager.GetSessionEnumerator(out warmup);
                AudioDevices.Release(warmup);

                sessionCreatedEvents = new SessionCreatedEvents(this);
                manager.RegisterSessionNotification(sessionCreatedEvents);
            }
            finally { AudioDevices.Release(device); }
        }

        IMMDevice FindDevice(string id, string name)
        {
            if (id.Length > 0)
            {
                IMMDevice device = null;
                try
                {
                    enumerator.GetDevice(id, out device);
                    uint state;
                    device.GetState(out state);
                    if (state == AudioDevices.DeviceStateActive) return device;
                }
                catch { }
                AudioDevices.Release(device);
            }

            // Kimlik degistiyse (or. Sonar yeniden kuruldu) ayni isimli cihazi ara
            if (name.Length > 0)
            {
                IMMDeviceCollection collection = null;
                try
                {
                    enumerator.EnumAudioEndpoints(EDataFlow.Render, AudioDevices.DeviceStateActive, out collection);
                    uint count;
                    collection.GetCount(out count);
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice device;
                        collection.Item(i, out device);
                        if (string.Equals(AudioDevices.GetName(device), name, StringComparison.OrdinalIgnoreCase)) return device;
                        AudioDevices.Release(device);
                    }
                }
                catch { }
                finally { AudioDevices.Release(collection); }
            }
            return null;
        }

        // enforce = true: Discord'u sustur ve izle. false: Discord'un sesini geri ac.
        void Scan(bool enforce)
        {
            IAudioSessionEnumerator sessions = null;
            int found = 0, muted = 0, kept = 0;
            bool keepUi = keepUiSounds;
            try
            {
                manager.GetSessionEnumerator(out sessions);
                int count;
                sessions.GetCount(out count);
                if (enforce) foreach (TrackedSession t in tracked.Values) t.Seen = false;

                for (int i = 0; i < count; i++)
                {
                    object raw = null;
                    bool keep = false;
                    try
                    {
                        sessions.GetSession(i, out raw);
                        var control = (IAudioSessionControl2)raw;
                        AudioSessionState state;
                        control.GetState(out state);
                        if (state == AudioSessionState.Expired || !IsTargetProcess(control)) continue;

                        found++;
                        bool spare = enforce && keepUi && RoleOf(control) == DiscordRole.UiSounds;
                        bool wantMuted = enforce && !spare;
                        var volume = (ISimpleAudioVolume)raw;
                        bool isMuted;
                        volume.GetMute(out isMuted);
                        if (wantMuted && !isMuted) { volume.SetMute(true, ref ownContext); isMuted = true; }
                        else if (!wantMuted && isMuted) { volume.SetMute(false, ref ownContext); isMuted = false; }
                        if (spare) kept++;
                        else if (isMuted) muted++;

                        if (enforce) keep = Track(control);
                    }
                    catch { } // oturum tam o anda kapandiysa
                    finally { if (!keep) AudioDevices.Release(raw); }
                }
            }
            finally { AudioDevices.Release(sessions); }

            if (enforce)
            {
                var gone = new List<string>();
                foreach (KeyValuePair<string, TrackedSession> pair in tracked)
                    if (!pair.Value.Seen) gone.Add(pair.Key);
                foreach (string key in gone)
                {
                    Untrack(tracked[key]);
                    tracked.Remove(key);
                }
            }
            lastSessions = found;
            lastMuted = muted;
            lastKept = kept;
        }

        DiscordRole RoleOf(IAudioSessionControl2 control)
        {
            uint processId;
            if (control.GetProcessId(out processId) < 0 || processId == 0) return DiscordRole.Unknown;
            DiscordRole role;
            if (!roleCache.TryGetValue(processId, out role))
            {
                role = DiscordRoles.OfProcess(processId);
                if (roleCache.Count > 64) roleCache.Clear();
                roleCache[processId] = role;
            }
            return role;
        }

        // Yeni oturumsa ses olaylarina abone olur; birisi sesi acarsa aninda haber alinir.
        bool Track(IAudioSessionControl2 control)
        {
            string key;
            control.GetSessionInstanceIdentifier(out key);
            TrackedSession existing;
            if (tracked.TryGetValue(key, out existing))
            {
                existing.Seen = true;
                return false;
            }
            var events = new SessionEvents(this);
            control.RegisterAudioSessionNotification(events);
            tracked[key] = new TrackedSession(control, events);
            return true;
        }

        static void Untrack(TrackedSession session)
        {
            try { session.Control.UnregisterAudioSessionNotification(session.Events); } catch { }
            AudioDevices.Release(session.Control);
        }

        void Detach()
        {
            foreach (TrackedSession t in tracked.Values) Untrack(t);
            tracked.Clear();
            if (manager != null)
            {
                try { if (sessionCreatedEvents != null) manager.UnregisterSessionNotification(sessionCreatedEvents); } catch { }
                AudioDevices.Release(manager);
            }
            manager = null;
            sessionCreatedEvents = null;
            attachedId = null;
            attachedName = null;
            lastSessions = 0;
            lastMuted = 0;
            lastKept = 0;
            roleCache.Clear();
        }

        bool IsTargetProcess(IAudioSessionControl2 control)
        {
            // Exe adi Windows'un oturum kimliginden okunur; oyun dahil hicbir surece erisim acilmaz.
            string sessionId = null;
            try { control.GetSessionIdentifier(out sessionId); } catch { }
            string exe = AudioDevices.ExeFromSessionId(sessionId);

            // Kimlik beklenmedik bicimdeyse (nadir) Gorev Yoneticisi'nin kullandigi sinirli sorguya dus
            if (exe == null)
            {
                uint processId;
                if (control.GetProcessId(out processId) >= 0 && processId != 0) exe = ProcessInfo.GetExeName(processId);
            }
            return exe != null && exe.IndexOf(processMatch, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Report()
        {
            var status = new EngineStatus();
            status.DeviceFound = manager != null;
            lock (targetLock) status.DeviceName = attachedName ?? targetName;
            status.Sessions = lastSessions;
            status.Muted = lastMuted;
            status.Kept = lastKept;
            status.Paused = paused;
            status.Error = lastError;

            string key = status.Key();
            if (key == lastStatusKey) return;
            lastStatusKey = key;
            Action<EngineStatus> handler = StatusChanged;
            if (handler != null)
            {
                try { handler(status); } catch { }
            }
        }
    }

    sealed class TrackedSession
    {
        public readonly IAudioSessionControl2 Control;
        public readonly SessionEvents Events;
        public bool Seen = true;
        public TrackedSession(IAudioSessionControl2 control, SessionEvents events) { Control = control; Events = events; }
    }

    // Windows'tan gelen bildirimler: sadece motoru uyandirir, asil isi motor thread'i yapar.

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class EndpointEvents : IMMNotificationClient
    {
        readonly MuteEngine engine;
        internal EndpointEvents(MuteEngine engine) { this.engine = engine; }
        public int OnDeviceStateChanged(IntPtr deviceId, uint newState) { engine.DevicesChanged(); return 0; }
        public int OnDeviceAdded(IntPtr deviceId) { engine.DevicesChanged(); return 0; }
        public int OnDeviceRemoved(IntPtr deviceId) { engine.DevicesChanged(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, IntPtr defaultDeviceId) { return 0; }
        public int OnPropertyValueChanged(IntPtr deviceId, PropertyKey key) { return 0; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class SessionCreatedEvents : IAudioSessionNotification
    {
        readonly MuteEngine engine;
        internal SessionCreatedEvents(MuteEngine engine) { this.engine = engine; }
        public int OnSessionCreated(IntPtr newSession) { engine.Wake(); return 0; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class SessionEvents : IAudioSessionEvents
    {
        readonly MuteEngine engine;
        internal SessionEvents(MuteEngine engine) { this.engine = engine; }

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext)
        {
            // Birisi (karistirici, Discord...) sesi actiysa hemen tekrar kapat
            if (!newMute && engine.Enforcing && !engine.IsOwnContext(eventContext)) engine.Wake();
            return 0;
        }

        public int OnStateChanged(AudioSessionState newState)
        {
            if (newState == AudioSessionState.Expired) engine.Wake();
            return 0;
        }

        public int OnSessionDisconnected(int disconnectReason) { engine.DevicesChanged(); return 0; }
        public int OnDisplayNameChanged(IntPtr newDisplayName, IntPtr eventContext) { return 0; }
        public int OnIconPathChanged(IntPtr newIconPath, IntPtr eventContext) { return 0; }
        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumes, uint changedChannel, IntPtr eventContext) { return 0; }
        public int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext) { return 0; }
    }

    // =====================================================================
    // Ayarlar ve Windows ile baslatma
    // =====================================================================

    public sealed class AppConfig
    {
        public string DeviceId = "";
        public string DeviceName = "";
        public string ProcessMatch = "discord";
        public bool KeepUiSounds;

        public bool HasDevice { get { return DeviceId.Length > 0 || DeviceName.Length > 0; } }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "SonarDiscordMute", "ayarlar.ini");
            }
        }

        public static AppConfig Load(string path)
        {
            var config = new AppConfig();
            try
            {
                if (!File.Exists(path)) return config;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.TrimStart().StartsWith("#")) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "cihaz_id") config.DeviceId = value;
                    else if (key == "cihaz_adi") config.DeviceName = value;
                    else if (key == "uygulama" && value.Length > 0) config.ProcessMatch = value;
                    else if (key == "arayuz_seslerini_koru") config.KeepUiSounds = value == "1";
                }
            }
            catch { }
            return config;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new string[] {
                "# Sonar Discord Mute ayarlari",
                "cihaz_id=" + DeviceId,
                "cihaz_adi=" + DeviceName,
                "uygulama=" + ProcessMatch,
                "arayuz_seslerini_koru=" + (KeepUiSounds ? "1" : "0")
            }, new UTF8Encoding(false));
        }
    }

    public static class StartupEntry
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "SonarDiscordMute";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Set(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(ValueName, false);
            }
        }

        public static void TrySet(bool enable)
        {
            try { Set(enable); } catch { }
        }
    }

    // =====================================================================
    // Arayuz: simge, ayar penceresi
    // =====================================================================

    public static class Icons
    {
        public static readonly Color Active = Color.FromArgb(46, 160, 67);
        public static readonly Color Paused = Color.FromArgb(130, 130, 130);
        public static readonly Color Warning = Color.FromArgb(214, 136, 20);

        static readonly Dictionary<string, Icon> cache = new Dictionary<string, Icon>();

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static Icon Get(Color color, int size)
        {
            string key = color.ToArgb().ToString() + "_" + size.ToString();
            Icon icon;
            if (!cache.TryGetValue(key, out icon))
            {
                using (Bitmap bitmap = Draw(color, size))
                {
                    IntPtr handle = bitmap.GetHicon();
                    using (Icon temp = Icon.FromHandle(handle)) icon = (Icon)temp.Clone();
                    DestroyIcon(handle);
                }
                cache[key] = icon;
            }
            return icon;
        }

        // Renkli daire + beyaz "ses kapali" isareti
        public static Bitmap Draw(Color color, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 0, 0, size, size);

                FontFamily family = null;
                try { family = new FontFamily("Segoe MDL2 Assets"); } catch (ArgumentException) { }
                if (family == null)
                {
                    using (var pen = new Pen(Color.White, Math.Max(1.5f, size / 8f)))
                        g.DrawLine(pen, size * 0.3f, size * 0.3f, size * 0.7f, size * 0.7f);
                    return bitmap;
                }
                using (family)
                using (var path = new GraphicsPath())
                {
                    path.AddString("", family, (int)FontStyle.Regular, size * 0.7f, PointF.Empty, StringFormat.GenericTypographic);
                    RectangleF bounds = path.GetBounds();
                    using (var matrix = new Matrix())
                    {
                        matrix.Translate(size / 2f - (bounds.X + bounds.Width / 2f), size / 2f - (bounds.Y + bounds.Height / 2f));
                        path.Transform(matrix);
                    }
                    g.FillPath(Brushes.White, path);
                }
            }
            return bitmap;
        }
    }

    public sealed class SetupForm : Form
    {
        readonly AppConfig config;
        readonly ListBox deviceList;
        readonly Label hintLabel;
        readonly CheckBox startupBox;
        readonly Button saveButton;
        bool anySonar;
        string loadError;

        public AudioDeviceInfo SelectedDevice { get { return deviceList.SelectedItem as AudioDeviceInfo; } }
        public bool StartWithWindows { get { return startupBox.Checked; } }

        public SetupForm(AppConfig config, bool firstRun, bool startupChecked, bool startupAvailable)
        {
            this.config = config;
            Text = firstRun ? "Sonar Discord Mute – Kurulum" : "Sonar Discord Mute – Ayarlar";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Icon = Icons.Get(Icons.Active, 32);
            ClientSize = new Size(500, 390);

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(16, 14, 16, 12);
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var title = new Label();
            title.AutoSize = true;
            title.Text = "Discord'un susturulacağı ses cihazını seç";
            title.Font = new Font(Font.FontFamily, Font.SizeInPoints + 2.5f, FontStyle.Bold);
            title.Margin = new Padding(0, 0, 0, 6);

            var description = new Label();
            description.AutoSize = true;
            description.MaximumSize = new Size(466, 0);
            description.Margin = new Padding(0, 0, 0, 10);
            description.Text = "Normalde bu \"SteelSeries Sonar - Microphone\" cihazıdır. Program, ses düzeyi " +
                               "karıştırıcısında bu cihazdaki Discord sesini kapatmakla aynı şeyi yapar ve onu sürekli kapalı tutar.";

            deviceList = new ListBox();
            deviceList.Dock = DockStyle.Fill;
            deviceList.IntegralHeight = false;
            deviceList.Margin = new Padding(0, 0, 0, 6);
            deviceList.SelectedIndexChanged += delegate { UpdateState(); };
            deviceList.DoubleClick += delegate { if (saveButton.Enabled) saveButton.PerformClick(); };

            hintLabel = new Label();
            hintLabel.AutoSize = true;
            hintLabel.MaximumSize = new Size(466, 0);
            hintLabel.Margin = new Padding(0, 0, 0, 8);

            startupBox = new CheckBox();
            startupBox.AutoSize = true;
            startupBox.Text = "Windows açılışında arka planda otomatik başlat";
            startupBox.Checked = startupChecked;
            startupBox.Enabled = startupAvailable;
            startupBox.Margin = new Padding(0, 0, 0, 8);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0);
            saveButton = MakeButton(firstRun ? "Kaydet ve başlat" : "Kaydet");
            saveButton.DialogResult = DialogResult.OK;
            Button cancelButton = MakeButton("İptal");
            cancelButton.DialogResult = DialogResult.Cancel;
            Button refreshButton = MakeButton("Yenile");
            refreshButton.Click += delegate { RefreshDevices(); };
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(refreshButton);
            AcceptButton = saveButton;
            CancelButton = cancelButton;

            var note = new Label();
            note.AutoSize = true;
            note.MaximumSize = new Size(466, 0);
            note.ForeColor = SystemColors.GrayText;
            note.Margin = new Padding(0, 10, 0, 0);
            note.Text = "Bundan sonra pencere açılmaz; program saatin yanındaki simgede sessizce çalışır. " +
                        "Ayarları değiştirmek için simgeye çift tıkla.";

            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(description, 0, 1);
            layout.Controls.Add(deviceList, 0, 2);
            layout.Controls.Add(hintLabel, 0, 3);
            layout.Controls.Add(startupBox, 0, 4);
            layout.Controls.Add(buttons, 0, 5);
            layout.Controls.Add(note, 0, 6);
            Controls.Add(layout);

            Load += delegate { RefreshDevices(); };
        }

        static Button MakeButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.MinimumSize = new Size(92, 30);
            button.Padding = new Padding(8, 0, 8, 0);
            button.UseVisualStyleBackColor = true;
            return button;
        }

        public void RefreshDevices()
        {
            List<AudioDeviceInfo> devices;
            loadError = null;
            try { devices = AudioDevices.ListActiveOutputs(); }
            catch (Exception ex) { devices = new List<AudioDeviceInfo>(); loadError = ex.Message; }

            deviceList.BeginUpdate();
            deviceList.Items.Clear();
            foreach (AudioDeviceInfo device in devices) deviceList.Items.Add(device);
            deviceList.EndUpdate();

            // Secim sirasi: kayitli cihaz kimligi > kayitli cihaz adi > en iyi Sonar eslesmesi
            int pick = -1, best = 0;
            anySonar = false;
            for (int i = 0; i < devices.Count; i++)
            {
                if (AudioDevices.SonarScore(devices[i].Name) > 0) anySonar = true;
                if (pick < 0 && config.DeviceId.Length > 0 && devices[i].Id == config.DeviceId) pick = i;
            }
            for (int i = 0; pick < 0 && i < devices.Count; i++)
                if (config.DeviceName.Length > 0 && string.Equals(devices[i].Name, config.DeviceName, StringComparison.OrdinalIgnoreCase)) pick = i;
            if (pick < 0)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    int score = AudioDevices.SonarScore(devices[i].Name);
                    if (score > best) { best = score; pick = i; }
                }
            }
            if (pick >= 0) deviceList.SelectedIndex = pick;
            UpdateState();
        }

        void UpdateState()
        {
            AudioDeviceInfo selected = SelectedDevice;
            saveButton.Enabled = selected != null;

            if (loadError != null)
                SetHint("Ses cihazları okunamadı: " + loadError, Color.Firebrick);
            else if (selected != null && AudioDevices.SonarScore(selected.Name) == 2)
                SetHint("✓ Sonar Microphone cihazı seçili.", Color.FromArgb(30, 130, 60));
            else if (!anySonar)
                SetHint("Sonar cihazı görünmüyor. SteelSeries GG açık mı? Açıp \"Yenile\"ye bas. " +
                        "İstersen listeden elle de seçebilirsin.", Color.FromArgb(176, 96, 0));
            else
                SetHint("Doğru seçim genelde adında \"Sonar\" ve \"Microphone\" geçen cihazdır.", Color.FromArgb(176, 96, 0));
        }

        void SetHint(string text, Color color)
        {
            hintLabel.Text = text;
            hintLabel.ForeColor = color;
        }
    }

    public sealed class TrayApp : ApplicationContext
    {
        readonly AppConfig config;
        readonly string configPath;
        readonly bool startupAvailable;
        readonly Control invoker;
        readonly MuteEngine engine;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem statusItem, deviceItem, pauseItem, keepUiItem, startupItem;
        readonly RegisteredWaitHandle showWait, quitWait;
        readonly System.Windows.Forms.Timer trimTimer;
        SetupForm settingsForm;
        bool exiting;

        public TrayApp(AppConfig config, string configPath, bool startupAvailable, WaitHandle showSignal, WaitHandle quitSignal)
        {
            this.config = config;
            this.configPath = configPath;
            this.startupAvailable = startupAvailable;

            invoker = new Control();
            IntPtr unused = invoker.Handle; // motor thread'inden arayuze mesaj gondermek icin

            statusItem = new ToolStripMenuItem("Başlatılıyor...");
            statusItem.Enabled = false;
            deviceItem = new ToolStripMenuItem("Cihaz: " + Shorten(config.DeviceName, 60));
            deviceItem.Enabled = false;
            pauseItem = new ToolStripMenuItem("Duraklat (Discord sesini aç)");
            pauseItem.CheckOnClick = true;
            pauseItem.Click += delegate { engine.SetPaused(pauseItem.Checked); };
            keepUiItem = new ToolStripMenuItem("Giriş/çıkış seslerini koru (deneysel)");
            keepUiItem.CheckOnClick = true;
            keepUiItem.Checked = config.KeepUiSounds;
            keepUiItem.ToolTipText = "Discord'un giriş/çıkış ve bildirim seslerini susturmaz. Yankı geri gelirse kapat.";
            keepUiItem.Click += delegate
            {
                config.KeepUiSounds = keepUiItem.Checked;
                try { config.Save(configPath); } catch { }
                engine.SetKeepUiSounds(keepUiItem.Checked);
            };
            var changeItem = new ToolStripMenuItem("Cihazı değiştir...", null, delegate { ShowSettings(); });
            var diagnosticsItem = new ToolStripMenuItem("Tanı bilgisini kopyala", null, delegate { CopyDiagnostics(); });
            startupItem = new ToolStripMenuItem("Windows açılışında başlat");
            startupItem.CheckOnClick = true;
            startupItem.Checked = startupAvailable && StartupEntry.IsEnabled();
            startupItem.Enabled = startupAvailable;
            startupItem.Click += delegate
            {
                StartupEntry.TrySet(startupItem.Checked);
                startupItem.Checked = StartupEntry.IsEnabled();
            };
            var exitItem = new ToolStripMenuItem("Çıkış (Discord sesini geri aç)", null, delegate { ExitApp(true); });

            menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] {
                statusItem, deviceItem, new ToolStripSeparator(),
                pauseItem, keepUiItem, changeItem, startupItem, new ToolStripSeparator(),
                diagnosticsItem, exitItem });

            tray = new NotifyIcon();
            tray.Icon = Icons.Get(Icons.Warning, SystemInformation.SmallIconSize.Width);
            tray.Text = "Sonar Discord Mute";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowSettings(); };
            tray.Visible = true;

            engine = new MuteEngine(config.DeviceId, config.DeviceName, config.ProcessMatch);
            engine.SetKeepUiSounds(config.KeepUiSounds);
            engine.StatusChanged +=delegate(EngineStatus status) { PostToUi(delegate { ApplyStatus(status); }); };
            engine.Start();

            // Program tekrar acilirsa (ikinci kopya) ayar penceresini goster
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
                delegate { PostToUi(ShowSettings); }, null, Timeout.Infinite, false);

            // Daha yeni bir surum acildiginda yerini ona birak (Discord susturulmus kalir)
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal,
                delegate { PostToUi(delegate { ExitApp(false); }); }, null, Timeout.Infinite, true);

            trimTimer = new System.Windows.Forms.Timer();
            trimTimer.Interval = 5000;
            trimTimer.Tick += delegate { trimTimer.Stop(); Memory.Trim(); };
            trimTimer.Start();
        }

        void PostToUi(Action action)
        {
            try
            {
                if (!invoker.IsDisposed) invoker.BeginInvoke(action);
            }
            catch { }
        }

        static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "-";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        void ApplyStatus(EngineStatus s)
        {
            if (exiting) return;
            string text;
            Color color;
            if (s.Paused)
            {
                text = "Duraklatıldı – Discord sesi açık";
                color = Icons.Paused;
            }
            else if (!s.DeviceFound)
            {
                text = string.IsNullOrEmpty(s.Error) ? "Cihaz bulunamadı (Sonar kapalı olabilir)" : "Hata: " + s.Error;
                color = Icons.Warning;
            }
            else if (s.Sessions == 0)
            {
                text = "Aktif – şu an bu cihazda Discord sesi yok";
                color = Icons.Active;
            }
            else if (s.Muted + s.Kept == s.Sessions && s.Kept > 0)
            {
                text = "Aktif – " + s.Muted + " oturum susturuldu, giriş/çıkış sesleri açık";
                color = Icons.Active;
            }
            else if (s.Muted == s.Sessions)
            {
                text = "Aktif – Discord susturuldu (" + s.Sessions + " oturum)";
                color = Icons.Active;
            }
            else
            {
                text = "Uyarı – " + s.Muted + "/" + s.Sessions + " oturum susturulabildi";
                color = Icons.Warning;
            }
            statusItem.Text = text;
            deviceItem.Text = "Cihaz: " + Shorten(s.DeviceName, 60);
            tray.Text = Shorten("Sonar Discord Mute\n" + text, 63);
            tray.Icon = Icons.Get(color, SystemInformation.SmallIconSize.Width);
        }

        void CopyDiagnostics()
        {
            var thread = new Thread(delegate()
            {
                string report = Diagnostics.BuildReport(config);
                PostToUi(delegate
                {
                    try
                    {
                        Clipboard.SetText(report);
                        tray.ShowBalloonTip(5000, "Sonar Discord Mute",
                            "Tanı bilgisi panoya kopyalandı. Discord'a yapıştırıp gönderebilirsin.", ToolTipIcon.Info);
                    }
                    catch { }
                });
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        void ShowSettings()
        {
            if (exiting) return;
            if (settingsForm != null)
            {
                settingsForm.WindowState = FormWindowState.Normal;
                settingsForm.Activate();
                return;
            }
            settingsForm = new SetupForm(config, false, startupItem.Checked, startupAvailable);
            try
            {
                if (settingsForm.ShowDialog() == DialogResult.OK && settingsForm.SelectedDevice != null)
                {
                    config.DeviceId = settingsForm.SelectedDevice.Id;
                    config.DeviceName = settingsForm.SelectedDevice.Name;
                    try { config.Save(configPath); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Ayarlar kaydedilemedi: " + ex.Message, "Sonar Discord Mute",
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    engine.SetTarget(config.DeviceId, config.DeviceName);
                    if (startupAvailable)
                    {
                        StartupEntry.TrySet(settingsForm.StartWithWindows);
                        startupItem.Checked = StartupEntry.IsEnabled();
                    }
                }
            }
            finally
            {
                settingsForm.Dispose();
                settingsForm = null;
                Memory.Trim();
            }
        }

        void ExitApp(bool restoreDiscord)
        {
            if (exiting) return;
            exiting = true;
            showWait.Unregister(null);
            quitWait.Unregister(null);
            if (settingsForm != null) settingsForm.Close();
            trimTimer.Dispose();
            engine.Stop(3000, restoreDiscord);
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            invoker.Dispose();
            ExitThread();
        }
    }

    // Guncelleme: yeni exe'ye cift tiklamak yeterli. Calisan eski surumu kapatip yerini alir;
    // ayarlar korunur ve Discord bu sirada susturulmus kalir.
    static class Updater
    {
        public static Version FileVersionOf(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            }
            catch { return null; }
        }

        public static bool TryTakeOver(string instanceName, Mutex mutex)
        {
            Version mine = FileVersionOf(Application.ExecutablePath);
            Process running = FindRunningCopy();
            if (mine == null || running == null) return false;
            string runningPath = ProcessInfo.GetExePath((uint)running.Id);
            Version theirs = runningPath == null ? null : FileVersionOf(runningPath);
            if (theirs == null || theirs >= mine) return false; // ayni/yeni surum: ayar penceresini ac

            // 1.1+ surumler kibarca kapanir; 1.0'da bu sinyal yok, onu sonlandiririz.
            bool asked = false;
            try
            {
                using (EventWaitHandle quit = EventWaitHandle.OpenExisting(instanceName + "_Quit")) quit.Set();
                asked = true;
            }
            catch { }
            try
            {
                if (!asked || !running.WaitForExit(5000))
                {
                    running.Kill();
                    running.WaitForExit(5000);
                }
            }
            catch { }

            try { return mutex.WaitOne(5000); }
            catch (AbandonedMutexException) { return true; }
        }

        static Process FindRunningCopy()
        {
            Process self = Process.GetCurrentProcess();
            foreach (string name in new string[] { self.ProcessName, "SonarDiscordMute" })
                foreach (Process p in Process.GetProcessesByName(name))
                    if (p.Id != self.Id) return p;
            return null;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        static void Main(string[] args)
        {
            // --config <dosya>: farkli bir ayar dosyasi (gelismis/test kullanimi)
            string configPath = AppConfig.DefaultPath;
            bool customConfig = false;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--config")
                {
                    configPath = Path.GetFullPath(args[i + 1]);
                    customConfig = true;
                }
            }

            string instanceName = @"Local\SonarDiscordMute" + (customConfig ? "_" + StableHash(configPath.ToUpperInvariant()) : "");
            bool firstInstance;
            using (var mutex = new Mutex(true, instanceName, out firstInstance))
            {
                if (!firstInstance && !Updater.TryTakeOver(instanceName, mutex))
                {
                    // Zaten calisiyor: calisan kopyaya ayar penceresini actir
                    try
                    {
                        AllowSetForegroundWindow(-1);
                        using (EventWaitHandle show = EventWaitHandle.OpenExisting(instanceName + "_Show")) show.Set();
                    }
                    catch { }
                    return;
                }

                using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "_Show"))
                using (var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "_Quit"))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    bool startupAvailable = !customConfig;
                    AppConfig config = AppConfig.Load(configPath);
                    if (!config.HasDevice)
                    {
                        // Ilk acilis: cihaz secimi
                        using (var form = new SetupForm(config, true, startupAvailable, startupAvailable))
                        {
                            if (form.ShowDialog() != DialogResult.OK || form.SelectedDevice == null) return;
                            config.DeviceId = form.SelectedDevice.Id;
                            config.DeviceName = form.SelectedDevice.Name;
                            try { config.Save(configPath); }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Ayarlar kaydedilemedi: " + ex.Message, "Sonar Discord Mute",
                                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                            if (startupAvailable) StartupEntry.TrySet(form.StartWithWindows);
                        }
                    }
                    else if (startupAvailable && StartupEntry.IsEnabled())
                    {
                        StartupEntry.TrySet(true); // exe tasindiysa baslangic yolunu guncelle
                    }

                    Application.Run(new TrayApp(config, configPath, startupAvailable, showSignal, quitSignal));
                }
                GC.KeepAlive(mutex);
            }
        }

        static string StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text) { hash ^= c; hash *= 16777619; }
            return hash.ToString("x8");
        }
    }
}
