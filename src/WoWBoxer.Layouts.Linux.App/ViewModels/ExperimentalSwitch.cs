using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>One row in Settings › Experimental: a risky feature's on/off switch (off until the user turns it on).</summary>
public sealed class ExperimentalSwitch(ExperimentalFeature feature, AppSettings settings, StatusLog status) : ObservableObject
{
    public string Id => feature.Id;
    public string Title => feature.Title;
    public string Description => feature.Description;

    public bool IsOn
    {
        get => ExperimentalFeatures.IsOn(settings.Experimental, feature.Id);
        set
        {
            if (value == IsOn) return;
            settings.Experimental = ExperimentalFeatures.Set(settings.Experimental, feature.Id, value);
            settings.Save();
            status.Ok(value ? $"Experimental: {feature.Title} is on." : $"Experimental: {feature.Title} is off.");
            OnPropertyChanged();
        }
    }
}
