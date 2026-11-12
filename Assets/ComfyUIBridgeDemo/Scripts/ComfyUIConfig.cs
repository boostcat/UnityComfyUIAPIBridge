using UnityEngine;

namespace ComfyUI.Definition
{
    /// <summary>
    /// ComfyUI 工作流调用相关的全局共享定义（连接地址、ClientId、采样器/调度器选项、WebSocket 重连、工作流目录等）。
    /// </summary>
    public static class ComfyUIConfig
    {
        // ── 连接地址与客户端标识（可被 Config.txt 覆盖） ──────
        /// <summary>ComfyUI HTTP 接口默认地址</summary>
        public static string HttpAddress = "http://127.0.0.1:8188";

        /// <summary>ComfyUI 实时进度 WebSocket 默认地址（连接时会自动补 clientId）</summary>
        public static string WsAddress = "ws://127.0.0.1:8188/ws";

        /// <summary>ComfyUI 客户端标识，作为提交任务与 WebSocket 进度消息路由的 clientId</summary>
        public static string ClientId = "shmqc";

        // ── WebSocket 重连（可被 Config.txt 覆盖） ──────────
        /// <summary>WebSocket 断线后是否自动重连（ComfyProgressDisplay 默认开启）</summary>
        public static bool WsAutoReconnect = true;

        /// <summary>WebSocket 自动重连延迟（秒）</summary>
        public static float WsReconnectDelay = 3f;

        // ── 工作流目录与默认工作流文件名 ──────────────────
        /// <summary>工作流 JSON 所在目录名（位于 Resources 下，即 Resources/工作流）</summary>
        public const string WorkflowFolderName = "工作流";

        /// <summary>绘画场景默认使用的工作流文件名（位于 Resources/工作流 下），可由 Config.txt 的“实时绘画工作流”覆盖</summary>
        public static string RealtimePaintWorkflow = "实时绘画工作流（油画花卉）.json";

        /// <summary>本地超分工作流文件名（位于 Resources/工作流 下），可由 Config.txt 覆盖</summary>
        public static string LocalUpscaleWorkflow = "LocalUpscaleWorkflow.json";

        /// <summary>
        /// ComfyUI 常用采样器列表；工作流里若出现新值，运行时各 Demo 会自动并入下拉选项。
        /// </summary>
        public static readonly string[] BaseSamplers =
        {
            "euler", "euler_ancestral", "heun", "dpm_2", "dpm_2_ancestral", "dpmpp_2s_ancestral",
            "dpmpp_2m", "dpmpp_2m_sde", "dpmpp_3m_sde", "dpmpp_sde", "ddim", "uni_pc", "uni_pc_bh2", "lcm"
        };

        /// <summary>
        /// ComfyUI 常用调度器列表；工作流里若出现新值，运行时各 Demo 会自动并入下拉选项。
        /// </summary>
        public static readonly string[] BaseSchedulers =
        {
            "normal", "karras", "exponential", "sgm_uniform", "simple", "ddim_uniform", "beta"
        };

        // 本地 ComfyUI 输入/输出目录（本机部署时直接读写，不经过 HTTP 图片接口）
        /// <summary>ComfyUI 输入目录（图片上传落盘处）</summary>
        public static string LocalInputDir = @"D:\ComfyUI\ComfyUI-aki-v3.2\ComfyUI\input";

        /// <summary>ComfyUI 输出目录（生图结果落盘处）</summary>
        public static string LocalOutputDir = @"D:\ComfyUI\ComfyUI-aki-v3.2\ComfyUI\output";

        // 默认生成参数（生图 / 图生图），可由 Config.txt 覆盖
        /// <summary>默认正向提示词</summary>
        public static string PositivePrompt = "1girl";

        /// <summary>默认负面提示词</summary>
        public static string NegativePrompt = "";

        /// <summary>默认随机种子（-1 表示每次随机）</summary>
        public static long Seed = -1;

        /// <summary>默认采样步数</summary>
        public static int Steps = 20;

        /// <summary>默认 CFG 值</summary>
        public static float Cfg = 7f;

        /// <summary>默认采样器</summary>
        public static string Sampler = "euler";

        /// <summary>默认调度器</summary>
        public static string Scheduler = "dpmpp_2m";

        /// <summary>默认重绘幅度（0~1）</summary>
        public static float Denoise = 0.5f;

        /// <summary>默认宽度</summary>
        public static int Width = 512;

        /// <summary>默认高度</summary>
        public static int Height = 512;

        /// <summary>默认画笔提交间隔</summary>
        public static float PaintSubmitInterval = 0.5f;

        /// <summary>默认画板背景色RGB</summary>
        public static Color BackgroundColor = new Color(240f / 255f, 225f / 255f, 168f / 255f);
    }

    /// <summary>
    /// ComfyUI 生图 / 图生图工作流的可调参数模型
    /// （Checkpoint / 正负提示词 / 尺寸 / 种子 / 步数 / CFG / 采样器 / 调度器 / 重绘幅度）。
    /// </summary>
    [System.Serializable]
    public class ComfyPaintWorkflowParameters
    {
        [TextArea] public string positivePrompt;
        [TextArea] public string negativePrompt;
        [Min(8)] public int width;
        [Min(8)] public int height;
        public long seed;
        [Min(1)] public int steps;
        [Min(0)] public float cfg;
        public string samplerName;
        public string scheduler;
        [Range(0f, 1f)] public float denoise;
    }
}
