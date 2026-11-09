using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FragLab.Modes.Scrim;

/// <summary>
/// Uploads a finished demo to a Cloudflare R2 bucket (S3 API, signed with AWS Signature V4) and returns a public link.
/// The file name gets a random part so the link can't be guessed. Old demos are removed by a lifecycle rule in
/// Cloudflare (see the README), not by the plugin.
/// </summary>
public static class R2Upload
{
    private const string Region = "auto";
    private const string Service = "s3";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static bool IsConfigured(ScrimConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.R2AccountId) && !string.IsNullOrWhiteSpace(cfg.R2Bucket) &&
        !string.IsNullOrWhiteSpace(cfg.R2AccessKeyId) && !string.IsNullOrWhiteSpace(cfg.R2SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(cfg.R2PublicBaseUrl);

    /// <summary>Zips the demo, uploads it, posts the link to Discord (if a webhook is set) and returns the chat line.</summary>
    public static async Task<string> UploadDemoAsync(string demoPath, string summary, ScrimConfig cfg)
    {
        var zipPath = demoPath + ".zip";
        try
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(demoPath, Path.GetFileName(demoPath), CompressionLevel.SmallestSize);

            var baseName = Sanitize(Path.GetFileNameWithoutExtension(demoPath));
            var key = $"{baseName}-{Guid.NewGuid().ToString("N")[..8]}.zip";
            var url = await UploadAsync(zipPath, key, cfg);

            if (!string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl))
            {
                var text = $"{summary}\nDemo (link works for 24 hours): {url}";
                await Http.PostAsync(cfg.DiscordWebhookUrl, new StringContent(JsonSerializer.Serialize(new { content = text }), Encoding.UTF8, "application/json"));
            }
            return $"Demo ready (link works for 24 hours): {url}";
        }
        catch (Exception ex)
        {
            return $"Demo upload failed: {ex.Message}";
        }
        finally
        {
            try { File.Delete(zipPath); }
            catch (IOException) { }
        }
    }

    private static async Task<string> UploadAsync(string file, string fileName, ScrimConfig cfg)
    {
        var host = $"{cfg.R2AccountId}.r2.cloudflarestorage.com";
        var path = $"/{cfg.R2Bucket}/demos/{fileName}";

        var payloadHash = Hex(await SHA256.HashDataAsync(File.OpenRead(file)));
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        var canonicalRequest = $"PUT\n{path}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{dateStamp}/{Region}/{Service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + cfg.R2SecretAccessKey), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(Region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(Service));
        var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
        var signature = Hex(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));

        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://{host}{path}");
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={cfg.R2AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        await using var stream = File.OpenRead(file);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"R2 answered {(int)response.StatusCode}: {body}");
        }

        return $"{cfg.R2PublicBaseUrl.TrimEnd('/')}/demos/{fileName}";
    }

    private static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return clean.Length == 0 ? "demo" : clean;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
