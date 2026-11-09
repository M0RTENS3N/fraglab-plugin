using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FragLab.Modes.Scrim;

/// <summary>
/// Posts a finished demo to a Discord webhook: zipped and attached when it fits Discord's upload limit,
/// otherwise a message with the match info and a download link (DemoDownloadUrl) when one is set.
/// Runs off the game thread; the result is a short line for chat.
/// </summary>
public static class DemoUploader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<string> UploadAsync(string demoPath, string summary, ScrimConfig cfg)
    {
        if (!await WaitForFileAsync(demoPath))
            return $"Demo upload failed: {Path.GetFileName(demoPath)} was not found on the server.";

        if (R2Upload.IsConfigured(cfg))
            return await R2Upload.UploadDemoAsync(demoPath, summary, cfg);

        var demoName = Path.GetFileName(demoPath);
        string? zipPath = null;
        try
        {
            var upload = demoPath;
            if (cfg.ZipDemoForDiscord)
            {
                zipPath = demoPath + ".zip";
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    zip.CreateEntryFromFile(demoPath, demoName, CompressionLevel.SmallestSize);
                upload = zipPath;
            }

            var size = new FileInfo(upload).Length;
            var limit = (long)(cfg.DiscordMaxUploadMb * 1024 * 1024);
            var link = string.IsNullOrWhiteSpace(cfg.DemoDownloadUrl)
                ? null
                : cfg.DemoDownloadUrl.Replace("{file}", Uri.EscapeDataString(demoName));

            var text = new StringBuilder(summary)
                .Append($"\nDemo: `{demoName}` ({Mb(new FileInfo(demoPath).Length)})");

            if (size <= limit)
            {
                if (link != null)
                    text.Append($"\nDownload: {link}");

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(Payload(text), Encoding.UTF8, "application/json"), "payload_json");
                await using var stream = File.OpenRead(upload);
                form.Add(new StreamContent(stream), "files[0]", Path.GetFileName(upload));
                var response = await Http.PostAsync(cfg.DiscordWebhookUrl, form);
                return response.IsSuccessStatusCode
                    ? "Demo uploaded."
                    : $"Demo upload failed: Discord answered {(int)response.StatusCode}.";
            }

            text.Append(link != null
                ? $"\nToo big to attach ({Mb(size)}), download: {link}"
                : $"\nToo big to attach ({Mb(size)}, limit {cfg.DiscordMaxUploadMb} MB). It's on the server in csgo/{cfg.DemoDirectory}.");
            var post = await Http.PostAsync(cfg.DiscordWebhookUrl, new StringContent(Payload(text), Encoding.UTF8, "application/json"));
            return post.IsSuccessStatusCode
                ? "Demo info uploaded (the file was too big to attach)."
                : $"Discord post failed: Discord answered {(int)post.StatusCode}.";
        }
        catch (Exception ex)
        {
            return $"Demo upload failed: {ex.Message}";
        }
        finally
        {
            if (zipPath != null)
            {
                try { File.Delete(zipPath); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>The demo is written when recording stops; wait until it exists and stops growing (up to a minute).</summary>
    private static async Task<bool> WaitForFileAsync(string path)
    {
        long lastSize = -1;
        for (var i = 0; i < 60; i++)
        {
            if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && size == lastSize)
                    return true;
                lastSize = size;
            }
            await Task.Delay(1000);
        }
        return false;
    }

    private static string Payload(StringBuilder text) => JsonSerializer.Serialize(new { content = text.ToString() });

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}
