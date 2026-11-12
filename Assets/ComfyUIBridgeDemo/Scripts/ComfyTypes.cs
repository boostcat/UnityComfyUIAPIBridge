using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ComfyUI.API
{
    /// <summary>
    /// 工作流中的单个节点（ComfyUI API JSON 格式）。
    /// inputs 使用 JObject 以兼容任意节点参数（含 ["节点ID", 输出序号] 形式的连线引用）。
    /// </summary>
    [System.Serializable]
    public class ComfyNode
    {
        [JsonProperty("class_type")] public string ClassType;
        [JsonProperty("inputs")] public JObject Inputs = new JObject();
        [JsonProperty("_meta")] public JObject Meta;
    }

    /// <summary>
    /// 工作流节点图构造器。通过 Node() 添加节点，最后 ToJObject() 得到提交给 /prompt 的 graph。
    /// </summary>
    public class ComfyGraph
    {
        private readonly Dictionary<string, ComfyNode> _nodes = new Dictionary<string, ComfyNode>();

        public ComfyGraph Node(string id, string classType, JObject inputs, string title = null)
        {
            var node = new ComfyNode { ClassType = classType, Inputs = inputs };
            if (!string.IsNullOrEmpty(title)) node.Meta = new JObject { ["title"] = title };
            _nodes[id] = node;
            return this;
        }

        public JObject ToJObject() => JObject.FromObject(_nodes);

        public Dictionary<string, ComfyNode> Nodes => _nodes;
    }

    /// <summary> 图片引用，/view 下载与历史 outputs 共用。 </summary>
    [System.Serializable]
    public class ComfyImageRef
    {
        public string filename;
        public string subfolder;
        public string type;
    }

    /// <summary> 单条历史任务（对应 GET /history 或 /history/{prompt_id} 的 value）。 </summary>
    [System.Serializable]
    public class ComfyHistoryEntry
    {
        [JsonProperty("prompt")] public JArray Prompt;
        [JsonProperty("outputs")] public Dictionary<string, JObject> Outputs = new Dictionary<string, JObject>();
        [JsonProperty("status")] public ComfyStatus Status = new ComfyStatus();

        /// <summary> 从指定 SaveImage 节点的输出中提取图片引用列表。 </summary>
        public List<ComfyImageRef> GetImages(string nodeId)
        {
            var list = new List<ComfyImageRef>();
            if (Outputs != null && Outputs.TryGetValue(nodeId, out var nodeOut) && nodeOut["images"] is JArray arr)
            {
                foreach (var t in arr) list.Add(t.ToObject<ComfyImageRef>());
            }
            return list;
        }
    }

    [System.Serializable]
    public class ComfyStatus
    {
        [JsonProperty("status_str")] public string StatusStr;
        public bool completed;
        public JArray messages; // [ [type, data], ... ]
    }

    [System.Serializable]
    public class ComfyQueueRemaining
    {
        public ExecInfo exec_info;
    }

    [System.Serializable]
    public class ExecInfo
    {
        public int queue_remaining;
    }

    [System.Serializable]
    public class ComfyQueue
    {
        public JArray queue_running;
        public JArray queue_pending;
    }

    [System.Serializable]
    public class ComfyPromptResponse
    {
        public string prompt_id;
        public int number;
        public JObject node_errors;
    }

    [System.Serializable]
    public class ComfyPromptError
    {
        public ComfyErrorDetail error;
        public JObject node_errors;
    }

    [System.Serializable]
    public class ComfyErrorDetail
    {
        public string type;
        public string message;
        public string details;
        public JObject extra_info;
    }

    [System.Serializable]
    public class ComfyUploadResult
    {
        public string name;
        public string subfolder;
        public string type;
    }
}
