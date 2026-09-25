using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using ClassIsland.TimedShutdown.Models;
using ClassIsland.TimedShutdown.Services;

namespace ClassIsland.TimedShutdown.Views.SettingsPages;

[SettingsPageInfo("local.classisland.timedshutdown.page", "定时关机")]
public partial class TimedShutdownSettingsPage : SettingsPageBase
{
    private readonly ShutdownTaskService _service;
    private Guid? _editingTaskId;
    private bool _eventsAttached;

    // Avalonia 的 XAML 资源分析器需要一个公开的无参构造函数；真正的设置页由
    // ClassIsland 的 keyed transient DI 使用下面的带服务构造函数创建。
    public TimedShutdownSettingsPage()
        : this(IAppHost.GetService<ShutdownTaskService>())
    {
    }

    public TimedShutdownSettingsPage(ShutdownTaskService service)
    {
        _service = service;
        InitializeComponent();
        DataContext = this;

        RepeatComboBox.SelectedIndex = 0;
        IntervalDaysTextBox.Text = "1";
        WeekdayComboBox.SelectedIndex = 0;
        InitializeTimeSelectors();

        Loaded += PageLoaded;
        Unloaded += PageUnloaded;
    }

    private void PageLoaded(object? sender, RoutedEventArgs e)
    {
        if (!_eventsAttached)
        {
            _service.CurrentTimeChanged += ServiceCurrentTimeChanged;
            _service.TasksChanged += ServiceTasksChanged;
            _eventsAttached = true;
        }

        RefreshView();
        UpdateClock();
    }

    private void PageUnloaded(object? sender, RoutedEventArgs e)
    {
        if (!_eventsAttached)
        {
            return;
        }

        _service.CurrentTimeChanged -= ServiceCurrentTimeChanged;
        _service.TasksChanged -= ServiceTasksChanged;
        _eventsAttached = false;
    }

    private void ServiceCurrentTimeChanged(DateTime now)
    {
        Dispatcher.UIThread.Post(() => UpdateClock(now));
    }

    private void ServiceTasksChanged()
    {
        Dispatcher.UIThread.Post(RefreshView);
    }

    private void OnAddTaskClick(object? sender, RoutedEventArgs e)
    {
        DateTime now;
        try
        {
            now = _service.GetCurrentCisTime();
        }
        catch (Exception ex)
        {
            SetValidation($"无法获取 ClassIsland 时间：{ex.Message}");
            return;
        }

        // 新建任务默认安排在“今天”而不是“明天的这个时刻”，当天时间已过时才顺延到明天。
        var suggested = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (suggested < now)
        {
            suggested = suggested.AddMinutes(1);
        }

        BeginEditor(
            editingTaskId: null,
            name: "定时关机",
            time: new TimeSpan(suggested.Hour, suggested.Minute, 0),
            repeatMode: ShutdownRepeatMode.Once,
            intervalDays: 1,
            weekday: now.DayOfWeek,
            isEnabled: true,
            title: "新增定时关机任务");
    }

    private void InitializeTimeSelectors()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            HourComboBox.Items.Add(hour.ToString("D2", CultureInfo.InvariantCulture));
        }

        for (var minute = 0; minute < 60; minute++)
        {
            MinuteComboBox.Items.Add(minute.ToString("D2", CultureInfo.InvariantCulture));
        }

        for (var second = 0; second < 60; second++)
        {
            SecondComboBox.Items.Add(second.ToString("D2", CultureInfo.InvariantCulture));
        }
    }

    private void SetTimeSelector(TimeSpan time)
    {
        HourComboBox.SelectedIndex = Math.Clamp(time.Hours, 0, 23);
        MinuteComboBox.SelectedIndex = Math.Clamp(time.Minutes, 0, 59);
        SecondComboBox.SelectedIndex = Math.Clamp(time.Seconds, 0, 59);
    }

    private TimeSpan GetSelectedTime()
    {
        var hour = HourComboBox.SelectedIndex < 0 ? 0 : HourComboBox.SelectedIndex;
        var minute = MinuteComboBox.SelectedIndex < 0 ? 0 : MinuteComboBox.SelectedIndex;
        var second = SecondComboBox.SelectedIndex < 0 ? 0 : SecondComboBox.SelectedIndex;
        return new TimeSpan(Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59));
    }

    private void OnEditTaskClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ShutdownTaskRow row })
        {
            return;
        }

        var task = _service.GetTask(row.Id);
        if (task is null)
        {
            RefreshView();
            return;
        }

        BeginEditor(
            editingTaskId: task.Id,
            name: task.Name,
            time: task.TimeOfDay,
            repeatMode: task.RepeatMode,
            intervalDays: task.IntervalDays,
            weekday: task.Weekday,
            isEnabled: task.IsEnabled,
            title: "编辑定时关机任务");
    }

    private void OnSaveTaskClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEditor(out var name, out var time, out var repeatMode,
                out var intervalDays, out var weekday, out var isEnabled))
        {
            return;
        }

        try
        {
            if (_editingTaskId is Guid id)
            {
                if (!_service.UpdateTask(id, name, time, repeatMode, intervalDays, weekday, isEnabled))
                {
                    SetValidation("任务不存在，可能已经被删除。");
                    RefreshView();
                    return;
                }
            }
            else
            {
                _service.AddTask(name, time, repeatMode, intervalDays, weekday, isEnabled);
            }

            HideEditor();
            SetValidation(string.Empty);
            RefreshView();
        }
        catch (Exception ex)
        {
            SetValidation($"保存任务失败：{ex.Message}");
        }
    }

    private void OnCancelEditClick(object? sender, RoutedEventArgs e)
    {
        HideEditor();
        SetValidation(string.Empty);
    }

    private void OnRepeatModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateEditorModeVisibility();
    }

    private void OnToggleTaskClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ShutdownTaskRow row } checkBox)
        {
            return;
        }

        try
        {
            if (_service.SetEnabled(row.Id, checkBox.IsChecked != true))
            {
                SetValidation(string.Empty);
            }

            RefreshView();
        }
        catch (Exception ex)
        {
            SetValidation($"更新任务失败：{ex.Message}");
            RefreshView();
        }
    }

    private void OnDeleteTaskClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ShutdownTaskRow row })
        {
            return;
        }

        try
        {
            if (_service.DeleteTask(row.Id))
            {
                if (_editingTaskId == row.Id)
                {
                    HideEditor();
                }

                SetValidation("任务已删除。");
            }

            RefreshView();
        }
        catch (Exception ex)
        {
            SetValidation($"删除任务失败：{ex.Message}");
            RefreshView();
        }
    }

    private void BeginEditor(
        Guid? editingTaskId,
        string name,
        TimeSpan time,
        ShutdownRepeatMode repeatMode,
        int intervalDays,
        DayOfWeek weekday,
        bool isEnabled,
        string title)
    {
        _editingTaskId = editingTaskId;
        EditorTitleText.Text = title;
        NameTextBox.Text = name;
        SetTimeSelector(time);
        RepeatComboBox.SelectedIndex = GetRepeatIndex(repeatMode);
        IntervalDaysTextBox.Text = intervalDays.ToString(CultureInfo.InvariantCulture);
        WeekdayComboBox.SelectedIndex = GetWeekdayIndex(weekday);
        EnabledCheckBox.IsChecked = isEnabled;
        SetValidation(string.Empty);
        UpdateEditorModeVisibility();
        EditorBorder.IsVisible = true;
    }

    private void HideEditor()
    {
        _editingTaskId = null;
        EditorBorder.IsVisible = false;
    }

    private void UpdateEditorModeVisibility()
    {
        var mode = GetSelectedRepeatMode();
        IntervalPanel.IsVisible = mode == ShutdownRepeatMode.EveryNDays;
        WeekdayPanel.IsVisible = mode == ShutdownRepeatMode.Weekly;
    }

    private bool TryReadEditor(
        out string name,
        out TimeSpan time,
        out ShutdownRepeatMode repeatMode,
        out int intervalDays,
        out DayOfWeek weekday,
        out bool isEnabled)
    {
        name = NameTextBox.Text?.Trim() ?? string.Empty;
        repeatMode = GetSelectedRepeatMode();
        intervalDays = 1;
        weekday = GetSelectedWeekday();
        isEnabled = EnabledCheckBox.IsChecked == true;
        time = GetSelectedTime();

        if (repeatMode == ShutdownRepeatMode.EveryNDays)
        {
            if (!int.TryParse(
                    IntervalDaysTextBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out intervalDays)
                || intervalDays is < 1 or > 365)
            {
                SetValidation("间隔天数必须是 1 到 365 之间的整数。");
                return false;
            }
        }

        SetValidation(string.Empty);
        return true;
    }

    private ShutdownRepeatMode GetSelectedRepeatMode()
    {
        return RepeatComboBox.SelectedIndex switch
        {
            1 => ShutdownRepeatMode.Daily,
            2 => ShutdownRepeatMode.Weekly,
            3 => ShutdownRepeatMode.EveryNDays,
            _ => ShutdownRepeatMode.Once
        };
    }

    private DayOfWeek GetSelectedWeekday()
    {
        return WeekdayComboBox.SelectedIndex switch
        {
            0 => DayOfWeek.Monday,
            1 => DayOfWeek.Tuesday,
            2 => DayOfWeek.Wednesday,
            3 => DayOfWeek.Thursday,
            4 => DayOfWeek.Friday,
            5 => DayOfWeek.Saturday,
            6 => DayOfWeek.Sunday,
            _ => DayOfWeek.Monday
        };
    }

    private static int GetRepeatIndex(ShutdownRepeatMode mode)
    {
        return mode switch
        {
            ShutdownRepeatMode.Daily => 1,
            ShutdownRepeatMode.Weekly => 2,
            ShutdownRepeatMode.EveryNDays => 3,
            _ => 0
        };
    }

    private static int GetWeekdayIndex(DayOfWeek weekday)
    {
        return weekday switch
        {
            DayOfWeek.Monday => 0,
            DayOfWeek.Tuesday => 1,
            DayOfWeek.Wednesday => 2,
            DayOfWeek.Thursday => 3,
            DayOfWeek.Friday => 4,
            DayOfWeek.Saturday => 5,
            DayOfWeek.Sunday => 6,
            _ => 0
        };
    }

    private void RefreshView()
    {
        try
        {
            var rows = _service.GetRows();
            TaskItems.ItemsSource = rows;
            EmptyHint.IsVisible = rows.Count == 0;
            UpdateClock();
        }
        catch (Exception ex)
        {
            SetValidation($"读取任务失败：{ex.Message}");
        }
    }

    private void UpdateClock(DateTime? knownTime = null)
    {
        try
        {
            var now = knownTime ?? _service.GetCurrentCisTime();
            CisTimeText.Text = $"ClassIsland 时间：{now:yyyy-MM-dd HH:mm:ss}";
            SyncStatusText.Text = $"时间同步状态：{_service.TimeSyncStatus}";

            var next = _service.GetNextScheduledRun();
            NextRunText.Text = next is null
                ? "最近一次计划执行：—"
                : $"最近一次计划执行：{next:yyyy-MM-dd HH:mm:ss}";
            StatusText.Text = $"调度状态：{_service.LastMessage}";
        }
        catch (Exception ex)
        {
            CisTimeText.Text = "ClassIsland 时间：暂时不可用";
            StatusText.Text = $"调度状态：{ex.Message}";
        }
    }

    private void SetValidation(string message)
    {
        ValidationText.Text = message;
    }
}
