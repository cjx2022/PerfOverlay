using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PerfOverlay.Services
{
    /// <summary>用户配置。用 DataContractJsonSerializer 序列化，避免第三方依赖。</summary>
    [DataContract]
    public class AppSettings
    {
        // ---- 显示项 ----
        [DataMember(Order = 1)] public bool ShowCpu = true;
        [DataMember(Order = 2)] public bool ShowMemory = true;
        [DataMember(Order = 3)] public bool ShowFps = true;
        [DataMember(Order = 4)] public bool ShowLowFps = true;
        [DataMember(Order = 5)] public bool ShowVram = true;

        // ---- 外观 ----
        [DataMember(Order = 6)] public int RefreshMs = 500;
        [DataMember(Order = 7)] public double Opacity = 1.0;
        [DataMember(Order = 8)] public string SizeMode = "M";     // S / M / L
        [DataMember(Order = 9)] public bool Compact = false;      // 已废弃，保留字段以兼容旧配置
        [DataMember(Order = 10)] public bool ShowLabels = true;   // 是否显示 FPS/CPU 等前缀

        // ---- 行为 ----
        [DataMember(Order = 11)] public bool LockPosition = false;
        [DataMember(Order = 12)] public bool ClickThrough = false;
        [DataMember(Order = 13)] public bool StartWithWindows = false;
        [DataMember(Order = 14)] public bool ShowTrayIcon = true;
        [DataMember(Order = 15)] public bool OverlayVisible = true;

        // ---- 位置（相对主显示器工作区右上角的偏移，DIP）----
        [DataMember(Order = 16)] public double OffsetX = 0;   // >0 表示距右边距
        [DataMember(Order = 17)] public double OffsetY = 0;

        [DataMember(Order = 18)] public bool FirstRunDone = false;

        // ---- 文字外观 ----
        /// <summary>文字颜色 "#RRGGBB"。默认纯白。</summary>
        [DataMember(Order = 19)] public string TextColor = "#FFFFFF";
        /// <summary>是否给文字加描边（关掉后只剩纯色文字）。</summary>
        [DataMember(Order = 20)] public bool ShowOutline = true;

        /// <summary>
        /// 显示 / 隐藏浮窗的全局热键，形如 "Ctrl+Alt+P"。空串 = 不设。
        /// 旧配置文件里没有这个字段，反序列化后会是 null，用之前一律判空。
        /// </summary>
        [DataMember(Order = 21)] public string Hotkey = "";

        /// <summary>可选文字颜色（控制面板上的色块）。</summary>
        public static readonly string[] ColorChoices =
        {
            "#FFFFFF", // 白
            "#FFD84D", // 黄
            "#5CE1E6", // 青
            "#7DFF9E", // 绿
            "#FF9F45", // 橙
            "#101418", // 近黑
        };

        // ---- 运行时派生 ----
        public static readonly int[] RefreshChoices = { 250, 500, 1000, 2000 };

        public int FontSizeMain
        {
            get
            {
                if (SizeMode == "S") return 15;
                if (SizeMode == "L") return 22;
                return 18;
            }
        }

        public int FontSizeLabel
        {
            get
            {
                if (SizeMode == "S") return 11;
                if (SizeMode == "L") return 15;
                return 13;
            }
        }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        public bool AnyMetricVisible()
        {
            return ShowCpu || ShowMemory || ShowFps || ShowLowFps || ShowVram;
        }
    }

    public static class SettingsStore
    {
        private static string _dir;
        private static string _filePath;

        // 序列化器构造要反射整个类型，没必要每次存盘都做一遍。
        // DataContractJsonSerializer 的 Read/Write 不是线程安全的，所以配一把锁串行用。
        private static readonly object SerGate = new object();
        private static DataContractJsonSerializer _ser;

        private static DataContractJsonSerializer Ser
        {
            get
            {
                if (_ser == null) _ser = new DataContractJsonSerializer(typeof(AppSettings));
                return _ser;
            }
        }

        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfOverlay");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch (Exception) { }
                _dir = d;
                return _dir;
            }
        }

        public static string FilePath
        {
            get
            {
                if (_filePath == null) _filePath = Path.Combine(Dir, "settings.json");
                return _filePath;
            }
        }

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                lock (SerGate)
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var s = (AppSettings)Ser.ReadObject(fs);
                        if (s != null) return s;
                    }
                }
            }
            catch (Exception)
            {
                // 配置损坏不应阻塞启动：回落默认值，旧文件留作 .bak
                try
                {
                    var bak = FilePath + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    if (File.Exists(FilePath)) File.Move(FilePath, bak);
                }
                catch (Exception) { }
            }
            return new AppSettings();
        }

        public static bool Save(AppSettings s)
        {
            if (s == null) return false;
            try
            {
                // 先写临时文件再替换：掉电/强杀时不会留下半个 JSON
                var tmp = FilePath + ".tmp";
                lock (SerGate)
                {
                    using (var fs = File.Create(tmp)) Ser.WriteObject(fs, s);
                }
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
