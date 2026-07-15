using System.Text;

namespace NasMover.Core.Tools;

/// <summary>
/// 按日期自动分文件的日志记录器。
/// 文件位于 {logDir}/yyyy-MM-dd.log，每天自动轮换。
/// </summary>
public class FileLogger : IDisposable
{
    private readonly string _logDir;
    private StreamWriter? _writer;
    private string? _currentDate;

    /// <param name="logDir">日志文件存放目录，不存在会自动创建</param>
    public FileLogger(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
    }

    /// <summary>
    /// 写入一条带时间戳的日志。
    /// 线程安全（StreamWriter 内部同步）。
    /// </summary>
    public void Write(string message)
    {
        var now = DateTime.Now;
        var date = now.ToString("yyyy-MM-dd");

        // 日期变了 → 轮换文件
        if (date != _currentDate)
            Rotate(date, now);

        var line = $"[{now:yyyy-MM-dd HH:mm:ss}] {message}";
        _writer?.WriteLine(line);
        _writer?.Flush();
    }

    private void Rotate(string date, DateTime now)
    {
        _writer?.Dispose();
        var path = Path.Combine(_logDir, $"{date}.log");
        _writer = new StreamWriter(path, append: true, Encoding.UTF8);
        _currentDate = date;

        // 文件刚创建时写入分隔行
        if (_writer.BaseStream.Length < 10)
        {
            _writer.WriteLine($"╌╌╌ NAS-Mover 启动 ╌╌╌ {now:yyyy-MM-dd HH:mm:ss} ╌╌╌");
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
    }
}
