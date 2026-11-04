using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 全局热键：一个 0×0 的隐藏窗口 + RegisterHotKey。
    ///
    /// 冲突检测不需要自己猜：注册权在系统手里，别人占着这个组合就注册不上，
    /// 失败时 <see cref="Marshal.GetLastWin32Error"/> 会给出 1409（已被占用）。
    /// 这比读注册表 / 猜「常见软件用哪些键」可靠得多。
    ///
    /// 隐藏窗口而不是挂在浮窗上：浮窗可能被 Hide 甚至从未创建，
    /// 而热键必须在浮窗藏起来时也照样管用。
    /// </summary>
    public static class HotkeyService
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x5046;                 // 'PF'，本进程内唯一即可
        private const int ErrorHotkeyAlreadyRegistered = 1409;

        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000;             // 按住不放只触发一次

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private static HwndSource _src;
        private static bool _registered;

        /// <summary>当前真正生效的组合键（"Ctrl+Alt+P"）。空串 = 没设。</summary>
        public static string Current { get; private set; }

        public static bool Active { get { return _registered; } }

        /// <summary>热键被按下。回调在 UI 线程上（WM_HOTKEY 投递到注册它的线程）。</summary>
        public static event Action Pressed;

        private static void EnsureWindow()
        {
            if (_src != null) return;
            var p = new HwndSourceParameters("PerfOverlayHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0
            };
            _src = new HwndSource(p);
            _src.AddHook(Hook);
        }

        private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                var h = Pressed;
                if (h != null)
                {
                    try { h(); } catch (Exception) { }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 换成新的组合键。传空串 = 取消。失败时 error 是给用户看的一句话，
        /// 并且**旧的组合键已经被解掉了**——调用方要负责把旧值重新应用回去。
        /// </summary>
        public static bool Apply(string spec, out string error)
        {
            error = null;
            spec = (spec ?? "").Trim();

            if (spec.Length == 0)
            {
                Release();
                Current = "";
                return true;
            }

            if (_registered && string.Equals(Current, spec, StringComparison.OrdinalIgnoreCase)) return true;

            uint mods, vk;
            if (!TryParse(spec, out mods, out vk))
            {
                error = "无法识别这个组合键";
                return false;
            }

            try { EnsureWindow(); }
            catch (Exception ex)
            {
                error = "无法建立热键通道：" + ex.Message;
                return false;
            }

            Release();
            if (!RegisterHotKey(_src.Handle, HotkeyId, mods | ModNoRepeat, vk))
            {
                int code = Marshal.GetLastWin32Error();
                if (code == ErrorHotkeyAlreadyRegistered)
                    error = "这个组合已被其他程序占用";
                else
                    error = "系统拒绝了这个组合（错误码 " + code + "）";
                return false;
            }

            _registered = true;
            Current = spec;
            return true;
        }

        public static void Release()
        {
            if (!_registered || _src == null) { _registered = false; return; }
            try { UnregisterHotKey(_src.Handle, HotkeyId); } catch (Exception) { }
            _registered = false;
        }

        public static void Dispose()
        {
            Release();
            Current = "";
            if (_src != null)
            {
                try { _src.Dispose(); } catch (Exception) { }
                _src = null;
            }
        }

        // ------------------------------------------------------------------
        // 探针用：直接碰系统注册接口，不改 Current，也不影响正式注册的那一把。
        // ------------------------------------------------------------------

        /// <summary>试注册后立刻撤销，用来判断「这个组合现在能不能用」。</summary>
        public static bool ProbeRegister(string spec, int id, out int code)
        {
            code = 0;
            uint mods, vk;
            if (!TryParse(spec, out mods, out vk)) { code = -1; return false; }
            EnsureWindow();
            if (RegisterHotKey(_src.Handle, id, mods | ModNoRepeat, vk))
            {
                UnregisterHotKey(_src.Handle, id);
                return true;
            }
            code = Marshal.GetLastWin32Error();
            return false;
        }

        /// <summary>占住不放，用来在同一测试里制造一次真实冲突。</summary>
        public static bool ProbeHold(string spec, int id, out int code)
        {
            code = 0;
            uint mods, vk;
            if (!TryParse(spec, out mods, out vk)) { code = -1; return false; }
            EnsureWindow();
            if (RegisterHotKey(_src.Handle, id, mods | ModNoRepeat, vk)) return true;
            code = Marshal.GetLastWin32Error();
            return false;
        }

        public static void ProbeRelease(int id)
        {
            if (_src == null) return;
            try { UnregisterHotKey(_src.Handle, id); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 组合键的写法：固定顺序 Ctrl+Alt+Shift+Win，最后一段是主键。
        // 自己写自己读，不追 Windows 的各种别名，够用且不会歧义。
        // ------------------------------------------------------------------

        public static string Format(ModifierKeys mods, Key key)
        {
            var parts = new List<string>(5);
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        public static bool TryParse(string spec, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(spec)) return false;

            string[] parts = spec.Split('+');
            bool hasKey = false;

            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 0) return false;

                if (Eq(s, "Ctrl") || Eq(s, "Control")) { mods |= ModControl; continue; }
                if (Eq(s, "Alt")) { mods |= ModAlt; continue; }
                if (Eq(s, "Shift")) { mods |= ModShift; continue; }
                if (Eq(s, "Win") || Eq(s, "Windows")) { mods |= ModWin; continue; }

                if (hasKey) return false;          // 只能有一个主键
                if (!TryKey(s, out vk)) return false;
                hasKey = true;
            }

            // 纯字母/数字不带修饰键会抢走全局输入，这里直接拒绝
            if (!hasKey) return false;
            if ((mods & (ModControl | ModAlt | ModWin)) == 0) return false;
            if (vk == 0) return false;
            return true;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryKey(string name, out uint vk)
        {
            vk = 0;
            Key k;

            // 面板上显示的是 "1"~"9"，要先补成枚举名 D1~D9。
            // 注意不能先走 Enum.TryParse：它对纯数字串会按**数值**解析，
            // "1" 会变成 (Key)1 = Cancel（vk=0x03），注册上去就是另一个键了。
            if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
                name = "D" + name;

            if (!Enum.TryParse<Key>(name, true, out k)) return false;
            if (!IsRealKey(k) && k != Key.Space && k != Key.Tab) return false;

            vk = (uint)KeyInterop.VirtualKeyFromKey(k);
            return vk != 0;
        }

        /// <summary>Key.D1 → "1"，其余原样（P / F5 / Space / OemMinus …）。</summary>
        public static string KeyName(Key key)
        {
            string s = key.ToString();
            if (s.Length == 2 && s[0] == 'D' && s[1] >= '0' && s[1] <= '9') return s.Substring(1);
            return s;
        }

        /// <summary>给界面看的写法：数字键显示成 "1" 而不是 "D1"。</summary>
        public static string Pretty(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return "";
            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 2 && (s[0] == 'D' || s[0] == 'd') && s[1] >= '0' && s[1] <= '9')
                    parts[i] = s.Substring(1);
            }
            return string.Join("+", parts);
        }

        /// <summary>合法组合键的可读描述，用于提示为什么不让设。</summary>
        public static bool IsAcceptable(ModifierKeys mods, Key key)
        {
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return false;
            return IsRealKey(key);
        }

        /// <summary>光按修饰键不算一个组合键。</summary>
        public static bool IsRealKey(Key key)
        {
            if (key == Key.None || key == Key.System) return false;
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LWin:
                case Key.RWin:
                    return false;
                default:
                    return true;
            }
        }
    }
}
