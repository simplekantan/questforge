using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Dalamud.Plugin;

namespace QuestForge.Plugin.DataDelivery;

internal sealed class DataUpdateService : IDisposable
{
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/simplekantan/questforge-data/releases/latest";

    private static readonly byte[] PublicKeySpki = Convert.FromBase64String(
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvR2VLxRXmKIm+zo+/A6ynA3pEz3X" +
        "qy59BMvaPf9yYYbwutq6gKnekilvt5VkRg4mSjzcWeIJS+7peT7PQaBSmw==");

    private readonly HttpClient _http;
    private readonly string _dataRoot;
    private readonly string _versionFile;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
    private Action? _onDataUpdated;

    internal DataUpdateService(IDalamudPluginInterface pi, Dalamud.Plugin.Services.IPluginLog log, Action? onDataUpdated = null)
    {
        _log = log;
        _onDataUpdated = onDataUpdated;
        _dataRoot = pi.GetPluginConfigDirectory();
        _versionFile = Path.Combine(_dataRoot, "data-version.txt");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuestForge/1.0");
    }

    internal string? LocalVersion
    {
        get
        {
            try { return File.Exists(_versionFile) ? File.ReadAllText(_versionFile).Trim() : null; }
            catch { return null; }
        }
    }

    internal async Task CheckAndUpdateAsync(CancellationToken ct)
    {
        try
        {
            var release = await FetchLatestRelease(ct);
            if (release is null) return;

            var local = LocalVersion;
            if (string.Equals(local, release.TagName, StringComparison.Ordinal))
            {
                _log.Debug($"[DataUpdate] up to date: {local}");
                return;
            }

            _log.Info($"[DataUpdate] new version available: {release.TagName} (local: {local ?? "none"})");

            var zipAsset = release.Assets.FirstOrDefault(a => a.Name == "questforge-data.zip");
            var shaAsset = release.Assets.FirstOrDefault(a => a.Name == "questforge-data.zip.sha256");
            var sigAsset = release.Assets.FirstOrDefault(a => a.Name == "questforge-data.zip.sig");

            if (zipAsset is null || shaAsset is null || sigAsset is null)
            {
                _log.Warning("[DataUpdate] release is missing expected assets, skipping");
                return;
            }

            var zipBytes = await Download(zipAsset.BrowserDownloadUrl, ct);
            var shaRawBytes = await Download(shaAsset.BrowserDownloadUrl, ct);
            var shaText = System.Text.Encoding.UTF8.GetString(shaRawBytes).Trim();
            var sigBytes = await Download(sigAsset.BrowserDownloadUrl, ct);

            if (!VerifyIntegrity(zipBytes, shaText))
            {
                _log.Error("[DataUpdate] SHA256 mismatch — download corrupted or tampered");
                return;
            }

            if (!VerifySignature(shaRawBytes, sigBytes))
            {
                _log.Error("[DataUpdate] signature verification failed — rejecting update");
                return;
            }

            ExtractData(zipBytes);

            File.WriteAllText(_versionFile, release.TagName);
            _log.Info($"[DataUpdate] updated to {release.TagName}");
            _onDataUpdated?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex)
        {
            _log.Debug($"[DataUpdate] network error (offline?): {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[DataUpdate] unexpected error during update check");
        }
    }

    private async Task<GitHubRelease?> FetchLatestRelease(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ReleaseApiUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.Debug($"[DataUpdate] GitHub API returned {response.StatusCode}");
            return null;
        }
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
    }

    private async Task<byte[]> Download(string url, CancellationToken ct)
    {
        _log.Debug($"[DataUpdate] downloading {url}");
        return await _http.GetByteArrayAsync(url, ct);
    }

    private static bool VerifyIntegrity(byte[] zipBytes, string expectedHash)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool VerifySignature(byte[] shaFileBytes, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(PublicKeySpki, out _);
        return ecdsa.VerifyData(shaFileBytes, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
    }

    private void ExtractData(byte[] zipBytes)
    {
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var targetPath = Path.Combine(_dataRoot, entry.FullName);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir is not null)
                Directory.CreateDirectory(targetDir);

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

