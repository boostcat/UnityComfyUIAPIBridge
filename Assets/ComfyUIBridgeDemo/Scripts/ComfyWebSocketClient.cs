using System;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace ComfyUI.API
{
    public sealed class ComfyWebSocketClient : IDisposable
    {
        private string _address;
        private SimpleWebSocket _ws;
        private Timer _reconnectTimer;
        private bool _disposed;
        private bool _wantConnected;

        public string Url { get; private set; }

        public string ClientId { get; private set; }

        public bool AutoReconnect { get; set; }

        public float ReconnectDelay { get; set; }

        public bool IsOpen => _ws != null && _ws.IsOpen;

        public Action<Action> Dispatcher { get; set; }

        public string ActivePromptId { get; set; }

        public event Action Opened;

        public event Action<UInt16, string> Closed;

        public event Action<string> ErrorOccurred;

        public event Action<string> StateChanged;

        public event Action<string, JObject, string> MessageReceived;

        public event Action<string, byte[]> ImageReceived;

        public event Action<byte[]> UnknownBinaryReceived;

        public ComfyWebSocketClient(string wsAddress, string clientId, bool autoReconnect, float reconnectDelay)
        {
            _address = wsAddress;
            ClientId = clientId;
            AutoReconnect = autoReconnect;
            ReconnectDelay = reconnectDelay;
        }

        public bool Connect()
        {
            if (_disposed) return false;
            if (IsOpen) return true;
            if (_ws != null && _ws.IsAlive) return true;
            if (string.IsNullOrWhiteSpace(_address))
            {
                Debug.LogWarning("[WS] WebSocket 地址为空，无法连接");
                return false;
            }

            _wantConnected = true;
            Url = ComfyWsProtocol.BuildUrl(_address, ClientId);
            try
            {
                Debug.Log("[WS] 连接实时进度 WebSocket：" + Url);
                var socket = new SimpleWebSocket(new Uri(Url));
                socket.OnOpen += HandleOpen;
                socket.OnMessage += HandleMessage;
                socket.OnBinary += HandleBinary;
                socket.OnClosed += HandleClosed;
                socket.OnError += HandleError;
                _ws = socket;
                socket.Open();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[WS] 连接初始化失败：" + e.Message);
                PostError("[WS] 连接初始化失败：" + e.Message);
                PostState("[WS] 连接失败");
                ScheduleReconnect();
                return false;
            }
        }

        public void Close()
        {
            _wantConnected = false;
            DisposeTimer();
            var socket = _ws;
            _ws = null;
            if (socket == null) return;
            Unbind(socket);
            try { socket.Close(); }
            catch (Exception e) { Debug.LogWarning("[WS] 关闭连接异常：" + e.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            Opened = null;
            Closed = null;
            ErrorOccurred = null;
            StateChanged = null;
            MessageReceived = null;
            ImageReceived = null;
            UnknownBinaryReceived = null;
        }

        private void HandleOpen(SimpleWebSocket w)
        {
            Debug.Log("[WS] 连接已打开");
            Post(() => Opened?.Invoke());
            PostState("[WS] 已连接");
        }

        private void HandleMessage(SimpleWebSocket w, string message)
        {
            Debug.Log("[WS] 消息：" + message);
            string type;
            JObject data;
            try
            {
                var jo = JObject.Parse(message);
                type = jo["type"]?.ToString() ?? "unknown";
                data = jo["data"] as JObject;
            }
            catch (Exception e)
            {
                Debug.LogError("[WS] 解析 WS 消息失败：" + e.Message);
                PostError("[WS] 解析消息失败：" + e.Message);
                return;
            }
            Post(() => MessageReceived?.Invoke(type, data, message));
        }

        private void HandleBinary(SimpleWebSocket w, byte[] data)
        {
            try
            {
                string pid;
                byte[] png = ComfyWsProtocol.DecodeImage(data, out pid);
                if (png == null)
                {
                    byte first = (data != null && data.Length > 0) ? data[0] : (byte)0;
                    bool maybePng = data != null && data.Length >= 4
                        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
                    Debug.Log(
                        "[WS] 收到非图片二进制帧（长度=" + (data?.Length ?? 0) + "），忽略。" +
                        " 首字节=0x" + first.ToString("X2") + "（" + first + "）。" +
                        " 是否旧协议(0x03)=" + (first == 0x03) + "。" +
                        " 是否PNG头(0x89 50 4E 47)=" + maybePng + "。" +
                        " 前 64 字节(hex)=" + ComfyWsProtocol.DumpHexPrefix(data, 64));
                    Post(() => UnknownBinaryReceived?.Invoke(data));
                    return;
                }

                if (string.IsNullOrEmpty(pid))
                {
                    pid = ActivePromptId;
                    if (string.IsNullOrEmpty(pid))
                    {
                        Debug.LogWarning("[WS] 收到裸 PNG 图片，但没有活跃的 ActivePromptId，丢弃图片");
                        return;
                    }
                    Debug.Log("[WS] 检测新版裸 PNG 协议，使用活跃任务 pid=" + pid);
                }

                bool strippedPrefix = data != null && data.Length > 0 && data[0] != 0x89;
                Debug.Log($"[WS] 收到二进制图片帧：prompt_id={pid}，PNG 字节={png.Length}" +
                         (strippedPrefix ? "（已剥离前导非 PNG 前缀）" : ""));

                Post(() => ImageReceived?.Invoke(pid, png));
            }
            catch (Exception e)
            {
                Debug.LogError("[WS] 解析二进制图片失败：" + e.Message);
            }
        }

        private void HandleClosed(SimpleWebSocket w, UInt16 code, string message)
        {
            Debug.Log("[WS] 连接关闭：code=" + code + " msg=" + message);
            CleanupSocket(w);
            var c = code;
            var m = message;
            Post(() => Closed?.Invoke(c, m));
            PostState("[WS] 已关闭");
            ScheduleReconnect();
        }

        private void HandleError(SimpleWebSocket w, string error)
        {
            Debug.LogError("[WS] 错误：" + error);
            PostError(error);
            PostState("[WS] 错误");

            CleanupSocket(w);
            ScheduleReconnect();
        }

        private void CleanupSocket(SimpleWebSocket w)
        {
            if (w == null) return;
            Unbind(w);
            if (ReferenceEquals(w, _ws)) _ws = null;
        }

        private void Post(Action action)
        {
            if (action == null) return;
            var d = Dispatcher;
            if (d != null) d(action);
            else action();
        }

        private void PostState(string text) => Post(() => StateChanged?.Invoke(text));

        private void PostError(string error) => Post(() => ErrorOccurred?.Invoke(error));

        private void ScheduleReconnect()
        {
            if (_disposed || !AutoReconnect || !_wantConnected) return;
            if (_reconnectTimer != null) return;
            _reconnectTimer = new Timer(_ =>
            {
                DisposeTimer();
                if (_wantConnected && !IsOpen) Connect();
            }, null, (long)(Math.Max(0.1f, ReconnectDelay) * 1000), Timeout.Infinite);
        }

        private void DisposeTimer()
        {
            var t = _reconnectTimer;
            _reconnectTimer = null;
            if (t != null) t.Dispose();
        }

        private void Unbind(SimpleWebSocket socket)
        {
            if (socket == null) return;
            socket.OnOpen -= HandleOpen;
            socket.OnMessage -= HandleMessage;
            socket.OnBinary -= HandleBinary;
            socket.OnClosed -= HandleClosed;
            socket.OnError -= HandleError;
        }
    }

    public static class ComfyWsProtocol
    {
        public static string BuildUrl(string address, string clientId)
        {
            var addr = (address ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clientId) || addr.Contains("clientId=")) return addr;
            return addr + (addr.Contains("?") ? "&" : "?") + "clientId=" + clientId;
        }

        public static byte[] DecodeImage(byte[] buf, out string outPromptId)
        {
            outPromptId = null;
            if (buf == null || buf.Length < 8)
                return null;

            if (buf[0] == 0x03)
            {
                if (buf.Length < 9) return null;

                ulong strLen = 0;
                for (int i = 0; i < 8; i++)
                    strLen = (strLen << 8) | buf[1 + i];

                int idStart = 1 + 8;
                if (idStart + (int)strLen > buf.Length) return null;

                outPromptId = Encoding.UTF8.GetString(buf, idStart, (int)strLen);

                int pngStart = idStart + (int)strLen;
                int pngLen = buf.Length - pngStart;
                if (pngLen <= 0) return null;

                byte[] pngData = new byte[pngLen];
                Array.Copy(buf, pngStart, pngData, 0, pngLen);
                return pngData;
            }

            int sigIdx = FindPngSignature(buf, 0, 32);
            if (sigIdx >= 0)
            {
                byte[] pngData = new byte[buf.Length - sigIdx];
                Array.Copy(buf, sigIdx, pngData, 0, pngData.Length);
                return pngData;
            }

            return null;
        }

        private static int FindPngSignature(byte[] buf, int startOffset, int windowSize)
        {
            int sigLen = 8;
            if (buf == null || buf.Length < sigLen) return -1;
            int lastStart = Math.Min(buf.Length - sigLen, startOffset + windowSize);
            for (int i = startOffset; i <= lastStart; i++)
            {
                if (buf[i] == 0x89 && buf[i + 1] == 0x50 && buf[i + 2] == 0x4E && buf[i + 3] == 0x47
                    && buf[i + 4] == 0x0D && buf[i + 5] == 0x0A && buf[i + 6] == 0x1A && buf[i + 7] == 0x0A)
                    return i;
            }
            return -1;
        }

        public static string DumpHexPrefix(byte[] buf, int maxBytes)
        {
            if (buf == null) return "null";
            int n = Math.Min(maxBytes, buf.Length);
            var sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                sb.Append(buf[i].ToString("X2"));
                sb.Append(' ');
            }
            if (buf.Length > n) sb.Append("...");
            return sb.ToString();
        }

        public static string BuildProgressDetail(string type, JObject data)
        {
            if (data == null) return null;
            switch (type)
            {
                case "status":
                {
                    var qr = data["status"]?["exec_info"]?["queue_remaining"];
                    return qr != null ? $"队列剩余：{qr}" : null;
                }
                case "execution_start":
                    return $"开始执行任务（prompt_id={data["prompt_id"]}）";
                case "execution_cached":
                {
                    var nodes = data["nodes"] as JArray;
                    if (nodes != null) return $"命中缓存节点：{string.Join(", ", nodes.Select(n => n.ToString()))}";
                    return "命中缓存";
                }
                case "executing":
                {
                    var nodeTok = data["node"];
                    string node = (nodeTok != null && nodeTok.Type != JTokenType.Null) ? nodeTok.ToString() : null;
                    return string.IsNullOrEmpty(node) ? "执行完成！" : $"正在执行节点：{node}";
                }
                case "progress_state":
                {
                    var sb = new StringBuilder();
                    if (data["nodes"] is JObject nd)
                    {
                        foreach (var n in nd.Properties())
                        {
                            var ni = n.Value;
                            var val = ni["value"];
                            var max = ni["max"];
                            var state = ni["state"]?.ToString();
                            sb.Append($"节点{n.Name}: {state} {val}/{max}  ");
                        }
                    }
                    return sb.Length > 0 ? sb.ToString().Trim() : null;
                }
                case "progress":
                {
                    int value = data["value"] != null ? (int)data["value"] : 0;
                    int max = data["max"] != null ? (int)data["max"] : 0;
                    string node = data["node"]?.ToString();
                    float pct = max > 0 ? (float)value / max * 100f : 0f;
                    return $"节点{node} 进度：{value}/{max}（{pct:F0}%）";
                }
                case "executed":
                {
                    string outNode = data["node"]?.ToString();
                    var imgs = data["output"]?["images"] as JArray;
                    string fn = (imgs != null && imgs.Count > 0) ? imgs[0]["filename"]?.ToString() : null;
                    return $"节点{outNode} 完成" + (fn != null ? $"，输出图片：{fn}" : "，无图片输出");
                }
                case "execution_success":
                    return "任务执行成功";
                case "execution_error":
                    return "执行错误";
                case "execution_interrupted":
                    return "执行已中断";
                default:
                    return null;
            }
        }

        public static string TypeToChinese(string type)
        {
            switch (type)
            {
                case "status": return "状态";
                case "execution_start": return "开始执行";
                case "execution_cached": return "命中缓存";
                case "executing": return "执行节点";
                case "progress_state": return "进度状态";
                case "progress": return "进度";
                case "executed": return "节点完成";
                case "execution_success": return "执行成功";
                case "execution_error": return "执行错误";
                case "execution_interrupted": return "已中断";
                default: return string.IsNullOrEmpty(type) ? "未知" : type;
            }
        }

        public static string BuildMessageSummary(string type, JObject data)
        {
            if (data == null) return TypeToChinese(type);
            switch (type)
            {
                case "status":
                {
                    var qr = data["status"]?["exec_info"]?["queue_remaining"];
                    return "状态：队列剩余 " + (qr != null ? qr.ToString() : "?");
                }
                case "execution_start":
                    return "开始执行任务（" + (data["prompt_id"]?.ToString() ?? "") + "）";
                case "execution_cached":
                {
                    var nodes = data["nodes"] as JArray;
                    return "命中缓存节点：" + (nodes != null ? string.Join(", ", nodes.Select(n => n.ToString())) : "?");
                }
                case "executing":
                {
                    var nodeTok = data["node"];
                    string node = (nodeTok != null && nodeTok.Type != JTokenType.Null) ? nodeTok.ToString() : null;
                    return string.IsNullOrEmpty(node) ? "执行完成！" : "正在执行节点 " + node;
                }
                case "progress_state":
                {
                    var sb = new StringBuilder();
                    if (data["nodes"] is JObject nd)
                    {
                        foreach (var n in nd.Properties())
                        {
                            var ni = n.Value;
                            string state = ni["state"]?.ToString() ?? "";
                            if (state.Equals("finished", StringComparison.OrdinalIgnoreCase)) continue;
                            sb.Append($"节点{n.Name} {state} {ni["value"]}/{ni["max"]}；");
                        }
                    }
                    if (sb.Length == 0) return "进度状态：全部节点已完成";
                    return sb.ToString().TrimEnd('；');
                }
                case "progress":
                {
                    int value = data["value"] != null ? (int)data["value"] : 0;
                    int max = data["max"] != null ? (int)data["max"] : 0;
                    string node = data["node"]?.ToString();
                    float pct = max > 0 ? (float)value / max * 100f : 0f;
                    return $"节点{node} 进度 {value}/{max}（{pct:F0}%）状态：运行中";
                }
                case "executed":
                {
                    string node = data["node"]?.ToString();
                    var imgs = data["output"]?["images"] as JArray;
                    string fn = (imgs != null && imgs.Count > 0) ? imgs[0]["filename"]?.ToString() : null;
                    return "节点" + node + " 完成" + (fn != null ? "，输出：" + fn : "");
                }
                case "execution_success":
                    return "任务执行成功";
                case "execution_error":
                    return "执行错误";
                case "execution_interrupted":
                    return "执行已中断";
                default:
                    return TypeToChinese(type);
            }
        }
    }
}
