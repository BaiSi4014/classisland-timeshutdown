using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.TimedShutdown.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.TimedShutdown.Services;

/// <summary>
/// 定时关机任务服务。
///
/// 调度时间只从 ClassIsland 的 IExactTimeService 获取。ClassIsland 时间服务
/// 尚未可用时，调度器不会使用系统时间替代，也不会修改任务的调度状态。
/// </summary>
public sealed class ShutdownTaskService : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IExactTimeService _exactTimeService;
    private readonly ILogger<ShutdownTaskService> _logger;
    private readonly string _configPath;
    private readonly List<ShutdownTask> _tasks = [];
    private readonly object _gate = new();

    private Timer? _timer;
    private readonly ManualResetEventSlim _tickIdle = new(initialState: true);
    private int _tickInProgress;
    private int _stopping;
    private bool _needsSchedulingNormalization;
    private DateTime _nextShutdownAttemptAt = DateTime.MinValue;
    private string _lastMessage = "等待中";

    public ShutdownTaskService(
        IExactTimeService exactTimeService,
        ILogger<ShutdownTaskService> logger,
        string configPath)
    {
        _exactTimeService = exactTimeService;
        _logger = logger;
        _configPath = configPath;
        LoadTasks();
    }

    /// <summary>当前调度器状态信息。</summary>
    public string LastMessage
    {
        get
        {
            lock (_gate)
            {
                return _lastMessage;
            }
        }
    }

    /// <summary>ClassIsland 时间服务的同步状态。</summary>
    public string TimeSyncStatus
    {
        get
        {
            try
            {
                return _exactTimeService.SyncStatusMessage;
            }
            catch
            {
                return "时间服务尚未准备好";
            }
        }
    }

    public string ConfigurationPath => _configPath;

    /// <summary>每次成功取得 ClassIsland 时间时触发，参数就是 ClassIsland 当前时间。</summary>
    public event Action<DateTime>? CurrentTimeChanged;

    /// <summary>任务增删改、执行或状态变化后触发。</summary>
    public event Action? TasksChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null)
        {
            return Task.CompletedTask;
        }

        Volatile.Write(ref _stopping, 0);
        _tickIdle.Reset();
        _timer = new Timer(
            _ => Tick(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // 先阻止新的 Tick，再等待已经进入临界区的 Tick 完成，避免停止服务后
        // 仍由排队回调启动关机命令。
        lock (_gate)
        {
            Volatile.Write(ref _stopping, 1);
        }

        var timer = Interlocked.Exchange(ref _timer, null);
        timer?.Change(Timeout.Infinite, Timeout.Infinite);
        timer?.Dispose();

        if (Volatile.Read(ref _tickInProgress) != 0)
        {
            _tickIdle.Wait(cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>返回设置页面所需的不可变任务行。</summary>
    public IReadOnlyList<ShutdownTaskRow> GetRows()
    {
        lock (_gate)
        {
            return _tasks.Select(CreateRow).ToArray();
        }
    }

    public ShutdownTask? GetTask(Guid id)
    {
        lock (_gate)
        {
            return _tasks.FirstOrDefault(task => task.Id == id)?.Clone();
        }
    }

    public DateTime GetCurrentCisTime()
    {
        return _exactTimeService.GetCurrentLocalDateTime();
    }

    public DateTime? GetNextScheduledRun()
    {
        lock (_gate)
        {
            var nextRuns = _tasks
                .Where(task => task.IsEnabled && task.NextRunAt.HasValue)
                .Select(task => task.NextRunAt!.Value)
                .ToArray();
            return nextRuns.Length == 0 ? null : nextRuns.Min();
        }
    }

    public ShutdownTask AddTask(
        string name,
        TimeSpan timeOfDay,
        ShutdownRepeatMode repeatMode,
        int intervalDays,
        DayOfWeek weekday,
        bool isEnabled)
    {
        var now = GetCurrentCisTime();
        var task = new ShutdownTask
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "定时关机" : name.Trim(),
            TimeOfDay = NormalizeTime(timeOfDay),
            RepeatMode = repeatMode,
            IntervalDays = Math.Clamp(intervalDays, 1, 365),
            Weekday = weekday,
            AnchorDate = now.Date,
            IsEnabled = isEnabled,
            LastTriggeredAt = null
        };
        task.NextRunAt = task.IsEnabled
            ? ComputeNextRun(task, now, includeCurrent: true)
            : null;

        lock (_gate)
        {
            _tasks.Add(task);
            _lastMessage = "任务已保存";
            if (!SaveLocked())
            {
                _tasks.Remove(task);
                throw new IOException(_lastMessage);
            }

            _nextShutdownAttemptAt = DateTime.MinValue;
        }

        NotifyTasksChanged();
        return task.Clone();
    }

    public bool UpdateTask(
        Guid id,
        string name,
        TimeSpan timeOfDay,
        ShutdownRepeatMode repeatMode,
        int intervalDays,
        DayOfWeek weekday,
        bool isEnabled)
    {
        var now = GetCurrentCisTime();
        lock (_gate)
        {
            var task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null)
            {
                return false;
            }

            var original = task.Clone();
            var updated = task.Clone();
            updated.Name = string.IsNullOrWhiteSpace(name) ? "定时关机" : name.Trim();
            updated.TimeOfDay = NormalizeTime(timeOfDay);
            updated.RepeatMode = repeatMode;
            updated.IntervalDays = Math.Clamp(intervalDays, 1, 365);
            if (updated.RepeatMode == ShutdownRepeatMode.EveryNDays
                && (task.RepeatMode != ShutdownRepeatMode.EveryNDays
                    || task.IntervalDays != updated.IntervalDays))
            {
                // 切换到每 N 天或修改间隔时，以保存当天作为新的计算锚点。
                updated.AnchorDate = now.Date;
            }
            updated.Weekday = weekday;
            updated.IsEnabled = isEnabled;
            updated.LastTriggeredAt = null;
            updated.NextRunAt = isEnabled
                ? ComputeNextRun(updated, now, includeCurrent: true)
                : null;
            CopyTaskState(task, updated);

            _lastMessage = "任务已更新";
            if (!SaveLocked())
            {
                CopyTaskState(task, original);
                throw new IOException(_lastMessage);
            }

            _nextShutdownAttemptAt = DateTime.MinValue;
        }

        NotifyTasksChanged();
        return true;
    }

    public bool SetEnabled(Guid id, bool isEnabled)
    {
        var now = GetCurrentCisTime();
        lock (_gate)
        {
            var task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null || task.IsEnabled == isEnabled)
            {
                return false;
            }

            var original = task.Clone();
            var updated = task.Clone();
            updated.IsEnabled = isEnabled;
            updated.NextRunAt = isEnabled
                ? ComputeNextRun(updated, now, includeCurrent: true)
                : null;
            CopyTaskState(task, updated);

            _lastMessage = isEnabled ? "任务已启用" : "任务已停用";
            if (!SaveLocked())
            {
                CopyTaskState(task, original);
                throw new IOException(_lastMessage);
            }

            _nextShutdownAttemptAt = DateTime.MinValue;
        }

        NotifyTasksChanged();
        return true;
    }

    public bool DeleteTask(Guid id)
    {
        lock (_gate)
        {
            var index = _tasks.FindIndex(task => task.Id == id);
            if (index < 0)
            {
                return false;
            }

            var removedTask = _tasks[index];
            _tasks.RemoveAt(index);
            _lastMessage = "任务已删除";
            if (!SaveLocked())
            {
                _tasks.Insert(index, removedTask);
                throw new IOException(_lastMessage);
            }

            _nextShutdownAttemptAt = DateTime.MinValue;
        }

        NotifyTasksChanged();
        return true;
    }

    private void Tick()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _tickInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            if (!TryGetCisTime(out var now))
            {
                return;
            }

            var tasksChanged = false;
            var triggerStatePersisted = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                {
                    return;
                }

                // 首次取得 ClassIsland 时间后，再清洗持久化任务并计算下一次执行时间。
                // 在此之前绝不使用系统时间参与调度。
                if (_needsSchedulingNormalization)
                {
                    NormalizeTasks(now);
                    _needsSchedulingNormalization = false;
                    tasksChanged = true;
                }

                // 兼容手动编辑过的配置文件：启用任务没有下一次时间时重新计算。
                foreach (var task in _tasks.Where(task => task.IsEnabled && !task.NextRunAt.HasValue))
                {
                    task.NextRunAt = ComputeNextRun(task, now, includeCurrent: false);
                    _lastMessage = "已重新计算任务时间";
                    tasksChanged = true;
                }

                var dueTasks = _tasks
                    .Where(task => task.IsEnabled && task.NextRunAt.HasValue && task.NextRunAt.Value <= now)
                    .ToArray();

                if (dueTasks.Length > 0 && now >= _nextShutdownAttemptAt)
                {
                    // 先把“已处理”状态持久化，再尝试调用系统命令。这样即使进程在
                    // 命令执行后崩溃，重启也不会因为旧配置而重复执行同一个任务。
                    var hadPendingChanges = tasksChanged;
                    var snapshots = dueTasks.Select(task => task.Clone()).ToArray();
                    foreach (var task in dueTasks)
                    {
                        ApplyTriggeredState(task, now);
                    }
                    tasksChanged = true;

                    if (SaveLocked())
                    {
                        triggerStatePersisted = true;
                        try
                        {
                            ExecuteShutdown();
                            _nextShutdownAttemptAt = now.AddSeconds(1);
                            _lastMessage = $"已执行关机命令（{dueTasks.Length} 个任务到期）";
                        }
                        catch (Exception ex)
                        {
                            // 命令可能已经被 Windows 接受但未及时返回；为避免重复
                            // 发起关机，保留已持久化的处理状态，不自动重试。
                            _nextShutdownAttemptAt = DateTime.MaxValue;
                            _lastMessage = $"执行关机命令失败：{ex.Message}（为避免重复执行，本次不再自动重试）";
                            _logger.LogError(ex, "执行 shutdown.exe /s /t 0 失败。");
                        }
                    }
                    else
                    {
                        for (var i = 0; i < dueTasks.Length; i++)
                        {
                            CopyTaskState(dueTasks[i], snapshots[i]);
                        }
                        tasksChanged = hadPendingChanges;
                        _nextShutdownAttemptAt = now.AddSeconds(30);
                        _lastMessage = "保存任务状态失败，本次未执行关机命令";
                    }
                }

                if (tasksChanged && !triggerStatePersisted)
                {
                    SaveLocked();
                }
            }

            NotifyCurrentTime(now);
            if (tasksChanged)
            {
                NotifyTasksChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定时关机调度器发生异常。");
        }
        finally
        {
            Volatile.Write(ref _tickInProgress, 0);
            _tickIdle.Set();
        }
    }

    private void LoadTasks()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_configPath))
            {
                return;
            }

            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var tasks = JsonSerializer.Deserialize<List<ShutdownTask>>(json, JsonOptions);
            if (tasks is null)
            {
                return;
            }

            _tasks.AddRange(tasks);
            NormalizeTaskShapes();

            // 调度相关字段必须等 ClassIsland 时间服务可用后再清洗和计算，
            // 不能在插件加载阶段用系统时间代替 ClassIsland 时间。
            _needsSchedulingNormalization = true;
            _lastMessage = $"已加载 {_tasks.Count} 个任务";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取定时关机任务配置失败：{Path}", _configPath);
            TryBackupCorruptConfig();
            _tasks.Clear();
            _lastMessage = "配置文件读取失败，已使用空任务列表";
        }
    }

    private static void CopyTaskState(ShutdownTask target, ShutdownTask source)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.TimeOfDay = source.TimeOfDay;
        target.RepeatMode = source.RepeatMode;
        target.IntervalDays = source.IntervalDays;
        target.Weekday = source.Weekday;
        target.AnchorDate = source.AnchorDate;
        target.IsEnabled = source.IsEnabled;
        target.NextRunAt = source.NextRunAt;
        target.LastTriggeredAt = source.LastTriggeredAt;
    }

    private void NormalizeTaskShapes()
    {
        var ids = new HashSet<Guid>();
        foreach (var task in _tasks)
        {
            if (task.Id == Guid.Empty || !ids.Add(task.Id))
            {
                Guid replacement;
                do
                {
                    replacement = Guid.NewGuid();
                }
                while (!ids.Add(replacement));
                task.Id = replacement;
            }

            if (string.IsNullOrWhiteSpace(task.Name))
            {
                task.Name = "定时关机";
            }

            task.TimeOfDay = NormalizeTime(task.TimeOfDay);
            task.IntervalDays = Math.Clamp(task.IntervalDays <= 0 ? 1 : task.IntervalDays, 1, 365);
            if (!Enum.IsDefined(typeof(ShutdownRepeatMode), task.RepeatMode))
            {
                task.RepeatMode = ShutdownRepeatMode.Once;
            }

            if (!Enum.IsDefined(typeof(DayOfWeek), task.Weekday))
            {
                task.Weekday = DayOfWeek.Monday;
            }

            if (task.AnchorDate != default)
            {
                task.AnchorDate = task.AnchorDate.Date;
            }
        }
    }

    private void NormalizeTasks(DateTime now)
    {
        NormalizeTaskShapes();
        foreach (var task in _tasks)
        {
            if (task.AnchorDate == default)
            {
                task.AnchorDate = now.Date;
            }

            if (!task.IsEnabled)
            {
                task.NextRunAt = null;
                continue;
            }

            if (task.RepeatMode == ShutdownRepeatMode.Once)
            {
                if (!task.NextRunAt.HasValue)
                {
                    task.NextRunAt = ComputeNextRun(task, now, includeCurrent: true);
                }
                else if (task.NextRunAt.Value < now)
                {
                    // 关闭应用期间错过的一次性任务不在重新打开应用时突然执行。
                    task.IsEnabled = false;
                    task.NextRunAt = null;
                }
            }
            else if (!task.NextRunAt.HasValue || task.NextRunAt.Value < now)
            {
                task.NextRunAt = ComputeNextRun(task, now, includeCurrent: false);
            }
        }
    }

    private bool SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _configPath + ".tmp";
            var json = JsonSerializer.Serialize(
                _tasks.Select(task => task.Clone()).ToList(),
                JsonOptions);
            File.WriteAllText(temporaryPath, json, Utf8NoBom);
            File.Move(temporaryPath, _configPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存定时关机任务配置失败：{Path}", _configPath);
            _lastMessage = $"保存配置失败：{ex.Message}";
            return false;
        }
    }

    private void TryBackupCorruptConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return;
            }

            var backupPath = $"{_configPath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Copy(_configPath, backupPath, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "备份损坏的定时关机配置失败。");
        }
    }

    private bool TryGetCisTime(out DateTime now)
    {
        try
        {
            now = _exactTimeService.GetCurrentLocalDateTime();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClassIsland 精确时间服务暂时不可用。");
            now = default;
            return false;
        }
    }

    private ShutdownTaskRow CreateRow(ShutdownTask task)
    {
        var nextRunText = task.NextRunAt.HasValue
            ? task.NextRunAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : task.IsEnabled ? "计算中" : "—";

        return new ShutdownTaskRow
        {
            Id = task.Id,
            Name = task.Name,
            TimeText = task.TimeOfDay.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            RepeatText = GetRepeatDescription(task),
            NextRunText = nextRunText,
            StatusText = task.IsEnabled ? "等待中" : "已关闭",
            IsEnabled = task.IsEnabled
        };
    }

    private static string GetRepeatDescription(ShutdownTask task)
    {
        return task.RepeatMode switch
        {
            ShutdownRepeatMode.Once => "仅一次",
            ShutdownRepeatMode.Daily => "每天",
            ShutdownRepeatMode.Weekly => $"每周{GetWeekdayName(task.Weekday)}",
            ShutdownRepeatMode.EveryNDays => $"每 {Math.Max(1, task.IntervalDays)} 天",
            _ => "未知"
        };
    }

    private static string GetWeekdayName(DayOfWeek weekday)
    {
        return weekday switch
        {
            DayOfWeek.Monday => "一",
            DayOfWeek.Tuesday => "二",
            DayOfWeek.Wednesday => "三",
            DayOfWeek.Thursday => "四",
            DayOfWeek.Friday => "五",
            DayOfWeek.Saturday => "六",
            DayOfWeek.Sunday => "日",
            _ => "未知"
        };
    }

    private static TimeSpan NormalizeTime(TimeSpan value)
    {
        var totalSeconds = (long)Math.Clamp(Math.Floor(value.TotalSeconds), 0, 23 * 60 * 60 + 59 * 60 + 59);
        return TimeSpan.FromSeconds(totalSeconds);
    }

    private static void ApplyTriggeredState(ShutdownTask task, DateTime now)
    {
        task.LastTriggeredAt = now;
        if (task.RepeatMode == ShutdownRepeatMode.Once)
        {
            task.IsEnabled = false;
            task.NextRunAt = null;
        }
        else
        {
            // 加一秒是为了确保本次触发不会在同一秒再次命中。
            task.NextRunAt = ComputeNextRun(
                task,
                now.AddSeconds(1),
                includeCurrent: false);
        }
    }

    private static DateTime ComputeNextRun(ShutdownTask task, DateTime now, bool includeCurrent)
    {
        var time = task.TimeOfDay;
        DateTime candidate;

        switch (task.RepeatMode)
        {
            case ShutdownRepeatMode.Weekly:
            {
                var daysUntilWeekday = ((int)task.Weekday - (int)now.DayOfWeek + 7) % 7;
                candidate = now.Date.AddDays(daysUntilWeekday) + time;
                if (candidate < now || (!includeCurrent && candidate == now))
                {
                    candidate = candidate.AddDays(7);
                }

                return candidate;
            }

            case ShutdownRepeatMode.EveryNDays:
            {
                var interval = Math.Max(1, task.IntervalDays);
                var anchor = task.AnchorDate.Date;
                var elapsedDays = Math.Max(0, (now.Date - anchor).Days);
                var remainder = elapsedDays % interval;
                var occurrenceDate = anchor.AddDays(elapsedDays - remainder);
                candidate = occurrenceDate + time;
                if (candidate < now || (!includeCurrent && candidate == now))
                {
                    candidate = occurrenceDate.AddDays(interval) + time;
                }

                return candidate;
            }

            case ShutdownRepeatMode.Daily:
            default:
                candidate = now.Date + time;
                if (candidate < now || (!includeCurrent && candidate == now))
                {
                    candidate = candidate.AddDays(1);
                }

                return candidate;
        }
    }

    private static void ExecuteShutdown()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("shutdown.exe 只能在 Windows 上运行。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add("0");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 shutdown.exe。");

        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 记录原始超时异常即可。
            }

            throw new TimeoutException("shutdown.exe 未在 5 秒内退出。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"shutdown.exe 返回错误代码 {process.ExitCode}。");
        }
    }

    private void NotifyCurrentTime(DateTime now)
    {
        try
        {
            CurrentTimeChanged?.Invoke(now);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知当前时间失败。");
        }
    }

    private void NotifyTasksChanged()
    {
        try
        {
            TasksChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知任务列表变化失败。");
        }
    }
}
