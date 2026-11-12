using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ComfyUI.API
{
    /// <summary> ComfyUI API 调用异常，含服务端返回的校验错误详情。 </summary>
    public class ComfyApiException : Exception
    {
        public ComfyPromptError Error { get; }
        public ComfyApiException(string message, ComfyPromptError error = null) : base(message) { Error = error; }
    }

    /// <summary>
    /// ComfyUI 本地 HTTP API 客户端。
    /// 覆盖API文档中的全部端点：/history、/history/{id}、/object_info、/prompt(GET|POST)、/upload/image、/upload/mask、/interrupt、/queue(GET|POST)、/view、/embeddings、/extensions。
    /// </summary>
    public class ComfyClient
    {
        public string BaseUrl { get; }
        public string ClientId { get; }

        public ComfyClient(string baseUrl = "http://127.0.0.1:8188", string clientId = null)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            ClientId = clientId ?? Guid.NewGuid().ToString();
        }

        // ===== GET /history : 获取所有历史任务 =====
        public async Task<Dictionary<string, ComfyHistoryEntry>> GetHistoryAsync()
        {
            string json = await GetTextAsync("/history");
            return JsonConvert.DeserializeObject<Dictionary<string, ComfyHistoryEntry>>(json);
        }

        // ===== GET /history/{prompt_id} : 获取单个任务 =====
        public async Task<ComfyHistoryEntry> GetHistoryAsync(string promptId)
        {
            string json = await GetTextAsync("/history/" + Uri.EscapeDataString(promptId));
            var dict = JsonConvert.DeserializeObject<Dictionary<string, ComfyHistoryEntry>>(json);
            return dict != null && dict.TryGetValue(promptId, out var entry) ? entry : null;
        }

        // ===== GET /prompt : 获取服务器剩余队列数量 =====
        public async Task<ComfyQueueRemaining> GetQueueRemainingAsync()
        {
            return JsonConvert.DeserializeObject<ComfyQueueRemaining>(await GetTextAsync("/prompt"));
        }

        // ===== GET /object_info : 获取节点信息 =====
        public async Task<JObject> GetObjectInfoAsync()
        {
            return JObject.Parse(await GetTextAsync("/object_info"));
        }

        // ===== POST /prompt : 提交绘图任务，返回 prompt_id；校验失败抛 ComfyApiException =====
        public async Task<string> PostPromptAsync(JObject promptGraph, string clientId = null)
        {
            var body = new JObject
            {
                ["prompt"] = promptGraph,
                ["client_id"] = clientId ?? ClientId
            };
            string json = await PostJsonAsync("/prompt", body.ToString(Formatting.None));
            var parsed = JObject.Parse(json);
            if (parsed["prompt_id"] != null) return (string)parsed["prompt_id"];
            var err = parsed.ToObject<ComfyPromptError>();
            throw new ComfyApiException("提交任务失败: " + (err?.error?.message ?? json), err);
        }

        // ===== GET /view : 下载图片字节（生图结果、上传图、蒙版均通过此接口） =====
        public async Task<byte[]> GetViewAsync(string filename, string subfolder = "", string type = "output")
        {
            var qs = "filename=" + Uri.EscapeDataString(filename);
            if (!string.IsNullOrEmpty(subfolder)) qs += "&subfolder=" + Uri.EscapeDataString(subfolder);
            if (!string.IsNullOrEmpty(type)) qs += "&type=" + Uri.EscapeDataString(type);
            return await GetBytesAsync("/view?" + qs);
        }

        // ===== POST /upload/image : 上传图片到 ComfyUI input 目录，返回文件名信息 =====
        public async Task<ComfyUploadResult> UploadImageAsync(byte[] data, string fileName, string subfolder = "", string type = "input")
        {
            var form = new List<IMultipartFormSection>();
            form.Add(new MultipartFormFileSection("image", data, fileName, "image/png"));
            if (!string.IsNullOrEmpty(subfolder)) form.Add(new MultipartFormDataSection("subfolder", subfolder));
            if (!string.IsNullOrEmpty(type)) form.Add(new MultipartFormDataSection("type", type));
            return JsonConvert.DeserializeObject<ComfyUploadResult>(await PostMultipartAsync("/upload/image", form));
        }

        // ===== POST /interrupt : 取消当前正在执行的任务 =====
        public async Task InterruptAsync()
        {
            await PostJsonAsync("/interrupt", "{}");
        }

        // ===== GET /queue : 获取详细队列信息（运行中 + 挂起） =====
        public async Task<ComfyQueue> GetQueueAsync()
        {
            return JsonConvert.DeserializeObject<ComfyQueue>(await GetTextAsync("/queue"));
        }

        // ===== POST /queue : 删除指定任务 =====
        public async Task DeleteFromQueueAsync(string promptId)
        {
            var body = new JObject { ["delete"] = new JArray(promptId), ["clear"] = false };
            await PostJsonAsync("/queue", body.ToString(Formatting.None));
        }

        // ===== POST /queue : 清空整个队列 =====
        public async Task ClearQueueAsync()
        {
            await PostJsonAsync("/queue", new JObject { ["clear"] = true }.ToString(Formatting.None));
        }

        // ===== GET /embeddings : 获取 embeddings 列表 =====
        public async Task<JToken> GetEmbeddingsAsync()
        {
            return JToken.Parse(await GetTextAsync("/embeddings"));
        }

        // ===== GET /extensions : 获取扩展节点文件列表 =====
        public async Task<JToken> GetExtensionsAsync()
        {
            return JToken.Parse(await GetTextAsync("/extensions"));
        }

        // ===== POST /upload/mask : 上传蒙版图片（局部重绘用） =====
        public async Task UploadMaskAsync(byte[] data, string originalRef, string type = "input", string subfolder = "")
        {
            var form = new List<IMultipartFormSection>();
            form.Add(new MultipartFormFileSection("image", data, "mask.png", "image/png"));
            form.Add(new MultipartFormDataSection("type", type));
            form.Add(new MultipartFormDataSection("subfolder", subfolder));
            form.Add(new MultipartFormDataSection("original_ref", originalRef));
            await PostMultipartAsync("/upload/mask", form);
        }

        // ---------------- 底层 HTTP ----------------

        /// <summary>
        /// 跨版本兼容的 await 包装：用 TaskCompletionSource + AsyncOperation.completed 回调，
        /// 不依赖 UnityEngine 为 UnityWebRequestAsyncOperation 提供的 GetAwaiter 扩展方法
        /// （在带 .asmdef 的脚本目录中该扩展方法常因程序集引用问题不可用）。
        /// AsyncOperation.completed 始终在 Unity 主线程触发，故 await 后续代码也跑在主线程，安全访问 Unity API。
        /// </summary>
        private static Task<UnityWebRequest> SendAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            req.SendWebRequest().completed += _ => tcs.TrySetResult(req);
            return tcs.Task;
        }

        private async Task<string> GetTextAsync(string path)
        {
            using (var req = UnityWebRequest.Get(BaseUrl + path))
            {
                await SendAsync(req);
                EnsureSuccess(req);
                return req.downloadHandler.text;
            }
        }

        private async Task<byte[]> GetBytesAsync(string path)
        {
            using (var req = UnityWebRequest.Get(BaseUrl + path))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                await SendAsync(req);
                EnsureSuccess(req);
                return req.downloadHandler.data;
            }
        }

        private async Task<string> PostJsonAsync(string path, string json)
        {
            using (var req = new UnityWebRequest(BaseUrl + path, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                await SendAsync(req);
                EnsureSuccess(req);
                return req.downloadHandler.text;
            }
        }

        private async Task<string> PostMultipartAsync(string path, List<IMultipartFormSection> sections)
        {
            using (var req = UnityWebRequest.Post(BaseUrl + path, sections))
            {
                await SendAsync(req);
                EnsureSuccess(req);
                return req.downloadHandler.text;
            }
        }

        private static void EnsureSuccess(UnityWebRequest req)
        {
            if (req.result != UnityWebRequest.Result.Success)
                throw new ComfyApiException($"HTTP {(int)req.responseCode}: {req.error} {req.downloadHandler?.text}");
        }
    }
}
