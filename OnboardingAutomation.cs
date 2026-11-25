using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

public sealed class OnboardingAutomationService
{
    private readonly ConfigStore _config;
    private readonly StackHealthService _health;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public OnboardingAutomationService(ConfigStore config, StackHealthService health)
    {
        _config = config;
        _health = health;
    }

    public async Task<AutomationResult> DiscoverAsync(bool apply, CancellationToken cancellationToken)
    {
        var options = _config.GetBotOptions();
        var found = new List<AutomationItem>();
        var patch = new JsonObject();

        await DiscoverArrAsync("Radarr", options.Radarr, "http://127.0.0.1:7878", @"C:\ProgramData\Radarr\config.xml", patch, found, cancellationToken);
        await DiscoverArrAsync("Sonarr", options.Sonarr, "http://127.0.0.1:8989", @"C:\ProgramData\Sonarr\config.xml", patch, found, cancellationToken);
        await DiscoverArrAsync("Prowlarr", options.Prowlarr, "http://127.0.0.1:9696", @"C:\ProgramData\Prowlarr\config.xml", patch, found, cancellationToken);
        await DiscoverUrlAsync("qBittorrent", options.Qbittorrent.Url, "http://127.0.0.1:18080", "/api/v2/app/version", patch, "Qbittorrent", found, cancellationToken);
        await DiscoverUrlAsync("FlareSolverr", options.FlareSolverr.Url, "http://127.0.0.1:8191", "/", patch, "FlareSolverr", found, cancellationToken);

        if (apply && patch.Count > 0)
        {
            _config.SaveConfig(patch);
        }

        return new AutomationResult(found.All(item => item.Ok), apply ? "Discovery completed and detected settings were saved." : "Discovery completed. Review detected settings before applying them.", found);
    }

    public async Task<AutomationResult> TestConnectionAsync(string service, CancellationToken cancellationToken)
    {
        var options = _config.GetBotOptions();
        try
        {
            return service.ToLowerInvariant() switch
            {
                "radarr" => await TestArrAsync("Radarr", options.Radarr, "/api/v3/system/status", cancellationToken),
                "sonarr" => await TestArrAsync("Sonarr", options.Sonarr, "/api/v3/system/status", cancellationToken),
                "prowlarr" => await TestArrAsync("Prowlarr", options.Prowlarr, "/api/v1/system/status", cancellationToken),
                "qbittorrent" => await TestQbittorrentAsync(options.Qbittorrent, cancellationToken),
                "flaresolverr" => await TestFlareSolverrAsync(options.FlareSolverr.Url, cancellationToken),
                "discord" => await TestDiscordAsync(options.DiscordToken, cancellationToken),
                _ => new AutomationResult(false, $"Unknown service: {service}", Array.Empty<AutomationItem>())
            };
        }
        catch (Exception ex)
        {
            return Failure(service, Clean(ex));
        }
    }

    public async Task<AutomationResult> WireIntegrationsAsync(CancellationToken cancellationToken)
    {
        var options = _config.GetBotOptions();
        var items = new List<AutomationItem>();
        await EnsureRootFolderAsync("Radarr", options.Radarr, options.Radarr.RootFolderPath, items, cancellationToken);
        await EnsureRootFolderAsync("Sonarr", options.Sonarr, options.Sonarr.RootFolderPath, items, cancellationToken);
        await EnsureProwlarrApplicationAsync("Radarr", options.Prowlarr, options.Radarr, items, cancellationToken);
        await EnsureProwlarrApplicationAsync("Sonarr", options.Prowlarr, options.Sonarr, items, cancellationToken);
        await EnsureDownloadClientAsync("Radarr", options.Radarr, options.Qbittorrent, "radarr", items, cancellationToken);
        await EnsureDownloadClientAsync("Sonarr", options.Sonarr, options.Qbittorrent, "sonarr", items, cancellationToken);
        var ok = items.Count > 0 && items.All(item => item.Ok);
        return new AutomationResult(ok, ok ? "Required application and download-client links are configured." : "Some integrations still need attention.", items);
    }

    public async Task<AutomationResult> ConfigureRootFoldersAsync(string service, CancellationToken cancellationToken)
    {
        var options = _config.GetBotOptions();
        var items = new List<AutomationItem>();
        if (service.Equals("radarr", StringComparison.OrdinalIgnoreCase))
            await EnsureRootFolderAsync("Radarr", options.Radarr, options.Radarr.RootFolderPath, items, cancellationToken);
        else if (service.Equals("sonarr", StringComparison.OrdinalIgnoreCase))
            await EnsureRootFolderAsync("Sonarr", options.Sonarr, options.Sonarr.RootFolderPath, items, cancellationToken);
        else
            return new AutomationResult(false, "Service must be radarr or sonarr.", Array.Empty<AutomationItem>());
        var ok = items.All(item => item.Ok);
        return new AutomationResult(ok, ok ? $"{items[0].Name} is configured." : "The root folder still needs attention.", items);
    }

    private async Task EnsureRootFolderAsync(string appName, ArrEndpointOptions endpoint, string path, List<AutomationItem> items, CancellationToken cancellationToken)
    {
        var label = $"{appName} root folder";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new AutomationItem(label, false, "Choose a media root path in the wizard first."));
                return;
            }

            var roots = await SendArrAsync(endpoint, HttpMethod.Get, "/api/v3/rootfolder", null, cancellationToken);
            if (roots?.AsArray().Any(node => PathsEqual(node?["path"]?.ToString(), path)) == true)
            {
                items.Add(new AutomationItem(label, true, $"Already configured: {path}"));
                return;
            }

            await SendArrAsync(endpoint, HttpMethod.Post, "/api/v3/rootfolder", new JsonObject { ["path"] = path }, cancellationToken);
            items.Add(new AutomationItem(label, true, $"Created in {appName}: {path}"));
        }
        catch (Exception ex)
        {
            items.Add(new AutomationItem(label, false, Clean(ex)));
        }
    }

    public Task<AutomationResult> ValidatePathsAsync(CancellationToken cancellationToken)
    {
        var options = _config.GetBotOptions();
        var items = new[]
        {
            ValidatePath("Radarr movies root", options.Radarr.RootFolderPath),
            ValidatePath("Sonarr TV root", options.Sonarr.RootFolderPath),
            ValidatePath("Starrboard data", Path.GetDirectoryName(options.DatabasePath) ?? options.DatabasePath)
        };
        return Task.FromResult(new AutomationResult(items.All(item => item.Ok), items.All(item => item.Ok) ? "All configured paths are readable and writable by Starrboard." : "One or more paths failed access validation.", items));
    }

    public async Task<AutomationResult> TestWorkflowAsync(CancellationToken cancellationToken)
    {
        var options = _config.GetBotOptions();
        var items = new List<AutomationItem>();
        var status = await _health.GetStatusAsync(cancellationToken);
        items.Add(new AutomationItem("Stack health", status.OverallStatus == "ok", status.OverallStatus == "ok" ? "All standard health checks pass." : $"Stack health is {status.OverallStatus}."));
        items.Add(await DryRunLookupAsync("Radarr movie lookup", options.Radarr, "/api/v3/movie/lookup?term=tmdb:550", cancellationToken));
        items.Add(await DryRunLookupAsync("Sonarr series lookup", options.Sonarr, "/api/v3/series/lookup?term=tvdb:121361", cancellationToken));
        items.Add(await HasProwlarrAppsAsync(options.Prowlarr, cancellationToken));
        items.Add((await TestQbittorrentAsync(options.Qbittorrent, cancellationToken)).Items.First());
        items.Add((await TestDiscordAsync(options.DiscordToken, cancellationToken)).Items.First());
        var paths = await ValidatePathsAsync(cancellationToken);
        items.AddRange(paths.Items);
        var ok = items.All(item => item.Ok);
        return new AutomationResult(ok, ok ? "Dry-run workflow passed. No media was added or downloaded." : "Dry-run workflow found blocking items. No media was added or downloaded.", items);
    }

    private async Task DiscoverArrAsync(string name, ArrEndpointOptions current, string defaultUrl, string configPath, JsonObject patch, List<AutomationItem> items, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(current.Url) ? defaultUrl : current.Url;
        var apiKey = IsUsableSecret(current.ApiKey) ? current.ApiKey : ReadApiKey(name, configPath);
        var endpoint = name == "Prowlarr" ? "/api/v1/system/status" : "/api/v3/system/status";
        var ok = !string.IsNullOrWhiteSpace(apiKey) && await ProbeArrAsync(url, apiKey, endpoint, cancellationToken);
        var failure = string.IsNullOrWhiteSpace(apiKey)
            ? $"{name} is installed, but its API key file is not available yet. Starrboard will retry while the service starts."
            : $"Found the {name} API key, but could not validate {url} yet.";
        items.Add(new AutomationItem(name, ok, ok ? $"Detected at {url}; API key validated." : failure));
        if (ok)
        {
            patch[name] = new JsonObject { ["Url"] = url, ["ApiKey"] = apiKey };
        }
    }

    private async Task DiscoverUrlAsync(string name, string currentUrl, string defaultUrl, string path, JsonObject patch, string section, List<AutomationItem> items, CancellationToken cancellationToken)
    {
        var candidates = new[] { currentUrl, defaultUrl }.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var url in candidates)
        {
            try
            {
                using var response = await _http.GetAsync(url.TrimEnd('/') + path, cancellationToken);
                if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    items.Add(new AutomationItem(name, true, $"Detected at {url}."));
                    patch[section] = new JsonObject { ["Url"] = url };
                    return;
                }
            }
            catch { }
        }
        items.Add(new AutomationItem(name, false, $"Not detected at {defaultUrl}."));
    }

    private async Task<AutomationResult> TestArrAsync(string name, ArrEndpointOptions endpoint, string path, CancellationToken cancellationToken)
    {
        var node = await SendArrAsync(endpoint, HttpMethod.Get, path, null, cancellationToken);
        var version = node?["version"]?.ToString();
        return Success(name, string.IsNullOrWhiteSpace(version) ? "API connection succeeded." : $"API connection succeeded. Version {version}.");
    }

    private async Task<AutomationResult> TestQbittorrentAsync(DownloadClientOptions options, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            using var login = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = options.Username, ["password"] = options.Password });
            using var loginResponse = await client.PostAsync(options.Url.TrimEnd('/') + "/api/v2/auth/login", login, cancellationToken);
            if (!loginResponse.IsSuccessStatusCode)
            {
                return Failure("qBittorrent", "Web UI authentication failed.");
            }
        }
        using var response = await client.GetAsync(options.Url.TrimEnd('/') + "/api/v2/app/version", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? Success("qBittorrent", $"Web UI API connection succeeded. Version {body.Trim()}.")
            : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? Failure("qBittorrent", "Web UI requires authentication. Save its username and password in this onboarding step, then test again.")
                : Failure("qBittorrent", $"Web UI returned HTTP {(int)response.StatusCode}.");
    }

    private async Task<AutomationResult> TestFlareSolverrAsync(string url, CancellationToken cancellationToken)
    {
        var payload = new StringContent("{\"cmd\":\"sessions.list\"}", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url.TrimEnd('/') + "/v1", payload, cancellationToken);
        return response.IsSuccessStatusCode ? Success("FlareSolverr", "API connection succeeded.") : Failure("FlareSolverr", $"API returned HTTP {(int)response.StatusCode}.");
    }

    private async Task<AutomationResult> TestDiscordAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsUsableSecret(token)) return Failure("Discord", "Bot token is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return Failure("Discord", $"Discord returned HTTP {(int)response.StatusCode}.");
        var bot = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Success("Discord", $"Authenticated as {bot?["username"]?.ToString() ?? "configured bot"}.");
    }

    private async Task EnsureProwlarrApplicationAsync(string appName, ArrEndpointOptions prowlarr, ArrEndpointOptions target, List<AutomationItem> items, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await SendArrAsync(prowlarr, HttpMethod.Get, "/api/v1/applications", null, cancellationToken);
            if (existing?.AsArray().Any(node => string.Equals(node?["implementation"]?.ToString(), appName, StringComparison.OrdinalIgnoreCase)) == true)
            {
                items.Add(new AutomationItem($"Prowlarr to {appName}", true, "Application link already exists; no change made."));
                return;
            }
            var schemas = await SendArrAsync(prowlarr, HttpMethod.Get, "/api/v1/applications/schema", null, cancellationToken);
            var schema = schemas?.AsArray().Select(node => node?.DeepClone().AsObject()).FirstOrDefault(node => string.Equals(node?["implementation"]?.ToString(), appName, StringComparison.OrdinalIgnoreCase));
            if (schema == null) throw new InvalidOperationException($"Prowlarr did not return a {appName} application schema.");
            schema["name"] = appName;
            SetField(schema, "prowlarrUrl", prowlarr.Url);
            SetField(schema, "baseUrl", target.Url);
            SetField(schema, "apiKey", target.ApiKey);
            SetField(schema, "syncLevel", "fullSync");
            await SendArrAsync(prowlarr, HttpMethod.Post, "/api/v1/applications", schema, cancellationToken);
            items.Add(new AutomationItem($"Prowlarr to {appName}", true, "Application link created."));
        }
        catch (Exception ex) { items.Add(new AutomationItem($"Prowlarr to {appName}", false, Clean(ex))); }
    }

    private async Task EnsureDownloadClientAsync(string appName, ArrEndpointOptions arr, DownloadClientOptions qbit, string category, List<AutomationItem> items, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await SendArrAsync(arr, HttpMethod.Get, "/api/v3/downloadclient", null, cancellationToken);
            if (existing?.AsArray().Any(node => node?["implementation"]?.ToString()?.Contains("qBittorrent", StringComparison.OrdinalIgnoreCase) == true) == true)
            {
                items.Add(new AutomationItem($"{appName} to qBittorrent", true, "Download client already exists; no change made."));
                return;
            }
            if (!Uri.TryCreate(qbit.Url, UriKind.Absolute, out var uri)) throw new InvalidOperationException("qBittorrent URL is invalid.");
            var schemas = await SendArrAsync(arr, HttpMethod.Get, "/api/v3/downloadclient/schema", null, cancellationToken);
            var schema = schemas?.AsArray().Select(node => node?.DeepClone().AsObject()).FirstOrDefault(node => node?["implementation"]?.ToString()?.Contains("qBittorrent", StringComparison.OrdinalIgnoreCase) == true);
            if (schema == null) throw new InvalidOperationException($"{appName} did not return a qBittorrent download-client schema.");
            schema["name"] = "qBittorrent";
            schema["enable"] = true;
            SetField(schema, "host", uri.Host);
            SetField(schema, "port", uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port);
            SetField(schema, "useSsl", uri.Scheme == "https");
            SetField(schema, "urlBase", uri.AbsolutePath == "/" ? "" : uri.AbsolutePath.TrimEnd('/'));
            SetField(schema, "username", qbit.Username);
            SetField(schema, "password", qbit.Password);
            SetField(schema, appName == "Radarr" ? "movieCategory" : "tvCategory", category);
            await SendArrAsync(arr, HttpMethod.Post, "/api/v3/downloadclient", schema, cancellationToken);
            items.Add(new AutomationItem($"{appName} to qBittorrent", true, "Download client created."));
        }
        catch (Exception ex) { items.Add(new AutomationItem($"{appName} to qBittorrent", false, Clean(ex))); }
    }

    private static AutomationItem ValidatePath(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase)) return new AutomationItem(name, false, "Path is not configured.");
        try
        {
            if (!Directory.Exists(path)) return new AutomationItem(name, false, $"Path does not exist or is not visible: {path}");
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
            var probe = Path.Combine(path, $".starrboard-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "Starrboard access test");
            File.Delete(probe);
            return new AutomationItem(name, true, $"Read and write access confirmed: {path}");
        }
        catch (Exception ex) { return new AutomationItem(name, false, $"{path}: {Clean(ex)}"); }
    }

    private async Task<AutomationItem> DryRunLookupAsync(string name, ArrEndpointOptions endpoint, string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendArrAsync(endpoint, HttpMethod.Get, path, null, cancellationToken);
            var count = result?.AsArray().Count ?? 0;
            return new AutomationItem(name, count > 0, count > 0 ? "Lookup succeeded without adding media." : "Lookup returned no results.");
        }
        catch (Exception ex) { return new AutomationItem(name, false, Clean(ex)); }
    }

    private async Task<AutomationItem> HasProwlarrAppsAsync(ArrEndpointOptions endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var apps = await SendArrAsync(endpoint, HttpMethod.Get, "/api/v1/applications", null, cancellationToken);
            var names = apps?.AsArray()
                .Select(node => node?["implementation"]?.ToString() ?? "")
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ok = names.Contains("Radarr") && names.Contains("Sonarr");
            return new AutomationItem("Prowlarr application sync", ok, ok ? "Radarr and Sonarr application links exist." : "Radarr and/or Sonarr application links are missing.");
        }
        catch (Exception ex) { return new AutomationItem("Prowlarr application sync", false, Clean(ex)); }
    }

    private async Task<bool> ProbeArrAsync(string url, string apiKey, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + path);
            request.Headers.Add("X-Api-Key", apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<JsonNode?> SendArrAsync(ArrEndpointOptions endpoint, HttpMethod method, string path, JsonNode? payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Url) || !IsUsableSecret(endpoint.ApiKey)) throw new InvalidOperationException("URL or API key is not configured.");
        using var request = new HttpRequestMessage(method, endpoint.Url.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        if (payload != null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API returned HTTP {(int)response.StatusCode}: {Trim(body)}");
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private static void SetField(JsonObject schema, string name, JsonNode? value)
    {
        var field = schema["fields"]?.AsArray().Select(node => node?.AsObject()).FirstOrDefault(node => string.Equals(node?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
        if (field != null) field["value"] = value?.DeepClone();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        static string Normalize(string? value) => (value ?? "").Trim().TrimEnd('\\', '/').Replace('/', '\\');
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadApiKey(string serviceName, string primaryPath)
    {
        var candidates = new List<string>
        {
            primaryPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), serviceName, "config.xml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), serviceName, "config.xml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), serviceName, "config.xml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config", "systemprofile", "AppData", "Roaming", serviceName, "config.xml")
        };
        var commonRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), serviceName);
        try
        {
            if (Directory.Exists(commonRoot)) candidates.AddRange(Directory.EnumerateFiles(commonRoot, "config.xml", SearchOption.AllDirectories));
        }
        catch { }

        foreach (var path in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var root = XDocument.Load(path).Root;
                var key = root?.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("ApiKey", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                if (IsUsableSecret(key)) return key;
            }
            catch { }
        }
        return null;
    }

    private static bool IsUsableSecret(string? value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase) && value != "********";
    private static AutomationResult Success(string name, string message) => new(true, message, new[] { new AutomationItem(name, true, message) });
    private static AutomationResult Failure(string name, string message) => new(false, message, new[] { new AutomationItem(name, false, message) });
    private static string Clean(Exception ex) => (ex.GetBaseException().Message ?? ex.Message).Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}

public sealed record AutomationResult(bool Ok, string Message, IReadOnlyList<AutomationItem> Items);
public sealed record AutomationItem(string Name, bool Ok, string Message);
