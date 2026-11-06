using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// The community links (Discord, Discord help, YouTube, website), compiled in. Upstream refreshes them from the website
/// once a day; the Linux build makes no network calls, so it keeps the built-in ones.
/// </summary>
internal static class Community
{
    public static CommunityLinks Links => CommunityLinks.Defaults;
}
