using System.Text.Json.Serialization;

namespace ClassIsland.TimedShutdown.Models;

/// <summary>
/// 定时关机任务的循环方式。
/// </summary>
public enum ShutdownRepeatMode
{
    /// <summary>只执行一次。</summary>
    Once,

    /// <summary>每天执行一次。</summary>
    Daily,

    /// <summary>每周指定星期几执行一次。</summary>
    Weekly,

    /// <summary>每隔指定天数执行一次。</summary>
    EveryNDays
}

/// <summary>
/// 持久化的定时关机任务。
/// </summary>
public sealed class ShutdownTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "定时关机";

    /// <summary>当天的关机时间，精确到秒，范围为 00:00:00（含）至 23:59:59。</summary>
    public TimeSpan TimeOfDay { get; set; } = new(22, 0, 0);

    public ShutdownRepeatMode RepeatMode { get; set; } = ShutdownRepeatMode.Once;

    /// <summary>EveryNDays 模式下的间隔天数。</summary>
    public int IntervalDays { get; set; } = 1;

    /// <summary>Weekly 模式下的星期几。</summary>
    public DayOfWeek Weekday { get; set; } = DayOfWeek.Monday;

    /// <summary>EveryNDays 模式的起始日期。加载配置时由 ClassIsland 时间补齐。</summary>
    public DateTime AnchorDate { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? NextRunAt { get; set; }

    public DateTime? LastTriggeredAt { get; set; }

    public ShutdownTask Clone()
    {
        return new ShutdownTask
        {
            Id = Id,
            Name = Name,
            TimeOfDay = TimeOfDay,
            RepeatMode = RepeatMode,
            IntervalDays = IntervalDays,
            Weekday = Weekday,
            AnchorDate = AnchorDate,
            IsEnabled = IsEnabled,
            NextRunAt = NextRunAt,
            LastTriggeredAt = LastTriggeredAt
        };
    }
}

/// <summary>
/// 设置页面使用的只读任务行，避免 UI 直接修改服务内部状态。
/// </summary>
public sealed class ShutdownTaskRow
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string TimeText { get; init; } = string.Empty;

    public string RepeatText { get; init; } = string.Empty;

    public string NextRunText { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }
}
