using System;
using System.Collections.Generic;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 帧时间统计。抽成独立函数是为了能拿已知序列验证算法本身，而不是靠"看着差不多"。
    /// </summary>
    public static class FrameStats
    {
        /// <summary>
        /// 1% Low FPS。
        ///
        /// 口径：把帧时间从大到小（最卡在前）排序，取最差 percentile（默认 1%）那一批帧，
        /// 求它们的**平均帧时间**，再 1000 / 平均 = FPS。
        ///
        /// 三个必须守住的点：
        /// 1) 用帧时间排序，不能用 FPS 排序 —— 1000/ms 是非线性的，
        ///    按 FPS 取 1% 会把结果拉高很多。
        /// 2) 取「最差一批的平均」，不是第 99 百分位那一个点 ——
        ///    单点会被一次偶发尖刺带飞，平均值才是 CapFrameX / Afterburner 的口径。
        /// 3) 最差样本数向上取整且至少 1 帧（600 帧 → 6 帧），否则少量样本下会算出 0。
        /// </summary>
        public static double LowFps(IList<double> frameMs, double percentile)
        {
            if (frameMs == null || frameMs.Count == 0) return 0;
            if (percentile <= 0) percentile = 0.01;

            int n = frameMs.Count;
            int worst = (int)Math.Ceiling(n * percentile);
            if (worst < 1) worst = 1;
            if (worst > n) worst = n;

            // 复用线程内缓冲区：这个函数每 0.5 秒被调一次，n 最大 3000，
            // 每次 new 一个 24 KB 数组会让 gen0 每秒回收一次。缓冲只增不减。
            var arr = Scratch(n);
            for (int i = 0; i < n; i++) arr[i] = frameMs[i];

            Array.Sort(arr, 0, n);    // 升序，最卡的排在尾部

            double sum = 0;
            int used = 0;
            for (int i = 0; i < worst; i++)
            {
                double v = arr[n - 1 - i];   // 从最慢的往里取，等价于原来的降序取前 worst 个
                if (v <= 0) continue;        // 无效帧不参与
                sum += v;
                used++;
            }

            if (used == 0 || sum <= 0) return 0;
            return 1000.0 / (sum / used);
        }

        [ThreadStatic]
        private static double[] _scratch;

        private static double[] Scratch(int n)
        {
            if (_scratch == null || _scratch.Length < n) _scratch = new double[n < 512 ? 512 : n];
            return _scratch;
        }

        /// <summary>平均 FPS（按帧时间求平均再取倒数，而不是对各帧 FPS 求算术平均）。</summary>
        public static double AverageFps(IList<double> frameMs)
        {
            if (frameMs == null || frameMs.Count == 0) return 0;
            double sum = 0;
            int used = 0;
            for (int i = 0; i < frameMs.Count; i++)
            {
                if (frameMs[i] <= 0) continue;
                sum += frameMs[i];
                used++;
            }
            if (used == 0 || sum <= 0) return 0;
            return 1000.0 / (sum / used);
        }
    }
}
