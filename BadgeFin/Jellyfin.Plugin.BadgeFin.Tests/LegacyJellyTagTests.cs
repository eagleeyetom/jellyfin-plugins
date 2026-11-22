using Jellyfin.Plugin.BadgeFin.Migration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.BadgeFin.Tests;

[TestClass]
public class LegacyJellyTagTests
{
    private string _root = null!;
    private string _configurations = null!;
    private string _plugins = null!;
    private string _configFile = null!;
    private string _dataFolder = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "BadgeFinTests", Guid.NewGuid().ToString("N"));
        _configurations = Path.Combine(_root, "plugins", "configurations");
        _plugins = Path.Combine(_root, "plugins");
        _configFile = Path.Combine(_configurations, "Jellyfin.Plugin.BadgeFin.xml");
        _dataFolder = Path.Combine(_plugins, "Jellyfin.Plugin.BadgeFin");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string LegacyBadges => Path.Combine(_plugins, LegacyJellyTag.AssemblyName, LegacyJellyTag.CustomBadgesFolderName);

    private string Badges => Path.Combine(_dataFolder, LegacyJellyTag.CustomBadgesFolderName);

    private MigrationResult Migrate() => LegacyJellyTag.Migrate(_configurations, _plugins, _configFile, _dataFolder);

    private void WriteLegacyConfig(string content)
    {
        Directory.CreateDirectory(_configurations);
        File.WriteAllText(Path.Combine(_configurations, "Jellyfin.Plugin.JellyTag.xml"), content);
    }

    [TestMethod]
    public void Migrate_CopiesSettingsAndCustomBadges()
    {
        WriteLegacyConfig("<PluginConfiguration><Enabled>false</Enabled></PluginConfiguration>");
        Directory.CreateDirectory(LegacyBadges);
        File.WriteAllText(Path.Combine(LegacyBadges, "4k.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(LegacyBadges, "logo-atmos.png"), "png");
        File.WriteAllText(Path.Combine(LegacyBadges, ".4k.svg.abc.tmp"), "partial upload");

        var result = Migrate();

        Assert.IsTrue(result.ConfigurationCopied);
        Assert.AreEqual(2, result.CustomBadgesCopied);
        Assert.AreEqual("<PluginConfiguration><Enabled>false</Enabled></PluginConfiguration>", File.ReadAllText(_configFile));
        CollectionAssert.AreEquivalent(
            new[] { "4k.svg", "logo-atmos.png" },
            Directory.GetFiles(Badges).Select(Path.GetFileName).ToArray());

        // Copied, not moved: a JellyTag install that is still present keeps its files.
        Assert.IsTrue(File.Exists(Path.Combine(_configurations, "Jellyfin.Plugin.JellyTag.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(LegacyBadges, "4k.svg")));
    }

    [TestMethod]
    public void Migrate_NeverOverwritesExistingBadgeFinData()
    {
        WriteLegacyConfig("<PluginConfiguration>old</PluginConfiguration>");
        File.WriteAllText(_configFile, "<PluginConfiguration>new</PluginConfiguration>");
        Directory.CreateDirectory(LegacyBadges);
        File.WriteAllText(Path.Combine(LegacyBadges, "4k.svg"), "old");
        Directory.CreateDirectory(Badges);

        var result = Migrate();

        Assert.IsFalse(result.ConfigurationCopied);
        Assert.AreEqual(0, result.CustomBadgesCopied);
        Assert.AreEqual("<PluginConfiguration>new</PluginConfiguration>", File.ReadAllText(_configFile));
        Assert.IsEmpty(Directory.GetFiles(Badges));
    }

    [TestMethod]
    public void Migrate_WithoutJellyTag_DoesNothing()
    {
        var result = Migrate();

        Assert.IsFalse(result.ConfigurationCopied);
        Assert.AreEqual(0, result.CustomBadgesCopied);
        Assert.IsFalse(File.Exists(_configFile));
        Assert.IsFalse(Directory.Exists(Badges));
    }

    [TestMethod]
    public void Migrate_RetriesAfterAnInterruptedBadgeCopy()
    {
        Directory.CreateDirectory(LegacyBadges);
        File.WriteAllText(Path.Combine(LegacyBadges, "4k.svg"), "<svg/>");
        Directory.CreateDirectory(Badges + ".migrating");
        File.WriteAllText(Path.Combine(Badges + ".migrating", "stale.svg"), "half-copied");

        var result = Migrate();

        Assert.AreEqual(1, result.CustomBadgesCopied);
        CollectionAssert.AreEquivalent(new[] { "4k.svg" }, Directory.GetFiles(Badges).Select(Path.GetFileName).ToArray());
        Assert.IsFalse(Directory.Exists(Badges + ".migrating"));
    }

    [TestMethod]
    public void IsActive_OnlyForAnEnabledJellyTag()
    {
        static LocalPlugin Plugin(Guid id, PluginStatus status) =>
            new("/plugins/x", true, new PluginManifest { Id = id, Name = "x", Status = status });

        Assert.IsTrue(LegacyJellyTag.IsActive(new[] { Plugin(LegacyJellyTag.PluginId, PluginStatus.Active) }));
        Assert.IsFalse(LegacyJellyTag.IsActive(new[] { Plugin(LegacyJellyTag.PluginId, PluginStatus.Disabled) }));
        Assert.IsFalse(LegacyJellyTag.IsActive(new[] { Plugin(LegacyJellyTag.PluginId, PluginStatus.Deleted) }));
        Assert.IsFalse(LegacyJellyTag.IsActive(new[] { Plugin(Guid.NewGuid(), PluginStatus.Active) }));
        Assert.IsFalse(LegacyJellyTag.IsActive(Array.Empty<LocalPlugin>()));
    }
}
