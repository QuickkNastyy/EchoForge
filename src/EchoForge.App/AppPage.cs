namespace EchoForge.App;

/// <summary>
/// The three places EchoForge can be.
///
/// <para>
/// Record exists to record. Recordings is where a meeting is opened, processed and read. Settings
/// owns every choice about models, runtimes and how processing behaves — which is why none of
/// those choices appear on the other two. A person recording a call should not be looking at a
/// compute profile, and a person reading a brief should not be choosing one.
/// </para>
/// </summary>
public enum AppPage
{
    Record,
    Recordings,
    Settings,
}

/// <summary>
/// The sections of the Settings page, mirrored as sub-items in the navigation rail.
///
/// <para>
/// Settings expands in the rail rather than carrying a second sidebar of its own, so the one rail
/// is the only navigation in the application. Each section is a screen, not a scroll position.
/// </para>
/// </summary>
public enum SettingsSection
{
    Transcription,
    Briefs,
    Recordings,
    Models,
    Machine,
    Compare,
}
