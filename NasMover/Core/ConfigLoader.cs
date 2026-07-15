using Newtonsoft.Json;

namespace NasMover.Core;

/// <summary>
/// 负责定位、加载、解析相对路径、校验和保存 config.json
/// </summary>
public static class ConfigLoader
{
    /// <summary>最近一次成功加载的 config.json 完整路径</summary>
    public static string? ConfigFilePath { get; private set; }

    /// <summary>
    /// 定位文件 → 反序列化 → 解析相对路径 → 校验
    /// </summary>
    public static AppConfig LoadConfig()
    {
        // 1. 找到 config.json 的完整路径
        string configPath = LocateConfigFile();
        ConfigFilePath = configPath;

        // 2. 读取全部文本
        string json = File.ReadAllText(configPath);

        // 3. 反序列化成 AppConfig 对象
        var config = JsonConvert.DeserializeObject<AppConfig>(json)
            ?? throw new InvalidOperationException($"config.json 反序列化失败: {configPath}");

        // 4. 解析相对路径（相对于 config.json 所在目录）
        string configDir = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("无法获取 config.json 的目录路径");
        ResolveRelativePaths(config, configDir);

        // 5. 校验配置合理性
        ValidateConfig(config);

        return config;
    }

    /// <summary>
    /// 按优先级查找 config.json：
    /// </summary>
    public static string LocateConfigFile()
    {
        var candidates = new List<string>();

        // 1 exe 同级目录
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            candidates.Add(Path.Combine(exeDir, "config.json"));
            candidates.Add(Path.Combine(exeDir, "config", "config.json"));

            // 2 从 exe 目录向上回退 4 级
            var dir = new DirectoryInfo(exeDir);
            for (int i = 0; i < 4; i++)
            {
                dir = dir.Parent;
                if (dir == null) break;
                candidates.Add(Path.Combine(dir.FullName, "config.json"));
            }
        }

        // 3 当前工作目录兜底
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "config.json"));

        // 返回第一个真实存在的文件
        string? found = candidates.FirstOrDefault(File.Exists);
        if (found != null)
            return found;

        // 全部找不到则报详细错误
        string attempted = string.Join("\n", candidates.Select(p => $"  {p}"));
        throw new FileNotFoundException(
            $"未找到 config.json，已尝试以下位置：\n{attempted}");
    }

    /// <summary>
    /// 将 nas_base_path 和 base_dirs 中的相对路径展开为绝对路径。
    /// 如果是绝对路径或 UNC 路径（\\开头）则保留原样，
    /// 否则相对于 config.json 所在目录
    /// </summary>
    public static void ResolveRelativePaths(AppConfig config, string configDir)
    {
        config.HotConfig.NasBasePath = ResolveSinglePath(config.HotConfig.NasBasePath, configDir);

        foreach (var model in config.HotConfig.Models.Values)
        {
            for (int i = 0; i < model.BaseDirs.Count; i++)
            {
                model.BaseDirs[i] = ResolveSinglePath(model.BaseDirs[i], configDir);
            }
        }
    }

    /// <summary>
    /// 单一路径解析：统一反斜杠 → 判断是否绝对路径 → 决定是否拼接 configDir
    /// </summary>
    private static string ResolveSinglePath(string path, string configDir)
    {
        // Windows 上将正斜杠统一转反斜杠
        string normalized = path.Replace('/', '\\');

        // 已经是绝对路径（如 D:\... 或 \\NAS\...）→ 原样返回
        if (Path.IsPathRooted(normalized))
            return normalized;

        // 相对路径 → 拼上 config 目录再转成完整绝对路径
        return Path.GetFullPath(Path.Combine(configDir, normalized));
    }


    /// <summary>
    /// 检查配置的合理性，不合理的值做 clamp 修正，
    /// 严重错误直接抛异常阻止启动
    /// </summary>
    public static void ValidateConfig(AppConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. ActiveModel 是否在 Models 字典中
        if (string.IsNullOrWhiteSpace(config.HotConfig.ActiveModel))
        {
            errors.Add("hot_config.active_model 为空");
        }
        else if (!config.HotConfig.Models.ContainsKey(config.HotConfig.ActiveModel))
        {
            string available = string.Join(", ", config.HotConfig.Models.Keys);
            errors.Add(
                $"active_model [{config.HotConfig.ActiveModel}] 在 models 中不存在。" +
                $"可用机种: [{available}]");
        }

        // 2. WorkerThreadsLimit 不能小于 1
        if (config.HotConfig.WorkerThreadsLimit < 1)
        {
            warnings.Add(
                $"worker_threads_limit={config.HotConfig.WorkerThreadsLimit} 无效，已重置为 1");
            config.HotConfig.WorkerThreadsLimit = 1;
        }

        // 3. DefaultWorkerThreads 不能超过 WorkerThreadsLimit
        if (config.HotConfig.DefaultWorkerThreads > config.HotConfig.WorkerThreadsLimit)
        {
            warnings.Add(
                $"default_worker_threads={config.HotConfig.DefaultWorkerThreads} 超过上限 " +
                $"worker_threads_limit={config.HotConfig.WorkerThreadsLimit}，已自动限制");
            config.HotConfig.DefaultWorkerThreads = config.HotConfig.WorkerThreadsLimit;
        }

        // 4. NasBasePath 不能为空
        if (string.IsNullOrWhiteSpace(config.HotConfig.NasBasePath))
        {
            errors.Add("hot_config.nas_base_path 为空，无法启动传输");
        }

        // 5 各超时时间 clamp 到至少 1
        ClampMin(config.CloudControl, c => c.RequestTimeoutSeconds, (c, v) => c.RequestTimeoutSeconds = v);
        ClampMin(config.UpdateControl, c => c.RequestTimeoutSeconds, (c, v) => c.RequestTimeoutSeconds = v);
        if (config.StatusReport != null)
        {
            if (config.StatusReport.RequestTimeoutSeconds < 1)
                config.StatusReport.RequestTimeoutSeconds = 1;
            if (config.StatusReport.HeartbeatIntervalSeconds < 1)
                config.StatusReport.HeartbeatIntervalSeconds = 1;
            if (config.StatusReport.ActiveReportIntervalSeconds < 1)
                config.StatusReport.ActiveReportIntervalSeconds = 1;
        }

        // 输出警告（不阻止启动）
        foreach (string warn in warnings)
        {
            Console.WriteLine($"[WARN] 配置: {warn}");
        }

        // 有错误则抛出，阻止启动
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"配置校验失败，共 {errors.Count} 个错误：\n" +
                string.Join("\n", errors.Select(e => $"  • {e}")));
        }
    }

    /// <summary>
    /// 如果可空配置对象的某个 int 属性小于 1 则设为 1
    /// </summary>
    private static void ClampMin<T>(T? config, Func<T, int> getter, Action<T, int> setter)
        where T : class
    {
        if (config == null) return;
        int value = getter(config);
        if (value < 1)
            setter(config, 1);
    }

    /// <summary>
    /// 将当前配置保存回 config.json 文件。
    /// </summary>
    public static void SaveConfig(AppConfig config)
    {
        string configPath = ConfigFilePath ?? LocateConfigFile();
        ConfigFilePath = configPath;

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
        };

        string json = JsonConvert.SerializeObject(config, settings);
        File.WriteAllText(configPath, json);
    }
}
