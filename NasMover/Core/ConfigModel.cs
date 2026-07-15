using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NasMover.Core;

/// <summary>
/// 最外层配置，对应 config.json 的全部内容
/// </summary>
public class AppConfig
{
    /// <summary>冷配置：需重启生效</summary>
    [JsonProperty("cold_config", Required = Required.Always)]
    public ColdConfig ColdConfig { get; set; } = new();

    /// <summary>热配置：运行时立即生效</summary>
    [JsonProperty("hot_config", Required = Required.Always)]
    public HotConfig HotConfig { get; set; } = new();

    /// <summary>HTTP 云控配置</summary>
    [JsonProperty("cloud_control")]
    public CloudControlConfig? CloudControl { get; set; }

    /// <summary>OTA 更新配置</summary>
    [JsonProperty("update_control")]
    public UpdateControlConfig? UpdateControl { get; set; }

    /// <summary>设备状态上报配置</summary>
    [JsonProperty("status_report")]
    public StatusReportConfig? StatusReport { get; set; }

    /// <summary>
    /// 目标根目录 = NasBasePath / MachineId / ActiveModel
    /// </summary>
    [JsonIgnore]
    public string DestinationRoot =>
        Path.Combine(HotConfig.NasBasePath, ColdConfig.MachineId, HotConfig.ActiveModel);
}

/// <summary>
/// 冷配置：修改后需要重启应用才能完全生效
/// </summary>
public class ColdConfig
{
    /// <summary>机台唯一标识符，UI 顶部显示用</summary>
    [JsonProperty("machine_id", Required = Required.Always)]
    public string MachineId { get; set; } = string.Empty;

    /// <summary>定时任务 Cron 表达式，默认每天凌晨 2:00</summary>
    [JsonProperty("schedule_cron", Required = Required.Always)]
    public string ScheduleCron { get; set; } = "0 2 * * *";

    /// <summary>软件新版本下载地址，用于 OTA 更新提醒</summary>
    [JsonProperty("software_update_url", Required = Required.Always)]
    public string SoftwareUpdateUrl { get; set; } = string.Empty;
}

/// <summary>
/// 热配置：修改后运行时立即生效，不打断正在执行的传输任务
/// </summary>
public class HotConfig
{
    /// <summary>NAS 根目录 UNC 路径，如 \\192.168.1.100\Backup</summary>
    [JsonProperty("nas_base_path", Required = Required.Always)]
    public string NasBasePath { get; set; } = string.Empty;

    /// <summary>默认并发 Worker 数，为 null 时取 WorkerThreadsLimit</summary>
    [JsonProperty("default_worker_threads")]
    public int? DefaultWorkerThreads { get; set; }

    /// <summary>最大允许并发 Worker 数</summary>
    [JsonProperty("worker_threads_limit", Required = Required.Always)]
    public int WorkerThreadsLimit { get; set; } = 1;

    /// <summary>传输完成后是否删除本地源文件</summary>
    [JsonProperty("delete_after_transfer")]
    public bool DeleteAfterTransfer { get; set; }

    /// <summary>当前激活的机种名称，用于在 Models 字典中查找对应规则</summary>
    [JsonProperty("active_model", Required = Required.Always)]
    public string ActiveModel { get; set; } = string.Empty;

    /// <summary>机种配置字典，key 为机种名称</summary>
    [JsonProperty("models", Required = Required.Always)]
    public Dictionary<string, ModelConfig> Models { get; set; } = new();

    /// <summary>经过 clamp 后的最终有效并发数，范围 [1, WorkerThreadsLimit]</summary>
    [JsonIgnore]
    public int EffectiveWorkerThreads =>
        Math.Clamp(DefaultWorkerThreads ?? WorkerThreadsLimit, 1, WorkerThreadsLimit);

    /// <summary>获取当前激活机种的配置对象，不存在则返回 null</summary>
    [JsonIgnore]
    public ModelConfig? ActiveModelConfig =>
        Models.GetValueOrDefault(ActiveModel);
}

/// <summary>
/// 单个机种的扫描目录与文件过滤规则
/// </summary>
public class ModelConfig
{
    /// <summary>需要扫描的本地根目录列表</summary>
    [JsonProperty("base_dirs")]
    public List<string> BaseDirs { get; set; } = new();

    /// <summary>当天日期格式，匹配此格式的当日文件夹将被跳过；空字符串表示不启用此规则</summary>
    [JsonProperty("ignore_today_format")]
    public string IgnoreTodayFormat { get; set; } = "%Y_%m_%d";

    /// <summary>路径中包含这些关键字的目录将被跳过（如 TestRun、Debug）</summary>
    [JsonProperty("exclude_dirs")]
    public List<string> ExcludeDirs { get; set; } = new();

    /// <summary>文件级过滤规则</summary>
    [JsonProperty("file_rules")]
    public FileRules FileRules { get; set; } = new();
}

/// <summary>
/// 文件级过滤规则：按后缀、前缀、排除后缀筛选待传输文件
/// </summary>
public class FileRules
{
    /// <summary>允许传输的文件后缀（不带点号，如 ["jpg", "bmp"]）；为空则不限制</summary>
    [JsonProperty("allowed_extensions")]
    public List<string> AllowedExtensions { get; set; } = new();

    /// <summary>文件名必须包含的前缀（如 ["Pick_", "OK_"]）；为空则不限制</summary>
    [JsonProperty("require_prefix")]
    public List<string> RequirePrefix { get; set; } = new();

    /// <summary>文件名若包含此后缀则忽略（如 ["_NG", "_FAIL"]）</summary>
    [JsonProperty("exclude_suffix")]
    public List<string> ExcludeSuffix { get; set; } = new();
}

/// <summary>
/// HTTP 静态云控配置：通过远端 manifest + config.json 实现远程配置分发
/// </summary>
public class CloudControlConfig
{
    /// <summary>是否启用云控拉取</summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    /// <summary>配置 manifest 文件的 URL</summary>
    [JsonProperty("manifest_url")]
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>拉取模式：manual / daily / weekly</summary>
    [JsonProperty("check_mode")]
    public RemoteCheckMode CheckMode { get; set; } = RemoteCheckMode.Manual;

    /// <summary>Daily 模式下的检查小时（0-23）</summary>
    [JsonProperty("check_hour")]
    public int? CheckHour { get; set; }

    /// <summary>Weekly 模式下的检查星期几（1=周日，7=周六）</summary>
    [JsonProperty("check_weekday")]
    public int? CheckWeekday { get; set; }

    /// <summary>HTTP 请求超时（秒），最小 1</summary>
    [JsonProperty("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>机台分组名，用于匹配 manifest 中的 scope.machine_group</summary>
    [JsonProperty("machine_group")]
    public string MachineGroup { get; set; } = string.Empty;

    /// <summary>验证 token，通过 Bearer 头传递</summary>
    [JsonProperty("auth_token")]
    public string AuthToken { get; set; } = string.Empty;
}

/// <summary>
/// OTA 更新配置：定期检查远端是否有软件新版本
/// </summary>
public class UpdateControlConfig
{
    /// <summary>是否启用更新检查</summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    /// <summary>更新 manifest 文件的 URL</summary>
    [JsonProperty("manifest_url")]
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>拉取模式：manual / daily / weekly</summary>
    [JsonProperty("check_mode")]
    public RemoteCheckMode CheckMode { get; set; } = RemoteCheckMode.Manual;

    /// <summary>Daily 模式下的检查小时（0-23）</summary>
    [JsonProperty("check_hour")]
    public int? CheckHour { get; set; }

    /// <summary>Weekly 模式下的检查星期几（1=周日，7=周六）</summary>
    [JsonProperty("check_weekday")]
    public int? CheckWeekday { get; set; }

    /// <summary>HTTP 请求超时（秒），最小 1</summary>
    [JsonProperty("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>验证 token，通过 Bearer 头传递</summary>
    [JsonProperty("auth_token")]
    public string AuthToken { get; set; } = string.Empty;
}

/// <summary>
/// 设备状态上报配置：周期性向远端 endpoint 上报机台运行状态
/// </summary>
public class StatusReportConfig
{
    /// <summary>是否启用状态上报</summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    /// <summary>接收状态上报的 URL</summary>
    [JsonProperty("endpoint_url")]
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>空闲（Idle）状态下的上报间隔（秒），默认 30</summary>
    [JsonProperty("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>运行中的上报间隔（秒），默认 15</summary>
    [JsonProperty("active_report_interval_seconds")]
    public int ActiveReportIntervalSeconds { get; set; } = 15;

    /// <summary>HTTP 请求超时（秒），最小 1</summary>
    [JsonProperty("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; set; } = 5;

    /// <summary>验证 token，通过 Bearer 头传递</summary>
    [JsonProperty("auth_token")]
    public string AuthToken { get; set; } = string.Empty;
}

/// <summary>
/// 传输协议类型
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum RemoteTransport
{
    [JsonProperty("http")] Http
}

/// <summary>
/// 远程配置拉取检查模式
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum RemoteCheckMode
{
    [JsonProperty("manual")] Manual,
    [JsonProperty("daily")] Daily,
    [JsonProperty("weekly")] Weekly
}
