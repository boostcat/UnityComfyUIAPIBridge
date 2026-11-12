using ComfyUI.API;
using ComfyUI.Definition;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ComfyUI.Demo
{
    public class ComfyUIDemo : MonoBehaviour
    {
        [Header("UI 引用")]
        [SerializeField] private TMP_Dropdown workflowDropdown;   // 工作流下拉框
        [SerializeField] private TMP_Dropdown samplerDropdown;     // 采样器下拉框
        [SerializeField] private TMP_Dropdown schedulerDropdown;   // 调度器下拉框
        [SerializeField] private TMP_InputField seedInput;         // 随机种子输入框
        [SerializeField] private TMP_InputField stepsInput;        // 采样步数输入框
        [SerializeField] private TMP_InputField cfgInput;          // CFG 输入框
        [SerializeField] private TMP_InputField denoiseInput;      // 降噪输入框
        [SerializeField] private TMP_InputField widthInput;        // 宽度输入框
        [SerializeField] private TMP_InputField heightInput;       // 高度输入框
        [SerializeField] private TextMeshProUGUI checkpointText;   // Checkpoint 名称文本

        // 地址 / 提示词 / 图片上传 / 任务查询
        [SerializeField] private TMP_InputField httpAddressInput;      // HTTP 地址输入框
        [SerializeField] private TMP_InputField wsAddressInput;       // WebSocket 地址输入框
        [SerializeField] private TMP_InputField promptInput;          // 正面提示词输入框
        [SerializeField] private TMP_InputField negativePromptInput;  // 负面提示词输入框
        [SerializeField] private TMP_InputField imageFileInput;       // 本地图片路径输入框
        [SerializeField] private TMP_InputField taskIdInput;         // 任务 ID 输入框

        // 按钮
        [SerializeField] private Button publishTaskButton;       // 发布绘图任务按钮
        [SerializeField] private Button connectionTestButton;    // 连接测试按钮（HTTP）
        [SerializeField] private Button uploadImageButton;       // 上传图片按钮
        [SerializeField] private Button refreshHistoryButton;    // 刷新历史任务按钮
        [SerializeField] private Button refreshQueueButton;      // 刷新队列按钮
        [SerializeField] private Button refreshWorkflowButton;   // 刷新工作流按钮
        [SerializeField] private Button queryTaskButton;         // 查询任务按钮
        [SerializeField] private Button disconnectButton;        // 断开连接按钮
        [SerializeField] private Button clearMessagesButton;     // 清空消息按钮
        [SerializeField] private Button useEmbeddingButton;      // 使用 Embedding 按钮
        [SerializeField] private Button refreshEmbeddingsButton; // 刷新 Embeddings 按钮
        [SerializeField] private Button refreshPreviewButton;    // 刷新预览按钮

        // 文本状态
        [SerializeField] private TextMeshProUGUI connectionStatusTxt; // 连接状态文本（HTTP 连接测试结果）
        [SerializeField] private TextMeshProUGUI httpStatusTxt;       // HTTP 状态文本
        [SerializeField] private TextMeshProUGUI wsStatusTxt;         // WebSocket 状态文本
        [SerializeField] private TextMeshProUGUI progressStatusTxt;   // 进度状态文本
        [SerializeField] private TextMeshProUGUI uploadStatusTxt;     // 上传状态文本
        [SerializeField] private TextMeshProUGUI historyStatusTxt;   // 历史任务状态文本
        [SerializeField] private TextMeshProUGUI queueSummaryTxt;     // 队列概要文本
        [SerializeField] private TextMeshProUGUI queueStateTxt;       // 队列状态文本
        [SerializeField] private TextMeshProUGUI previewInfoTxt;      // 预览信息文本
        [SerializeField] private TextMeshProUGUI systemInfoTxt;       // 系统信息文本
        [SerializeField] private Slider progressSlider;               // 进度条

        // 结果预览
        [SerializeField] private RawImage resultRawImage;          // 结果预览 RawImage
        [SerializeField] private AspectRatioFitter resultAspectFitter; // 结果预览 AspectRatioFitter（长按/自动计算长宽比）

        [Header("UI 根节点")]
        [SerializeField] private Canvas rootCanvas;   // UI 根 Canvas 引用（作为按名称查找的起点，必须从 Inspector 指定）

        // 预览放大图（点击预览图居中显示，再次点击关闭）
        private Texture2D _currentPreviewTex;   // 当前预览纹理，供放大图复用
        private GameObject _zoomOverlay;        // 全屏遮罩（大图容器）
        private RawImage _zoomImage;            // 居中显示的大图 RawImage

        // Message 滚动列表：与 Embeddings/History/Queue 统一逻辑（Prefab 隐藏，新消息插到索引 0）
        private ScrollList messageList;       // 实时消息列表

        // Embeddings / History / Queue 三个滚动列表：统一采用
        // Scroll -> Viewport -> Content -> 第一个子物体作为 Prefab（隐藏），其余子物体初始化时删除
        private ScrollList embeddingList;   // Embeddings 列表
        private ScrollList historyList;     // 历史任务列表
        private ScrollList queueList;       // 队列列表

        // 工作流显示名(文件名) -> Resources 加载路径（位于 Resources/工作流 下，不含扩展名）
        private readonly Dictionary<string, string> _workflowFiles = new Dictionary<string, string>();
        // 当前选中工作流解析后的节点图（供“提交任务”等扩展使用）
        private JObject _currentGraph;

        // 运行时对象
        private ComfyClient client;
        // 实时进度 WebSocket：通用封装（地址拼接/建连/重连/消息解析/二进制图片解码见 ComfyWebSocketClient）
        private ComfyWebSocketClient wsClient;
        // WebSocket 回调发生在网络线程，统一投递到主线程队列，由 Update 在主线程执行，避免跨线程操作 Unity 对象
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private bool queuePolling = false;          // 常规 3 秒队列轮询是否运行
        private bool autoQueueRefreshing = false;    // 发布任务后的“每1秒自动刷新队列”是否运行
        private int lastQueueRunning = 0;            // RefreshQueueAsync 最近一次的运行中数量（用于判断队列是否空）
        private int lastQueuePending = 0;            // RefreshQueueAsync 最近一次的等待数量
        private long lastSeed = 0;                   // 最近一次自动生成的随机种子（作为 seedInput 缺失时的兜底）

        private readonly Dictionary<string, List<byte[]>> _wsImages = new Dictionary<string, List<byte[]>>();
        private readonly object _wsImageLock = new object();
        private string _activePromptId;              // 当前发布绘图任务的 prompt_id（用于日志/匹配）

        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _wsDoneSignals =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>();

        private class ScrollList
        {
            public Transform Content;        // Viewport/Content 节点
            public GameObject Prefab;        // Content 第一个子物体（隐藏，作为实例化模板）
            public readonly List<GameObject> Items = new List<GameObject>();
        }

        private void Start()
        {
            //创建HTTP客户端和WebSocket客户端
            client = new ComfyClient(ComfyUIConfig.HttpAddress, ComfyUIConfig.ClientId);
            if (wsClient != null) return;
            wsClient = new ComfyWebSocketClient(ComfyUIConfig.WsAddress, ComfyUIConfig.ClientId, ComfyUIConfig.WsAutoReconnect, ComfyUIConfig.WsReconnectDelay);
            wsClient.Dispatcher = a => _mainThreadActions.Enqueue(a);   // 回调转主线程执行
            wsClient.StateChanged += s => SetText(wsStatusTxt, s);
            wsClient.MessageReceived += WsOnMessage;
            wsClient.ImageReceived += WsOnImage;   // SaveImageWebsocket 的二进制图片帧
            wsClient.Connect();

            InitDropdownOptions();
            LoadWorkflowList();
            InitListTemplates();
            BindEvents();

            // 自动填充默认地址
            httpAddressInput.text = ComfyUIConfig.HttpAddress;
            wsAddressInput.text = ComfyUIConfig.WsAddress;

            if (workflowDropdown != null && workflowDropdown.options.Count > 0)
            {
                workflowDropdown.value = 0;
                OnWorkflowSelected(0);
            }

            Debug.Log("[HTTP] 队列轮询暂不启动，将在发布任务后开启");

            SetText(systemInfoTxt, "就绪 · " + DateTime.Now.ToString("HH:mm:ss"));
            Debug.Log("初始化完成");
        }

        // 主线程泵：执行 WebSocket 回调投递过来的动作（UI 操作必须在主线程）
        private void Update()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception e) { Debug.LogError("[WS] 主线程执行回调异常：" + e.Message); }
            }
        }

        private void OnDestroy()
        {
            queuePolling = false;
            wsClient?.Dispose();
            wsClient = null;
        }

        private static T FindChild<T>(Transform root, string name) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true))
                if (c.name == name) return c;
            return null;
        }

        private static GameObject FindGameObject(Transform root, params string[] names)
        {
            foreach (var name in names)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t.gameObject;
            }
            return null;
        }

        private static TextMeshProUGUI FindText(Transform root, params string[] names)
        {
            foreach (var n in names)
            {
                var t = FindChild<TextMeshProUGUI>(root, n);
                if (t != null) return t;
            }
            return null;
        }

        private static void SetText(TextMeshProUGUI t, string s)
        {
            if (t != null) t.text = s;
        }

        private static void SetChildText(Transform root, string name, string text)
        {
            var t = FindChild<TextMeshProUGUI>(root, name);
            if (t != null) t.text = text;
        }

        // ---------- 初始化采样器 / 调度器下拉基础选项 ----------
        private void InitDropdownOptions()
        {
            if (samplerDropdown != null) samplerDropdown.options = new List<TMP_Dropdown.OptionData>();
            if (schedulerDropdown != null) schedulerDropdown.options = new List<TMP_Dropdown.OptionData>();
            foreach (var s in ComfyUIConfig.BaseSamplers) samplerDropdown?.options.Add(new TMP_Dropdown.OptionData(s));
            foreach (var s in ComfyUIConfig.BaseSchedulers) schedulerDropdown?.options.Add(new TMP_Dropdown.OptionData(s));
            samplerDropdown?.RefreshShownValue();
            schedulerDropdown?.RefreshShownValue();
        }

        private void InitListTemplates()
        {
            embeddingList = InitScrollList("Embeddings Scroll", "Embeddings");
            historyList = InitScrollList("History Scroll", "History");
            queueList = InitScrollList("Queue Scroll", "Queue");

            messageList = InitScrollList("Message Scroll", "Message");

            if (embeddingList != null) _ = RefreshEmbeddingsAsync();

            Debug.Log("列表模板初始化完成（Embeddings/History/Queue 子元素已清空）");
        }

        private ScrollList InitScrollList(string scrollName, string label)
        {
            var scrollGo = FindGameObject(rootCanvas.transform, scrollName);
            if (scrollGo == null)
            {
                Debug.LogWarning($"[UI] 未找到 {label} 滚动视图：{scrollName}");
                return null;
            }
            var viewport = FindChild<RectTransform>(scrollGo.transform, "Viewport");
            if (viewport == null) viewport = scrollGo.transform as RectTransform;
            var content = FindChild<RectTransform>(viewport, "Content");
            if (content == null)
            {
                Debug.LogWarning($"[UI] {label} 滚动视图缺少 Content 节点");
                return null;
            }
            if (content.childCount == 0)
            {
                Debug.LogWarning($"[UI] {label} 的 Content 下没有子物体，缺少列表项 Prefab");
                return null;
            }
            // 第一个子物体作为 Prefab，其余全部删除 -> 初始化状态为空
            var prefab = content.GetChild(0).gameObject;
            for (int i = content.childCount - 1; i >= 1; i--)
                Destroy(content.GetChild(i).gameObject);
            prefab.SetActive(false);
            Debug.Log($"[UI] {label} 列表初始化：Prefab={prefab.name}，其余子元素已删除");
            return new ScrollList { Content = content.transform, Prefab = prefab };
        }

        private void LoadWorkflowList()
        {
            if (workflowDropdown == null) return;

            _workflowFiles.Clear();

            string folder = ComfyUIConfig.WorkflowFolderName;
            var assets = Resources.LoadAll<TextAsset>(folder);
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("Resources/" + folder + " 下没有工作流 JSON（TextAsset）");
                return;
            }
            Array.Sort(assets, (a, b) => StringComparer.Ordinal.Compare(a.name, b.name));

            var displays = new List<string>();
            foreach (var asset in assets)
            {
                if (asset == null) continue;
                string display = asset.name + ".json";
                string resourcePath = folder + "/" + asset.name;
                _workflowFiles[display] = resourcePath;
                displays.Add(display);
            }

            workflowDropdown.options = new List<TMP_Dropdown.OptionData>();
            foreach (var key in displays)
                workflowDropdown.options.Add(new TMP_Dropdown.OptionData(key));
            workflowDropdown.RefreshShownValue();
            Debug.Log($"已加载工作流 {_workflowFiles.Count} 个（来源：Resources/{folder}）");
        }

        private void BindEvents()
        {
            if (workflowDropdown != null)
                workflowDropdown.onValueChanged.AddListener(OnWorkflowSelected);

            publishTaskButton?.onClick.AddListener(OnPublishTask);
            connectionTestButton?.onClick.AddListener(OnConnectionTest);
            uploadImageButton?.onClick.AddListener(OnUploadImage);
            refreshHistoryButton?.onClick.AddListener(OnRefreshHistory);
            refreshQueueButton?.onClick.AddListener(OnRefreshQueue);
            refreshWorkflowButton?.onClick.AddListener(OnRefreshWorkflow);
            queryTaskButton?.onClick.AddListener(OnQueryTask);
            disconnectButton?.onClick.AddListener(OnDisconnect);
            clearMessagesButton?.onClick.AddListener(OnClearMessages);
            useEmbeddingButton?.onClick.AddListener(OnUseEmbedding);
            refreshEmbeddingsButton?.onClick.AddListener(OnRefreshEmbeddings);
            refreshPreviewButton?.onClick.AddListener(OnRefreshPreview);

            // 点击预览图 -> 居中显示大图
            if (resultRawImage != null)
            {
                var btn = resultRawImage.gameObject.GetComponent<Button>()
                           ?? resultRawImage.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(OnPreviewClick);
            }

            Debug.Log("事件绑定完成");
        }

        private void OnWorkflowSelected(int index)
        {
            if (workflowDropdown == null) return;
            if (index < 0 || index >= workflowDropdown.options.Count) return;
            string display = workflowDropdown.options[index].text;
            Debug.Log("切换工作流：" + display);
            // 读取“某个特定”工作流的内容（按需从 Resources 加载，而非一次性全部载入内存）
            if (_workflowFiles.TryGetValue(display, out var path))
            {
                string json = ReadWorkflowFile(path);
                if (!string.IsNullOrEmpty(json))
                    ApplyWorkflowValues(json);
            }
        }

        private string ReadWorkflowFile(string path)
        {
            try
            {
                var asset = Resources.Load<TextAsset>(path);
                if (asset == null)
                {
                    Debug.LogError("加载工作流资源失败：Resources/" + path);
                    return null;
                }
                return asset.text;
            }
            catch (Exception e)
            {
                Debug.LogError("读取工作流资源失败：" + path + " -> " + e.Message);
                return null;
            }
        }

        private void ApplyWorkflowValues(string json)
        {
            JObject graph;
            try { graph = JObject.Parse(json); }
            catch
            {
                Debug.LogError("工作流 JSON 解析失败");
                return;
            }
            _currentGraph = graph;

            string ckpt = null, sampler = null, scheduler = null, pos = "", neg = "";
            long seed = 0; int steps = 0, width = 0, height = 0; float cfg = 0, denoise = 0;
            bool hasSeed = false, hasSteps = false, hasCfg = false, hasDenoise = false, hasWidth = false, hasHeight = false;

            foreach (var node in graph.Properties())
            {
                var cls = node.Value["class_type"]?.ToString();
                var inputs = node.Value["inputs"] as JObject;
                if (inputs == null) continue;

                if (cls == "KSampler")
                {
                    if (inputs["seed"] is JValue jvSeed && jvSeed.Type == JTokenType.Integer)
                    { seed = (long)jvSeed; hasSeed = true; }
                    if (inputs["steps"] is JValue jvSteps && jvSteps.Type == JTokenType.Integer)
                    { steps = (int)jvSteps; hasSteps = true; }
                    if (inputs["cfg"] is JValue jvCfg && (jvCfg.Type == JTokenType.Float || jvCfg.Type == JTokenType.Integer))
                    { cfg = (float)jvCfg; hasCfg = true; }
                    if (inputs["denoise"] is JValue jvDen && (jvDen.Type == JTokenType.Float || jvDen.Type == JTokenType.Integer))
                    { denoise = (float)jvDen; hasDenoise = true; }
                    if (inputs["sampler_name"] is JValue jvSampler && jvSampler.Type == JTokenType.String)
                        sampler = jvSampler.ToString();
                    if (inputs["scheduler"] is JValue jvSched && jvSched.Type == JTokenType.String)
                        scheduler = jvSched.ToString();
                }
                else if (cls == "CheckpointLoaderSimple" || cls == "CheckpointLoader")
                {
                    if (inputs["ckpt_name"] is JValue jvCkpt && jvCkpt.Type == JTokenType.String)
                        ckpt = jvCkpt.ToString();
                }
                else if (cls == "EmptyLatentImage")
                {
                    if (inputs["width"] is JValue jvW && jvW.Type == JTokenType.Integer)
                    { width = (int)jvW; hasWidth = true; }
                    if (inputs["height"] is JValue jvH && jvH.Type == JTokenType.Integer)
                    { height = (int)jvH; hasHeight = true; }
                }
            }

            // 通过 KSampler 的 positive/negative 引用，从对应 CLIPTextEncode 中读取提示词
            ExtractPrompts(graph, out pos, out neg);

            // UI 回填
            if (checkpointText != null) checkpointText.text = ckpt ?? "-";
            if (seedInput != null && hasSeed) seedInput.text = (seed < 0 ? 0 : seed).ToString();
            if (stepsInput != null && hasSteps) stepsInput.text = steps.ToString();
            if (cfgInput != null && hasCfg) cfgInput.text = cfg.ToString();
            if (denoiseInput != null && hasDenoise) denoiseInput.text = denoise.ToString();
            if (widthInput != null && hasWidth) widthInput.text = width.ToString();
            if (heightInput != null && hasHeight) heightInput.text = height.ToString();
            SetDropdown(samplerDropdown, sampler);
            SetDropdown(schedulerDropdown, scheduler);
            if (promptInput != null) promptInput.text = pos;
            if (negativePromptInput != null) negativePromptInput.text = neg;

            // 打印加载出来的工作流信息
            Debug.Log($"加载工作流参数：Checkpoint={ckpt}, seed={seed}, steps={steps}, cfg={cfg}, denoise={denoise}, sampler={sampler}, scheduler={scheduler}, width={width}, height={height}");
            Debug.Log("正面提示词：" + (string.IsNullOrEmpty(pos) ? "(空)" : pos));
            Debug.Log("负面提示词：" + (string.IsNullOrEmpty(neg) ? "(空)" : neg));
        }

        // 通过 KSampler 的 positive/negative 连线，定位 CLIPTextEncode 节点并读取 text
        private void ExtractPrompts(JObject graph, out string positive, out string negative)
        {
            positive = negative = "";
            string posNode = null, negNode = null;
            foreach (var node in graph.Properties())
            {
                if (node.Value["class_type"]?.ToString() == "KSampler")
                {
                    var inputs = node.Value["inputs"] as JObject;
                    posNode = inputs?["positive"] is JArray p ? p[0]?.ToString() : null;
                    negNode = inputs?["negative"] is JArray n ? n[0]?.ToString() : null;
                    break;
                }
            }
            if (posNode != null) positive = ReadNodeText(graph, posNode);
            if (negNode != null) negative = ReadNodeText(graph, negNode);
        }

        private static string ReadNodeText(JObject graph, string nodeId)
        {
            var t = graph[nodeId]?["inputs"]?["text"];
            return t?.ToString() ?? "";
        }

        // 设置下拉值；若选项中不存在该值，则追加后再选中
        private static void SetDropdown(TMP_Dropdown dd, string value)
        {
            if (dd == null || string.IsNullOrEmpty(value)) return;
            int idx = dd.options.FindIndex(o => o.text == value);
            if (idx < 0)
            {
                dd.options.Add(new TMP_Dropdown.OptionData(value));
                idx = dd.options.Count - 1;
            }
            dd.value = idx;
            dd.RefreshShownValue();
        }

        public void OnConnectionTest()
        {
            Debug.Log("[HTTP] 点击 连接测试");
            _ = ConnectionTestAsync();
        }

        private async Task ConnectionTestAsync()
        {
            try
            {
                var remaining = await client.GetQueueRemainingAsync();
                Debug.Log("[HTTP] 连接测试成功，队列剩余=" + remaining.exec_info.queue_remaining);
                SetText(connectionStatusTxt, "[HTTP] 已连接 ✅");
                SetText(httpStatusTxt, "OK");
            }
            catch (Exception e)
            {
                Debug.LogError("[HTTP] 连接测试失败：" + e.Message);
                SetText(connectionStatusTxt, "[HTTP] 连接失败 ❌");
                SetText(httpStatusTxt, "FAIL");
            }
        }

        public void OnPublishTask()
        {
            Debug.Log("点击 发布绘图任务");
            _ = PublishTaskAsync();
        }

        private async Task PublishTaskAsync()
        {
            string pendingPromptId = null;   // 用于 finally 中注销该任务的 WS 结束信号
            try
            {
                long seed = ComfyUIConfig.Seed == -1 ? UnityEngine.Random.Range(0, int.MaxValue) : ComfyUIConfig.Seed;
                lastSeed = seed;
                if (seedInput != null) seedInput.text = seed.ToString();

                var graph = BuildCurrentGraph();
                if (graph == null)
                {
                    Debug.LogWarning("当前无可用工作流图，无法发布任务");
                    SetText(previewInfoTxt, "请先选择工作流");
                    return;
                }

                WritePromptsToGraph(graph);
                Debug.Log("提交绘图任务，节点数=" + graph.Count);
                SetText(previewInfoTxt, "提交中...");

                string promptId = await client.PostPromptAsync(graph, ComfyUIConfig.ClientId);
                pendingPromptId = promptId;
                _activePromptId = promptId;
                if (wsClient != null) wsClient.ActivePromptId = promptId;   // 裸 PNG 帧的归属兜底
                lock (_wsImageLock) { _wsImages.Remove(promptId); }   // 清理上一轮（若有）残留的二进制图片
                // 登记本任务的 WS 结束信号，供 execution_success 触发
                var doneTcs = new TaskCompletionSource<bool>();
                _wsDoneSignals[promptId] = doneTcs;
                Debug.Log("已提交任务，prompt_id=" + promptId);
                SetText(previewInfoTxt, "任务已提交：" + promptId);

                // 发布任务后自动每 1 秒刷新队列，直到队列为空停止
                _ = AutoRefreshQueueUntilEmpty();

                ComfyHistoryEntry entry = null;
                bool completed = await WaitForCompletionAsync(promptId, doneTcs);
                if (!completed)
                {
                    Debug.LogWarning("任务在超时时间内未完成");
                    SetText(previewInfoTxt, "任务超时未完成");
                    return;
                }
                Debug.Log("任务完成，开始取图...");

                for (int r = 0; r < 6; r++)
                {
                    entry = await client.GetHistoryAsync(promptId);
                    if (entry != null && entry.Status != null && entry.Status.completed) break;
                    await Task.Delay(200);
                }

                byte[] imageBytes = null;
                string displayName = promptId;
                const int binaryWaitMs = 500;
                const int binaryMaxWait = 20;   // 最多等待 ~10 秒
                for (int k = 0; k < binaryMaxWait; k++)
                {
                    lock (_wsImageLock)
                    {
                        if (_wsImages.TryGetValue(promptId, out var imgs) && imgs != null && imgs.Count > 0)
                        {
                            imageBytes = imgs[0];
                            break;
                        }
                    }
                    await Task.Delay(binaryWaitMs);
                }

                bool usedBinary = imageBytes != null;
                if (!usedBinary)
                {
                    // 回退：读取磁盘文件（保存图像节点返回的路径）
                    var images = GetOutputImages(entry);
                    if (images.Count == 0)
                    {
                        Debug.LogWarning("历史任务未包含输出图片，且未收到 WS 二进制图片");
                        SetText(previewInfoTxt, "未找到输出图片");
                        return;
                    }
                    var img = images[0];
                    displayName = img.filename;
                    Debug.Log($"读取磁盘图片 filename={img.filename} subfolder={img.subfolder} type={img.type}");
                    imageBytes = await client.GetViewAsync(img.filename, img.subfolder, img.type);
                }
                else
                {
                    Debug.Log("[WS] 优先使用 WebSocket 二进制图片，已跳过磁盘文件读取");
                }

                var tex = new Texture2D(2, 2);
                tex.LoadImage(imageBytes);
                ShowPreview(tex, displayName);
                SetText(previewInfoTxt, (usedBinary ? "生成完成（WS 二进制）：" : "生成完成（磁盘）：") + displayName);
            }
            catch (Exception e)
            {
                Debug.LogError("发布绘图任务异常：" + e);
                SetText(previewInfoTxt, "异常：" + e.Message);
            }
            finally
            {
                ReleaseWsTaskSignal(pendingPromptId);   // 无论成败都注销该任务的 WS 结束信号
            }
        }

        private async Task<bool> WaitForCompletionAsync(string promptId, TaskCompletionSource<bool> doneTcs)
        {
            if (wsClient != null && wsClient.IsOpen)
            {
                Debug.Log("[WS] 已连接，等待 execution_success 信号判断任务完成");
                var timeout = Task.Delay(TimeSpan.FromSeconds(180));
                var finished = await Task.WhenAny(doneTcs.Task, timeout);
                if (finished == doneTcs.Task)
                {
                    bool ok = doneTcs.Task.Result;
                    Debug.Log("[WS] 任务结束信号已到达，成功=" + ok);
                    return ok;
                }
                Debug.LogWarning("[WS] 等待 execution_success 超时，回退轮询 /history");
            }
            else
            {
                Debug.Log("[WS] 未连接，退化使用 /history 轮询判断任务完成");
            }

            for (int i = 0; i < 180; i++)
            {
                await Task.Delay(1000);
                var entry = await client.GetHistoryAsync(promptId);
                if (entry != null && entry.Status != null && entry.Status.completed) return true;
                Debug.Log($"等待任务完成... ({i + 1}s)");
            }
            return false;
        }

        private void MarkWsTaskDone(string promptId, bool success)
        {
            if (string.IsNullOrEmpty(promptId)) return;
            if (_wsDoneSignals.TryGetValue(promptId, out var tcs) && tcs != null)
            {
                Debug.Log("[WS] 任务结束信号置位，prompt_id=" + promptId + "，成功=" + success);
                tcs.TrySetResult(success);
            }
        }

        private void ReleaseWsTaskSignal(string promptId)
        {
            if (!string.IsNullOrEmpty(promptId)) _wsDoneSignals.TryRemove(promptId, out _);
        }

        private static List<ComfyImageRef> GetOutputImages(ComfyHistoryEntry entry)
        {
            var list = new List<ComfyImageRef>();
            if (entry?.Outputs == null) return list;
            foreach (var nodeOut in entry.Outputs.Values)
            {
                if (nodeOut?["images"] is JArray arr)
                    foreach (var t in arr) list.Add(t.ToObject<ComfyImageRef>());
            }
            return list;
        }

        private void ShowPreview(Texture2D tex, string name)
        {
            if (tex == null) return;
            resultRawImage.texture = tex;
            _currentPreviewTex = tex;

            // 自动计算长宽比并赋值给 AspectRatioFitter（不使用 SetNativeSize）
            if (resultAspectFitter != null && tex.height > 0)
            {
                float aspect = (float)tex.width / tex.height;
                resultAspectFitter.aspectRatio = aspect;
                Debug.Log($"[UI] 预览图长宽比已适配：{aspect:F3}（{tex.width}x{tex.height}）");
            }
            else if (resultAspectFitter == null)
            {
                Debug.LogWarning("[UI] 预览图未挂载 AspectRatioFitter，无法自动适配长宽比");
            }
        }

        // ---------- 点击预览图 -> 屏幕居中显示大图；点击大图 -> 关闭 ----------
        private void OnPreviewClick()
        {
            if (_currentPreviewTex == null)
            {
                Debug.Log("[UI] 暂无预览图可放大");
                return;
            }
            EnsureZoomOverlay();
            if (_zoomImage != null)
            {
                _zoomImage.texture = _currentPreviewTex;
                var zFit = _zoomImage.GetComponent<AspectRatioFitter>();
                if (zFit != null && _currentPreviewTex.height > 0)
                    zFit.aspectRatio = (float)_currentPreviewTex.width / _currentPreviewTex.height;
            }
            if (_zoomOverlay != null) _zoomOverlay.SetActive(true);
        }

        private void CloseZoom()
        {
            if (_zoomOverlay != null) _zoomOverlay.SetActive(false);
        }

        // 运行时构建全屏大图遮罩（无需在场景中预先放置）
        private void EnsureZoomOverlay()
        {
            if (_zoomOverlay != null) return;
            var canvas = (resultRawImage != null) ? resultRawImage.canvas : null;
            if (canvas == null) canvas = FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[UI] 未找到 Canvas，无法创建大图遮罩");
                return;
            }

            _zoomOverlay = new GameObject("PreviewZoomOverlay");
            _zoomOverlay.transform.SetParent(canvas.transform, false);
            var ovRT = _zoomOverlay.AddComponent<RectTransform>();
            ovRT.anchorMin = Vector2.zero;
            ovRT.anchorMax = Vector2.one;
            ovRT.sizeDelta = Vector2.zero;
            ovRT.anchoredPosition = Vector2.zero;
            var ovImg = _zoomOverlay.AddComponent<UnityEngine.UI.Image>();
            ovImg.color = new Color(0f, 0f, 0f, 0.85f);
            ovImg.raycastTarget = true;
            var ovBtn = _zoomOverlay.AddComponent<UnityEngine.UI.Button>();
            ovBtn.transition = Selectable.Transition.None;
            ovBtn.onClick.AddListener(CloseZoom);

            var zoomGo = new GameObject("ZoomRawImage");
            zoomGo.transform.SetParent(_zoomOverlay.transform, false);
            var zrt = zoomGo.AddComponent<RectTransform>();
            zrt.anchorMin = new Vector2(0.5f, 0.5f);
            zrt.anchorMax = new Vector2(0.5f, 0.5f);
            zrt.sizeDelta = Vector2.zero; // 最终尺寸由 AspectRatioFitter(FitInParent) 接管
            zrt.anchoredPosition = Vector2.zero;
            _zoomImage = zoomGo.AddComponent<RawImage>();
            _zoomImage.raycastTarget = true;
            var zFit = zoomGo.AddComponent<AspectRatioFitter>();
            zFit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            zFit.aspectRatio = 1f;

            _zoomOverlay.SetActive(false);
            Debug.Log("[UI] 大图遮罩已创建");
        }

        public void OnUploadImage()
        {
            _ = UploadImageAsync();
        }

        private async Task UploadImageAsync()
        {
            string path = imageFileInput?.text;
            if (string.IsNullOrWhiteSpace(path))
            {
                Debug.LogWarning("图片路径为空");
                SetText(uploadStatusTxt, "路径为空");
                return;
            }
            if (!File.Exists(path))
            {
                Debug.LogWarning("图片不存在：" + path);
                SetText(uploadStatusTxt, "文件不存在");
                return;
            }
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var result = await client.UploadImageAsync(data, Path.GetFileName(path));
                Debug.Log($"上传成功 name={result.name} subfolder={result.subfolder} type={result.type}");
                SetText(uploadStatusTxt, "上传成功：" + result.name);
            }
            catch (Exception e)
            {
                Debug.LogError("上传图片失败：" + e.Message);
                SetText(uploadStatusTxt, "上传失败");
            }
        }

        // =========================================================
        //  历史任务：刷新 /history 并填充列表
        // =========================================================
        public void OnRefreshHistory()
        {
            _ = RefreshHistoryAsync();
        }

        private async Task RefreshHistoryAsync()
        {
            try
            {
                var dict = await client.GetHistoryAsync();
                Debug.Log("[HTTP] 历史任务数量：" + dict.Count);
                var rows = new List<Dictionary<string, string>>();
                foreach (var kv in dict)
                {
                    var e = kv.Value;
                    string status;
                    if (e?.Status != null)
                        status = e.Status.completed ? "已完成"
                               : (string.IsNullOrEmpty(e.Status.StatusStr) ? "运行中" : e.Status.StatusStr);
                    else status = "-";
                    rows.Add(new Dictionary<string, string>
                    {
                        ["History Id Txt"] = kv.Key,
                        ["History Status Txt"] = status,
                        ["History Status Text"] = status
                    });
                }
                FillScrollList(historyList, rows, (go, row) =>
                {
                    var c = ColorForStatus(row["History Status Txt"]);
                    foreach (var n in new[] { "History Status Txt", "History Status Text" })
                    {
                        var t = FindChild<TextMeshProUGUI>(go.transform, n);
                        if (t != null) t.color = c;
                    }
                });
                SetText(historyStatusTxt, $"历史任务：{dict.Count} 条");
            }
            catch (Exception e)
            {
                Debug.LogError("[HTTP] 刷新历史任务失败：" + e.Message);
                SetText(historyStatusTxt, "刷新失败");
            }
        }

        // =========================================================
        //  队列信息：轮询 /queue（实时更新）
        // =========================================================
        public void OnRefreshQueue()
        {
            _ = RefreshQueueAsync();
        }

        private async void StartQueuePolling()
        {
            queuePolling = true;
            while (queuePolling)
            {
                try { await RefreshQueueAsync(); }
                catch (Exception e) { Debug.LogError("[HTTP] 队列轮询异常：" + e.Message); }
                await Task.Delay(3000);
            }
        }

        // 发布任务后：每 1 秒刷新一次队列，直到队列为空（运行中+等待均为0）才停止。
        // 期间暂停常规 3 秒轮询，避免重复刷新；结束后若原本开着轮询则恢复。
        private async Task AutoRefreshQueueUntilEmpty()
        {
            if (autoQueueRefreshing) return;
            autoQueueRefreshing = true;
            bool resumePolling = queuePolling;
            queuePolling = false;   // 暂停常规 3 秒轮询
            Debug.Log("[HTTP] 开始自动刷新队列：每1秒，直到队列为空");
            int guard = 0;
            try
            {
                while (guard++ < 600)   // 最多 ~10 分钟，防止异常情况下死循环
                {
                    await RefreshQueueAsync();
                    if (lastQueueRunning <= 0 && lastQueuePending <= 0)
                    {
                        Debug.Log("[HTTP] 队列已空（运行中=0 等待=0），停止自动刷新队列");
                        break;
                    }
                    await Task.Delay(1000);
                }
            }
            finally
            {
                autoQueueRefreshing = false;
                if (resumePolling) StartQueuePolling();   // 恢复常规轮询
                Debug.Log("[HTTP] 自动刷新队列已结束");
            }
        }

        private async Task<bool> RefreshQueueAsync()
        {
            try
            {
                var q = await client.GetQueueAsync();
                int running = q.queue_running?.Count ?? 0;
                int pending = q.queue_pending?.Count ?? 0;
                lastQueueRunning = running;
                lastQueuePending = pending;
                SetText(queueSummaryTxt, $"运行中：{running}   等待中：{pending}");
                var rows = new List<Dictionary<string, string>>();
                if (q.queue_running != null)
                    foreach (var item in q.queue_running) rows.Add(QueueRow(item, "运行中"));
                if (q.queue_pending != null)
                    foreach (var item in q.queue_pending) rows.Add(QueueRow(item, "等待"));
                FillScrollList(queueList, rows, (go, row) =>
                {
                    var c = ColorForStatus(row["Queue State Txt"]);
                    foreach (var n in new[] { "Queue State Txt", "Queue Status Txt", "Queue State Text" })
                    {
                        var t = FindChild<TextMeshProUGUI>(go.transform, n);
                        if (t != null) { t.text = row["Queue State Txt"]; t.color = c; }
                    }
                });
                SetText(queueStateTxt, $"队列更新：{DateTime.Now:HH:mm:ss}");
                Debug.Log($"[HTTP] 队列刷新成功：运行中={running} 等待中={pending}");
                return true;
            }
            catch (Exception e)
            {
                // 之前这里没有 try/catch，且 OnRefreshQueue 用 “_ =” 丢弃了 Task，
                // 导致 /queue 请求异常被静默吞掉：既无日志也无刷新。现在显式打印错误。
                Debug.LogError("[HTTP] 刷新队列失败：" + e.Message);
                SetText(queueStateTxt, "队列刷新失败");
                return false;
            }
        }

        private static Dictionary<string, string> QueueRow(JToken item, string type)
        {
            string pid = item is JArray arr && arr.Count > 1 ? arr[1]?.ToString() : "-";
            return new Dictionary<string, string>
            {
                ["Queue Id Txt"] = pid,
                ["Queue State Txt"] = type,
                ["Queue Status Txt"] = type
            };
        }

        // =========================================================
        //  查询单个任务（Task Id Input + Query Task Button）
        // =========================================================
        public void OnQueryTask()
        {
            Debug.Log("点击 查询任务");
            _ = QueryTaskAsync();
        }

        private async Task QueryTaskAsync()
        {
            string id = taskIdInput?.text;
            if (string.IsNullOrWhiteSpace(id))
            {
                Debug.LogWarning("任务ID为空");
                SetText(previewInfoTxt, "任务ID为空");
                return;
            }
            try
            {
                var entry = await client.GetHistoryAsync(id);
                if (entry == null)
                {
                    Debug.Log("未找到任务：" + id);
                    SetText(previewInfoTxt, "未找到任务");
                    return;
                }
                var images = GetOutputImages(entry);
                if (images.Count == 0)
                {
                    Debug.Log("任务无输出图片：" + id);
                    SetText(previewInfoTxt, "无输出图片");
                    return;
                }
                var img = images[0];
                byte[] bytes = await client.GetViewAsync(img.filename, img.subfolder, img.type);
                var tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                ShowPreview(tex, img.filename);
                Debug.Log("查询任务成功：" + img.filename);
            }
            catch (Exception e)
            {
                Debug.LogError("查询任务失败：" + e.Message);
            }
        }

        // ---------- WebSocket 文本消息：解析并展示实时进度 ----------
        private void WsOnMessage(string type, JObject data, string raw)
        {
            // 任务结束信号：execution_success 到达即代表该任务执行完成，可立即进入取图流程，
            // 不再需要轮询 /history 等待。execution_error / execution_interrupted 同样视为“已结束”（但非成功）。
            if (type == "execution_success" || type == "execution_error" || type == "execution_interrupted")
            {
                string donePid = data?["prompt_id"]?.ToString();
                if (string.IsNullOrEmpty(donePid)) donePid = _activePromptId;   // 消息未带 pid 时用活跃任务兜底
                MarkWsTaskDone(donePid, type == "execution_success");
            }

            // 解析并在实时进度 Txt 中展示更详细、可读的信息
            string detail = ComfyWsProtocol.BuildProgressDetail(type, data);
            if (!string.IsNullOrEmpty(detail))
            {
                SetText(progressStatusTxt, detail);
            }

            // 进度类消息同步驱动进度条
            if (type == "progress" && data != null)
            {
                int max = data["max"] != null ? (int)data["max"] : 0;
                int value = data["value"] != null ? (int)data["value"] : 0;
                if (progressSlider != null) progressSlider.value = max > 0 ? (float)value / max : 0f;
            }
            else if (type == "execution_success" && progressSlider != null)
            {
                progressSlider.value = 1f;
            }

            // status 消息同时刷新队列剩余信息（HTTP/WS 区分）
            if (type == "status" && data != null)
            {
                var q = data["status"]?["exec_info"]?["queue_remaining"];
                if (q != null) SetText(queueStateTxt, "[WS] 队列剩余：" + q);
            }

            AddMessage(ComfyWsProtocol.BuildMessageSummary(type, data), raw);
        }

        private void WsOnImage(string pid, byte[] png)
        {
            lock (_wsImageLock)
            {
                if (!_wsImages.TryGetValue(pid, out var list) || list == null)
                {
                    list = new List<byte[]>();
                    _wsImages[pid] = list;
                }
                list.Add(png);
                Debug.Log($"[WS] 已缓存二进制图片：prompt_id={pid}，PNG 字节={png.Length}（该任务累计 {list.Count} 张）");
            }
            // 实时提示：拿到图了（二进制可能晚于 execution_complete，这里补一个状态）
            SetText(progressStatusTxt, $"已收到二进制图片（{pid}）：{png.Length} 字节");
        }

        private void AddMessage(string type, string content)
        {
            if (messageList == null || messageList.Prefab == null)
            {
                Debug.LogWarning("[WS] 消息列表未初始化，无法添加消息");
                return;
            }
            var go = Instantiate(messageList.Prefab, messageList.Content);
            go.SetActive(true);
            SetChildText(go.transform, "Message Time Txt", DateTime.Now.ToString("HH:mm:ss"));
            SetChildText(go.transform, "Message Type Txt", type);
            SetChildText(go.transform, "Message Content Txt", content);
            go.transform.SetSiblingIndex(0);
            messageList.Items.Insert(0, go);

            const int maxMessages = 100;
            if (messageList.Items.Count > maxMessages)
            {
                int removeCount = messageList.Items.Count / 2;   // 删除最旧的一半
                for (int i = 0; i < removeCount; i++)
                {
                    int last = messageList.Items.Count - 1;
                    var old = messageList.Items[last];
                    messageList.Items.RemoveAt(last);
                    Destroy(old);
                }
                Debug.Log($"[WS] 消息数超过 {maxMessages}，已删除最旧的 {removeCount} 条，当前 {messageList.Items.Count} 条");
            }
        }

        public void OnDisconnect()
        {
            queuePolling = false;
            wsClient?.Close();
            SetText(wsStatusTxt, "[WS] 已断开");
        }

        public void OnClearMessages()
        {
            if (messageList != null)
            {
                foreach (var it in messageList.Items) Destroy(it);
                messageList.Items.Clear();   // 隐藏的首个 Prefab 仍保留，不被销毁
                Debug.Log("[UI] 消息列表已清空（保留隐藏 Prefab）");
            }
        }

        public void OnRefreshWorkflow()
        {
            LoadWorkflowList();
            if (workflowDropdown != null && workflowDropdown.options.Count > 0)
            {
                workflowDropdown.value = 0;
                OnWorkflowSelected(0);
            }
        }

        public void OnUseEmbedding()
        {
            Debug.Log("[UI] 点击 使用 Embedding（请使用列表项内的 Use Embedding 按钮）");
        }

        public void OnRefreshEmbeddings()
        {
            _ = RefreshEmbeddingsAsync();
        }

        private async Task RefreshEmbeddingsAsync()
        {
            try
            {
                var emb = await client.GetEmbeddingsAsync();
                var names = new List<string>();
                if (emb is JArray arr)
                    foreach (var t in arr) if (!string.IsNullOrEmpty(t?.ToString())) names.Add(t.ToString());
                else if (emb is JObject obj)
                    foreach (var p in obj.Properties()) names.Add(p.Name);

                Debug.Log("[HTTP] Embeddings 数量：" + names.Count);
                var rows = new List<Dictionary<string, string>>();
                foreach (var n in names)
                    rows.Add(new Dictionary<string, string> { ["Embedding Name Txt"] = n });

                FillScrollList(embeddingList, rows, (go, row) =>
                {
                    var name = row["Embedding Name Txt"];
                    var btn = FindChild<Button>(go.transform, "Use Embedding Button");
                    if (btn != null) btn.onClick.AddListener(() => UseEmbedding(name));
                });
                SetText(systemInfoTxt, "Embeddings：" + names.Count);
            }
            catch (Exception e)
            {
                Debug.LogError("[HTTP] 获取 Embeddings 失败：" + e.Message);
                SetText(systemInfoTxt, "Embeddings 获取失败");
            }
        }

        private void UseEmbedding(string name)
        {
            if (negativePromptInput != null) negativePromptInput.text = name;
            Debug.Log("[UI] 使用 Embedding：" + name + " -> 已填入反向提示词");
        }

        public void OnRefreshPreview()
        {
            Debug.Log("点击 刷新预览");
            SetText(previewInfoTxt, "请重新发布任务或查询任务以刷新预览");
        }

        private void FillScrollList(ScrollList list, List<Dictionary<string, string>> rows,
            Action<GameObject, Dictionary<string, string>> onItem = null)
        {
            if (list == null || list.Prefab == null) return;
            foreach (var it in list.Items) Destroy(it);
            list.Items.Clear();
            list.Prefab.SetActive(false);
            // 新条目插入到索引 0（最前面），与实时消息列表一致：最新数据排在最上方
            foreach (var row in rows)
            {
                var go = Instantiate(list.Prefab, list.Content);
                go.SetActive(true);
                foreach (var kv in row)
                    SetChildText(go.transform, kv.Key, kv.Value);
                onItem?.Invoke(go, row);
                go.transform.SetSiblingIndex(0);   // 放在 Content 最前面
                list.Items.Insert(0, go);
            }
        }

        private static Color ColorForStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return Color.white;
            var s = status.ToLower();
            if (s.Contains("完成") || s.Contains("complete") || s.Contains("done") || s.Contains("success"))
                return Color.green;
            if (s.Contains("失败") || s.Contains("error") || s.Contains("fail") || s.Contains("exception"))
                return Color.red;
            if (s.Contains("运行") || s.Contains("run") || s.Contains("progress") || s.Contains("执行"))
                return new Color(0.3f, 0.8f, 1f);   // 青蓝
            if (s.Contains("等待") || s.Contains("pending") || s.Contains("wait") || s.Contains("队列"))
                return Color.yellow;
            return Color.white;
        }

        public JObject BuildCurrentGraph()
        {
            if (_currentGraph == null) return null;
            var graph = (JObject)_currentGraph.DeepClone();
            WritePromptsToGraph(graph);
            foreach (var node in graph.Properties())
            {
                var cls = node.Value["class_type"]?.ToString();
                var inputs = node.Value["inputs"] as JObject;
                if (inputs == null) continue;

                if (cls == "KSampler")
                {
                    // 优先使用 Seed Input 框内的值；若控件缺失则回退到最近一次自动生成的随机种子
                    if (seedInput != null && long.TryParse(seedInput.text, out var s)) inputs["seed"] = Math.Max(0, s);
                    else inputs["seed"] = Math.Max(0, lastSeed);
                    if (stepsInput != null && int.TryParse(stepsInput.text, out var st)) inputs["steps"] = st;
                    if (cfgInput != null && float.TryParse(cfgInput.text, out var c)) inputs["cfg"] = c;
                    if (denoiseInput != null && float.TryParse(denoiseInput.text, out var d)) inputs["denoise"] = d;
                    if (samplerDropdown != null && samplerDropdown.options.Count > 0)
                        inputs["sampler_name"] = samplerDropdown.options[samplerDropdown.value].text;
                    if (schedulerDropdown != null && schedulerDropdown.options.Count > 0)
                        inputs["scheduler"] = schedulerDropdown.options[schedulerDropdown.value].text;
                }
                else if ((cls == "CheckpointLoaderSimple" || cls == "CheckpointLoader") && checkpointText != null)
                {
                    inputs["ckpt_name"] = checkpointText.text;
                }
                else if (cls == "EmptyLatentImage")
                {
                    if (widthInput != null && int.TryParse(widthInput.text, out var w)) inputs["width"] = w;
                    if (heightInput != null && int.TryParse(heightInput.text, out var h)) inputs["height"] = h;
                }
            }
            return graph;
        }

        // 把 UI 的提示词写入对应 CLIPTextEncode 节点
        private void WritePromptsToGraph(JObject graph)
        {
            string posNode = null, negNode = null;
            foreach (var node in graph.Properties())
            {
                if (node.Value["class_type"]?.ToString() == "KSampler")
                {
                    var inputs = node.Value["inputs"] as JObject;
                    posNode = inputs?["positive"] is JArray p ? p[0]?.ToString() : null;
                    negNode = inputs?["negative"] is JArray n ? n[0]?.ToString() : null;
                    break;
                }
            }
            if (posNode != null) SetNodeText(graph, posNode, promptInput?.text);
            if (negNode != null) SetNodeText(graph, negNode, negativePromptInput?.text);
        }

        private static void SetNodeText(JObject graph, string nodeId, string text)
        {
            if (graph[nodeId]?["inputs"] is JObject inputs)
                inputs["text"] = text ?? "";
        }
    }
}
