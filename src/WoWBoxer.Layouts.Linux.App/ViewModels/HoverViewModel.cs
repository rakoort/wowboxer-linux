using System.Windows.Input;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>
/// The hover-to-activate switch (Setup, Settings, tray, Guide). On Linux it is WoWBoxer's own setting, done by its KWin
/// script between WoW windows only (upstream changes Windows' "Activate a window by hovering over it with the mouse").
/// Changes only when the user asks; the values are kept in the settings and given to the agent at start. Hover ends when
/// the app quits; with "turn off on quit" unticked it is on again at the next start.
/// </summary>
public sealed class HoverViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly StatusLog _status;
    private HoverSettings _now;
    private int _delayPreview;

    public HoverViewModel(AppSettings settings, StatusLog status, Action openGuide, Action<string> openUrl)
    {
        _settings = settings;
        _status = status;
        if (settings.Hover is { } saved) HoverActivation.Write(saved);
        _now = HoverActivation.Read();
        _delayPreview = _now.DelayMs;
        OpenGuideCommand = new RelayCommand(openGuide);
        OpenRulesCommand = new RelayCommand(() => openUrl(RulesUrl));
        CommitDelayCommand = new RelayCommand(CommitDelay);
        ResetDelayCommand = new RelayCommand(ResetDelay, () => _now.DelayMs != DefaultDelay || _delayPreview != DefaultDelay);
    }

    public const string RulesUrl = MainViewModel.WebsiteUrl + "/rules/#hover-to-activate-a-windows-setting";
    private const int DefaultDelay = HoverSettings.DefaultDelayMs;

    public ICommand OpenGuideCommand { get; }
    public ICommand OpenRulesCommand { get; }
    /// <summary>The delay slider was let go (mouse up or a key): give the new delay to the agent.</summary>
    public ICommand CommitDelayCommand { get; }
    /// <summary>"Reset to default": the delay back to 30 ms, at once.</summary>
    public ICommand ResetDelayCommand { get; }

    public bool Enabled
    {
        get => _now.Enabled;
        set
        {
            // On always starts at the default delay (written together with on); the slider jumps there with it.
            if (value) Apply(s => s.TurnedOn(), $"Hover to activate is on (delay {DefaultDelay} ms): the WoW window under the mouse becomes active.");
            else Apply(s => s with { Enabled = false }, "Hover to activate is off.");
        }
    }

    public bool BringToFront
    {
        get => _now.BringToFront;
        set => Apply(s => s with { BringToFront = value }, value ? "Hover to activate brings the window to the front." : "Hover to activate leaves the window where it is in the stack.");
    }

    /// <summary>The slider's value while it moves (label only, steps of 5 ms); <see cref="CommitDelay"/> writes it when it is let go.</summary>
    public double DelayPreview
    {
        get => _delayPreview;
        set => SetDelayPreview((int)Math.Round(value / 5) * 5);
    }

    public string DelayText => _delayPreview == DefaultDelay ? $"{_delayPreview} ms · default" : $"{_delayPreview} ms";

    public string DefaultDelayText => $"Default: {DefaultDelay} ms";

    private void SetDelayPreview(int ms)
    {
        if (ms == _delayPreview) return;
        _delayPreview = ms;
        OnPropertyChanged(nameof(DelayPreview));
        OnPropertyChanged(nameof(DelayText));
    }

    public void CommitDelay()
    {
        var ms = HoverSettings.ClampDelay(_delayPreview);
        if (ms != _now.DelayMs) Apply(s => s with { DelayMs = ms }, $"Hover to activate: delay {ms} ms.");
    }

    private void ResetDelay()
    {
        SetDelayPreview(DefaultDelay);
        Apply(s => s with { DelayMs = DefaultDelay }, $"Hover to activate: delay back to the default, {DefaultDelay} ms.");
    }

    /// <summary>Turn hover to activate off when WoWBoxer quits (on by default). Off: it is on again at the next start.</summary>
    public bool TurnOffOnExit
    {
        get => _settings.TurnHoverOffOnExit;
        set
        {
            _settings.TurnHoverOffOnExit = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Picks up what the agent has now (another WoWBoxer start, wbx). Cheap: no system call.</summary>
    public void Refresh()
    {
        var now = HoverActivation.Read();
        if (now == _now) return;
        var delayChanged = now.DelayMs != _now.DelayMs;
        _now = now;
        if (delayChanged) SetDelayPreview(now.DelayMs);
        RaiseAll();
    }

    /// <summary>On quit: hover off for the next start too, unless the user keeps it (the agent stops it either way).</summary>
    public void OnAppExit()
    {
        if (!_settings.TurnHoverOffOnExit || _settings.Hover is not { Enabled: true } saved) return;
        _settings.Hover = saved with { Enabled = false };
        _settings.Save();
    }

    /// <summary>One change on top of what the agent has now.</summary>
    private void Apply(Func<HoverSettings, HoverSettings> change, string message)
    {
        var now = HoverActivation.Read();
        var wanted = change(now);
        if (wanted == now)
        {
            Refresh();
            return;
        }
        if (HoverActivation.Write(wanted))
        {
            _settings.Hover = wanted;
            _settings.Save();
            _status.Ok(message);
        }
        else _status.Warn("WoWBoxer's KWin script did not take the hover-to-activate change.");
        _now = HoverActivation.Read();
        SetDelayPreview(_now.DelayMs);
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(BringToFront));
        CommandManager.InvalidateRequerySuggested();
    }
}
