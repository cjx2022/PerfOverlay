using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PerfOverlay.Services
{
    /// <summary>一次检查更新的结果。</summary>
    public sealed class UpdateResult
    {
        /// <summary>当前程序版本，如 0.9.2。</summary>
        public string Current = "";
        /// <summary>GitHub 上最新版本，如 0.9.3；查询失败时为空。</summary>
        public string Latest = "";
        /// <summary>发布页地址（releases/latest 会自动跳到最新 tag）。</summary>
        public string Url = "";
        /// <summary>最新版本比当前新。</summary>
        public bool HasUpdate;
        /// <summary>这次检查没成功，原因在 Error 里。</summary>
        public bool Failed;
        /// <summary>失败原因，直接给用户看的一句话。</summary>
        public string Error = "";
    }

    /// <summary>
    /// 检查更新：问一次 GitHub 的 releases/latest，把 tag 跟程序集版本比一下。
    ///
    /// 只做「查」和「告诉用户去哪下」，不自动下载、不自动替换 exe —— 程序常驻托盘，
    /// 自我替换会和单实例互斥体、提权进程、ETW 采集互相打架，出问题的代价远大于省一次点击。
    ///
    /// 解析不引第三方 JSON 库：整个程序只有这一个 HTTP 调用，需要的就是两个短字符串字段，
    /// 手写正则比拖进 Newtonsoft 更划算（也保住了「单文件零依赖」）。
    /// </summary>
    public static class UpdateChecker
    {
        public const string RepoUrl = "https://github.com/cjx2022/PerfOverlay";
        public const string ReleasesUrl = "https://github.com/cjx2022/PerfOverlay/releases/latest";

        private const string ApiUrl = "https://api.github.com/repos/cjx2022/PerfOverlay/releases/latest";

        /// <summary>当前版本，如 "0.9.2"。取程序集版本，改版本号只改 csproj。</summary>
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? "0.0.0" : string.Format("{0}.{1}.{2}", v.Major, v.Minor, v.Build);
                }
                catch (Exception) { return "0.0.0"; }
            }
        }

        /// <summary>同步查询（几秒），调用方必须放到后台线程，别在 UI 线程上直接调。</summary>
        public static UpdateResult Check(int timeoutMs)
        {
            var r = new UpdateResult { Current = CurrentVersion, Url = ReleasesUrl };
            try
            {
                // .NET 4.8 默认走系统默认协议，但机器上被组策略改过时会落到 TLS 1.0 而被 GitHub 拒，
                // 显式补一条最省事。
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch (Exception) { }

                var req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                // GitHub API 强制要求 User-Agent，缺了直接 403
                req.UserAgent = "PerfOverlay/" + r.Current;
                req.Accept = "application/vnd.github+json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var sr = new StreamReader(stream, Encoding.UTF8))
                {
                    json = sr.ReadToEnd();
                }

                string tag = JsonField(json, "tag_name");
                if (tag == "")
                {
                    r.Failed = true;
                    r.Error = "GitHub 返回的内容里没有版本号";
                    return r;
                }

                r.Latest = Normalize(tag);
                string page = JsonField(json, "html_url");
                if (page != "") r.Url = page;

                r.HasUpdate = Compare(r.Latest, r.Current) > 0;
                return r;
            }
            catch (WebException we)
            {
                r.Failed = true;
                r.Error = DescribeWebError(we);
                return r;
            }
            catch (Exception ex)
            {
                r.Failed = true;
                r.Error = "检查失败：" + Short(ex.Message);
                return r;
            }
        }

        /// <summary>失败原因翻成一句人话，别把 .NET 的英文堆栈丢给用户。</summary>
        private static string DescribeWebError(WebException we)
        {
            var resp = we.Response as HttpWebResponse;
            if (resp != null)
            {
                int code = (int)resp.StatusCode;
                if (code == 403 || code == 429) return "GitHub 接口访问受限（" + code + "），请稍后重试";
                if (code == 404) return "GitHub 上找不到发布记录（404）";
                if (code >= 500) return "GitHub 服务暂时不可用（" + code + "）";
                return "GitHub 返回 " + code;
            }

            switch (we.Status)
            {
                case WebExceptionStatus.Timeout: return "连接 GitHub 超时，请检查网络后重试";
                case WebExceptionStatus.NameResolutionFailure: return "域名解析失败，请检查网络";
                case WebExceptionStatus.ProxyNameResolutionFailure: return "代理配置有问题，解析不了代理地址";
                case WebExceptionStatus.ConnectFailure: return "连不上 GitHub（网络或代理不通）";
                case WebExceptionStatus.TrustFailure: return "HTTPS 证书校验失败（时间不对或被拦截）";
                case WebExceptionStatus.SecureChannelFailure: return "TLS 握手失败（系统协议版本过旧）";
                default: return "网络错误：" + Short(we.Message);
            }
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        /// <summary>从 JSON 里取一个字符串字段（只处理字符串值，够用）。</summary>
        private static string JsonField(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            try { return Regex.Unescape(m.Groups[1].Value); }
            catch (Exception) { return m.Groups[1].Value; }
        }

        /// <summary>"v0.9.3" / "0.9.3" / "0.9.3-beta" → "0.9.3"。</summary>
        private static string Normalize(string tag)
        {
            string s = (tag ?? "").Trim();
            if (s.StartsWith("v") || s.StartsWith("V")) s = s.Substring(1);
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            return s;
        }

        /// <summary>按点分段比大小：新版本返回正数，相同返回 0。缺的段按 0 算（1.0 == 1.0.0）。</summary>
        public static int Compare(string a, string b)
        {
            var pa = Split(a);
            var pb = Split(b);
            int n = Math.Max(pa.Count, pb.Count);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Count ? pa[i] : 0;
                int vb = i < pb.Count ? pb[i] : 0;
                if (va != vb) return va - vb;
            }
            return 0;
        }

        private static List<int> Split(string s)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in Normalize(s).Split('.'))
            {
                int n;
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) list.Add(n);
                else break;
            }
            return list;
        }
    }
}
