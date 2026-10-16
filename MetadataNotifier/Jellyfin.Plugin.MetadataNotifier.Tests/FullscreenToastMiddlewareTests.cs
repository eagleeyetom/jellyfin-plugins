using System.IO;
using System.Text;
using Jellyfin.Plugin.MetadataNotifier.Middleware;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MetadataNotifier.Tests;

[TestClass]
public class FullscreenToastMiddlewareTests
{
    private const string IndexHtml = "<html><head></head><body><div id=\"app\"></div></body></html>";

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

    private static async Task<string> InvokeAsync(string path)
    {
        var middleware = new FullscreenToastMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html";
                await ctx.Response.WriteAsync(IndexHtml);
            },
            NullLogger<FullscreenToastMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/index.html")]
    [DataRow("/web/")]
    [DataRow("/web/index.html")]
    [DataRow("/jellyfin/web/")]
    [DataRow("/jellyfin/web/index.html")]
    [DataRow("/media/jellyfin/web/INDEX.HTML")]
    public async Task InvokeAsync_IndexHtmlWithOrWithoutBaseUrl_InjectsScript(string path)
    {
        var paths = new DummyApplicationPaths();
        try
        {
            var plugin = new Plugin(paths, new DummyXmlSerializer());
            plugin.Configuration.EnableFullscreenToastFix = true;

            var html = await InvokeAsync(path);

            StringAssert.Contains(html, "syncToastContainer");
            Assert.IsTrue(
                html.IndexOf("syncToastContainer", StringComparison.Ordinal) < html.LastIndexOf("</body>", StringComparison.Ordinal),
                "Script should be injected before </body>");
        }
        finally
        {
            paths.Cleanup();
        }
    }

    [TestMethod]
    [DataRow("/jellyfin/web/main.bundle.js")]
    [DataRow("/jellyfin/")]
    [DataRow("/Items/abc/Images/Primary")]
    [DataRow("/webhooks/index.html")]
    public async Task InvokeAsync_NonIndexPath_LeavesResponseUnchanged(string path)
    {
        var paths = new DummyApplicationPaths();
        try
        {
            var plugin = new Plugin(paths, new DummyXmlSerializer());
            plugin.Configuration.EnableFullscreenToastFix = true;

            var html = await InvokeAsync(path);

            Assert.AreEqual(IndexHtml, html);
        }
        finally
        {
            paths.Cleanup();
        }
    }
}
