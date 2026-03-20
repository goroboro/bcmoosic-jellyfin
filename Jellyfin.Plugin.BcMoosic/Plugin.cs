using System;
using System.Collections.Generic;
using Jellyfin.Plugin.BcMoosic.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BcMoosic;

/// <summary>
/// bcMoosic Jellyfin plugin — browse and download Bandcamp purchases into your music library.
/// The web app is served by BcMoosicController at /BcMoosic/.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Gets the singleton plugin instance (set on construction).</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets Jellyfin's own temp directory — guaranteed writable by the Jellyfin process.</summary>
    public string JellyfinTempPath { get; }

    /// <inheritdoc />
    public override Guid Id => new("d8f3c2a4-e1b7-4f5d-8c9e-2a3b4c5d6e7f");

    /// <inheritdoc />
    public override string Name => "bcMoosic";

    /// <inheritdoc />
    public override string Description =>
        "Browse and download your Bandcamp music collection into your Jellyfin library.";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        JellyfinTempPath = applicationPaths.TempDirectory;
        Instance = this;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        }
    ];
}
