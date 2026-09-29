using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OpenVideoMenu;

/// <summary>
/// Open Video Menu for Jellyfin: serves menu manifests (and only the menu art they name) from the
/// libraries' own folders, over the same API as the standalone Open Video Menu asset server, so TV apps
/// need no separate server. Signed-in Jellyfin users only.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "Open Video Menu";

    public override Guid Id => Guid.Parse("6d1c3a52-4f0b-4d7e-9a5e-3c2b7f1e8a90");

    public override string Description =>
        "Menus for your films and shows: serves .openvideomenu.json manifests and their menu art to Open Video Menu players such as ovmplayer.";
}

/// <summary>No settings yet: the plugin reads the libraries Jellyfin already has.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
}

/// <summary>Registers the manifest catalog with Jellyfin's service container.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MenuCatalog>();
    }
}
