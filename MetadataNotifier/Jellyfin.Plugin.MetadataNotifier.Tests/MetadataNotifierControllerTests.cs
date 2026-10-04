using System.IO;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using Jellyfin.Plugin.MetadataNotifier.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MetadataNotifier.Tests;

[TestClass]
public class MetadataNotifierControllerTests
{
    private class DummyApplicationPaths : IApplicationPaths
    {
        private readonly string _tempDir;

        public DummyApplicationPaths()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MetadataNotifierTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public string ProgramDataPath => _tempDir;
        public string WebPath => _tempDir;
        public string ProgramSystemPath => _tempDir;
        public string DataPath => _tempDir;
        public string ImageCachePath => _tempDir;
        public string PluginsPath => _tempDir;
        public string PluginConfigurationsPath => _tempDir;
        public string LogDirectoryPath => _tempDir;
        public string ConfigurationDirectoryPath => _tempDir;
        public string SystemConfigurationFilePath => Path.Combine(_tempDir, "system.xml");
        public string CachePath => _tempDir;
        public string TempDirectory => _tempDir;
        public string VirtualDataPath => _tempDir;
        public string TrickplayPath => _tempDir;
        public string BackupPath => _tempDir;

        public void MakeSanityCheckOrThrow() { }
        public void CreateAndCheckMarker(string path, string marker, bool deleteMarker) { }

        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
            }
        }
    }

    private class DummyXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromBytes(Type type, byte[] buffer) => Activator.CreateInstance(type)!;
        public object DeserializeFromFile(Type type, string file) => Activator.CreateInstance(type)!;
        public object DeserializeFromStream(Type type, Stream stream) => Activator.CreateInstance(type)!;
        public object DeserializeFromString(Type type, string text) => Activator.CreateInstance(type)!;
        public void SerializeToFile(object obj, string file) { }
        public void SerializeToStream(object obj, Stream stream) { }
    }

    private static IFormFile CreateFormFile(string content, string fileName = "config.json")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/json"
        };
    }

    [TestMethod]
    public void ExportConfig_WhenPluginLoaded_ReturnsJsonFile()
    {
        var paths = new DummyApplicationPaths();
        try
        {
            var plugin = new Plugin(paths, new DummyXmlSerializer());
            plugin.Configuration.NotificationDurationMs = 7500;
            plugin.Configuration.ShowBitrate = false;

            var controller = new MetadataNotifierController();
            var result = controller.ExportConfig();

            Assert.IsInstanceOfType<FileContentResult>(result);
            var fileResult = (FileContentResult)result;
            Assert.AreEqual("application/json", fileResult.ContentType);
            Assert.AreEqual("metadata-notifier-config.json", fileResult.FileDownloadName);

            var json = Encoding.UTF8.GetString(fileResult.FileContents);
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(7500, doc.RootElement.GetProperty("NotificationDurationMs").GetInt32());
            Assert.IsFalse(doc.RootElement.GetProperty("ShowBitrate").GetBoolean());
        }
        finally
        {
            paths.Cleanup();
        }
    }

    [TestMethod]
    public async Task ImportConfig_WhenValidFile_UpdatesConfiguration()
    {
        var paths = new DummyApplicationPaths();
        try
        {
            var plugin = new Plugin(paths, new DummyXmlSerializer());
            plugin.Configuration.NotificationDurationMs = 5000;
            plugin.Configuration.ShowSdr = true;

            var newConfig = new PluginConfiguration
            {
                NotificationDurationMs = 9000,
                ShowSdr = false,
                CustomTemplate = "{title} - {hdr}"
            };
            var json = JsonSerializer.Serialize(newConfig);
            var file = CreateFormFile(json);

            var controller = new MetadataNotifierController();
            var result = await controller.ImportConfig(file);

            Assert.IsInstanceOfType<NoContentResult>(result);
            Assert.AreEqual(9000, plugin.Configuration.NotificationDurationMs);
            Assert.IsFalse(plugin.Configuration.ShowSdr);
            Assert.AreEqual("{title} - {hdr}", plugin.Configuration.CustomTemplate);
        }
        finally
        {
            paths.Cleanup();
        }
    }

    [TestMethod]
    public async Task ImportConfig_WhenEmptyOrNullFile_ReturnsBadRequest()
    {
        var paths = new DummyApplicationPaths();
        try
        {
            _ = new Plugin(paths, new DummyXmlSerializer());
            var controller = new MetadataNotifierController();

            var nullResult = await controller.ImportConfig(null!);
            Assert.IsInstanceOfType<BadRequestObjectResult>(nullResult);

            var emptyFile = CreateFormFile(string.Empty);
            var emptyResult = await controller.ImportConfig(emptyFile);
            Assert.IsInstanceOfType<BadRequestObjectResult>(emptyResult);
        }
        finally
        {
            paths.Cleanup();
        }
    }

    [TestMethod]
    public async Task ImportConfig_WhenInvalidJson_ReturnsBadRequest()
    {
        var paths = new DummyApplicationPaths();
        try
        {
            _ = new Plugin(paths, new DummyXmlSerializer());
            var controller = new MetadataNotifierController();
            var file = CreateFormFile("This is not JSON");

            var result = await controller.ImportConfig(file);
            Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        }
        finally
        {
            paths.Cleanup();
        }
    }

    [TestMethod]
    public void ResetConfig_WhenPluginLoaded_ResetsToDefaults()
    {
        var paths = new DummyApplicationPaths();
        try
        {
            var plugin = new Plugin(paths, new DummyXmlSerializer());
            plugin.Configuration.NotificationDurationMs = 12000;
            plugin.Configuration.ShowSdr = false;
            plugin.Configuration.ShowAudio = false;

            var controller = new MetadataNotifierController();
            var result = controller.ResetConfig();

            Assert.IsInstanceOfType<NoContentResult>(result);
            Assert.AreEqual(5000, plugin.Configuration.NotificationDurationMs);
            Assert.IsTrue(plugin.Configuration.ShowSdr);
            Assert.IsTrue(plugin.Configuration.ShowAudio);
        }
        finally
        {
            paths.Cleanup();
        }
    }
}
