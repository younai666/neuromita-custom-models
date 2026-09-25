using BepInEx.Logging;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// 薄日志封装。
    ///
    /// 分两级：<see cref="Info"/> 是给用户看的（装了什么、成没成、为什么没成），
    /// <see cref="Verbose"/> 是给排错看的（对齐矩阵、骨骼表、槽位匹配过程），
    /// 由配置项 Diagnostics/Verbose 控制。
    /// </summary>
    public static class Logging
    {
        private static ManualLogSource _src;

        /// <summary>是否输出 Verbose 级别的日志。由 Plugin 读取配置后设置。</summary>
        public static bool VerboseEnabled = true;

        public static void Init(ManualLogSource src) => _src = src;

        public static void Info(string msg)
        {
            if (_src != null) _src.LogInfo(msg);
            else System.Console.WriteLine(msg);
        }

        public static void Warn(string msg)
        {
            if (_src != null) _src.LogWarning(msg);
            else System.Console.WriteLine(msg);
        }

        public static void Error(string msg)
        {
            if (_src != null) _src.LogError(msg);
            else System.Console.WriteLine(msg);
        }

        /// <summary>只在 Verbose 打开时输出的细节日志。</summary>
        public static void Verbose(string msg)
        {
            if (VerboseEnabled) Info(msg);
        }
    }
}
