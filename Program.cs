using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

ConfigStore.EnsureRuntimeConfig();
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(ConfigStore.RuntimeConfigPath, optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("ARRBOT_");

var startupOptions = builder.Configuration.GetSection("Bot").Get<BotOptions>() ?? new BotOptions();
builder.WebHost.UseUrls(startupOptions.Admin.Enabled ? startupOptions.Admin.Url : "http://127.0.0.1:0");

builder.Host
    .UseWindowsService(options => options.ServiceName = "Starrboard")
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile(ConfigStore.RuntimeConfigPath, optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables("ARRBOT_");
    });

builder.Logging.AddProvider(new SimpleFileLoggerProvider(startupOptions.Logs.Path));
builder.Services.AddSingleton(startupOptions);
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<RequestRepository>();
builder.Services.AddSingleton<ArrClient>();
builder.Services.AddSingleton<StackHealthService>();
builder.Services.AddSingleton<OnboardingService>();
builder.Services.AddSingleton<OnboardingAutomationService>();
builder.Services.AddSingleton<BootstrapService>();
builder.Services.AddHostedService<StarrboardService>();

var app = builder.Build();
if (startupOptions.Admin.Enabled)
{
    AdminEndpoints.Map(app);
}

await app.RunAsync();

public sealed class BotOptions
{
    public string DiscordToken { get; set; } = "";
    public ulong GuildId { get; set; }
    public string DatabasePath { get; set; } = StarrboardPaths.DatabaseFile;
    public int CompletionCheckSeconds { get; set; } = 300;
    public int SessionTtlMinutes { get; set; } = 60;
    public int DmMaxAttempts { get; set; } = 25;
    public AdminOptions Admin { get; set; } = new();
    public ArrEndpointOptions Radarr { get; set; } = new();
    public ArrEndpointOptions Sonarr { get; set; } = new();
    public ArrEndpointOptions Prowlarr { get; set; } = new() { Url = "http://127.0.0.1:9696" };
    public ExternalEndpointOptions FlareSolverr { get; set; } = new() { Url = "http://127.0.0.1:8191" };
    public DownloadClientOptions Qbittorrent { get; set; } = new();
    public ArrServiceAccountOptions ArrServices { get; set; } = new();
    public LogOptions Logs { get; set; } = new();
    public bool OnboardingCompleted { get; set; }
}

public sealed class AdminOptions
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://127.0.0.1:8686";
}

public sealed class ArrEndpointOptions
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int QualityProfileId { get; set; }
    public string RootFolderPath { get; set; } = "";
    public string MinimumAvailability { get; set; } = "announced";
    public string SeriesType { get; set; } = "standard";
    public bool SearchOnAdd { get; set; } = true;
}

public sealed class ExternalEndpointOptions
{
    public string Url { get; set; } = "";
}

public sealed class DownloadClientOptions
{
    public string Url { get; set; } = "http://127.0.0.1:18080";
    public string ProcessName { get; set; } = "qbittorrent";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public sealed class ArrServiceAccountOptions
{
    public bool ApplyToRadarr { get; set; } = true;
    public bool ApplyToSonarr { get; set; } = true;
    public bool ApplyToProwlarr { get; set; } = true;
    public bool ApplyToStarrboard { get; set; } = false;
}

public sealed class LogOptions
{
    public string Path { get; set; } = StarrboardPaths.LogFile;
}

public static class StarrboardPaths
{
    public static readonly string Root = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Starrboard");
    public static readonly string Data = System.IO.Path.Combine(Root, "data");
    public static readonly string Logs = System.IO.Path.Combine(Root, "logs");
    public static readonly string RuntimeConfig = System.IO.Path.Combine(Data, "appsettings.runtime.json");
    public static readonly string DatabaseFile = System.IO.Path.Combine(Data, "requests.db");
    public static readonly string LogFile = System.IO.Path.Combine(Logs, "Starrboard.log");
}

public sealed record PendingRequest(
    long Id,
    ulong UserId,
    string MediaType,
    string Title,
    int ExternalId,
    int ArrId,
    IReadOnlyList<int> SeasonNumbers,
    IReadOnlyList<int>? CompletionEpisodeIds,
    IReadOnlyList<int> SearchCommandIds,
    int DmAttempts);

public sealed record ArrAddResult(
    string Title,
    int ExternalId,
    int ArrId,
    bool AlreadyAvailable,
    IReadOnlyList<int> SearchCommandIds);

public sealed record SeasonRequestStatus(int SeasonNumber, string State);

public sealed record ExistingMediaStatus(
    string Title,
    bool IsAvailable);

public sealed record RequestSession(
    string Id,
    ulong UserId,
    string MediaType,
    JsonArray Results,
    DateTimeOffset CreatedAt);

public sealed class StarrboardService : BackgroundService
{
    private BotOptions _options;
    private readonly ConfigStore _config;
    private readonly RequestRepository _repository;
    private readonly ArrClient _arr;
    private readonly ILogger<StarrboardService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DiscordSocketClient _client;
    private bool _commandsRegistered;
    private bool _discordReady;

    public StarrboardService(
        BotOptions options,
        ConfigStore config,
        RequestRepository repository,
        ArrClient arr,
        ILogger<StarrboardService> logger,
        IHostApplicationLifetime lifetime)
    {
        _options = options;
        _config = config;
        _repository = repository;
        _arr = arr;
        _logger = logger;
        _lifetime = lifetime;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.DirectMessages,
            AlwaysDownloadUsers = false,
            LogLevel = LogSeverity.Info
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Windows Service Control Manager observe a prompt service start
        // before SQLite/Discord initialization encounters boot-time network delay.
        await Task.Yield();
        await _repository.InitializeAsync(stoppingToken);
        await WaitForConfiguredDiscordTokenAsync(stoppingToken);

        _client.Log += message =>
        {
            var level = message.Severity switch
            {
                LogSeverity.Critical => LogLevel.Critical,
                LogSeverity.Error => LogLevel.Error,
                LogSeverity.Warning => LogLevel.Warning,
                LogSeverity.Info => LogLevel.Information,
                LogSeverity.Verbose => LogLevel.Debug,
                _ => LogLevel.Trace
            };

            _logger.Log(level, message.Exception, "[Discord] {Message}", message.Message);
            return Task.CompletedTask;
        };

        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.SelectMenuExecuted += OnSelectMenuAsync;
        _client.ButtonExecuted += OnButtonAsync;

        await _client.LoginAsync(TokenType.Bot, _options.DiscordToken);
        await _client.StartAsync();

        _ = Task.Run(() => CompletionLoopAsync(stoppingToken), stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForConfiguredDiscordTokenAsync(CancellationToken stoppingToken)
    {
        var loggedWaiting = false;
        while (true)
        {
            _options = _config.GetBotOptions();
            if (!string.IsNullOrWhiteSpace(_options.DiscordToken) &&
                !_options.DiscordToken.Contains("PUT_", StringComparison.OrdinalIgnoreCase))
            {
                if (loggedWaiting)
                {
                    _logger.LogInformation("Discord token was configured. Starting Discord request worker.");
                }

                return;
            }

            if (!loggedWaiting)
            {
                _logger.LogWarning("Discord token is not configured. Admin console remains online for onboarding; Discord request worker is waiting for runtime config.");
                loggedWaiting = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task OnReadyAsync()
    {
        if (_commandsRegistered)
        {
            return;
        }

        var requestCommand = new SlashCommandBuilder()
            .WithName("request")
            .WithDescription("Privately request a movie or TV show")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("movie")
                .WithDescription("Request a movie")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("title", ApplicationCommandOptionType.String, "Movie title", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("tv")
                .WithDescription("Request a TV show")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("title", ApplicationCommandOptionType.String, "TV show title", isRequired: true));

        var commandProperties = new[] { requestCommand.Build() };
        var emptyCommandSet = Array.Empty<ApplicationCommandProperties>();
        var usedGlobalFallback = false;
        if (_options.GuildId != 0)
        {
            var guild = _client.GetGuild(_options.GuildId);
            if (guild == null)
            {
                _logger.LogWarning("Configured GuildId {GuildId} was not found. Overwriting commands for all connected guilds instead.", _options.GuildId);
                usedGlobalFallback = await OverwriteConnectedGuildCommandsAsync(commandProperties);
            }
            else
            {
                await guild.BulkOverwriteApplicationCommandAsync(commandProperties);
                _logger.LogInformation("Overwrote application commands for guild {GuildId}.", _options.GuildId);
            }
        }
        else
        {
            usedGlobalFallback = await OverwriteConnectedGuildCommandsAsync(commandProperties);
        }

        if (!usedGlobalFallback)
        {
            await ((IDiscordClient)_client).BulkOverwriteGlobalApplicationCommand(emptyCommandSet);
            _logger.LogInformation("Cleared global application command set to avoid duplicate guild/global request commands.");
        }

        _commandsRegistered = true;
        _discordReady = true;
    }

    private async Task<bool> OverwriteConnectedGuildCommandsAsync(ApplicationCommandProperties[] commandProperties)
    {
        var guilds = _client.Guilds.ToArray();
        if (guilds.Length == 0)
        {
            _logger.LogWarning("No connected guilds were available. Overwriting global command set as fallback.");
            await ((IDiscordClient)_client).BulkOverwriteGlobalApplicationCommand(commandProperties);
            return true;
        }

        foreach (var guild in guilds)
        {
            await guild.BulkOverwriteApplicationCommandAsync(commandProperties);
            _logger.LogInformation("Overwrote application commands for guild {GuildName} ({GuildId}).", guild.Name, guild.Id);
        }

        return false;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!string.Equals(command.Data.Name, "request", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await command.DeferAsync(ephemeral: true);

        try
        {
            var subcommand = command.Data.Options.FirstOrDefault();
            var title = subcommand?.Options.FirstOrDefault(o => o.Name == "title")?.Value?.ToString()?.Trim();
            if (subcommand == null || string.IsNullOrWhiteSpace(title))
            {
                await command.ModifyOriginalResponseAsync(m => m.Content = "Missing title.");
                return;
            }

            var mediaType = subcommand.Name.Equals("movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "tv";
            var results = mediaType == "movie"
                ? await _arr.LookupMoviesAsync(title, CancellationToken.None)
                : await _arr.LookupSeriesAsync(title, CancellationToken.None);

            if (results.Count == 0)
            {
                await command.ModifyOriginalResponseAsync(m => m.Content = $"No {mediaType} results found for `{title}`.");
                return;
            }

            var limited = new JsonArray(results.Take(10).Select(CloneNode).ToArray());
            var sessionId = Guid.NewGuid().ToString("N");
            await _repository.SaveSessionAsync(new RequestSession(sessionId, command.User.Id, mediaType, limited, DateTimeOffset.UtcNow), CancellationToken.None);

            if (limited.Count == 1)
            {
                await ShowDetailsAsync(command, sessionId, MakeResultKey(mediaType, limited[0]!.AsObject()), limited[0]!.AsObject(), mediaType);
                return;
            }

            await command.ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"Select the {DisplayMediaType(mediaType)} you want to request.";
                m.Embed = Optional<Embed>.Unspecified;
                m.Components = BuildSelectMenu(sessionId, mediaType, limited);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request slash command failed.");
            await SafeModifyAsync(command, "Request failed before it could be submitted. Check bot logs.");
        }
    }

    private async Task OnSelectMenuAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith("select|", StringComparison.Ordinal) &&
            !component.Data.CustomId.StartsWith("seasons|", StringComparison.Ordinal))
        {
            return;
        }

        await component.DeferAsync(ephemeral: true);
        RequestSession? activeSession = null;
        JsonObject? requestedResult = null;

        try
        {
            var parts = component.Data.CustomId.Split('|');
            var action = parts[0];
            var sessionId = parts[1];
            var session = await _repository.GetSessionAsync(sessionId, CancellationToken.None);
            activeSession = session;

            if (session == null || session.UserId != component.User.Id)
            {
                await component.ModifyOriginalResponseAsync(m => m.Content = "This private request session is no longer valid.");
                return;
            }

            var key = action == "select" ? component.Data.Values.First() : parts[2];
            var result = FindResult(session, key);
            if (result == null)
            {
                await component.ModifyOriginalResponseAsync(m => m.Content = "That result is no longer available.");
                return;
            }
            requestedResult = result;

            if (action == "seasons")
            {
                var selectableSeasons = GetSelectableSeasons(result);
                var selectedSeasons = component.Data.Values.Contains("all", StringComparer.OrdinalIgnoreCase)
                    ? selectableSeasons
                    : component.Data.Values
                        .Select(value => int.TryParse(value, out var seasonNumber) ? (int?)seasonNumber : null)
                        .Where(seasonNumber => seasonNumber is >= 0)
                        .Select(seasonNumber => seasonNumber!.Value)
                        .Distinct()
                        .OrderBy(seasonNumber => seasonNumber)
                        .ToArray();

                if (selectedSeasons.Count == 0)
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "No seasons were selected.");
                    return;
                }

                ClearSelectedSeasons(session);
                SaveSelectedSeasons(result, selectedSeasons);
                await _repository.SaveSessionAsync(session, CancellationToken.None);
                _logger.LogInformation("Prepared season confirmation for {Title}; seasons: {Seasons}.", GetString(result, "title"), FormatSeasonSelection(selectedSeasons));
                await ShowSeasonConfirmAsync(component, session, key, result, selectedSeasons);
                return;
            }

            await ShowDetailsAsync(component, session.Id, key, result, session.MediaType);
        }
        catch (ArrApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning(ex, "Arr rejected request.");
            var message = await BuildArrRejectionMessageAsync(ex, activeSession?.MediaType, requestedResult, CancellationToken.None);
            await SafeModifyAsync(component, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Select menu handling failed.");
            await SafeModifyAsync(component, "Selection failed. Check bot logs.");
        }
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith("request|", StringComparison.Ordinal) &&
            !component.Data.CustomId.StartsWith("requestseasons|", StringComparison.Ordinal) &&
            !component.Data.CustomId.StartsWith("rs|", StringComparison.Ordinal) &&
            !component.Data.CustomId.StartsWith("seasonpage|", StringComparison.Ordinal) &&
            !component.Data.CustomId.StartsWith("cancel|", StringComparison.Ordinal))
        {
            return;
        }

        await component.DeferAsync(ephemeral: true);
        RequestSession? activeSession = null;
        JsonObject? requestedResult = null;

        try
        {
            var parts = component.Data.CustomId.Split('|');
            var action = parts[0];
            var sessionId = parts[1];
            var session = await _repository.GetSessionAsync(sessionId, CancellationToken.None);
            activeSession = session;

            if (session == null || session.UserId != component.User.Id)
            {
                await component.ModifyOriginalResponseAsync(m => m.Content = "This private request session is no longer valid.");
                return;
            }

            if (action == "cancel")
            {
                await component.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = "Request cancelled.";
                    m.Components = new ComponentBuilder().Build();
                    m.Embed = Optional<Embed>.Unspecified;
                });
                return;
            }

            if (action == "seasonpage")
            {
                if (parts.Length < 4 || !int.TryParse(parts[3], out var page))
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "That season page is no longer valid.");
                    return;
                }

                var pageResult = FindResult(session, parts[2]);
                if (pageResult == null)
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "That result is no longer available.");
                    return;
                }

                var embed = BuildMediaEmbed(pageResult, session.MediaType, Color.DarkGreen, "Private TV show request");
                await component.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = "Choose which seasons to request.";
                    m.Embed = embed.Build();
                    m.Components = BuildSeasonSelectMenu(session.Id, parts[2], pageResult, page);
                });
                return;
            }

            if (action == "rs")
            {
                if (parts.Length < 3)
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "This request button is stale. Run `/request` again.");
                    return;
                }

                var selectedResult = FindResult(session, parts[2]);
                if (selectedResult == null)
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "No selected season was found for this request.");
                    return;
                }

                requestedResult = selectedResult;
                var selectedSeasons = GetSelectedSeasons(selectedResult);
                if (selectedSeasons.Count == 0)
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "No seasons were selected.");
                    return;
                }

                _logger.LogInformation("Confirmed season request for {Title}; seasons: {Seasons}.", GetString(selectedResult, "title"), FormatSeasonSelection(selectedSeasons));
                await AddRequestedMediaAsync(component, session, selectedResult, selectedSeasons, CancellationToken.None);
                return;
            }

            var key = parts[2];
            var result = FindResult(session, key);
            if (result == null)
            {
                await component.ModifyOriginalResponseAsync(m => m.Content = "That result is no longer available.");
                return;
            }
            requestedResult = result;

            if (action == "requestseasons")
            {
                var selectedSeasons = GetSelectedSeasons(result);

                if (selectedSeasons.Count == 0)
                {
                    await component.ModifyOriginalResponseAsync(m => m.Content = "No seasons were selected.");
                    return;
                }

                _logger.LogInformation("Confirmed season request for {Title}; seasons: {Seasons}.", GetString(result, "title"), FormatSeasonSelection(selectedSeasons));
                await AddRequestedMediaAsync(component, session, result, selectedSeasons, CancellationToken.None);
                return;
            }

            if (session.MediaType == "tv")
            {
                var seasons = GetSelectableSeasons(result);
                if (seasons.Count > 1)
                {
                    var embed = BuildMediaEmbed(result, session.MediaType, Color.DarkGreen, "Private TV show request");
                    await component.ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = "Choose which seasons to request.";
                        m.Embed = embed.Build();
                        m.Components = BuildSeasonSelectMenu(session.Id, key, result, 0);
                    });
                    return;
                }
            }

            var directRequestSeasons = session.MediaType == "tv" ? GetSelectableSeasons(result) : Array.Empty<int>();
            await AddRequestedMediaAsync(component, session, result, directRequestSeasons, CancellationToken.None);
        }
        catch (ArrApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning(ex, "Arr rejected request.");
            var message = await BuildArrRejectionMessageAsync(ex, activeSession?.MediaType, requestedResult, CancellationToken.None);
            await SafeModifyAsync(component, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request button handling failed.");
            await SafeModifyAsync(component, "Request failed. Check bot logs.");
        }
    }

    private async Task AddRequestedMediaAsync(
        SocketMessageComponent component,
        RequestSession session,
        JsonObject result,
        IReadOnlyCollection<int> selectedSeasons,
        CancellationToken cancellationToken)
    {
        var consumed = await _repository.TryConsumeSessionAsync(
            session.Id,
            TimeSpan.FromMinutes(Math.Max(5, _options.SessionTtlMinutes)),
            cancellationToken);
        if (!consumed)
        {
            await SafeModifyAsync(component, "This request was already submitted or the request session expired. Run `/request` again if you still need it.");
            return;
        }

        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = "Submitting request to Radarr/Sonarr...";
            m.Components = new ComponentBuilder().Build();
            m.Embed = Optional<Embed>.Unspecified;
        });

        var addResult = session.MediaType == "movie"
            ? await _arr.AddMovieAsync(result, cancellationToken)
            : await _arr.AddSeriesAsync(result, selectedSeasons, cancellationToken);

        IReadOnlyList<SeasonRequestStatus> seasonStatuses = Array.Empty<SeasonRequestStatus>();
        if (session.MediaType == "tv" && selectedSeasons.Count > 0)
        {
            try
            {
                // Give Arr a moment to create queue records for releases selected immediately.
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                seasonStatuses = await _arr.GetSeasonRequestStatusesAsync(addResult.ArrId, selectedSeasons, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to build season request status for Sonarr series id {SeriesId}.", addResult.ArrId);
            }
        }

        var seasonStatusText = BuildSeasonRequestStatusText(seasonStatuses);

        if (addResult.AlreadyAvailable)
        {
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"**Already in Plex**\n**{addResult.Title}** is already imported and should be watchable now.{seasonStatusText}";
                m.Components = new ComponentBuilder().Build();
                m.Embed = Optional<Embed>.Unspecified;
            });
            return;
        }

        IReadOnlyList<int>? completionEpisodeIds = null;
        if (session.MediaType == "tv" && selectedSeasons.Count == 0)
        {
            completionEpisodeIds = await _arr.GetAiredRegularEpisodeIdsAsync(addResult.ArrId, cancellationToken);
        }

        var saved = await _repository.SaveRequestAsync(
            component.User.Id,
            session.MediaType,
            addResult.Title,
            addResult.ExternalId,
            addResult.ArrId,
            selectedSeasons,
            completionEpisodeIds,
            addResult.SearchCommandIds,
            cancellationToken);

        var seasonText = session.MediaType == "tv" && selectedSeasons.Count > 0
            ? $" for {FormatSeasonSelection(selectedSeasons)}"
            : "";
        var duplicateText = saved ? "" : " You already had an active notification for this same request, so I did not add a duplicate notification.";
        var completionText = session.MediaType == "tv" && selectedSeasons.Count == 0
            ? "I will DM you when the currently aired episodes are imported."
            : "I will DM you when it is imported.";

        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"Requested **{addResult.Title}**{seasonText}. {completionText}{duplicateText}{seasonStatusText}";
            m.Components = new ComponentBuilder().Build();
            m.Embed = Optional<Embed>.Unspecified;
        });
    }

    private static string BuildSeasonRequestStatusText(IReadOnlyList<SeasonRequestStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return string.Empty;
        }

        var sections = new List<string>();
        AddSeasonStatusSection(sections, "Already in Plex", statuses, "available");
        AddSeasonStatusSection(sections, "Downloading", statuses, "downloading");
        AddSeasonStatusSection(sections, "Searching for a release", statuses, "searching");
        return sections.Count == 0 ? string.Empty : $"\n\n{string.Join("\n", sections)}";
    }

    private static void AddSeasonStatusSection(List<string> sections, string heading, IReadOnlyList<SeasonRequestStatus> statuses, string state)
    {
        var seasons = statuses
            .Where(status => status.State == state)
            .Select(status => FormatSeasonName(status.SeasonNumber))
            .ToArray();
        if (seasons.Length > 0)
        {
            sections.Add($"**{heading}:** {string.Join(", ", seasons)}");
        }
    }

    private static async Task ShowSeasonConfirmAsync(
        SocketMessageComponent component,
        RequestSession session,
        string key,
        JsonObject result,
        IReadOnlyCollection<int> selectedSeasons)
    {
        var embed = BuildMediaEmbed(result, session.MediaType, Color.DarkGreen, "Private TV show request");
        embed.AddField("Selected Seasons", FormatSeasonSelection(selectedSeasons), false);

        var components = new ComponentBuilder()
            .WithButton("Request Selected Season", $"rs|{session.Id}|{key}", ButtonStyle.Success)
            .WithButton("Cancel", $"cancel|{session.Id}", ButtonStyle.Secondary)
            .Build();

        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = "Confirm this TV show request.";
            m.Embed = embed.Build();
            m.Components = components;
        });
    }

    private async Task ShowDetailsAsync(SocketInteraction interaction, string sessionId, string key, JsonObject result, string mediaType)
    {
        var embed = BuildMediaEmbed(
            result,
            mediaType,
            mediaType == "movie" ? Color.DarkBlue : Color.DarkGreen,
            $"Private {DisplayMediaType(mediaType)} request");

        var components = new ComponentBuilder()
            .WithButton("Request", $"request|{sessionId}|{key}", ButtonStyle.Success)
            .WithButton("Cancel", $"cancel|{sessionId}", ButtonStyle.Secondary)
            .Build();

        await interaction.ModifyOriginalResponseAsync(m =>
        {
            m.Content = "Confirm this request.";
            m.Embed = embed.Build();
            m.Components = components;
        });
    }

    private async Task<string> BuildArrRejectionMessageAsync(ArrApiException ex, string? mediaType, JsonObject? result, CancellationToken cancellationToken)
    {
        var title = result == null ? null : GetString(result, "title");
        var titleText = string.IsNullOrWhiteSpace(title) ? "This title" : $"**{title}**";
        var reasons = ExtractArrValidationMessages(ex.Body);
        var alreadyExists = IsAlreadyExistsRejection(ex.Body, reasons);

        if (alreadyExists)
        {
            ExistingMediaStatus? existing = null;
            if (!string.IsNullOrWhiteSpace(mediaType) && result != null)
            {
                try
                {
                    existing = await _arr.GetExistingMediaStatusAsync(mediaType, result, cancellationToken);
                }
                catch (Exception lookupEx)
                {
                    _logger.LogWarning(lookupEx, "Could not look up existing media status after duplicate rejection.");
                }
            }

            if (existing?.IsAvailable == true)
            {
                var existingTitle = string.IsNullOrWhiteSpace(existing.Title) ? titleText : $"**{existing.Title}**";
                return $"**Already in Plex**\n{existingTitle} is already in the library and should be watchable now. I did not create another request.";
            }

            if (existing != null)
            {
                return $"**Already being tracked**\n{titleText} is already in Radarr/Sonarr, but I could not confirm that it is imported yet. I did not create a duplicate request.";
            }

            return $"**Already added**\n{titleText} has already been added to Radarr/Sonarr. Check Plex first; if it is not visible yet, it is already being tracked.";
        }

        if (reasons.Count > 0)
        {
            return $"**Request not added**\nRadarr/Sonarr rejected this request: {string.Join("; ", reasons)}";
        }

        return "**Request not added**\nRadarr/Sonarr rejected this request. Check bot logs if this keeps happening.";
    }

    private static IReadOnlyList<string> ExtractArrValidationMessages(string body)
    {
        var messages = new List<string>();

        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray array)
            {
                foreach (var item in array.OfType<JsonObject>())
                {
                    var message = GetString(item, "errorMessage") ?? GetString(item, "errorCode");
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        messages.Add(message);
                    }
                }
            }
            else if (node is JsonObject obj)
            {
                var message = GetString(obj, "errorMessage") ?? GetString(obj, "message") ?? GetString(obj, "errorCode");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    messages.Add(message);
                }
            }
        }
        catch
        {
        }

        return messages;
    }

    private static bool IsAlreadyExistsRejection(string body, IReadOnlyList<string> reasons)
    {
        if (body.Contains("ExistsValidator", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return reasons.Any(reason =>
            reason.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("already exists", StringComparison.OrdinalIgnoreCase));
    }

    private async Task CompletionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_discordReady || _client.ConnectionState != ConnectionState.Connected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }

                var pending = await _repository.GetPendingRequestsAsync(Math.Max(1, _options.DmMaxAttempts), stoppingToken);
                foreach (var request in pending)
                {
                    try
                    {
                        var (media, imported) = await GetCompletionStateAsync(request, stoppingToken);

                        if (!imported)
                        {
                            continue;
                        }

                        var user = await _client.Rest.GetUserAsync(request.UserId);
                        if (user == null)
                        {
                            throw new InvalidOperationException($"Discord user {request.UserId} was not found.");
                        }

                        var embed = BuildMediaEmbed(
                            media,
                            request.MediaType,
                            Color.Gold,
                            "Available in Plex",
                            "Now available");

                        await user.SendMessageAsync(
                            $"Your requested {DisplayMediaType(request.MediaType)} is now available in Plex.",
                            embed: embed.Build());

                        await _repository.MarkDmSentAsync(request.Id, stoppingToken);
                        _logger.LogInformation("Sent completion DM for {MediaType} {Title} to {UserId}.", request.MediaType, request.Title, request.UserId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Completion check failed for request {RequestId} ({MediaType} {Title}).", request.Id, request.MediaType, request.Title);
                        await _repository.MarkDmFailedAsync(request.Id, ex.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Completion watcher failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _options.CompletionCheckSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<(JsonObject Media, bool Imported)> GetCompletionStateAsync(
        PendingRequest request, CancellationToken stoppingToken)
    {
        var media = request.MediaType == "movie"
            ? await _arr.GetMovieAsync(request.ArrId, stoppingToken)
            : await _arr.GetSeriesAsync(request.ArrId, stoppingToken);

        bool imported = request.MediaType == "movie"
            ? MediaHasMovieFile(media)
            : request.CompletionEpisodeIds != null
                ? await _arr.AreEpisodesImportedAsync(request.ArrId, request.CompletionEpisodeIds, stoppingToken)
                : request.SeasonNumbers.Count > 0
                    ? MediaHasCompletedRequestedSeasons(media, request.SeasonNumbers)
                    : MediaHasImportedEpisode(media);

        // Historical search bookkeeping must not prevent an imported item completing.
        if (!imported)
        {
            await UpdateSearchCommandStatusAsync(request, stoppingToken);
        }

        return (media, imported);
    }

    private async Task UpdateSearchCommandStatusAsync(PendingRequest request, CancellationToken cancellationToken)
    {
        if (request.SearchCommandIds.Count == 0)
        {
            return;
        }

        var statuses = new List<string>();
        var allComplete = true;
        foreach (var commandId in request.SearchCommandIds)
        {
            JsonObject? command;
            try
            {
                command = await _arr.GetCommandAsync(request.MediaType, commandId, cancellationToken);
            }
            catch (ArrApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Radarr/Sonarr expire historical commands; this is not a notification failure.
                statuses.Add($"{commandId}:expired");
                _logger.LogDebug("Historical search command {CommandId} expired for request {RequestId}.", commandId, request.Id);
                continue;
            }
            var status = GetString(command, "status") ?? "unknown";
            var stateChangeTime = GetString(command, "stateChangeTime");
            statuses.Add($"{commandId}:{status}");
            if (!IsTerminalCommandStatus(status, stateChangeTime))
            {
                allComplete = false;
            }
        }

        await _repository.MarkSearchStatusAsync(request.Id, string.Join("; ", statuses), allComplete, cancellationToken);
    }

    private static bool IsTerminalCommandStatus(string status, string? stateChangeTime)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("aborted", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(stateChangeTime);
    }

    private static MessageComponent BuildSelectMenu(string sessionId, string mediaType, JsonArray results)
    {
        var options = results
            .Select(x => x!.AsObject())
            .Select(result =>
            {
                var title = GetString(result, "title");
                if (string.IsNullOrWhiteSpace(title)) title = "Unknown title";
                var year = GetYear(result);
                var label = Trim(year > 0 ? $"{title} ({year})" : title, 100);
                var overview = GetString(result, "overview");
                var description = Trim(
                    string.IsNullOrWhiteSpace(overview) ? $"Request this {DisplayMediaType(mediaType)}" : overview,
                    100);
                return new SelectMenuOptionBuilder(label, MakeResultKey(mediaType, result), description);
            })
            .ToList();

        return new ComponentBuilder()
            .WithSelectMenu($"select|{sessionId}", options, $"Choose a {DisplayMediaType(mediaType)}")
            .Build();
    }

    private static MessageComponent BuildSeasonSelectMenu(string sessionId, string key, JsonObject result, int page)
    {
        const int pageSize = 24;
        var allSeasons = GetSelectableSeasons(result).ToArray();
        var pageCount = Math.Max(1, (int)Math.Ceiling(allSeasons.Length / (double)pageSize));
        page = Math.Clamp(page, 0, pageCount - 1);
        var seasons = allSeasons.Skip(page * pageSize).Take(pageSize).ToArray();
        var options = new List<SelectMenuOptionBuilder>
        {
            new("All seasons", "all", "Request every listed season")
        };

        foreach (var seasonNumber in seasons)
        {
            options.Add(new SelectMenuOptionBuilder(
                FormatSeasonName(seasonNumber),
                seasonNumber.ToString(),
                GetSeasonOptionDescription(result, seasonNumber)));
        }

        var builder = new ComponentBuilder()
            .WithSelectMenu($"seasons|{sessionId}|{key}|{page}", options, pageCount > 1 ? $"Choose seasons ({page + 1}/{pageCount})" : "Choose seasons", minValues: 1, maxValues: options.Count);

        if (page > 0)
        {
            builder.WithButton("Previous", $"seasonpage|{sessionId}|{key}|{page - 1}", ButtonStyle.Secondary);
        }

        if (page < pageCount - 1)
        {
            builder.WithButton("Next", $"seasonpage|{sessionId}|{key}|{page + 1}", ButtonStyle.Secondary);
        }

        builder.WithButton("Cancel", $"cancel|{sessionId}", ButtonStyle.Secondary);
        return builder.Build();
    }

    private static IReadOnlyList<int> GetSelectableSeasons(JsonObject result)
    {
        var seasons = result["seasons"]?.AsArray();
        if (seasons == null)
        {
            return Array.Empty<int>();
        }

        return seasons
            .Select(season => season?.AsObject())
            .Where(season => season? ["seasonNumber"] != null)
            .Select(season => GetInt(season!, "seasonNumber"))
            .Where(seasonNumber => seasonNumber >= 0)
            .Distinct()
            .OrderBy(seasonNumber => seasonNumber)
            .ToArray();
    }

    private static string GetSeasonOptionDescription(JsonObject result, int seasonNumber)
    {
        var season = result["seasons"]?.AsArray()
            ?.Select(item => item?.AsObject())
            .FirstOrDefault(item => item != null && GetInt(item, "seasonNumber") == seasonNumber);

        var stats = season?["statistics"]?.AsObject();
        var episodeCount = stats == null ? 0 : GetInt(stats, "episodeCount");
        return episodeCount > 0 ? $"{episodeCount} episodes" : $"Request {FormatSeasonName(seasonNumber)}";
    }

    private static string FormatSeasonSelection(IReadOnlyCollection<int> selectedSeasons)
    {
        var seasons = selectedSeasons.OrderBy(seasonNumber => seasonNumber).ToArray();
        return seasons.Length == 1
            ? FormatSeasonName(seasons[0])
            : string.Join(", ", seasons.Select(FormatSeasonName));
    }

    private static string FormatSeasonName(int seasonNumber) => seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}";

    private static void SaveSelectedSeasons(JsonObject result, IReadOnlyCollection<int> selectedSeasons)
    {
        var seasons = new JsonArray();
        foreach (var seasonNumber in selectedSeasons.OrderBy(seasonNumber => seasonNumber))
        {
            seasons.Add(seasonNumber);
        }

        result["_selectedSeasons"] = seasons;
    }

    private static void ClearSelectedSeasons(RequestSession session)
    {
        foreach (var result in session.Results.Select(item => item?.AsObject()).Where(item => item != null))
        {
            result!.Remove("_selectedSeasons");
        }
    }

    private static IReadOnlyList<int> GetSelectedSeasons(JsonObject result)
    {
        var seasons = result["_selectedSeasons"]?.AsArray();
        if (seasons == null)
        {
            return Array.Empty<int>();
        }

        return seasons
            .Select(value =>
            {
                try { return value?.GetValue<int>(); }
                catch { return null; }
            })
            .Where(seasonNumber => seasonNumber is >= 0)
            .Select(seasonNumber => seasonNumber!.Value)
            .Distinct()
            .OrderBy(seasonNumber => seasonNumber)
            .ToArray();
    }

    private static JsonObject? FindSelectedSeasonResult(RequestSession session)
    {
        return session.Results
            .Select(x => x!.AsObject())
            .FirstOrDefault(result => GetSelectedSeasons(result).Count > 0);
    }

    private static JsonObject? FindResult(RequestSession session, string key)
    {
        return session.Results
            .Select(x => x!.AsObject())
            .FirstOrDefault(result => MakeResultKey(session.MediaType, result) == key);
    }

    private static string MakeResultKey(string mediaType, JsonObject result)
    {
        var id = mediaType == "movie"
            ? GetInt(result, "tmdbId")
            : GetInt(result, "tvdbId");

        return $"{mediaType}:{id}";
    }

    private static JsonNode CloneNode(JsonNode? node)
    {
        return JsonNode.Parse(node?.ToJsonString() ?? "{}")!;
    }

    private static async Task SafeModifyAsync(SocketInteraction interaction, string content)
    {
        try
        {
            await interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = content;
                m.Components = new ComponentBuilder().Build();
                m.Embed = Optional<Embed>.Unspecified;
            });
        }
        catch
        {
        }
    }

    private static string DisplayMediaType(string mediaType) => mediaType == "movie" ? "movie" : "TV show";

    private static string? GetString(JsonObject obj, string name) => obj[name]?.GetValue<string>();

    private static int GetInt(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private static int GetYear(JsonObject obj)
    {
        var year = GetInt(obj, "year");
        if (year > 0)
        {
            return year;
        }

        var date = GetString(obj, "firstAired") ?? GetString(obj, "releaseDate");
        return !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date[..4], out year) ? year : 0;
    }

    private static string? GetBestImageUrl(JsonObject obj)
    {
        var images = obj["images"]?.AsArray();
        if (images == null)
        {
            return null;
        }

        var preferred = images
            .Select(x => x!.AsObject())
            .FirstOrDefault(x => string.Equals(GetString(x, "coverType"), "poster", StringComparison.OrdinalIgnoreCase))
            ?? images.Select(x => x!.AsObject()).FirstOrDefault();

        return preferred == null ? null : GetString(preferred, "remoteUrl") ?? GetString(preferred, "url");
    }

    private static EmbedBuilder BuildMediaEmbed(JsonObject result, string mediaType, Color color, string footer, string? titlePrefix = null)
    {
        var title = GetString(result, "title") ?? "Unknown title";
        var year = GetYear(result);
        var overview = GetString(result, "overview") ?? "No synopsis available.";
        var image = GetBestImageUrl(result);
        var displayTitle = year > 0 ? $"{title} ({year})" : title;

        if (!string.IsNullOrWhiteSpace(titlePrefix))
        {
            displayTitle = $"{titlePrefix}: {displayTitle}";
        }

        var embed = new EmbedBuilder()
            .WithTitle(displayTitle)
            .WithDescription(Trim(overview, 850))
            .WithColor(color)
            .WithFooter(footer);

        if (!string.IsNullOrWhiteSpace(image))
        {
            embed.WithImageUrl(image);
        }

        AddMediaFields(embed, result, mediaType);
        return embed;
    }

    private static void AddMediaFields(EmbedBuilder embed, JsonObject result, string mediaType)
    {
        if (mediaType == "movie")
        {
            AddFieldIfValue(embed, "Release", FormatDate(GetString(result, "releaseDate") ?? GetString(result, "inCinemas") ?? GetString(result, "digitalRelease")), true);
            AddFieldIfValue(embed, "Runtime", FormatRuntime(GetInt(result, "runtime")), true);
            AddFieldIfValue(embed, "Rating", GetRatingText(result), true);
            AddFieldIfValue(embed, "Certification", GetString(result, "certification"), true);
            AddFieldIfValue(embed, "Studio", GetString(result, "studio"), true);
            AddFieldIfValue(embed, "Status", ToTitleCase(GetString(result, "status")), true);
        }
        else
        {
            AddFieldIfValue(embed, "First Aired", FormatDate(GetString(result, "firstAired")), true);
            AddFieldIfValue(embed, "Network", GetString(result, "network"), true);
            AddFieldIfValue(embed, "Status", ToTitleCase(GetString(result, "status")), true);
            AddFieldIfValue(embed, "Runtime", FormatRuntime(GetInt(result, "runtime")), true);
            AddFieldIfValue(embed, "Seasons", GetSeasonText(result), true);
            AddFieldIfValue(embed, "Rating", GetRatingText(result), true);
            AddFieldIfValue(embed, "Certification", GetString(result, "certification"), true);
        }

        AddFieldIfValue(embed, "Genres", GetGenresText(result), false);
    }

    private static bool MediaHasMovieFile(JsonObject media)
    {
        try { return media["hasFile"]?.GetValue<bool>() == true; }
        catch { return false; }
    }

    private static bool MediaHasImportedEpisode(JsonObject media)
    {
        try
        {
            var stats = media["statistics"]?.AsObject();
            return (stats?["episodeFileCount"]?.GetValue<int>() ?? 0) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool MediaHasCompletedRequestedSeasons(JsonObject media, IReadOnlyList<int> seasonNumbers)
    {
        var requested = seasonNumbers.Where(seasonNumber => seasonNumber >= 0).ToHashSet();
        if (requested.Count == 0)
        {
            return MediaHasImportedEpisode(media);
        }

        try
        {
            var seasons = media["seasons"]?.AsArray();
            if (seasons == null)
            {
                return false;
            }

            var matched = seasons
                .Select(item => item?.AsObject())
                .Where(item => item != null && requested.Contains(GetInt(item!, "seasonNumber")))
                .ToArray();

            if (matched.Length != requested.Count)
            {
                return false;
            }

            return matched.All(item =>
                {
                    var stats = item!["statistics"]?.AsObject();
                    var episodeFileCount = stats == null ? 0 : GetInt(stats, "episodeFileCount");
                    var episodeCount = stats == null ? 0 : GetInt(stats, "episodeCount");
                    var totalEpisodeCount = stats == null ? 0 : GetInt(stats, "totalEpisodeCount");
                    var expectedCount = episodeCount > 0 ? episodeCount : totalEpisodeCount;
                    return expectedCount > 0
                        ? episodeFileCount >= expectedCount
                        : episodeFileCount > 0;
                });
        }
        catch
        {
            return false;
        }
    }

    private static void AddFieldIfValue(EmbedBuilder embed, string name, string? value, bool inline)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            embed.AddField(name, value, inline);
        }
    }

    private static string? FormatDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var date)
            ? date.ToString("MMM d, yyyy")
            : value;
    }

    private static string? FormatRuntime(int minutes)
    {
        if (minutes <= 0)
        {
            return null;
        }

        if (minutes < 60)
        {
            return $"{minutes} min";
        }

        var hours = minutes / 60;
        var remaining = minutes % 60;
        return remaining == 0 ? $"{hours} hr" : $"{hours} hr {remaining} min";
    }

    private static string? GetRatingText(JsonObject obj)
    {
        var ratings = obj["ratings"]?.AsObject();
        if (ratings == null)
        {
            return null;
        }

        var imdb = ratings["imdb"]?.AsObject();
        if (imdb != null)
        {
            var imdbValue = GetDouble(imdb, "value");
            if (imdbValue > 0)
            {
                return $"{imdbValue:0.0}/10 IMDb";
            }
        }

        var tmdb = ratings["tmdb"]?.AsObject();
        if (tmdb != null)
        {
            var tmdbValue = GetDouble(tmdb, "value");
            if (tmdbValue > 0)
            {
                return $"{tmdbValue:0.0}/10 TMDb";
            }
        }

        var value = GetDouble(ratings, "value");
        return value > 0 ? $"{value:0.0}/10" : null;
    }

    private static string? GetGenresText(JsonObject obj)
    {
        var genres = obj["genres"]?.AsArray();
        if (genres == null || genres.Count == 0)
        {
            return null;
        }

        var values = genres
            .Select(x => x?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(6);

        var text = string.Join(", ", values);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? GetSeasonText(JsonObject obj)
    {
        var seasons = obj["seasons"]?.AsArray();
        if (seasons == null)
        {
            return null;
        }

        var count = seasons
            .Select(x => x?.AsObject())
            .Count(x => x != null && GetInt(x, "seasonNumber") > 0);

        if (count <= 0)
        {
            return null;
        }

        return count == 1 ? "1 season" : $"{count} seasons";
    }

    private static string? ToTitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static double GetDouble(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<double>() ?? 0; }
        catch { return 0; }
    }

    private static string Trim(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
    }
}

public sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, SimpleFileLogger> _loggers = new();

    public SimpleFileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new SimpleFileLogger(_path, name));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

public sealed class SimpleFileLogger : ILogger
{
    private static readonly object SyncRoot = new();
    private const long MaxLogBytes = 10 * 1024 * 1024;
    private const int RetainedLogFiles = 5;
    private readonly string _path;
    private readonly string _categoryName;

    public SimpleFileLogger(string path, string categoryName)
    {
        _path = path;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:o} [{logLevel}] {_categoryName}: {formatter(state, exception)}";
        if (exception != null)
        {
            line += Environment.NewLine + exception;
        }

        lock (SyncRoot)
        {
            RotateIfNeeded();
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        for (var i = RetainedLogFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            var destination = $"{_path}.{i + 1}";
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
        }

        var firstArchive = $"{_path}.1";
        if (File.Exists(firstArchive))
        {
            File.Delete(firstArchive);
        }

        File.Move(_path, firstArchive);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

public sealed class ArrClient
{
    private BotOptions _options;
    private readonly ConfigStore _config;
    private readonly ILogger<ArrClient> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public ArrClient(BotOptions options, ConfigStore config, ILogger<ArrClient> logger)
    {
        _options = options;
        _config = config;
        _logger = logger;
    }

    public async Task<JsonArray> LookupMoviesAsync(string term, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var node = await SendAsync(_options.Radarr, HttpMethod.Get, $"/api/v3/movie/lookup?term={Uri.EscapeDataString(term)}", null, cancellationToken);
        return node?.AsArray() ?? new JsonArray();
    }

    public async Task<JsonArray> LookupSeriesAsync(string term, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var node = await SendAsync(_options.Sonarr, HttpMethod.Get, $"/api/v3/series/lookup?term={Uri.EscapeDataString(term)}", null, cancellationToken);
        return node?.AsArray() ?? new JsonArray();
    }

    public async Task<ArrAddResult> AddMovieAsync(JsonObject lookupResult, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var payload = CloneObject(lookupResult);
        payload["qualityProfileId"] = _options.Radarr.QualityProfileId;
        payload["rootFolderPath"] = _options.Radarr.RootFolderPath;
        payload["monitored"] = true;
        payload["minimumAvailability"] = _options.Radarr.MinimumAvailability;
        payload["addOptions"] = new JsonObject { ["searchForMovie"] = false };

        JsonNode? response;
        try
        {
            response = await SendAsync(_options.Radarr, HttpMethod.Post, "/api/v3/movie", payload, cancellationToken);
        }
        catch (ArrApiException ex) when (IsAlreadyExistsResponse(ex.Body))
        {
            var existing = await GetExistingMovieByTmdbIdAsync(GetInt(lookupResult, "tmdbId"), cancellationToken);
            if (existing == null)
            {
                throw;
            }

            var movieId = GetInt(existing, "id");
            var alreadyAvailable = existing["hasFile"]?.GetValue<bool>() == true;
            var commandIds = new List<int>();
            if (!alreadyAvailable)
            {
                existing["monitored"] = true;
                await SendAsync(_options.Radarr, HttpMethod.Put, $"/api/v3/movie/{movieId}", existing, cancellationToken);
                if (_options.Radarr.SearchOnAdd)
                {
                    commandIds.Add(await QueueMovieSearchAsync(movieId, cancellationToken));
                }
            }

            return new ArrAddResult(GetString(existing, "title") ?? "Movie", GetInt(existing, "tmdbId"), movieId, alreadyAvailable, commandIds);
        }

        var obj = response!.AsObject();
        var arrId = GetInt(obj, "id");
        var searchCommandIds = new List<int>();
        if (_options.Radarr.SearchOnAdd)
        {
            searchCommandIds.Add(await QueueMovieSearchAsync(arrId, cancellationToken));
        }

        return new ArrAddResult(GetString(obj, "title") ?? "Movie", GetInt(obj, "tmdbId"), arrId, false, searchCommandIds);
    }

    public async Task<ArrAddResult> AddSeriesAsync(JsonObject lookupResult, IReadOnlyCollection<int> selectedSeasons, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var selectedSeasonSet = selectedSeasons
            .Where(seasonNumber => seasonNumber >= 0)
            .Distinct()
            .ToHashSet();
        var hasSelectedSeasons = selectedSeasonSet.Count > 0;

        // Sonarr returns 201 for a re-added series in some cases. Resolve an existing
        // series before POSTing so a season request can never reset its monitoring.
        var existingSeries = await GetExistingSeriesByTvdbIdAsync(GetInt(lookupResult, "tvdbId"), cancellationToken);
        if (existingSeries != null)
        {
            return await AddOrSearchExistingSeriesAsync(existingSeries, selectedSeasonSet, hasSelectedSeasons, cancellationToken);
        }

        var payload = CloneObject(lookupResult);
        RemoveInternalFields(payload);
        payload["qualityProfileId"] = _options.Sonarr.QualityProfileId;
        payload["rootFolderPath"] = _options.Sonarr.RootFolderPath;
        payload["monitored"] = true;
        payload["seasonFolder"] = true;
        payload["seriesType"] = _options.Sonarr.SeriesType;

        if (hasSelectedSeasons)
        {
            SetMonitoredSeasons(payload, selectedSeasonSet);
        }

        payload["addOptions"] = new JsonObject
        {
            ["monitor"] = hasSelectedSeasons ? "none" : "all",
            ["searchForMissingEpisodes"] = false
        };

        JsonNode? response;
        try
        {
            response = await SendAsync(_options.Sonarr, HttpMethod.Post, "/api/v3/series", payload, cancellationToken);
        }
        catch (ArrApiException ex) when (IsAlreadyExistsResponse(ex.Body))
        {
            var existing = await GetExistingSeriesByTvdbIdAsync(GetInt(lookupResult, "tvdbId"), cancellationToken);
            if (existing == null)
            {
                throw;
            }

            return await AddOrSearchExistingSeriesAsync(existing, selectedSeasonSet, hasSelectedSeasons, cancellationToken);
        }

        var obj = response!.AsObject();
        var seriesId = GetInt(obj, "id");

        if (hasSelectedSeasons)
        {
            // Sonarr performs post-add work asynchronously and its addOptions.monitor
            // handling can otherwise overwrite an immediate follow-up update.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var freshSeries = await GetSeriesAsync(seriesId, cancellationToken);
            var commandIds = await MonitorAndSearchSelectedSeasonsAsync(freshSeries, selectedSeasonSet, preserveExistingSeasonMonitoring: false, cancellationToken, verifyAfterDelay: true);
            obj = freshSeries;
            return new ArrAddResult(GetString(obj, "title") ?? "TV show", GetInt(obj, "tvdbId"), seriesId, false, commandIds);
        }

        var searchCommandIds = _options.Sonarr.SearchOnAdd
            ? await MonitorAndSearchSeriesAsync(await GetSeriesAsync(seriesId, cancellationToken), cancellationToken)
            : Array.Empty<int>();

        return new ArrAddResult(GetString(obj, "title") ?? "TV show", GetInt(obj, "tvdbId"), seriesId, false, searchCommandIds);
    }

    private async Task<ArrAddResult> AddOrSearchExistingSeriesAsync(
        JsonObject existing,
        ISet<int> selectedSeasonSet,
        bool hasSelectedSeasons,
        CancellationToken cancellationToken)
    {
        var existingAvailable = hasSelectedSeasons
            ? SeriesHasCompletedRequestedSeasons(existing, selectedSeasonSet)
            : SeriesHasImportedEpisode(existing);
        var commandIds = new List<int>();
        if (!existingAvailable)
        {
            if (hasSelectedSeasons)
            {
                commandIds.AddRange(await MonitorAndSearchSelectedSeasonsAsync(existing, selectedSeasonSet, preserveExistingSeasonMonitoring: true, cancellationToken));
            }
            else
            {
                commandIds.AddRange(await MonitorAndSearchSeriesAsync(existing, cancellationToken));
            }
        }

        return new ArrAddResult(GetString(existing, "title") ?? "TV show", GetInt(existing, "tvdbId"), GetInt(existing, "id"), existingAvailable, commandIds);
    }

    public async Task<bool> MovieIsImportedAsync(int radarrId, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var movie = await GetMovieAsync(radarrId, cancellationToken);
        return movie["hasFile"]?.GetValue<bool>() == true;
    }

    public async Task<bool> SeriesHasImportedEpisodeAsync(int sonarrId, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var series = await GetSeriesAsync(sonarrId, cancellationToken);
        var stats = series["statistics"]?.AsObject();
        return (stats?["episodeFileCount"]?.GetValue<int>() ?? 0) > 0;
    }

    public async Task<JsonObject> GetMovieAsync(int radarrId, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var node = await SendAsync(_options.Radarr, HttpMethod.Get, $"/api/v3/movie/{radarrId}", null, cancellationToken);
        return node!.AsObject();
    }

    public async Task<JsonObject> GetSeriesAsync(int sonarrId, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var node = await SendAsync(_options.Sonarr, HttpMethod.Get, $"/api/v3/series/{sonarrId}", null, cancellationToken);
        return node!.AsObject();
    }

    public async Task<IReadOnlyList<int>> GetAiredRegularEpisodeIdsAsync(int sonarrId, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var now = DateTimeOffset.UtcNow;
        var episodes = await SendAsync(_options.Sonarr, HttpMethod.Get, $"/api/v3/episode?seriesId={sonarrId}", null, cancellationToken);

        return episodes?.AsArray()
            .Select(item => item?.AsObject())
            .Where(item => item != null &&
                GetInt(item!, "seasonNumber") > 0 &&
                item!["monitored"]?.GetValue<bool>() == true &&
                DateTimeOffset.TryParse(GetString(item, "airDateUtc"), out var airDate) &&
                airDate <= now)
            .Select(item => GetInt(item!, "id"))
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray() ?? Array.Empty<int>();
    }

    public async Task<bool> AreEpisodesImportedAsync(
        int sonarrId,
        IReadOnlyCollection<int> episodeIds,
        CancellationToken cancellationToken)
    {
        var requested = episodeIds.Where(id => id > 0).ToHashSet();
        if (requested.Count == 0)
        {
            return false;
        }

        RefreshOptions();
        var episodes = await SendAsync(_options.Sonarr, HttpMethod.Get, $"/api/v3/episode?seriesId={sonarrId}", null, cancellationToken);
        var matched = episodes?.AsArray()
            .Select(item => item?.AsObject())
            .Where(item => item != null && requested.Contains(GetInt(item!, "id")))
            .ToArray() ?? Array.Empty<JsonObject?>();

        return matched.Length == requested.Count && matched.All(item => item!["hasFile"]?.GetValue<bool>() == true);
    }

    public async Task<IReadOnlyList<SeasonRequestStatus>> GetSeasonRequestStatusesAsync(
        int sonarrId,
        IReadOnlyCollection<int> selectedSeasons,
        CancellationToken cancellationToken)
    {
        RefreshOptions();
        var requested = selectedSeasons
            .Where(seasonNumber => seasonNumber >= 0)
            .Distinct()
            .OrderBy(seasonNumber => seasonNumber)
            .ToArray();
        if (requested.Length == 0)
        {
            return Array.Empty<SeasonRequestStatus>();
        }

        var series = await GetSeriesAsync(sonarrId, cancellationToken);
        var queue = await SendAsync(_options.Sonarr, HttpMethod.Get, "/api/v3/queue/details?includeEpisode=true", null, cancellationToken);
        var queueItems = queue?.AsArray() ?? new JsonArray();
        var seasons = series["seasons"]?.AsArray();
        var results = new List<SeasonRequestStatus>();

        foreach (var seasonNumber in requested)
        {
            var season = seasons?
                .Select(item => item?.AsObject())
                .FirstOrDefault(item => item != null && GetInt(item, "seasonNumber") == seasonNumber);
            var isAvailable = season != null && SeasonIsComplete(season);
            var isDownloading = queueItems
                .Select(item => item?.AsObject())
                .Any(item => item != null &&
                    GetInt(item, "seriesId") == sonarrId &&
                    GetInt(item, "seasonNumber") == seasonNumber &&
                    !string.Equals(GetString(item, "trackedDownloadState"), "importing", StringComparison.OrdinalIgnoreCase));
            results.Add(new SeasonRequestStatus(
                seasonNumber,
                isAvailable ? "available" : isDownloading ? "downloading" : "searching"));
        }

        return results;
    }

    public async Task<JsonObject> GetCommandAsync(string mediaType, int commandId, CancellationToken cancellationToken)
    {
        RefreshOptions();
        var endpoint = mediaType == "movie" ? _options.Radarr : _options.Sonarr;
        var node = await SendAsync(endpoint, HttpMethod.Get, $"/api/v3/command/{commandId}", null, cancellationToken);
        return node!.AsObject();
    }

    public async Task<ExistingMediaStatus?> GetExistingMediaStatusAsync(string mediaType, JsonObject lookupResult, CancellationToken cancellationToken)
    {
        RefreshOptions();
        if (mediaType == "movie")
        {
            var tmdbId = GetInt(lookupResult, "tmdbId");
            if (tmdbId <= 0)
            {
                return null;
            }

            var movies = await SendAsync(_options.Radarr, HttpMethod.Get, "/api/v3/movie", null, cancellationToken);
            var match = movies?.AsArray()
                .Select(x => x!.AsObject())
                .FirstOrDefault(x => GetInt(x, "tmdbId") == tmdbId);

            if (match == null)
            {
                return null;
            }

            return new ExistingMediaStatus(GetString(match, "title") ?? "Movie", match["hasFile"]?.GetValue<bool>() == true);
        }

        var tvdbId = GetInt(lookupResult, "tvdbId");
        if (tvdbId <= 0)
        {
            return null;
        }

        var series = await SendAsync(_options.Sonarr, HttpMethod.Get, "/api/v3/series", null, cancellationToken);
        var seriesMatch = series?.AsArray()
            .Select(x => x!.AsObject())
            .FirstOrDefault(x => GetInt(x, "tvdbId") == tvdbId);

        if (seriesMatch == null)
        {
            return null;
        }

        var stats = seriesMatch["statistics"]?.AsObject();
        var importedEpisodes = stats?["episodeFileCount"]?.GetValue<int>() ?? 0;
        return new ExistingMediaStatus(GetString(seriesMatch, "title") ?? "TV show", importedEpisodes > 0);
    }

    private void RefreshOptions()
    {
        _options = _config.GetBotOptions();
    }

    private async Task<JsonObject?> GetExistingSeriesByTvdbIdAsync(int tvdbId, CancellationToken cancellationToken)
    {
        if (tvdbId <= 0)
        {
            return null;
        }

        var series = await SendAsync(_options.Sonarr, HttpMethod.Get, "/api/v3/series", null, cancellationToken);
        return series?.AsArray()
            .Select(x => x!.AsObject())
            .FirstOrDefault(x => GetInt(x, "tvdbId") == tvdbId);
    }

    private async Task<JsonObject?> GetExistingMovieByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdbId <= 0)
        {
            return null;
        }

        var movies = await SendAsync(_options.Radarr, HttpMethod.Get, "/api/v3/movie", null, cancellationToken);
        return movies?.AsArray()
            .Select(x => x!.AsObject())
            .FirstOrDefault(x => GetInt(x, "tmdbId") == tmdbId);
    }

    private async Task<IReadOnlyList<int>> MonitorAndSearchSelectedSeasonsAsync(
        JsonObject series,
        ISet<int> selectedSeasons,
        bool preserveExistingSeasonMonitoring,
        CancellationToken cancellationToken,
        bool verifyAfterDelay = false)
    {
        var seriesId = GetInt(series, "id");
        var standardSeasons = selectedSeasons.Where(seasonNumber => seasonNumber > 0).ToHashSet();
        var includesSpecials = selectedSeasons.Contains(0);
        IReadOnlyList<int> specialEpisodeIds = Array.Empty<int>();
        JsonObject? verified = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = attempt == 0 ? series : verified!;
            target["monitored"] = true;
            if (preserveExistingSeasonMonitoring)
            {
                MarkSelectedSeasonsMonitored(target, standardSeasons);
            }
            else
            {
                SetMonitoredSeasons(target, standardSeasons);
            }

            await SendAsync(_options.Sonarr, HttpMethod.Put, $"/api/v3/series/{seriesId}", target, cancellationToken);
            if (includesSpecials)
            {
                specialEpisodeIds = await MonitorSpecialEpisodesAsync(seriesId, cancellationToken);
            }

            if (verifyAfterDelay)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            verified = await GetSeriesAsync(seriesId, cancellationToken);
            if (verified["monitored"]?.GetValue<bool>() == true &&
                SelectedSeasonsAreMonitored(verified, standardSeasons) &&
                (!includesSpecials || await SpecialEpisodesAreMonitoredAsync(seriesId, cancellationToken)))
            {
                break;
            }
        }

        if (verified == null || verified["monitored"]?.GetValue<bool>() != true ||
            !SelectedSeasonsAreMonitored(verified, standardSeasons) ||
            (includesSpecials && !await SpecialEpisodesAreMonitoredAsync(seriesId, cancellationToken)))
        {
            throw new InvalidOperationException($"Sonarr did not keep selected seasons monitored for series id {seriesId}.");
        }

        if (!_options.Sonarr.SearchOnAdd)
        {
            return Array.Empty<int>();
        }

        var commandIds = new List<int>();
        foreach (var seasonNumber in standardSeasons.OrderBy(x => x))
        {
            commandIds.Add(await QueueSeasonSearchAsync(seriesId, seasonNumber, cancellationToken));
        }

        if (includesSpecials && specialEpisodeIds.Count > 0)
        {
            commandIds.Add(await QueueEpisodeSearchAsync(specialEpisodeIds, cancellationToken));
        }

        return commandIds;
    }

    private async Task<IReadOnlyList<int>> MonitorAndSearchSeriesAsync(JsonObject series, CancellationToken cancellationToken)
    {
        var seriesId = GetInt(series, "id");
        series["monitored"] = true;
        SetAllSeasonsMonitored(series);
        await SendAsync(_options.Sonarr, HttpMethod.Put, $"/api/v3/series/{seriesId}", series, cancellationToken);
        if (!_options.Sonarr.SearchOnAdd)
        {
            return Array.Empty<int>();
        }

        return new[] { await QueueSeriesSearchAsync(seriesId, cancellationToken) };
    }

    private async Task<int> QueueMovieSearchAsync(int movieId, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            _options.Radarr,
            HttpMethod.Post,
            "/api/v3/command",
            new JsonObject
            {
                ["name"] = "MoviesSearch",
                ["movieIds"] = new JsonArray(movieId)
            },
            cancellationToken);
        return response == null ? 0 : GetInt(response.AsObject(), "id");
    }

    private async Task<int> QueueSeriesSearchAsync(int seriesId, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            _options.Sonarr,
            HttpMethod.Post,
            "/api/v3/command",
            new JsonObject
            {
                ["name"] = "SeriesSearch",
                ["seriesId"] = seriesId
            },
            cancellationToken);
        return response == null ? 0 : GetInt(response.AsObject(), "id");
    }

    private async Task<int> QueueSeasonSearchAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            _options.Sonarr,
            HttpMethod.Post,
            "/api/v3/command",
            new JsonObject
            {
                ["name"] = "SeasonSearch",
                ["seriesId"] = seriesId,
                ["seasonNumber"] = seasonNumber
            },
            cancellationToken);
        return response == null ? 0 : GetInt(response.AsObject(), "id");
    }

    private async Task<int> QueueEpisodeSearchAsync(IReadOnlyCollection<int> episodeIds, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            _options.Sonarr,
            HttpMethod.Post,
            "/api/v3/command",
            new JsonObject
            {
                ["name"] = "EpisodeSearch",
                ["episodeIds"] = new JsonArray(episodeIds.Select(id => JsonValue.Create(id)).ToArray())
            },
            cancellationToken);
        return response == null ? 0 : GetInt(response.AsObject(), "id");
    }

    private async Task<IReadOnlyList<int>> MonitorSpecialEpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var episodes = await GetSpecialEpisodeIdsAsync(seriesId, cancellationToken);
        if (episodes.Count == 0)
        {
            throw new InvalidOperationException($"Series id {seriesId} has no specials to monitor.");
        }

        await SendAsync(
            _options.Sonarr,
            HttpMethod.Put,
            "/api/v3/episode/monitor",
            new JsonObject
            {
                ["episodeIds"] = new JsonArray(episodes.Select(id => JsonValue.Create(id)).ToArray()),
                ["monitored"] = true
            },
            cancellationToken);
        return episodes;
    }

    private async Task<bool> SpecialEpisodesAreMonitoredAsync(int seriesId, CancellationToken cancellationToken)
    {
        var episodes = await SendAsync(_options.Sonarr, HttpMethod.Get, $"/api/v3/episode?seriesId={seriesId}", null, cancellationToken);
        var specials = episodes?.AsArray()
            .Select(item => item?.AsObject())
            .Where(item => item != null && GetInt(item!, "seasonNumber") == 0)
            .ToArray();
        return specials is { Length: > 0 } && specials.All(item => item!["monitored"]?.GetValue<bool>() == true);
    }

    private async Task<IReadOnlyList<int>> GetSpecialEpisodeIdsAsync(int seriesId, CancellationToken cancellationToken)
    {
        var episodes = await SendAsync(_options.Sonarr, HttpMethod.Get, $"/api/v3/episode?seriesId={seriesId}", null, cancellationToken);
        return episodes?.AsArray()
            .Select(item => item?.AsObject())
            .Where(item => item != null && GetInt(item!, "seasonNumber") == 0)
            .Select(item => GetInt(item!, "id"))
            .Where(id => id > 0)
            .ToArray() ?? Array.Empty<int>();
    }

    private async Task<JsonNode?> SendAsync(ArrEndpointOptions endpoint, HttpMethod method, string path, JsonNode? payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Url) || string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            throw new InvalidOperationException("Arr endpoint URL/API key is not configured.");
        }

        var url = endpoint.Url.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);

        if (payload != null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Arr API returned {StatusCode} for {Method} {Url}: {Body}", response.StatusCode, method, url, body);
            throw new ArrApiException(response.StatusCode, body);
        }

        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private static JsonObject CloneObject(JsonObject obj) => JsonNode.Parse(obj.ToJsonString())!.AsObject();

    private static void RemoveInternalFields(JsonObject obj)
    {
        obj.Remove("_selectedSeasons");
    }

    private static void SetMonitoredSeasons(JsonObject series, ISet<int> selectedSeasons)
    {
        var seasons = series["seasons"]?.AsArray();
        if (seasons == null)
        {
            return;
        }

        foreach (var season in seasons.Select(item => item?.AsObject()).Where(item => item != null))
        {
            var seasonNumber = GetInt(season!, "seasonNumber");
            season!["monitored"] = seasonNumber > 0 && selectedSeasons.Contains(seasonNumber);
        }
    }

    private static void SetAllSeasonsMonitored(JsonObject series)
    {
        var seasons = series["seasons"]?.AsArray();
        if (seasons == null)
        {
            return;
        }

        foreach (var season in seasons.Select(item => item?.AsObject()).Where(item => item != null))
        {
            var seasonNumber = GetInt(season!, "seasonNumber");
            season!["monitored"] = seasonNumber > 0;
        }
    }

    private static void MarkSelectedSeasonsMonitored(JsonObject series, ISet<int> selectedSeasons)
    {
        var seasons = series["seasons"]?.AsArray();
        if (seasons == null)
        {
            return;
        }

        foreach (var season in seasons.Select(item => item?.AsObject()).Where(item => item != null))
        {
            var seasonNumber = GetInt(season!, "seasonNumber");
            if (seasonNumber > 0 && selectedSeasons.Contains(seasonNumber))
            {
                season!["monitored"] = true;
            }
        }
    }

    private static bool SelectedSeasonsAreMonitored(JsonObject series, ISet<int> selectedSeasons)
    {
        try
        {
            if (series["monitored"]?.GetValue<bool>() != true)
            {
                return false;
            }

            var seasons = series["seasons"]?.AsArray();
            if (seasons == null)
            {
                return false;
            }

            var monitoredSeasons = seasons
                .Select(item => item?.AsObject())
                .Where(item => item != null && item["monitored"]?.GetValue<bool>() == true)
                .Select(item => GetInt(item!, "seasonNumber"))
                .ToHashSet();

            return selectedSeasons.All(monitoredSeasons.Contains);
        }
        catch
        {
            return false;
        }
    }

    private static bool SeriesHasImportedEpisode(JsonObject series)
    {
        try
        {
            var stats = series["statistics"]?.AsObject();
            return (stats == null ? 0 : GetInt(stats, "episodeFileCount")) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SeriesHasCompletedRequestedSeasons(JsonObject series, ISet<int> selectedSeasons)
    {
        var requested = selectedSeasons.Where(seasonNumber => seasonNumber >= 0).ToHashSet();
        if (requested.Count == 0)
        {
            return SeriesHasImportedEpisode(series);
        }

        try
        {
            var seasons = series["seasons"]?.AsArray();
            if (seasons == null)
            {
                return false;
            }

            var matched = seasons
                .Select(item => item?.AsObject())
                .Where(item => item != null && requested.Contains(GetInt(item!, "seasonNumber")))
                .ToArray();
            if (matched.Length != requested.Count)
            {
                return false;
            }

            return matched.All(item =>
            {
                return SeasonIsComplete(item!);
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool SeasonIsComplete(JsonObject season)
    {
        var stats = season["statistics"]?.AsObject();
        var episodeFileCount = stats == null ? 0 : GetInt(stats, "episodeFileCount");
        var episodeCount = stats == null ? 0 : GetInt(stats, "episodeCount");
        var totalEpisodeCount = stats == null ? 0 : GetInt(stats, "totalEpisodeCount");
        var expectedCount = episodeCount > 0 ? episodeCount : totalEpisodeCount;
        return expectedCount > 0
            ? episodeFileCount >= expectedCount
            : episodeFileCount > 0;
    }

    private static bool IsAlreadyExistsResponse(string body)
    {
        return body.Contains("ExistsValidator", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonObject obj, string name) => obj[name]?.GetValue<string>();

    private static int GetInt(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }
}

public sealed class ArrApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Body { get; }
    public string SafeMessage { get; }

    public ArrApiException(HttpStatusCode statusCode, string body)
        : base($"Arr API returned {(int)statusCode} {statusCode}")
    {
        StatusCode = statusCode;
        Body = body;
        SafeMessage = body.Length > 400 ? body[..400] : body;
    }
}

public sealed class RequestRepository
{
    private readonly BotOptions _options;

    public RequestRepository(BotOptions options)
    {
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
        await using var connection = Open();
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 30000;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
        await ExecuteAsync(connection, @"
            CREATE TABLE IF NOT EXISTS sessions (
              id TEXT PRIMARY KEY,
              user_id TEXT NOT NULL,
              media_type TEXT NOT NULL,
              results_json TEXT NOT NULL,
              created_at TEXT NOT NULL,
              consumed_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS requests (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              user_id TEXT NOT NULL,
              media_type TEXT NOT NULL,
              title TEXT NOT NULL,
              external_id INTEGER NOT NULL,
              arr_id INTEGER NOT NULL,
              season_numbers TEXT NULL,
              completion_episode_ids TEXT NULL,
              search_command_ids TEXT NULL,
              search_status TEXT NULL,
              search_completed_at TEXT NULL,
              requested_at TEXT NOT NULL,
              completed_at TEXT NULL,
              dm_attempts INTEGER NOT NULL DEFAULT 0,
              last_error TEXT NULL,
              dm_sent INTEGER NOT NULL DEFAULT 0
            );
            ", cancellationToken);
        await EnsureColumnAsync(connection, "sessions", "consumed_at", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "season_numbers", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "completion_episode_ids", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "search_command_ids", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "search_status", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "search_completed_at", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "dm_attempts", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "requests", "last_error", "TEXT NULL", cancellationToken);
        await DeleteExpiredSessionsAsync(connection, TimeSpan.FromMinutes(Math.Max(5, _options.SessionTtlMinutes)), cancellationToken);
    }

    public async Task SaveSessionAsync(RequestSession session, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO sessions (id, user_id, media_type, results_json, created_at, consumed_at)
            VALUES ($id, $user_id, $media_type, $results_json, $created_at, NULL);
            ";
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$user_id", session.UserId.ToString());
        command.Parameters.AddWithValue("$media_type", session.MediaType);
        command.Parameters.AddWithValue("$results_json", session.Results.ToJsonString());
        command.Parameters.AddWithValue("$created_at", session.CreatedAt.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RequestSession?> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, user_id, media_type, results_json, created_at, consumed_at FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var createdAt = DateTimeOffset.Parse(reader.GetString(4));
        if (!reader.IsDBNull(5) || createdAt < DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(Math.Max(5, _options.SessionTtlMinutes))))
        {
            return null;
        }

        return new RequestSession(
            reader.GetString(0),
            ulong.Parse(reader.GetString(1)),
            reader.GetString(2),
            JsonNode.Parse(reader.GetString(3))!.AsArray(),
            createdAt);
    }

    public async Task<bool> TryConsumeSessionAsync(string sessionId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE sessions
            SET consumed_at = $consumed_at
            WHERE id = $id
              AND consumed_at IS NULL
              AND created_at >= $minimum_created_at;
            ";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$consumed_at", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$minimum_created_at", DateTimeOffset.UtcNow.Subtract(ttl).ToString("o"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SaveRequestAsync(
        ulong userId,
        string mediaType,
        string title,
        int externalId,
        int arrId,
        IReadOnlyCollection<int> seasonNumbers,
        IReadOnlyCollection<int>? completionEpisodeIds,
        IReadOnlyCollection<int> searchCommandIds,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO requests (user_id, media_type, title, external_id, arr_id, season_numbers, completion_episode_ids, search_command_ids, requested_at, dm_sent)
            SELECT $user_id, $media_type, $title, $external_id, $arr_id, $season_numbers, $completion_episode_ids, $search_command_ids, $requested_at, 0
            WHERE NOT EXISTS (
                SELECT 1
                FROM requests
                WHERE user_id = $user_id
                  AND media_type = $media_type
                  AND external_id = $external_id
                  AND COALESCE(season_numbers, '') = COALESCE($season_numbers, '')
                  AND dm_sent = 0
            );
            ";
        command.Parameters.AddWithValue("$user_id", userId.ToString());
        command.Parameters.AddWithValue("$media_type", mediaType);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$external_id", externalId);
        command.Parameters.AddWithValue("$arr_id", arrId);
        command.Parameters.AddWithValue("$season_numbers", FormatSeasonNumbers(seasonNumbers));
        command.Parameters.AddWithValue("$completion_episode_ids", completionEpisodeIds == null ? DBNull.Value : FormatEpisodeIds(completionEpisodeIds));
        command.Parameters.AddWithValue("$search_command_ids", FormatCommandIds(searchCommandIds));
        command.Parameters.AddWithValue("$requested_at", DateTimeOffset.UtcNow.ToString("o"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PendingRequest>> GetPendingRequestsAsync(int maxDmAttempts, CancellationToken cancellationToken)
    {
        var results = new List<PendingRequest>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, user_id, media_type, title, external_id, arr_id, season_numbers, completion_episode_ids, search_command_ids, dm_attempts
            FROM requests
            WHERE dm_sent = 0 AND dm_attempts < $max_dm_attempts
            ORDER BY requested_at ASC;
            ";
        command.Parameters.AddWithValue("$max_dm_attempts", Math.Max(1, maxDmAttempts));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PendingRequest(
                reader.GetInt64(0),
                ulong.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                ParseSeasonNumbers(reader.IsDBNull(6) ? null : reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseEpisodeIds(reader.GetString(7)),
                ParseCommandIds(reader.IsDBNull(8) ? null : reader.GetString(8)),
                reader.GetInt32(9)));
        }

        return results;
    }

    public async Task MarkDmSentAsync(long requestId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE requests SET dm_sent = 1, completed_at = $completed_at WHERE id = $id;";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkDmFailedAsync(long requestId, string error, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE requests
            SET dm_attempts = dm_attempts + 1,
                last_error = $last_error
            WHERE id = $id;
            ";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$last_error", TrimForStorage(error, 500));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSearchStatusAsync(long requestId, string status, bool completed, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = completed
            ? "UPDATE requests SET search_status = $status, search_completed_at = COALESCE(search_completed_at, $completed_at) WHERE id = $id;"
            : "UPDATE requests SET search_status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$status", TrimForStorage(status, 500));
        if (completed)
        {
            command.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("o"));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_options.DatabasePath};Default Timeout=30");
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteExpiredSessionsAsync(SqliteConnection connection, TimeSpan ttl, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE created_at < $minimum_created_at;";
        command.Parameters.AddWithValue("$minimum_created_at", DateTimeOffset.UtcNow.Subtract(ttl).ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatSeasonNumbers(IReadOnlyCollection<int> seasonNumbers)
    {
        return string.Join(",", seasonNumbers
            .Where(seasonNumber => seasonNumber > 0)
            .Distinct()
            .OrderBy(seasonNumber => seasonNumber));
    }

    private static string FormatCommandIds(IReadOnlyCollection<int> commandIds)
    {
        return string.Join(",", commandIds
            .Where(commandId => commandId > 0)
            .Distinct()
            .OrderBy(commandId => commandId));
    }

    private static string FormatEpisodeIds(IReadOnlyCollection<int> episodeIds)
    {
        return string.Join(",", episodeIds
            .Where(episodeId => episodeId > 0)
            .Distinct()
            .OrderBy(episodeId => episodeId));
    }

    private static IReadOnlyList<int> ParseSeasonNumbers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var seasonNumber) ? seasonNumber : 0)
            .Where(seasonNumber => seasonNumber > 0)
            .Distinct()
            .OrderBy(seasonNumber => seasonNumber)
            .ToArray();
    }

    private static IReadOnlyList<int> ParseCommandIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var commandId) ? commandId : 0)
            .Where(commandId => commandId > 0)
            .Distinct()
            .OrderBy(commandId => commandId)
            .ToArray();
    }

    private static IReadOnlyList<int> ParseEpisodeIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var episodeId) ? episodeId : 0)
            .Where(episodeId => episodeId > 0)
            .Distinct()
            .OrderBy(episodeId => episodeId)
            .ToArray();
    }

    private static string TrimForStorage(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp != null && !IPAddress.IsLoopback(remoteIp))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Starrboard Console is restricted to local connections.");
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) ||
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsPatch(context.Request.Method) ||
                HttpMethods.IsDelete(context.Request.Method))
            {
                var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
                if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Cross-site Starrboard Console requests are blocked.");
                    return;
                }

                var origin = context.Request.Headers.Origin.ToString();
                var referer = context.Request.Headers.Referer.ToString();
                if ((!string.IsNullOrWhiteSpace(origin) && !IsAllowedLocalOrigin(origin)) ||
                    (!string.IsNullOrWhiteSpace(referer) && !IsAllowedLocalOrigin(referer)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Starrboard Console only accepts local browser writes.");
                    return;
                }
            }

            await next();
        });

        app.MapGet("/", () =>
        {
            var indexPath = Path.Combine(AppContext.BaseDirectory, "admin", "index.html");
            return File.Exists(indexPath)
                ? Results.File(indexPath, "text/html; charset=utf-8")
                : Results.Content(AdminPage.Html, "text/html; charset=utf-8");
        });
        app.MapGet("/favicon.ico", () => AssetFile("starrboard.ico", "image/x-icon"));
        app.MapGet("/assets/starrboard-icon.png", () => AssetFile("starrboard-icon.png", "image/png"));
        app.MapGet("/api/ready", () => Results.Json(new
        {
            ready = true,
            service = "Starrboard",
            checkedAt = DateTimeOffset.UtcNow
        }));
        app.MapGet("/api/status", async (StackHealthService health, CancellationToken cancellationToken) =>
            Results.Json(await health.GetStatusAsync(cancellationToken)));
        app.MapGet("/api/onboarding/status", async (OnboardingService onboarding, CancellationToken cancellationToken) =>
            Results.Json(await onboarding.GetStatusAsync(cancellationToken)));
        app.MapGet("/api/bootstrap/dependencies", (BootstrapService bootstrap) =>
            Results.Json(bootstrap.GetDependencies()));
        app.MapGet("/api/bootstrap/script", (BootstrapService bootstrap) =>
            Results.Text(bootstrap.BuildScript(), "text/plain; charset=utf-8"));
        app.MapPost("/api/onboarding/discover", async (bool? apply, OnboardingAutomationService automation, CancellationToken cancellationToken) =>
            Results.Json(await automation.DiscoverAsync(apply ?? true, cancellationToken)));
        app.MapPost("/api/onboarding/test/{service}", async (string service, OnboardingAutomationService automation, CancellationToken cancellationToken) =>
            Results.Json(await automation.TestConnectionAsync(service, cancellationToken)));
        app.MapPost("/api/onboarding/wire", async (OnboardingAutomationService automation, CancellationToken cancellationToken) =>
        {
            var result = await automation.WireIntegrationsAsync(cancellationToken);
            return result.Ok ? Results.Json(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        });
        app.MapPost("/api/onboarding/root-folders/{service}", async (string service, OnboardingAutomationService automation, CancellationToken cancellationToken) =>
        {
            var result = await automation.ConfigureRootFoldersAsync(service, cancellationToken);
            return result.Ok ? Results.Json(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        });
        app.MapPost("/api/onboarding/validate-paths", async (OnboardingAutomationService automation, CancellationToken cancellationToken) =>
        {
            var result = await automation.ValidatePathsAsync(cancellationToken);
            return result.Ok ? Results.Json(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        });
        app.MapPost("/api/onboarding/test-workflow", async (OnboardingAutomationService automation, CancellationToken cancellationToken) =>
        {
            var result = await automation.TestWorkflowAsync(cancellationToken);
            return result.Ok ? Results.Json(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        });
        app.MapGet("/api/arr/{service}/reference", async (string service, ConfigStore config, CancellationToken cancellationToken) =>
        {
            var options = config.GetBotOptions();
            var endpoint = service.Equals("radarr", StringComparison.OrdinalIgnoreCase)
                ? options.Radarr
                : service.Equals("sonarr", StringComparison.OrdinalIgnoreCase)
                    ? options.Sonarr
                    : null;

            if (endpoint == null)
            {
                return Results.BadRequest(new { error = "Service must be radarr or sonarr." });
            }

            if (string.IsNullOrWhiteSpace(endpoint.Url) ||
                string.IsNullOrWhiteSpace(endpoint.ApiKey) ||
                endpoint.ApiKey.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new ArrReference(Array.Empty<ArrQualityProfile>(), Array.Empty<ArrRootFolder>(), "Save the URL and API key first."));
            }

            try
            {
                return Results.Json(await ArrReferenceLoader.LoadAsync(endpoint, cancellationToken));
            }
            catch (Exception ex)
            {
                return Results.Json(new ArrReference(Array.Empty<ArrQualityProfile>(), Array.Empty<ArrRootFolder>(), ex.Message));
            }
        });
        app.MapGet("/api/services/account-script", (ConfigStore config) =>
            Results.Text(ServiceAccountScript.Build(config.GetBotOptions()), "text/plain; charset=utf-8"));
        app.MapGet("/api/config", (ConfigStore config) => Results.Json(config.GetRedactedConfig()));
        app.MapPost("/api/config", async (ConfigStore config, HttpRequest request, CancellationToken cancellationToken) =>
        {
            var payload = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (payload is not JsonObject obj)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload." });
            }

            config.SaveConfig(obj);
            return Results.Json(new { ok = true, restartRequired = true });
        });
        app.MapPost("/api/onboarding/complete", async (ConfigStore config, OnboardingService onboarding, CancellationToken cancellationToken) =>
        {
            var status = await onboarding.GetStatusAsync(cancellationToken);
            if (!status.Ready)
            {
                return Results.BadRequest(new { error = "Onboarding is not ready.", status.Summary, status.Steps, status.Dependencies });
            }

            config.MarkOnboardingComplete();
            return Results.Json(new { ok = true, restartRequired = false });
        });
    }

    private static bool IsAllowedLocalOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult AssetFile(string fileName, string contentType)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "admin", "assets", fileName);
        return File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();
    }
}

public sealed class StackHealthService
{
    private BotOptions _options;
    private readonly ConfigStore _config;
    private readonly ILogger<StackHealthService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public StackHealthService(BotOptions options, ConfigStore config, ILogger<StackHealthService> logger)
    {
        _options = options;
        _config = config;
        _logger = logger;
    }

    public async Task<StackStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        _options = _config.GetBotOptions();
        var checks = new List<HealthCheckResult>
        {
            ServiceCheck("Starrboard", "Starrboard", _options.Admin.Url),
            FileCheck("Database", _options.DatabasePath, null),
            FileCheck("Logs", GetLogDirectory(), null)
        };

        checks.Add(await ArrCheckAsync("Radarr", _options.Radarr, _options.Radarr.RootFolderPath, cancellationToken));
        checks.Add(await ArrCheckAsync("Sonarr", _options.Sonarr, _options.Sonarr.RootFolderPath, cancellationToken));
        checks.Add(await ArrCheckAsync("Prowlarr", _options.Prowlarr, null, cancellationToken));
        checks.Add(await QbittorrentCheckAsync(cancellationToken));
        checks.Add(await FlareSolverrCheckAsync(cancellationToken));
        checks.Add(await DiscordCheckAsync(cancellationToken));
        var overall = checks.All(x => x.Status == "ok")
            ? "ok"
            : checks.Any(x => x.Status == "error")
                ? "error"
                : "warning";

        return new StackStatus(
            "Starrboard",
            overall,
            DateTimeOffset.Now,
            _options.OnboardingCompleted,
            new StackLinks(
                _options.Radarr.Url,
                _options.Sonarr.Url,
                _options.Prowlarr.Url,
                _options.FlareSolverr.Url,
                _options.Qbittorrent.Url,
                _options.Admin.Url),
            checks);
    }

    private async Task<HealthCheckResult> ArrCheckAsync(string name, ArrEndpointOptions endpoint, string? expectedRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Url))
        {
            return new HealthCheckResult(name, "warning", "URL is not configured.", endpoint.Url, null);
        }

        if (string.IsNullOrWhiteSpace(endpoint.ApiKey) || endpoint.ApiKey.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase))
        {
            return new HealthCheckResult(name, "warning", "API key is not configured.", endpoint.Url, null);
        }

        try
        {
            var apiPrefix = name.Equals("Prowlarr", StringComparison.OrdinalIgnoreCase) ? "/api/v1" : "/api/v3";
            var status = await SendJsonAsync(endpoint, HttpMethod.Get, $"{apiPrefix}/system/status", null, cancellationToken);
            var version = status?["version"]?.GetValue<string>();
            var messages = new List<string> { string.IsNullOrWhiteSpace(version) ? "API reachable." : $"API reachable, version {version}." };
            var resultingStatus = "ok";

            var healthIssues = GetActionableHealthIssues(name, await SendJsonAsync(endpoint, HttpMethod.Get, $"{apiPrefix}/health", null, cancellationToken));
            if (healthIssues.ActionableCount > 0)
            {
                messages.Add($"{healthIssues.ActionableCount} workflow health issue(s) reported.");
                resultingStatus = healthIssues.HasError ? "error" : "warning";
            }

            if (healthIssues.IgnoredCount > 0)
            {
                messages.Add($"{healthIssues.IgnoredCount} non-blocking health notice(s) ignored.");
            }

            if (name.Equals("Prowlarr", StringComparison.OrdinalIgnoreCase))
            {
                var indexers = await SendJsonAsync(endpoint, HttpMethod.Get, $"{apiPrefix}/indexer", null, cancellationToken);
                var enabledIndexers = indexers?.AsArray().Count(item => item?.AsObject()?["enable"]?.GetValue<bool>() == true) ?? 0;
                messages.Add($"{enabledIndexers} enabled indexer(s).");
                if (enabledIndexers == 0)
                {
                    resultingStatus = "error";
                }
            }
            else
            {
                if (endpoint.QualityProfileId <= 0)
                {
                    messages.Add("Quality profile is not configured.");
                    resultingStatus = "warning";
                }

                var downloadClients = await SendJsonAsync(endpoint, HttpMethod.Get, $"{apiPrefix}/downloadclient", null, cancellationToken);
                var enabledClients = downloadClients?.AsArray().Count(item => item?.AsObject()?["enable"]?.GetValue<bool>() == true) ?? 0;
                messages.Add($"{enabledClients} enabled download client(s).");
                if (enabledClients == 0)
                {
                    resultingStatus = "error";
                }
            }

            if (endpoint.QualityProfileId > 0)
            {
                var qualityProfiles = await SendJsonAsync(endpoint, HttpMethod.Get, $"{apiPrefix}/qualityprofile", null, cancellationToken);
                var qualityExists = qualityProfiles?.AsArray().Any(item => item?.AsObject()?["id"]?.GetValue<int>() == endpoint.QualityProfileId) == true;
                messages.Add(qualityExists ? $"Quality profile {endpoint.QualityProfileId} exists." : $"Quality profile {endpoint.QualityProfileId} was not found.");
                if (!qualityExists)
                {
                    return new HealthCheckResult(name, "warning", string.Join(" ", messages), endpoint.Url, version);
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedRoot))
            {
                var roots = await SendJsonAsync(endpoint, HttpMethod.Get, $"{apiPrefix}/rootfolder", null, cancellationToken);
                var expected = NormalizeArrPath(expectedRoot);
                var rootExists = roots?.AsArray().Any(item =>
                    string.Equals(NormalizeArrPath(item?.AsObject()?["path"]?.GetValue<string>()), expected, StringComparison.OrdinalIgnoreCase)) == true;
                messages.Add(rootExists ? "Root folder is configured." : $"Root folder is not configured: {expectedRoot}");
                if (!rootExists)
                {
                    return new HealthCheckResult(name, "warning", string.Join(" ", messages), endpoint.Url, version);
                }
            }

            return new HealthCheckResult(name, resultingStatus, string.Join(" ", messages), endpoint.Url, version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{ServiceName} health check failed.", name);
            return new HealthCheckResult(name, "error", CleanError(ex), endpoint.Url, null);
        }
    }

    private static ArrHealthIssueSummary GetActionableHealthIssues(string serviceName, JsonNode? health)
    {
        var actionable = 0;
        var ignored = 0;
        var hasError = false;
        var array = health?.AsArray();
        if (array == null)
        {
            return new ArrHealthIssueSummary(0, 0, false);
        }

        foreach (var item in array.Select(node => node?.AsObject()).Where(obj => obj != null))
        {
            if (IsNonBlockingHealthIssue(serviceName, item!))
            {
                ignored++;
                continue;
            }

            actionable++;
            var type = GetString(item!, "type");
            hasError = hasError || string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);
        }

        return new ArrHealthIssueSummary(actionable, ignored, hasError);
    }

    private static bool IsNonBlockingHealthIssue(string serviceName, JsonObject issue)
    {
        var source = GetString(issue, "source") ?? "";
        var message = GetString(issue, "message") ?? "";

        if (source.Equals("DownloadClientRemovesCompletedDownloadsCheck", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return serviceName.Equals("Prowlarr", StringComparison.OrdinalIgnoreCase) &&
            source.Equals("ApplicationLongTermStatusCheck", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("LazyLibrarian", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HealthCheckResult> QbittorrentCheckAsync(CancellationToken cancellationToken)
    {
        var process = ProcessCheck(_options.Qbittorrent.ProcessName, "qBittorrent", _options.Qbittorrent.Url);
        if (string.IsNullOrWhiteSpace(_options.Qbittorrent.Url))
        {
            return process.Status == "ok"
                ? new HealthCheckResult("qBittorrent", "warning", "Process is running, but Web UI URL is not configured.", null, null)
                : process;
        }

        try
        {
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(12) };
            if (!string.IsNullOrWhiteSpace(_options.Qbittorrent.Username))
            {
                using var login = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = _options.Qbittorrent.Username,
                    ["password"] = _options.Qbittorrent.Password
                });
                using var loginResponse = await client.PostAsync(_options.Qbittorrent.Url.TrimEnd('/') + "/api/v2/auth/login", login, cancellationToken);
                if (!loginResponse.IsSuccessStatusCode)
                {
                    return new HealthCheckResult("qBittorrent", "error", $"Process check: {process.Message} Web UI authentication failed.", _options.Qbittorrent.Url, null);
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Qbittorrent.Url.TrimEnd('/') + "/api/v2/app/version");
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new HealthCheckResult("qBittorrent", "warning", $"Process check: {process.Message} Web UI is reachable, but credentials must be saved in Starrboard for integration testing.", _options.Qbittorrent.Url, null);
                }

                var status = process.Status == "ok" ? "warning" : "error";
                return new HealthCheckResult("qBittorrent", status, $"Process check: {process.Message} Web UI returned {(int)response.StatusCode}.", _options.Qbittorrent.Url, null);
            }

            var okStatus = ShouldRequireLocalProcess(_options.Qbittorrent.Url) && process.Status != "ok" ? "warning" : "ok";
            return new HealthCheckResult("qBittorrent", okStatus, $"Process check: {process.Message} Web UI reachable.", _options.Qbittorrent.Url, Trim(body.Trim(), 40));
        }
        catch (Exception ex)
        {
            var status = process.Status == "ok" ? "warning" : "error";
            return new HealthCheckResult("qBittorrent", status, $"Process check: {process.Message} Web UI check failed: {CleanError(ex)}", _options.Qbittorrent.Url, null);
        }
    }

    private async Task<HealthCheckResult> FlareSolverrCheckAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.FlareSolverr.Url))
        {
            return new HealthCheckResult("FlareSolverr", "warning", "URL is not configured.", null, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.FlareSolverr.Url.TrimEnd('/') + "/v1");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { cmd = "sessions.list", maxTimeout = 60000 }),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckResult("FlareSolverr", "warning", $"API returned {(int)response.StatusCode}.", _options.FlareSolverr.Url, null);
            }

            var node = JsonNode.Parse(body)?.AsObject();
            var status = GetString(node ?? new JsonObject(), "status");
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                ? new HealthCheckResult("FlareSolverr", "ok", "FlareSolverr API responded to sessions.list.", _options.FlareSolverr.Url, null)
                : new HealthCheckResult("FlareSolverr", "warning", $"Unexpected FlareSolverr response: {Trim(body, 120)}", _options.FlareSolverr.Url, null);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult("FlareSolverr", "warning", CleanError(ex), _options.FlareSolverr.Url, null);
        }
    }

    private async Task<HealthCheckResult> DiscordCheckAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.DiscordToken) || _options.DiscordToken.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase))
        {
            return new HealthCheckResult("Discord", "warning", "Bot token is not configured.", null, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
            request.Headers.Add("Authorization", "Bot " + _options.DiscordToken);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckResult("Discord", "error", $"Discord returned {(int)response.StatusCode}.", null, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(body)?.AsObject();
            var username = node?["username"]?.GetValue<string>() ?? "bot";
            return new HealthCheckResult("Discord", "ok", $"Token is valid for {username}.", null, null);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult("Discord", "error", CleanError(ex), null, null);
        }
    }

    private string GetLogDirectory()
    {
        var directory = Path.GetDirectoryName(_options.Logs.Path);
        return string.IsNullOrWhiteSpace(directory) ? StarrboardPaths.Logs : directory;
    }

    private HealthCheckResult ServiceCheck(string serviceName, string displayName, string? link)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HealthCheckResult(displayName, "warning", "Windows service status is only available on Windows.", link, null);
        }

        try
        {
            using var service = new ServiceController(serviceName);
            var status = service.Status == ServiceControllerStatus.Running ? "ok" : "warning";
            return new HealthCheckResult(displayName, status, $"Windows service is {service.Status}.", link, null);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(displayName, "warning", CleanError(ex), link, null);
        }
    }

    private static HealthCheckResult ProcessCheck(string processName, string displayName, string? link)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return new HealthCheckResult(displayName, "warning", "Process name is not configured.", link, null);
        }

        var processCount = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName)).Length;
        return processCount > 0
            ? new HealthCheckResult(displayName, "ok", $"{processCount} process instance(s) running.", link, null)
            : new HealthCheckResult(displayName, "warning", "Process is not running.", link, null);
    }

    private static HealthCheckResult FileCheck(string name, string path, string? link)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        return exists
            ? new HealthCheckResult(name, "ok", path, link, null)
            : new HealthCheckResult(name, "warning", $"Path not found: {path}", link, null);
    }

    private async Task<JsonNode?> SendJsonAsync(ArrEndpointOptions endpoint, HttpMethod method, string path, JsonNode? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint.Url.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        if (payload != null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{endpoint.Url} returned {(int)response.StatusCode}: {Trim(body, 160)}");
        }

        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private static string CleanError(Exception ex) => Trim(ex.Message.Replace(Environment.NewLine, " "), 220);

    private static string? GetString(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<string>(); }
        catch { return null; }
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";

    private static string NormalizeArrPath(string? value) =>
        (value ?? "").Trim().TrimEnd('\\', '/');

    private static bool ShouldRequireLocalProcess(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return true;
        }

        return uri.IsLoopback ||
            uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

}

public sealed class OnboardingService
{
    private BotOptions _options;
    private readonly ConfigStore _config;
    private readonly StackHealthService _health;

    public OnboardingService(BotOptions options, ConfigStore config, StackHealthService health)
    {
        _options = options;
        _config = config;
        _health = health;
    }

    public async Task<OnboardingStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        _options = _config.GetBotOptions();
        var stack = await _health.GetStatusAsync(cancellationToken);
        var dependencies = new List<DependencyItem>
        {
            RuntimeDependency(stack),
            ArrDependency(
                "radarr",
                "Radarr",
                _options.Radarr.Url,
                "Radarr",
                "https://radarr.video/",
                "https://wiki.servarr.com/radarr",
                FindCheck(stack, "Radarr")),
            ArrDependency(
                "sonarr",
                "Sonarr",
                _options.Sonarr.Url,
                "Sonarr",
                "https://sonarr.tv/",
                "https://wiki.servarr.com/sonarr",
                FindCheck(stack, "Sonarr")),
            ArrDependency(
                "prowlarr",
                "Prowlarr",
                _options.Prowlarr.Url,
                "Prowlarr",
                "https://prowlarr.com/",
                "https://wiki.servarr.com/prowlarr",
                FindCheck(stack, "Prowlarr")),
            ProcessDependency(
                "qbittorrent",
                "qBittorrent",
                "Download client",
                _options.Qbittorrent.Url,
                _options.Qbittorrent.ProcessName,
                "qBittorrent",
                "https://www.qbittorrent.org/download",
                "https://github.com/qbittorrent/qBittorrent/wiki/WebUI-usage",
                FindCheck(stack, "qBittorrent")),
            ProcessDependency(
                "flaresolverr",
                "FlareSolverr",
                "Indexer support",
                _options.FlareSolverr.Url,
                "flaresolverr",
                "FlareSolverr",
                "https://github.com/FlareSolverr/FlareSolverr/releases",
                "https://github.com/FlareSolverr/FlareSolverr",
                FindCheck(stack, "FlareSolverr"),
                required: false),
            DiscordDependency(FindCheck(stack, "Discord")),
            MediaFolderDependency(stack)
        };

        var ready = dependencies.Where(item => item.Required).All(item => item.Status == "ready");
        var steps = BuildSteps(stack, dependencies, ready);
        var summary = ready
            ? "All required dependencies are installed, configured, and passing health validation."
            : "Finish the items marked missing or needing attention, then refresh validation.";

        return new OnboardingStatus(DateTimeOffset.Now, ready, summary, steps, dependencies);
    }

    private DependencyItem RuntimeDependency(StackStatus stack)
    {
        var checks = new[] { FindCheck(stack, "Starrboard"), FindCheck(stack, "Database"), FindCheck(stack, "Logs") }
            .Where(check => check != null)
            .Cast<HealthCheckResult>()
            .ToList();
        var status = checks.All(check => check.Status == "ok")
            ? "ready"
            : checks.Any(check => check.Status == "error")
                ? "missing"
                : "attention";
        var message = checks.Count == 0
            ? "Runtime checks were not available."
            : string.Join(" ", checks.Select(check => $"{check.Name}: {check.Message}"));
        var actions = status == "ready"
            ? new[] { "No action required." }
            : new[] { "Confirm the Starrboard service is running.", "Confirm the database file and log directory are writable." };

        return new DependencyItem(
            "runtime",
            "Starrboard runtime",
            "Service runtime",
            status,
            true,
            message,
            null,
            UrlOrNull(_options.Admin.Url),
            null,
            null,
            actions);
    }

    private DependencyItem ArrDependency(
        string id,
        string name,
        string url,
        string serviceName,
        string downloadUrl,
        string setupUrl,
        HealthCheckResult? check)
    {
        var status = ClassifyRequired(check, ServiceExists(serviceName) || ProcessExists(serviceName), string.IsNullOrWhiteSpace(url));
        var message = status switch
        {
            "ready" => check?.Message ?? $"{name} is reachable.",
            "missing" => $"{name} was not detected. Install it, start it, then configure the URL and API key.",
            _ => check?.Message ?? $"{name} is installed or configured, but needs attention."
        };

        return new DependencyItem(
            id,
            name,
            "Arr service",
            status,
            true,
            message,
            check?.Version,
            UrlOrNull(url),
            downloadUrl,
            setupUrl,
            RequiredActions(status, name, needsConfig: string.IsNullOrWhiteSpace(url) || IsPlaceholderApiKey(check, name)));
    }

    private DependencyItem ProcessDependency(
        string id,
        string name,
        string category,
        string url,
        string processName,
        string displayProcessName,
        string downloadUrl,
        string setupUrl,
        HealthCheckResult? check)
        => ProcessDependency(id, name, category, url, processName, displayProcessName, downloadUrl, setupUrl, check, required: true);

    private DependencyItem ProcessDependency(
        string id,
        string name,
        string category,
        string url,
        string processName,
        string displayProcessName,
        string downloadUrl,
        string setupUrl,
        HealthCheckResult? check,
        bool required)
    {
        var status = required
            ? ClassifyRequired(check, ProcessExists(processName), string.IsNullOrWhiteSpace(url))
            : check?.Status == "ok"
                ? "ready"
                : "optional";
        var message = status switch
        {
            "ready" => check?.Message ?? $"{name} is reachable.",
            "optional" => $"{name} is optional. Configure it only for protected indexers that need challenge solving.",
            "missing" => $"{name} was not detected. Install and start it, then confirm the console URL.",
            _ => check?.Message ?? $"{name} is configured, but needs attention."
        };

        return new DependencyItem(
            id,
            name,
            category,
            status,
            required,
            message,
            check?.Version,
            UrlOrNull(url),
            downloadUrl,
            setupUrl,
            required
                ? RequiredActions(status, displayProcessName, needsConfig: string.IsNullOrWhiteSpace(url))
                : status == "ready"
                    ? new[] { "No action required." }
                    : new[] { "Leave disabled unless a protected Prowlarr indexer needs it.", "If needed, launch dependency setup and add the FlareSolverr proxy in Prowlarr." });
    }

    private DependencyItem DiscordDependency(HealthCheckResult? check)
    {
        var tokenMissing = string.IsNullOrWhiteSpace(_options.DiscordToken) ||
            _options.DiscordToken.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase);
        var status = tokenMissing ? "missing" : check?.Status == "ok" ? "ready" : "attention";
        var message = tokenMissing
            ? "Discord bot token is not configured."
            : _options.GuildId == 0
                ? $"{check?.Message ?? "Discord token is valid."} Global command registration is enabled because GuildId is 0."
                : check?.Message ?? "Discord token needs validation.";

        var actions = new List<string>();
        if (tokenMissing)
        {
            actions.Add("Create or reset a Discord bot token.");
            actions.Add("Paste the token in Configuration.");
        }

        if (_options.GuildId == 0)
        {
            actions.Add("Optional: set a Discord guild id for faster command registration during setup.");
        }

        if (actions.Count == 0)
        {
            actions.Add("No action required.");
        }

        return new DependencyItem(
            "discord",
            "Discord bot",
            "Bot identity",
            status,
            true,
            message,
            check?.Version,
            null,
            null,
            "https://discord.com/developers/applications",
            actions);
    }

    private DependencyItem MediaFolderDependency(StackStatus stack)
    {
        var paths = new[]
        {
            (Name: "Radarr", Path: _options.Radarr.RootFolderPath),
            (Name: "Sonarr", Path: _options.Sonarr.RootFolderPath)
        };
        var missing = paths.Where(item => string.IsNullOrWhiteSpace(item.Path)).Select(item => item.Name).ToList();
        var arrRootsConfigured = new[] { FindCheck(stack, "Radarr"), FindCheck(stack, "Sonarr") }
            .All(check => check?.Status == "ok" &&
                check.Message.Contains("Root folder is configured", StringComparison.OrdinalIgnoreCase));
        if (missing.Count == 0 && arrRootsConfigured)
        {
            return new DependencyItem(
                "media-folders",
                "Media folders",
                "Storage",
                "ready",
                true,
                "Radarr and Sonarr report their configured media root folders are valid.",
                null,
                null,
                null,
                "https://wiki.servarr.com/",
                new[] { "No action required." });
        }

        var unreachable = paths
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && !Directory.Exists(item.Path))
            .Select(item => $"{item.Name}: {item.Path}")
            .ToList();

        var status = missing.Count > 0 ? "missing" : unreachable.Count > 0 ? "attention" : "ready";
        var message = status switch
        {
            "ready" => "Radarr and Sonarr media root folders are configured and reachable from this host.",
            "missing" => $"Root folder path missing for {string.Join(", ", missing)}.",
            _ => $"Configured root folder path is not reachable: {string.Join("; ", unreachable)}."
        };
        var actions = new List<string>();
        if (missing.Count > 0)
        {
            actions.Add("Set the Radarr and Sonarr root folder paths in Configuration.");
        }

        if (unreachable.Count > 0)
        {
            actions.Add("Confirm the service account can access the media share.");
        }

        if (actions.Count == 0)
        {
            actions.Add("No action required.");
        }

        return new DependencyItem(
            "media-folders",
            "Media folders",
            "Storage",
            status,
            true,
            message,
            null,
            null,
            null,
            "https://wiki.servarr.com/",
            actions);
    }

    private static IReadOnlyList<OnboardingStep> BuildSteps(StackStatus stack, IReadOnlyList<DependencyItem> dependencies, bool ready)
    {
        var missing = dependencies.Count(item => item.Required && item.Status == "missing");
        var attention = dependencies.Count(item => item.Required && item.Status == "attention");
        var optionalAttention = dependencies.Count(item => !item.Required && item.Status == "attention");

        return new[]
        {
            new OnboardingStep("install", "Install dependencies", missing == 0 ? "ready" : "missing", missing == 0 ? "All required packages were detected or are reachable." : $"{missing} required package/configuration item(s) are missing."),
            new OnboardingStep("configure", "Configure integrations", attention == 0 && missing == 0 ? "ready" : "attention", attention == 0 && missing == 0 ? "Required URLs, API keys, profiles, and paths are validated." : $"{attention} required item(s) still need configuration or repair."),
            new OnboardingStep("advanced", "Review optional checks", optionalAttention == 0 ? "ready" : "attention", optionalAttention == 0 ? "Optional checks are disabled or passing." : $"{optionalAttention} optional check(s) need attention."),
            new OnboardingStep("validate", "Validate stack", ready ? "ready" : stack.OverallStatus == "error" ? "missing" : "attention", ready ? "Stack health is green and onboarding can be completed." : $"Current health state is {stack.OverallStatus}.")
        };
    }

    private static string ClassifyRequired(HealthCheckResult? check, bool detected, bool missingUrl)
    {
        if (check?.Status == "ok")
        {
            return "ready";
        }

        if (!detected && (missingUrl || check?.Status == "warning"))
        {
            return "missing";
        }

        if (!detected && check?.Status == "error")
        {
            return "missing";
        }

        return "attention";
    }

    private static IReadOnlyList<string> RequiredActions(string status, string name, bool needsConfig)
    {
        var actions = new List<string>();
        if (status == "missing")
        {
            actions.Add($"Install and start {name}.");
        }

        if (needsConfig || status != "ready")
        {
            actions.Add("Open Configuration and confirm URL, API key, and related settings.");
        }

        if (status == "attention")
        {
            actions.Add("Open the console and resolve the reported health issue.");
        }

        if (actions.Count == 0)
        {
            actions.Add("No action required.");
        }

        return actions;
    }

    private static bool IsPlaceholderApiKey(HealthCheckResult? check, string name) =>
        check?.Message.Contains("API key is not configured", StringComparison.OrdinalIgnoreCase) == true ||
        check?.Message.Contains("URL is not configured", StringComparison.OrdinalIgnoreCase) == true ||
        string.IsNullOrWhiteSpace(name);

    private static HealthCheckResult? FindCheck(StackStatus stack, string name) =>
        stack.Checks.FirstOrDefault(check => check.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? UrlOrNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool ServiceExists(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var service = new ServiceController(serviceName);
            _ = service.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProcessExists(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        try
        {
            return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName)).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}

public static class ArrReferenceLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<ArrReference> LoadAsync(ArrEndpointOptions endpoint, CancellationToken cancellationToken)
    {
        var qualityProfiles = await SendJsonAsync(endpoint, "/api/v3/qualityprofile", cancellationToken);
        var rootFolders = await SendJsonAsync(endpoint, "/api/v3/rootfolder", cancellationToken);
        return new ArrReference(
            qualityProfiles?.AsArray()
                .Select(item => item?.AsObject())
                .Where(item => item != null)
                .Select(item => new ArrQualityProfile(
                    GetInt(item!, "id"),
                    GetString(item!, "name") ?? $"Profile {GetInt(item!, "id")}"))
                .Where(item => item.Id > 0)
                .OrderBy(item => item.Name)
                .ToArray() ?? Array.Empty<ArrQualityProfile>(),
            rootFolders?.AsArray()
                .Select(item => item?.AsObject())
                .Where(item => item != null)
                .Select(item => new ArrRootFolder(
                    GetString(item!, "path") ?? "",
                    GetLong(item!, "freeSpace")))
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .OrderBy(item => item.Path)
                .ToArray() ?? Array.Empty<ArrRootFolder>(),
            null);
    }

    private static async Task<JsonNode?> SendJsonAsync(ArrEndpointOptions endpoint, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{endpoint.Url} returned {(int)response.StatusCode}: {Trim(body, 160)}");
        }

        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private static string? GetString(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<string>(); }
        catch { return null; }
    }

    private static int GetInt(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private static long GetLong(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<long>() ?? 0; }
        catch { return 0; }
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
}

public sealed class BootstrapService
{
    public IReadOnlyList<BootstrapDependency> GetDependencies() => Dependencies;

    public string BuildScript()
    {
        return """
# Starrboard dependency bootstrapper
# Run from an elevated PowerShell prompt on the target machine.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-DependencyPresent($name) {
    switch ($name) {
        'Radarr' { return [bool]((Get-Service Radarr -ErrorAction SilentlyContinue) -or (Test-Path "$env:ProgramFiles\Radarr")) }
        'Sonarr' { return [bool]((Get-Service Sonarr -ErrorAction SilentlyContinue) -or (Test-Path "$env:ProgramFiles\Sonarr")) }
        'Prowlarr' { return [bool]((Get-Service Prowlarr -ErrorAction SilentlyContinue) -or (Test-Path "$env:ProgramFiles\Prowlarr")) }
        'qBittorrent' { return [bool]((Test-Path "$env:ProgramFiles\qBittorrent\qbittorrent.exe") -or (Test-Path "${env:ProgramFiles(x86)}\qBittorrent\qbittorrent.exe")) }
        'FlareSolverr' { return [bool]((Test-Path (Join-Path $env:ProgramData 'Starrboard\FlareSolverr\flaresolverr.exe')) -or (Test-Path 'C:\Util\FlareSolverr\flaresolverr.exe') -or (Get-ScheduledTask -TaskName 'Starrboard-FlareSolverr' -ErrorAction SilentlyContinue)) }
    }
    return $false
}

function Write-DependencyManifest($before) {
    $path = Join-Path $env:ProgramData 'Starrboard\data\dependency-manifest.json'
    $previous = $null
    if (Test-Path -LiteralPath $path) {
        try { $previous = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { }
    }
    $ids = @{ Radarr = 'TeamRadarr.Radarr'; Sonarr = 'TeamSonarr.Sonarr'; Prowlarr = 'TeamProwlarr.Prowlarr'; qBittorrent = 'qBittorrent.qBittorrent'; FlareSolverr = '' }
    $dependencies = [ordered]@{}
    foreach ($name in @('Radarr','Sonarr','Prowlarr','qBittorrent','FlareSolverr')) {
        $wasManaged = $false
        if ($previous -and $previous.Dependencies -and $previous.Dependencies.$name) { $wasManaged = [bool]$previous.Dependencies.$name.InstalledByStarrboard }
        $present = Test-DependencyPresent $name
        $dependencies[$name] = [ordered]@{
            InstalledByStarrboard = [bool]($wasManaged -or ((-not $before[$name]) -and $present))
            PresentAfterSetup = [bool]$present
            WingetPackageId = $ids[$name]
        }
    }
    $manifest = [ordered]@{ Version = 1; UpdatedAt = (Get-Date).ToString('o'); Dependencies = $dependencies }
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Dependency ownership manifest saved to $path"
}

function Get-ToolPath($name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Invoke-Download($url, $path) {
    Write-Host "Downloading $url"
    $parent = Split-Path -Parent $path
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $path
    if (-not (Test-Path -LiteralPath $path)) { throw "Download did not create $path" }
    if ((Get-Item -LiteralPath $path).Length -lt 1024) { throw "Download looks too small: $path" }
}

function Invoke-GitHubAssetDownload($repo, $assetNamePattern, $path) {
    $release = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$repo/releases/latest"
    $asset = $release.assets | Where-Object { $_.name -match $assetNamePattern } | Select-Object -First 1
    if (-not $asset) { throw "No release asset matched $assetNamePattern for $repo" }
    Invoke-Download $asset.browser_download_url $path
}

function Repair-Winget {
    $winget = Get-ToolPath 'winget'
    if ($winget) { return $winget }

    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    if ($os -and $os.ProductType -ne 1) {
        Write-Warning 'winget is not installed on this Windows Server host. Using direct official installers.'
        return $null
    }

    Write-Warning 'winget was not found. Trying to repair/register Microsoft App Installer.'
    try {
        Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop
    } catch {
        Write-Warning "App Installer registration did not complete: $($_.Exception.Message)"
    }

    $winget = Get-ToolPath 'winget'
    if ($winget) { return $winget }

    try {
        $bundle = Join-Path $env:TEMP 'Microsoft.DesktopAppInstaller.msixbundle'
        Invoke-Download 'https://aka.ms/getwinget' $bundle
        Add-AppxPackage -Path $bundle -ErrorAction Stop
    } catch {
        Write-Warning "App Installer download/install did not complete: $($_.Exception.Message)"
    }

    return Get-ToolPath 'winget'
}

function Install-WithWinget($packageId, $name) {
    $winget = Get-ToolPath 'winget'
    if (-not $winget) { return $false }

    Write-Host "Installing or updating $name with winget..."
    & $winget install -e --id $packageId --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -eq 0) { return $true }

    Write-Warning "winget returned exit code $LASTEXITCODE for $name."
    return $false
}

function Install-Exe($name, $url, $fileName, $arguments) {
    try {
        $installer = Join-Path $env:TEMP $fileName
        Invoke-Download $url $installer
        Write-Host "Running $name installer..."
        $process = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru
        if (-not $process.WaitForExit(900000)) {
            Write-Warning "$name installer did not finish within 15 minutes and was stopped. It may be waiting for input."
            & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            return $false
        }
        if ($process.ExitCode -ne 0) {
            Write-Warning "$name installer returned exit code $($process.ExitCode)."
            return $false
        }

        return $true
    } catch {
        Write-Warning "$name direct install failed: $($_.Exception.Message)"
        return $false
    }
}

function Install-GitHubExe($name, $repo, $assetPattern, $fileName, $arguments) {
    try {
        $installer = Join-Path $env:TEMP $fileName
        Invoke-GitHubAssetDownload $repo $assetPattern $installer
        Write-Host "Running $name installer..."
        $process = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru
        if (-not $process.WaitForExit(900000)) {
            Write-Warning "$name installer did not finish within 15 minutes and was stopped. It may be waiting for input."
            & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            return $false
        }
        if ($process.ExitCode -ne 0) {
            Write-Warning "$name installer returned exit code $($process.ExitCode)."
            return $false
        }

        return $true
    } catch {
        Write-Warning "$name direct install failed: $($_.Exception.Message)"
        return $false
    }
}

function Find-FirstExistingPath([string[]]$paths) {
    foreach ($path in $paths) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }

    return $null
}

function Grant-ArrServiceDataAccess($name) {
    $dataRoot = Join-Path $env:ProgramData $name
    if (-not (Test-Path -LiteralPath $dataRoot)) { return }
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
    if (-not $service) { return }
    $account = $service.StartName
    if ([string]::IsNullOrWhiteSpace($account) -or $account -match 'LocalSystem') { return }
    if ($account -match 'LocalService') { $account = 'NT AUTHORITY\LOCAL SERVICE' }
    try {
        # Sonarr persists defaults back to config.xml during startup. The
        # installer can leave that file owned by the installing administrator,
        # so grant its actual Windows service identity modify access recursively.
        & icacls.exe $dataRoot /grant "${account}:(OI)(CI)M" /T /C | Out-Null
        if ($LASTEXITCODE -gt 0) { throw "icacls returned exit code $LASTEXITCODE" }
        Write-Host "Granted $account modify access to $dataRoot."
    } catch {
        Write-Warning "Could not grant $account access to ${dataRoot}: $($_.Exception.Message)"
    }
}

function Grant-QbittorrentProfileAccess {
    $profileRoot = Join-Path $env:ProgramData 'Starrboard\qBittorrent'
    if (-not (Test-Path -LiteralPath $profileRoot)) { return }
    $downloadRoot = Join-Path $profileRoot 'qBittorrent_starrboard\downloads'
    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
    foreach ($serviceName in @('Radarr','Sonarr')) {
        $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        if (-not $service) { continue }
        $account = $service.StartName
        if ([string]::IsNullOrWhiteSpace($account) -or $account -match 'LocalSystem') { continue }
        if ($account -match 'LocalService') {
            $account = 'NT AUTHORITY\LOCAL SERVICE'
        } elseif ($account.StartsWith('.\')) {
            $account = "$env:COMPUTERNAME\$($account.Substring(2))"
        }
        try {
            # qBittorrent's managed profile contains its default downloads
            # directory. Arr must be able to inspect and import those files.
            & icacls.exe $profileRoot /grant "${account}:(OI)(CI)M" /T /C | Out-Null
            if ($LASTEXITCODE -gt 0) { throw "icacls returned exit code $LASTEXITCODE" }
            & icacls.exe $downloadRoot /grant "${account}:(OI)(CI)M" /C | Out-Null
            if ($LASTEXITCODE -gt 0) { throw "icacls returned exit code $LASTEXITCODE" }
            Write-Host "Granted $account access to the Starrboard qBittorrent downloads directory for $serviceName imports."
        } catch {
            Write-Warning "Could not grant $account qBittorrent-profile access for ${serviceName}: $($_.Exception.Message)"
        }
    }
}

function Ensure-ArrService($name, [string[]]$consolePaths) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $service) {
        $consoleExe = Find-FirstExistingPath $consolePaths
        if ($consoleExe) {
            Write-Host "Creating $name Windows service from $consoleExe..."
            & sc.exe create $name binPath= "`"$consoleExe`"" start= auto | Out-Host
            Start-Sleep -Seconds 2
            $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        }
    }

    if ($service) {
        try {
            # Restore the vendor service behavior used before the readiness
            # regression. Starrboard must not replace Arr command lines or
            # repeatedly restart a process while it is initializing.
            $serviceConfig = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
            if ($serviceConfig -and $serviceConfig.PathName -match "-data=(?:`"|')?C:\\ProgramData\\$name") {
                $consoleExe = Find-FirstExistingPath $consolePaths
                if ($consoleExe) {
                    & sc.exe config $name binPath= "`"$consoleExe`"" | Out-Null
                    Write-Host "Restored the vendor $name service command."
                }
            }
            & sc.exe failure $name reset= 0 actions= "" | Out-Null
            Grant-ArrServiceDataAccess $name
            Set-Service -Name $name -StartupType Automatic
            if ($service.Status -ne 'Running') {
                Start-Service -Name $name
            }
            Write-Host "$name service start was requested and set to Automatic."
        } catch {
            Write-Warning "$name service exists but could not be started: $($_.Exception.Message)"
        }
    } else {
        Write-Warning "$name service was not found or created. Open the installer manually and choose Windows Service if prompted."
    }
}

function Start-Qbittorrent {
    $profileRoot = Join-Path $env:ProgramData 'Starrboard\qBittorrent'
    $matchingProcess = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ieq 'qbittorrent.exe' -and $_.CommandLine -like "*$profileRoot*"
    } | Select-Object -First 1
    if ($matchingProcess) {
        Write-Host 'Starrboard qBittorrent profile is already running.'
        return
    }

    $qbExe = Find-FirstExistingPath @(
        "$env:ProgramFiles\qBittorrent\qbittorrent.exe",
        "${env:ProgramFiles(x86)}\qBittorrent\qbittorrent.exe"
    )
    if ($qbExe) {
        # Initialize and validate the Web UI in the installer session first.
        # A background task is created only after this process is known-good.
        $arguments = ('--profile="{0}" --configuration=starrboard --confirm-legal-notice' -f $profileRoot)
        Start-Process -FilePath $qbExe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $qbExe)
        Write-Host "Started qBittorrent with Starrboard profile at $profileRoot."
    } else {
        Write-Warning 'qBittorrent executable was not found after install.'
    }
}

function Stop-StarrboardQbittorrent {
    $profileRoot = Join-Path $env:ProgramData 'Starrboard\qBittorrent'
    $task = Get-ScheduledTask -TaskName 'Starrboard-qBittorrent' -ErrorAction SilentlyContinue
    if ($task -and $task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName 'Starrboard-qBittorrent' -ErrorAction SilentlyContinue
    }
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ieq 'qbittorrent.exe' -and $_.CommandLine -like "*$profileRoot*"
    }
    foreach ($process in $processes) {
        taskkill.exe /PID $process.ProcessId /T /F 2>$null | Out-Null
    }
    if ($processes) {
        Start-Sleep -Seconds 2
        Write-Host 'Restarting the Starrboard qBittorrent profile to activate its Web UI credential.'
    }
}

function Enable-StarrboardQbittorrentStartupTask($username, $password) {
    $profileRoot = Join-Path $env:ProgramData 'Starrboard\qBittorrent'
    $qbExe = Find-FirstExistingPath @(
        "$env:ProgramFiles\qBittorrent\qbittorrent.exe",
        "${env:ProgramFiles(x86)}\qBittorrent\qbittorrent.exe"
    )
    if (-not $qbExe) {
        Write-Warning 'qBittorrent startup task was not created because the executable was not found.'
        return $false
    }

    $taskName = 'Starrboard-qBittorrent'
    $arguments = ('--profile="{0}" --configuration=starrboard --confirm-legal-notice' -f $profileRoot)
    try {
        $action = New-ScheduledTaskAction -Execute $qbExe -Argument $arguments -WorkingDirectory (Split-Path -Parent $qbExe)
        $trigger = New-ScheduledTaskTrigger -AtStartup
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -RunLevel Highest -User 'SYSTEM' -Force | Out-Null

        Stop-StarrboardQbittorrent
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        if (Test-QbittorrentLogin $username $password 10) {
            Write-Host 'qBittorrent background startup task is running and accepted the generated credential.'
            return $true
        }

        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        $diagnostic = if ($taskInfo) { "Last task result: $($taskInfo.LastTaskResult)." } else { 'Task state could not be read.' }
        throw "The qBittorrent background task did not expose a working Web UI. $diagnostic"
    } catch {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Start-Qbittorrent
        Write-Warning "qBittorrent is running for this session, but its automatic startup task could not be verified: $($_.Exception.Message)"
        return $false
    }
}

function Test-QbittorrentLogin($username, $password, $attempts = 1) {
    $script:QbittorrentLoginDiagnostic = 'No response received from qBittorrent.'
    foreach ($attempt in 1..$attempts) {
        if ($attempts -gt 1) { Start-Sleep -Seconds 2 }
        try {
            $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            $login = Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:18080/api/v2/auth/login' -Body @{ username=$username; password=$password } -WebSession $session -TimeoutSec 10
            if ($login.StatusCode -ge 200 -and $login.StatusCode -lt 300) { return $true }
            $script:QbittorrentLoginDiagnostic = "HTTP $($login.StatusCode): $($login.Content)"
        } catch { $script:QbittorrentLoginDiagnostic = $_.Exception.Message }
    }
    return $false
}

function Initialize-QbittorrentWebUi {
    $profileRoot = Join-Path $env:ProgramData 'Starrboard\qBittorrent'
    $configDir = Join-Path $profileRoot 'qBittorrent_starrboard\config'
    $configPath = Join-Path $configDir 'qBittorrent.ini'
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    Grant-QbittorrentProfileAccess
    $lines = if (Test-Path -LiteralPath $configPath) { @(Get-Content -LiteralPath $configPath) } else { @() }
    $hasExistingPassword = $lines | Where-Object { $_ -match '^WebUI\\Password_PBKDF2=' } | Select-Object -First 1
    if ($hasExistingPassword) {
        $existingUsername = $lines | Where-Object { $_ -match '^WebUI\\Username=' } | Select-Object -First 1
        $existingUsername = if ($existingUsername) { ($existingUsername -split '=', 2)[1] } else { 'admin' }
        Start-Qbittorrent
        $runtimePath = Join-Path $env:ProgramData 'Starrboard\data\appsettings.runtime.json'
        $savedUsername = $null
        $savedPassword = $null
        if (Test-Path -LiteralPath $runtimePath) {
            try {
                $runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
                $savedUsername = $runtime.Bot.Qbittorrent.Username
                $savedPassword = $runtime.Bot.Qbittorrent.Password
            } catch { }
        }
        if ($savedPassword -and (Test-QbittorrentLogin $savedUsername $savedPassword 4)) {
            Write-Host 'qBittorrent already has a working Web UI credential saved in Starrboard.'
            Write-Host "STARRBOARD_QBIT_CREDENTIAL|EXISTING|$existingUsername|"
            return
        }

        $managed = $false
        $manifestPath = Join-Path $env:ProgramData 'Starrboard\data\dependency-manifest.json'
        if (Test-Path -LiteralPath $manifestPath) {
            try {
                $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                $managed = [bool]$manifest.Dependencies.qBittorrent.InstalledByStarrboard
            } catch { }
        }
        if (-not $managed) {
            Write-Host 'qBittorrent already has a Web UI password. Existing credentials were preserved.'
            Write-Host 'Enter the existing Web UI username and password in the qBittorrent onboarding step.'
            Write-Host "STARRBOARD_QBIT_CREDENTIAL|EXISTING|$existingUsername|"
            return
        }

        Write-Warning 'The Starrboard-managed qBittorrent credential is not accepted. Regenerating it.'
        $existingProcesses = @(Get-Process qbittorrent -ErrorAction SilentlyContinue)
        $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        $existingProcesses | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }

    $existingProcesses = @(Get-Process qbittorrent -ErrorAction SilentlyContinue)
    $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $existingProcesses | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $randomBytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($randomBytes)
    $passwordChars = foreach ($value in $randomBytes) { $alphabet[$value % $alphabet.Length] }
    $password = -join $passwordChars
    # qBittorrent will not start the Web UI without an initial credential. This
    # bootstrap hash is replaced immediately through qBittorrent's own API.
    $salt = New-Object byte[] 16
    $rng.GetBytes($salt)
    $rng.Dispose()
    $derive = [Security.Cryptography.Rfc2898DeriveBytes]::new($password, $salt, 100000, [Security.Cryptography.HashAlgorithmName]::SHA512)
    try { $hash = $derive.GetBytes(64) } finally { $derive.Dispose() }
    $encoded = '@ByteArray(' + [Convert]::ToBase64String($salt) + ':' + [Convert]::ToBase64String($hash) + ')'

    $settings = @(
        'WebUI\Enabled=true',
        'WebUI\Address=*',
        'WebUI\Port=18080',
        'WebUI\Username=admin',
        "WebUI\Password_PBKDF2=`"$encoded`"",
        'WebUI\LocalHostAuth=false',
        'WebUI\HostHeaderValidation=true'
    )
    $filtered = @($lines | Where-Object { $_ -notmatch '^WebUI\\(Enabled|Address|Port|Username|Password_PBKDF2|LocalHostAuth|HostHeaderValidation)=' })
    $preferencesIndex = [Array]::IndexOf($filtered, '[Preferences]')
    if ($preferencesIndex -lt 0) {
        $filtered += ''
        $filtered += '[Preferences]'
        $filtered += $settings
    } else {
        $before = @($filtered[0..$preferencesIndex])
        $after = if ($preferencesIndex + 1 -lt $filtered.Count) { @($filtered[($preferencesIndex + 1)..($filtered.Count - 1)]) } else { @() }
        $filtered = @($before + $settings + $after)
    }
    Set-Content -LiteralPath $configPath -Value $filtered -Encoding UTF8

    # The vendor installer can leave an interactive qBittorrent process behind.
    # Stop it again immediately before launching the dedicated profile so it
    # cannot retain port 8080 or overwrite a default-profile config.
    $existingProcesses = @(Get-Process qbittorrent -ErrorAction SilentlyContinue)
    $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $existingProcesses | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    Start-Qbittorrent
    $preferencesApplied = $false
    $lastSetupError = $null
    foreach ($attempt in 1..12) {
        Start-Sleep -Seconds 2
        try {
            $preferences = [ordered]@{
                web_ui_username = 'admin'
                web_ui_password = $password
                bypass_local_auth = $false
            } | ConvertTo-Json -Compress
            $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:18080/api/v2/app/setPreferences' -Body @{ json=$preferences } -TimeoutSec 10
            if ($response.StatusCode -eq 200) {
                $preferencesApplied = $true
                break
            }
            $lastSetupError = "HTTP $($response.StatusCode): $($response.Content)"
        } catch {
            $lastSetupError = $_.Exception.Message
        }
    }
    if (-not $preferencesApplied) {
        throw "qBittorrent started, but its Web API did not accept the initial preferences. Config path: $configPath. Last error: $lastSetupError"
    }

    # qBittorrent 5.2.x persists the new password immediately but can retain
    # the bootstrap hash in the current Web UI process. Restarting the exact
    # Starrboard profile makes it reload the persisted credential before test.
    Stop-StarrboardQbittorrent
    Start-Qbittorrent
    if (-not (Test-QbittorrentLogin 'admin' $password 12)) {
        $settingsSummary = @(Get-Content -LiteralPath $configPath -ErrorAction SilentlyContinue | Where-Object {
            $_ -match '^WebUI\\(Enabled|Address|Port|Username|LocalHostAuth|HostHeaderValidation)='
        }) -join '; '
        throw "qBittorrent did not accept its generated Web UI credential. Config path: $configPath. Last login result: $script:QbittorrentLoginDiagnostic. Active settings: $settingsSummary"
    }
    Write-Host 'qBittorrent accepted the generated Web UI credential.'
    Enable-StarrboardQbittorrentStartupTask 'admin' $password | Out-Null

    try {
        $payload = @{ Qbittorrent = @{ Url = 'http://127.0.0.1:18080'; ProcessName = 'qbittorrent'; Username = 'admin'; Password = $password } } | ConvertTo-Json -Depth 4
        Invoke-RestMethod -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:8686/api/config' -ContentType 'application/json' -Body $payload | Out-Null
        Write-Host 'Saved the generated qBittorrent credential in Starrboard runtime configuration.'
    } catch {
        Write-Warning "qBittorrent was configured, but Starrboard could not save the generated credential automatically: $($_.Exception.Message)"
    }

    Write-Host ''
    Write-Host 'qBittorrent Web UI credentials created:'
    Write-Host '  URL: http://127.0.0.1:18080'
    Write-Host '  Username: admin'
    Write-Host 'Change this later in qBittorrent under Tools > Options > Web UI, then update the qBittorrent step in Starrboard.'
    Write-Host "STARRBOARD_QBIT_CREDENTIAL|CREATED|admin|$password"
    Write-Host ''
}

function Install-FlareSolverr {
    $installRoot = Join-Path $env:ProgramData 'Starrboard\FlareSolverr'
    $exe = Join-Path $installRoot 'flaresolverr.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        try {
            $zip = Join-Path $env:TEMP 'flaresolverr_windows_x64.zip'
            Invoke-GitHubAssetDownload 'FlareSolverr/FlareSolverr' 'windows.*x64.*\.zip$' $zip
            if (Test-Path -LiteralPath $installRoot) {
                Rename-Item -LiteralPath $installRoot -NewName ("FlareSolverr.backup.{0:yyyyMMddHHmmss}" -f (Get-Date)) -ErrorAction SilentlyContinue
            }

            New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
            Expand-Archive -LiteralPath $zip -DestinationPath $installRoot -Force
            $exe = Get-ChildItem -Path $installRoot -Recurse -Filter flaresolverr.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
        } catch {
            Write-Warning "FlareSolverr install failed: $($_.Exception.Message)"
        }
    }

    if ($exe -and (Test-Path -LiteralPath $exe)) {
        try {
            $watchScript = Join-Path $installRoot 'Ensure-FlareSolverr.ps1'
            $escapedExe = $exe.Replace("'", "''")
            $watchScriptContent = @'
param(
    [string]$ExecutablePath = '__FLARESOLVERR_EXE__',
    [string]$StartTaskName = 'Starrboard-FlareSolverr',
    [string]$HealthUrl = 'http://127.0.0.1:8191/v1',
    [int]$HealthTimeoutSeconds = 150,
    [int]$StartupTimeoutSeconds = 180,
    [string]$LogPath = (Join-Path $env:ProgramData 'Starrboard\logs\Ensure-FlareSolverr.log')
)

$ErrorActionPreference = 'Stop'

function Write-FlareLog {
    param([string]$Message, [string]$Level = 'INFO')
    $directory = Split-Path -Parent $LogPath
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    Add-Content -LiteralPath $LogPath -Value ("{0:u} [{1}] {2}" -f (Get-Date), $Level, $Message)
}

function Test-FlareSolverr {
    try {
        $body = @{ cmd = 'sessions.list'; maxTimeout = 60000 } | ConvertTo-Json -Compress
        $response = Invoke-RestMethod -Method Post -Uri $HealthUrl -ContentType 'application/json' -Body $body -TimeoutSec $HealthTimeoutSeconds
        return $response.status -eq 'ok'
    } catch {
        return $false
    }
}

function Get-FlareDiagnostics {
    $task = Get-ScheduledTask -TaskName $StartTaskName -ErrorAction SilentlyContinue
    $taskInfo = Get-ScheduledTaskInfo -TaskName $StartTaskName -ErrorAction SilentlyContinue
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'flaresolverr.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.Equals($ExecutablePath, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -ExpandProperty ProcessId)
    $listener = @(Get-NetTCPConnection -LocalPort 8191 -State Listen -ErrorAction SilentlyContinue).Count -gt 0

    return "taskState=$($task.State); taskResult=$($taskInfo.LastTaskResult); processIds=$($processes -join ','); port8191Listening=$listener"
}

function Stop-StaleFlareSolverr {
    $task = Get-ScheduledTask -TaskName $StartTaskName -ErrorAction SilentlyContinue
    if ($task -and $task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $StartTaskName -ErrorAction SilentlyContinue
    }

    Get-CimInstance Win32_Process -Filter "Name = 'flaresolverr.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.Equals($ExecutablePath, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { & taskkill.exe /PID $_.ProcessId /T /F | Out-Null }
}

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "FlareSolverr executable was not found: $ExecutablePath"
}

if (Test-FlareSolverr) {
    exit 0
}

Write-FlareLog ("FlareSolverr API is unavailable. $((Get-FlareDiagnostics))") 'WARN'
Stop-StaleFlareSolverr
Start-Sleep -Seconds 3
Start-ScheduledTask -TaskName $StartTaskName

$deadline = (Get-Date).AddSeconds([Math]::Max(30, $StartupTimeoutSeconds))
do {
    Start-Sleep -Seconds 5
    if (Test-FlareSolverr) {
        Write-FlareLog 'FlareSolverr API recovered.'
        exit 0
    }
} while ((Get-Date) -lt $deadline)

throw "FlareSolverr did not become healthy within $StartupTimeoutSeconds seconds."
'@
            $watchScriptContent.Replace('__FLARESOLVERR_EXE__', $escapedExe) | Set-Content -LiteralPath $watchScript -Encoding UTF8

            $exeDirectory = Split-Path -Parent $exe
            $startScript = Join-Path $exeDirectory 'Start-FlareSolverr.cmd'
            $startScriptContent = @"
@echo off
setlocal EnableExtensions
set "LOGDIR=%ProgramData%\Starrboard\logs"
set "LOG=%LOGDIR%\FlareSolverr-Starrboard.log"
set "LIFECYCLE=%LOGDIR%\FlareSolverr-Starrboard-lifecycle.log"
set "LOG_LEVEL=info"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
>> "%LIFECYCLE%" echo [%DATE% %TIME%] START
cd /d "$exeDirectory"
"$exe" >> "%LOG%" 2>&1
set "EXITCODE=%ERRORLEVEL%"
>> "%LIFECYCLE%" echo [%DATE% %TIME%] EXIT code=%EXITCODE%
exit /b %EXITCODE%
"@
            Set-Content -LiteralPath $startScript -Value $startScriptContent -Encoding ASCII

            $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "' + $startScript + '"') -WorkingDirectory $exeDirectory
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $primarySettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 30) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
            Register-ScheduledTask -TaskName 'Starrboard-FlareSolverr' -Action $action -Trigger $trigger -Settings $primarySettings -RunLevel Highest -User 'SYSTEM' -Force | Out-Null

            $watchArguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $watchScript
            $watchAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $watchArguments
            $watchBoot = New-ScheduledTaskTrigger -AtStartup
            $watchBoot.Delay = 'PT4M'
            $watchPeriodic = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)
            $watchSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 7) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable
            Register-ScheduledTask -TaskName 'Starrboard - Ensure FlareSolverr' -Action $watchAction -Trigger @($watchBoot, $watchPeriodic) -Settings $watchSettings -RunLevel Highest -User 'SYSTEM' -Force | Out-Null

            if (-not (Get-Process flaresolverr -ErrorAction SilentlyContinue)) {
                Start-ScheduledTask -TaskName 'Starrboard-FlareSolverr'
            }

            Write-Host 'FlareSolverr startup and health recovery tasks are configured.'
        } catch {
            Write-Warning "FlareSolverr was installed but the startup task could not be configured: $($_.Exception.Message)"
        }
    } else {
        Write-Warning 'FlareSolverr executable was not found. Download the Windows x64 release manually if needed.'
    }
}

if (-not (Test-Admin)) { throw 'Run this script from an elevated PowerShell prompt.' }

$dependencyStateBefore = @{}
foreach ($dependencyName in @('Radarr','Sonarr','Prowlarr','qBittorrent','FlareSolverr')) {
    $dependencyStateBefore[$dependencyName] = Test-DependencyPresent $dependencyName
}

Write-Host 'Preparing dependency installers...'
$winget = Repair-Winget
if ($winget) {
    Write-Host "winget is available at $winget"
} else {
    Write-Warning 'winget is still unavailable. Starrboard will use direct official downloads where possible.'
}

if (-not (Install-WithWinget 'TeamRadarr.Radarr' 'Radarr')) {
    # Radarr, Sonarr, and Prowlarr installers are Inno Setup (silent: /VERYSILENT). qBittorrent is NSIS (silent: /S).
    Install-Exe 'Radarr' 'https://radarr.servarr.com/v1/update/master/updatefile?arch=x64&installer=true&os=windows&runtime=netcore' 'RadarrInstaller.exe' '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' | Out-Null
}
if (-not (Install-WithWinget 'TeamSonarr.Sonarr' 'Sonarr')) {
    Install-Exe 'Sonarr' 'https://services.sonarr.tv/v1/download/main/latest?version=4&os=windows&arch=x64&installer=true' 'SonarrInstaller.exe' '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' | Out-Null
}
if (-not (Install-WithWinget 'TeamProwlarr.Prowlarr' 'Prowlarr')) {
    Install-Exe 'Prowlarr' 'https://prowlarr.servarr.com/v1/update/master/updatefile?arch=x64&installer=true&os=windows&runtime=netcore' 'ProwlarrInstaller.exe' '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' | Out-Null
}
if (-not (Install-WithWinget 'qBittorrent.qBittorrent' 'qBittorrent')) {
    Install-GitHubExe 'qBittorrent' 'qbittorrent/qBittorrent' 'qbittorrent_(?!.*lt20).*_x64_setup\.exe$' 'qBittorrentInstaller.exe' '/S' | Out-Null
}

# Record ownership before service/configuration work so a later failure can be
# cleanly rolled back by the uninstaller.
Write-DependencyManifest $dependencyStateBefore

Ensure-ArrService 'Radarr' @(
    "$env:ProgramData\Radarr\bin\Radarr.Console.exe",
    "$env:ProgramFiles\Radarr\Radarr.Console.exe",
    "$env:ProgramFiles\Radarr\bin\Radarr.Console.exe"
)
Ensure-ArrService 'Sonarr' @(
    "$env:ProgramData\Sonarr\bin\Sonarr.Console.exe",
    "$env:ProgramFiles\Sonarr\Sonarr.Console.exe",
    "$env:ProgramFiles\Sonarr\bin\Sonarr.Console.exe"
)
Ensure-ArrService 'Prowlarr' @(
    "$env:ProgramData\Prowlarr\bin\Prowlarr.Console.exe",
    "$env:ProgramFiles\Prowlarr\Prowlarr.Console.exe",
    "$env:ProgramFiles\Prowlarr\bin\Prowlarr.Console.exe"
)

Initialize-QbittorrentWebUi
Install-FlareSolverr
Write-DependencyManifest $dependencyStateBefore

Write-Host ''
Write-Host 'Dependency bootstrap completed.'
Write-Host 'Next: return to Starrboard onboarding. Installed services and local API keys will be discovered automatically.'
""";
    }

    private static readonly IReadOnlyList<BootstrapDependency> Dependencies = new[]
    {
        new BootstrapDependency(
            "radarr",
            "Radarr",
            "Movie automation",
            "TeamRadarr.Radarr",
            "Radarr",
            "Radarr",
            "http://127.0.0.1:7878",
            "https://radarr.video/",
            "https://wiki.servarr.com/radarr",
            "Install as a Windows Service. After first launch, open Settings > General to copy the API key."),
        new BootstrapDependency(
            "sonarr",
            "Sonarr",
            "TV automation",
            "TeamSonarr.Sonarr",
            "Sonarr",
            "Sonarr",
            "http://127.0.0.1:8989",
            "https://sonarr.tv/",
            "https://wiki.servarr.com/sonarr",
            "Install as a Windows Service. Use Settings > General for the API key and Settings > Media Management for root folders."),
        new BootstrapDependency(
            "prowlarr",
            "Prowlarr",
            "Indexer manager",
            "TeamProwlarr.Prowlarr",
            "Prowlarr",
            "Prowlarr",
            "http://127.0.0.1:9696",
            "https://prowlarr.com/",
            "https://wiki.servarr.com/prowlarr",
            "Add indexers here first, then add Radarr/Sonarr under Settings > Apps so searches sync to the Arr suite."),
        new BootstrapDependency(
            "qbittorrent",
            "qBittorrent",
            "Download client",
            "qBittorrent.qBittorrent",
            null,
            "qbittorrent",
            "http://127.0.0.1:18080",
            "https://www.qbittorrent.org/download",
            "https://github.com/qbittorrent/qBittorrent/wiki/WebUI-usage",
            "Enable Tools > Options > Web UI before Radarr/Sonarr can connect to it."),
        new BootstrapDependency(
            "flaresolverr",
            "FlareSolverr",
            "Cloudflare indexer proxy",
            null,
            null,
            "flaresolverr",
            "http://127.0.0.1:8191",
            "https://github.com/FlareSolverr/FlareSolverr/releases",
            "https://github.com/FlareSolverr/FlareSolverr",
            "Use this only for Prowlarr indexers that need Cloudflare/DDOS challenge solving. Add it in Prowlarr under Settings > Indexers > FlareSolverr proxy.")
    };
}

public static class ServiceAccountScript
{
    public static string Build(BotOptions options)
    {
        var services = new List<string>();
        if (options.ArrServices.ApplyToRadarr)
        {
            services.Add("Radarr");
        }

        if (options.ArrServices.ApplyToSonarr)
        {
            services.Add("Sonarr");
        }

        if (options.ArrServices.ApplyToProwlarr)
        {
            services.Add("Prowlarr");
        }

        if (options.ArrServices.ApplyToStarrboard)
        {
            services.Add("Starrboard");
        }

        var serviceList = string.Join(",", services.Select(service => $"'{service}'"));
        var rootPaths = new[]
            {
                options.Radarr.RootFolderPath,
                options.Sonarr.RootFolderPath,
                StarrboardPaths.Data,
                GetLogDirectory(options)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(EscapePowerShellSingleQuotedString);
        var pathList = string.Join(",", rootPaths.Select(path => $"'{path}'"));

        return $$"""
# Starrboard Arr service account helper
# Run from an elevated PowerShell prompt.
$ErrorActionPreference = 'Stop'
$Services = @({{serviceList}})
$LocalPermissionPaths = @({{pathList}})

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-AccountName {
    param([string] $Name)

    $trimmed = ''
    if ($null -ne $Name) { $trimmed = $Name.Trim() }
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw 'A service account name is required. Example: .\plexsvc, SERVER\plexsvc, or DOMAIN\plexsvc.'
    }

    if ($trimmed.StartsWith('.\')) {
        $trimmed = "$env:COMPUTERNAME\$($trimmed.Substring(2))"
    } elseif ($trimmed -notmatch '\\' -and $trimmed -notmatch '@') {
        $trimmed = "$env:COMPUTERNAME\$trimmed"
    }

    try {
        $account = [System.Security.Principal.NTAccount]::new($trimmed)
        $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
        return [pscustomobject]@{ Name = $trimmed; Sid = $sid }
    } catch {
        throw "Could not resolve service account '$trimmed' to a Windows SID. Confirm the local/domain account exists."
    }
}

Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class StarrboardNativeLogon
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public LSA_UNICODE_STRING ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool LogonUser(string username, string domain, string password, int logonType, int logonProvider, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes, uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LSA_UNICODE_STRING[] userRights, uint countOfRights);

    [DllImport("advapi32.dll")]
    public static extern uint LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll")]
    public static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool CloseHandle(IntPtr handle);

    public static void GrantAccountRight(string sidValue, string right)
    {
        SecurityIdentifier sid = new SecurityIdentifier(sidValue);
        byte[] sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        LSA_OBJECT_ATTRIBUTES attributes = new LSA_OBJECT_ATTRIBUTES();
        attributes.Length = (uint)Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES));

        IntPtr policy;
        const uint POLICY_CREATE_ACCOUNT = 0x00000010;
        uint result = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_ACCOUNT, out policy);
        if (result != 0)
        {
            throw new Win32Exception((int)LsaNtStatusToWinError(result));
        }

        LSA_UNICODE_STRING[] rights = new[] { CreateLsaString(right) };
        try
        {
            result = LsaAddAccountRights(policy, sidBytes, rights, 1);
            if (result != 0)
            {
                throw new Win32Exception((int)LsaNtStatusToWinError(result));
            }
        }
        finally
        {
            if (rights[0].Buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(rights[0].Buffer);
            }

            LsaClose(policy);
        }
    }

    private static LSA_UNICODE_STRING CreateLsaString(string value)
    {
        if (value.Length > 32767)
        {
            throw new ArgumentException("LSA string is too long.", "value");
        }

        return new LSA_UNICODE_STRING
        {
            Buffer = Marshal.StringToHGlobalUni(value),
            Length = (ushort)(value.Length * 2),
            MaximumLength = (ushort)((value.Length + 1) * 2)
        };
    }
}
'@

function Test-ServiceCredential {
    param(
        [string] $AccountName,
        [string] $Password,
        [int] $LogonType = 3,
        [string] $Label = 'credential'
    )

    $domain = $null
    $user = $AccountName
    if ($AccountName -match '^([^\\]+)\\(.+)$') {
        $domain = $matches[1]
        $user = $matches[2]
    }

    $token = [IntPtr]::Zero
    $ok = [StarrboardNativeLogon]::LogonUser($user, $domain, $Password, $LogonType, 0, [ref]$token)
    if ($token -ne [IntPtr]::Zero) {
        [StarrboardNativeLogon]::CloseHandle($token) | Out-Null
    }

    if (-not $ok) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $message = [ComponentModel.Win32Exception]::new($code).Message
        throw "$Label validation failed for $AccountName. Windows reported: $message"
    }
}

function Grant-ServiceLogonRight {
    param(
        [string] $AccountName,
        [string] $Sid
    )

    Write-Host "Granting Log on as a service to $AccountName"
    [StarrboardNativeLogon]::GrantAccountRight($Sid, 'SeServiceLogonRight')
}

function Test-RealLocalPath {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path.StartsWith('\\')) { return $false }
    if ($Path -like 'PUT_*_HERE') { return $false }
    return Test-Path -LiteralPath $Path
}

if (-not (Test-Admin)) { throw 'Run this script from an elevated PowerShell prompt.' }
if ($Services.Count -eq 0) { throw 'No Windows services were selected. Re-open Starrboard Configuration, select at least one service, and run setup again.' }
$usernamePrompt = 'Service account username. Use .\name, COMPUTER\name, DOMAIN\name, user@domain, or just name for a local account'
$enteredName = $env:STARRBOARD_SERVICE_ACCOUNT
if ([string]::IsNullOrWhiteSpace($enteredName)) {
    $enteredName = Read-Host $usernamePrompt
}
$accountInfo = Resolve-AccountName $enteredName
$plainPassword = $env:STARRBOARD_SERVICE_PASSWORD
if ([string]::IsNullOrEmpty($plainPassword)) {
    $securePassword = Read-Host -AsSecureString "Password for $($accountInfo.Name)"
    $credential = [pscredential]::new($accountInfo.Name, $securePassword)
    $plainPassword = $credential.GetNetworkCredential().Password
}
$env:STARRBOARD_SERVICE_PASSWORD = $null
$resolvedName = $accountInfo.Name
$resolvedSid = $accountInfo.Sid
Test-ServiceCredential $resolvedName $plainPassword 3 'Account credential'
Grant-ServiceLogonRight $resolvedName $resolvedSid
Test-ServiceCredential $resolvedName $plainPassword 5 'Service logon'

if ($resolvedName -match '\\Administrator$') {
    Write-Warning 'Using the built-in Administrator account for services is not recommended. A dedicated local service account is safer.'
}

Write-Host "This script will configure these services to run as ${resolvedName}:"
$Services | ForEach-Object { Write-Host "  $_" }
Write-Host ''
Write-Host 'The password is passed only to this elevated process and is not written to Starrboard config or this script.'
Write-Host "Resolved account SID: $resolvedSid"

foreach ($path in $LocalPermissionPaths) {
    if ([string]::IsNullOrWhiteSpace($path)) { continue }
    if ($path.StartsWith('\\')) {
        Write-Warning "Skipping local ACL grant for UNC path $path. Grant share and NTFS access on the NAS or file server."
        continue
    }

    if (Test-RealLocalPath $path) {
        Write-Host "Granting modify access on $path"
        $grant = "*${resolvedSid}:(OI)(CI)M"
        icacls $path /grant $grant | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "icacls failed while granting access to $path." }
    } else {
        Write-Warning "Skipping local ACL grant because the path is not configured or does not exist: $path"
    }
}

foreach ($serviceName in $Services) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Warning "$serviceName service does not exist. Install it as a Windows service first."
        continue
    }

    Write-Host "Configuring $serviceName to run as $resolvedName"
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', '00:00:30')
    }

    sc.exe config $serviceName obj= $resolvedName password= $plainPassword | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failed while configuring $serviceName." }
    Set-Service -Name $serviceName -StartupType Automatic
    try {
        Start-Service -Name $serviceName
    } catch {
        $recent = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = (Get-Date).AddMinutes(-3) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -like "*$serviceName*" } |
            Select-Object -First 1 -ExpandProperty Message
        if ($recent) {
            throw "Configured $serviceName, but it did not start. Recent Service Control Manager event: $recent"
        }

        throw
    }
    Write-Host "$serviceName is running."
}

Write-Host ''
Write-Host 'Done. Re-open Starrboard and validate onboarding. For NAS/TrueNAS media roots, confirm the same account has SMB share and dataset permissions.'
""";
    }

    private static string EscapePowerShellSingleQuotedString(string? value) =>
        (value ?? "").Replace("'", "''", StringComparison.Ordinal);

    private static string GetLogDirectory(BotOptions options)
    {
        var directory = Path.GetDirectoryName(options.Logs.Path);
        return string.IsNullOrWhiteSpace(directory) ? StarrboardPaths.Logs : directory;
    }
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    public static readonly string RuntimeConfigPath = StarrboardPaths.RuntimeConfig;
    private static readonly string PublishedConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    private readonly string _configPath = RuntimeConfigPath;

    public static void EnsureRuntimeConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimeConfigPath)!);
        if (File.Exists(RuntimeConfigPath))
        {
            return;
        }

        var source = File.Exists(PublishedConfigPath)
            ? PublishedConfigPath
            : Path.Combine(AppContext.BaseDirectory, "appsettings.template.json");
        if (!File.Exists(source))
        {
            return;
        }

        File.Copy(source, RuntimeConfigPath, overwrite: false);
    }

    public JsonObject GetRedactedConfig()
    {
        var root = ReadConfig();
        var bot = GetBot(root);
        Redact(bot, "DiscordToken");
        Redact(GetObject(bot, "Radarr"), "ApiKey");
        Redact(GetObject(bot, "Sonarr"), "ApiKey");
        Redact(GetObject(bot, "Prowlarr"), "ApiKey");
        Redact(GetObject(bot, "Qbittorrent"), "Password");
        StringifyScalar(bot, "GuildId");
        return root;
    }

    public BotOptions GetBotOptions()
    {
        var root = ReadConfig();
        var bot = GetBot(root);
        return bot.Deserialize<BotOptions>(ReadOptions) ?? new BotOptions();
    }

    public void SaveConfig(JsonObject patch)
    {
        var root = ReadConfig();
        var bot = GetBot(root);
        ApplyAllowedBotPatch(bot, patch);
        Normalize(bot);
        bot["OnboardingCompleted"] = false;
        BackupConfig();
        AtomicWrite(root);
    }

    public void MarkOnboardingComplete()
    {
        var root = ReadConfig();
        GetBot(root)["OnboardingCompleted"] = true;
        BackupConfig();
        AtomicWrite(root);
    }

    private JsonObject ReadConfig()
    {
        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException("Starrboard config was not found.", _configPath);
        }

        return JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
    }

    private void BackupConfig()
    {
        var backupDir = Path.Combine(StarrboardPaths.Data, "config-backups");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(_configPath, Path.Combine(backupDir, $"appsettings.json.bak-{stamp}"), overwrite: true);
    }

    private void AtomicWrite(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var tempPath = _configPath + ".tmp";
        File.WriteAllText(tempPath, root.ToJsonString(JsonOptions));
        File.Copy(tempPath, _configPath, overwrite: true);
        File.Delete(tempPath);
    }

    private static JsonObject GetBot(JsonObject root)
    {
        if (root["Bot"] is not JsonObject bot)
        {
            bot = new JsonObject();
            root["Bot"] = bot;
        }

        return bot;
    }

    private static JsonObject GetObject(JsonObject parent, string name)
    {
        if (parent[name] is not JsonObject obj)
        {
            obj = new JsonObject();
            parent[name] = obj;
        }

        return obj;
    }

    private static void ApplyAllowedBotPatch(JsonObject target, JsonObject patch)
    {
        CopyScalar(target, patch, "DiscordToken");
        CopyScalar(target, patch, "GuildId");
        CopyScalar(target, patch, "CompletionCheckSeconds");
        CopyScalar(target, patch, "SessionTtlMinutes");
        CopyScalar(target, patch, "DmMaxAttempts");
        CopySection(target, patch, "Radarr", "Url", "ApiKey", "QualityProfileId", "RootFolderPath", "MinimumAvailability", "SearchOnAdd");
        CopySection(target, patch, "Sonarr", "Url", "ApiKey", "QualityProfileId", "RootFolderPath", "SeriesType", "SearchOnAdd");
        CopySection(target, patch, "Prowlarr", "Url", "ApiKey");
        CopySection(target, patch, "FlareSolverr", "Url");
        CopySection(target, patch, "Qbittorrent", "Url", "ProcessName", "Username", "Password");
        CopySection(target, patch, "ArrServices", "ApplyToRadarr", "ApplyToSonarr", "ApplyToProwlarr", "ApplyToStarrboard");
        CopySection(target, patch, "Logs", "Path");
    }

    private static void CopySection(JsonObject target, JsonObject patch, string sectionName, params string[] allowedNames)
    {
        if (patch[sectionName] is not JsonObject sectionPatch)
        {
            return;
        }

        var section = GetObject(target, sectionName);
        foreach (var name in allowedNames)
        {
            CopyScalar(section, sectionPatch, name);
        }
    }

    private static void CopyScalar(JsonObject target, JsonObject patch, string name)
    {
        if (!patch.TryGetPropertyValue(name, out var value))
        {
            return;
        }

        if (IsSecret(name) && IsEmptyOrMasked(value))
        {
            return;
        }

        target[name] = value?.DeepClone();
    }

    private static void Normalize(JsonObject bot)
    {
        bot["GuildId"] = ClampLong(bot["GuildId"], 0, long.MaxValue);
        bot["CompletionCheckSeconds"] = ClampInt(bot["CompletionCheckSeconds"], 30, 86400, 300);
        bot["SessionTtlMinutes"] = ClampInt(bot["SessionTtlMinutes"], 5, 1440, 60);
        bot["DmMaxAttempts"] = ClampInt(bot["DmMaxAttempts"], 1, 200, 25);

        NormalizeUrl(GetObject(bot, "Radarr"), "Url");
        NormalizeUrl(GetObject(bot, "Sonarr"), "Url");
        NormalizeUrl(GetObject(bot, "Prowlarr"), "Url");
        NormalizeUrl(GetObject(bot, "FlareSolverr"), "Url");
        NormalizeUrl(GetObject(bot, "Qbittorrent"), "Url");
        var qbittorrent = GetObject(bot, "Qbittorrent");
        if (string.IsNullOrWhiteSpace(qbittorrent["Username"]?.GetValue<string>())) qbittorrent["Username"] = "admin";
        NormalizeLogPath(GetObject(bot, "Logs"));
    }

    private static int ClampInt(JsonNode? value, int min, int max, int fallback)
    {
        try
        {
            var parsed = value?.GetValue<int>() ?? fallback;
            return Math.Clamp(parsed, min, max);
        }
        catch
        {
            return fallback;
        }
    }

    private static long ClampLong(JsonNode? value, long min, long max)
    {
        try
        {
            var parsed = value switch
            {
                null => min,
                JsonValue jsonValue when jsonValue.TryGetValue<long>(out var number) => number,
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) && long.TryParse(text, out var number) => number,
                _ => min
            };
            return Math.Min(Math.Max(parsed, min), max);
        }
        catch
        {
            return min;
        }
    }

    private static void NormalizeUrl(JsonObject section, string name)
    {
        var value = section[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            section[name] = "";
        }
    }

    private static void NormalizeLogPath(JsonObject section)
    {
        var value = section["Path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            section["Path"] = StarrboardPaths.LogFile;
        }
    }

    private static void Redact(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var value) && !IsEmptyOrMasked(value))
        {
            obj[name] = "********";
        }
    }

    private static void StringifyScalar(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var value) || value == null)
        {
            return;
        }

        obj[name] = value.ToString();
    }

    private static bool IsSecret(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apiKey", StringComparison.Ordinal);

    private static bool IsEmptyOrMasked(JsonNode? value)
    {
        var text = value?.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) || text.All(ch => ch == '*');
    }
}

public sealed record StackStatus(
    string Name,
    string OverallStatus,
    DateTimeOffset CheckedAt,
    bool OnboardingCompleted,
    StackLinks Links,
    IReadOnlyList<HealthCheckResult> Checks);

public sealed record StackLinks(
    string Radarr,
    string Sonarr,
    string Prowlarr,
    string FlareSolverr,
    string Qbittorrent,
    string Starrboard);

public sealed record HealthCheckResult(
    string Name,
    string Status,
    string Message,
    string? Link,
    string? Version);

public sealed record ArrHealthIssueSummary(
    int ActionableCount,
    int IgnoredCount,
    bool HasError);

public sealed record OnboardingStatus(
    DateTimeOffset CheckedAt,
    bool Ready,
    string Summary,
    IReadOnlyList<OnboardingStep> Steps,
    IReadOnlyList<DependencyItem> Dependencies);

public sealed record OnboardingStep(
    string Id,
    string Name,
    string Status,
    string Message);

public sealed record DependencyItem(
    string Id,
    string Name,
    string Category,
    string Status,
    bool Required,
    string Message,
    string? DetectedVersion,
    string? ConsoleUrl,
    string? DownloadUrl,
    string? SetupUrl,
    IReadOnlyList<string> Actions);

public sealed record BootstrapDependency(
    string Id,
    string Name,
    string Category,
    string? WingetPackageId,
    string? ServiceName,
    string ProcessName,
    string ConsoleUrl,
    string DownloadUrl,
    string SetupUrl,
    string Guidance);

public sealed record ArrReference(
    IReadOnlyList<ArrQualityProfile> QualityProfiles,
    IReadOnlyList<ArrRootFolder> RootFolders,
    string? Message);

public sealed record ArrQualityProfile(
    int Id,
    string Name);

public sealed record ArrRootFolder(
    string Path,
    long FreeSpace);

public static class AdminPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Starrboard Console</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f7f9;
      --panel: #ffffff;
      --ink: #17202a;
      --muted: #647184;
      --line: #d8dee8;
      --ok: #168a54;
      --warn: #ad6a00;
      --err: #ba2636;
      --accent: #225ea8;
      --accent-2: #1a7f73;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--ink);
      font: 14px/1.45 "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif;
    }
    header {
      min-height: 92px;
      background: #182230;
      color: #fff;
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 22px clamp(18px, 4vw, 48px);
      gap: 20px;
    }
    h1 { margin: 0; font-size: 30px; font-weight: 700; letter-spacing: 0; }
    h2 { margin: 0 0 14px; font-size: 18px; letter-spacing: 0; }
    h3 { margin: 0; font-size: 15px; letter-spacing: 0; }
    main {
      width: min(1280px, calc(100vw - 32px));
      margin: 24px auto 48px;
      display: grid;
      grid-template-columns: 1.25fr .75fr;
      gap: 18px;
    }
    section, .panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }
    .status-pill {
      min-width: 110px;
      text-align: center;
      border-radius: 999px;
      padding: 7px 12px;
      font-weight: 700;
      background: #eef3f8;
      color: var(--ink);
    }
    .ok { color: var(--ok); }
    .warning { color: var(--warn); }
    .error { color: var(--err); }
    .grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }
    .check {
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 13px;
      min-height: 104px;
      display: grid;
      gap: 8px;
    }
    .check-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .dot {
      width: 11px;
      height: 11px;
      border-radius: 50%;
      display: inline-block;
      background: var(--muted);
      flex: 0 0 auto;
    }
    .dot.ok { background: var(--ok); }
    .dot.warning { background: var(--warn); }
    .dot.error { background: var(--err); }
    .muted { color: var(--muted); }
    .message {
      color: var(--muted);
      min-height: 40px;
      overflow-wrap: anywhere;
    }
    a.button, button {
      border: 1px solid var(--accent);
      color: var(--accent);
      background: #fff;
      border-radius: 6px;
      min-height: 36px;
      padding: 7px 12px;
      text-decoration: none;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      font-weight: 600;
      cursor: pointer;
    }
    button.primary {
      background: var(--accent);
      color: #fff;
    }
    button.secondary {
      border-color: var(--accent-2);
      color: var(--accent-2);
    }
    .actions {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      align-items: center;
    }
    form {
      display: grid;
      gap: 18px;
    }
    fieldset {
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 14px;
      margin: 0;
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }
    legend {
      padding: 0 6px;
      font-weight: 700;
    }
    label {
      display: grid;
      gap: 5px;
      color: var(--muted);
    }
    input {
      width: 100%;
      min-height: 36px;
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 7px 9px;
      font: inherit;
      color: var(--ink);
      background: #fff;
    }
    .full { grid-column: 1 / -1; }
    .side {
      display: grid;
      gap: 18px;
      align-content: start;
    }
    .links {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
    }
    .steps {
      display: grid;
      gap: 9px;
      counter-reset: step;
    }
    .step {
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 10px 11px;
      display: grid;
      gap: 3px;
    }
    .step::before {
      counter-increment: step;
      content: counter(step) ". " attr(data-title);
      font-weight: 700;
      color: var(--ink);
    }
    .toast {
      position: fixed;
      right: 18px;
      bottom: 18px;
      background: #182230;
      color: #fff;
      border-radius: 8px;
      padding: 12px 14px;
      display: none;
      max-width: 360px;
    }
    @media (max-width: 920px) {
      main { grid-template-columns: 1fr; }
      .grid, fieldset, .links { grid-template-columns: 1fr; }
      header { align-items: flex-start; flex-direction: column; }
    }
  </style>
</head>
<body>
  <header>
    <div>
      <h1>Starrboard Console</h1>
      <div class="muted" id="checkedAt">Loading stack status</div>
    </div>
    <div class="status-pill" id="overall">checking</div>
  </header>
  <main>
    <div class="panel">
      <div class="actions" style="justify-content: space-between; margin-bottom: 14px;">
        <h2>Health</h2>
        <button id="refresh" class="secondary" type="button">Refresh</button>
      </div>
      <div id="checks" class="grid"></div>
    </div>
    <div class="side">
      <section>
        <h2>Stack Consoles</h2>
        <div id="links" class="links"></div>
      </section>
      <section>
        <h2>Onboarding</h2>
        <div class="steps">
          <div class="step" data-title="Discord"><span class="muted">Bot token and guild command registration.</span></div>
          <div class="step" data-title="Arr APIs"><span class="muted">Radarr, Sonarr, and Prowlarr URLs and keys.</span></div>
          <div class="step" data-title="Paths"><span class="muted">Quality profiles and media root folders.</span></div>
          <div class="step" data-title="Solvers"><span class="muted">FlareSolverr and qBittorrent reachability.</span></div>
          <div class="step" data-title="Health"><span class="muted">Checks pass before user requests are enabled.</span></div>
        </div>
        <div class="actions" style="margin-top: 14px;">
          <button id="completeOnboarding" type="button">Mark Complete</button>
        </div>
      </section>
    </div>
    <section class="full">
      <h2>Configuration</h2>
      <form id="configForm">
        <fieldset>
          <legend>Discord</legend>
          <label class="full">Bot Token<input name="DiscordToken" type="password" autocomplete="off" placeholder="Leave blank to keep current token"></label>
          <label>Guild ID<input name="GuildId" inputmode="numeric"></label>
          <label>Completion Check Seconds<input name="CompletionCheckSeconds" inputmode="numeric"></label>
        </fieldset>
        <fieldset data-prefix="Radarr">
          <legend>Radarr</legend>
          <label>URL<input name="Url"></label>
          <label>API Key<input name="ApiKey" type="password" autocomplete="off" placeholder="Leave blank to keep current key"></label>
          <label>Quality Profile ID<input name="QualityProfileId" inputmode="numeric"></label>
          <label>Minimum Availability<input name="MinimumAvailability"></label>
          <label class="full">Root Folder Path<input name="RootFolderPath"></label>
        </fieldset>
        <fieldset data-prefix="Sonarr">
          <legend>Sonarr</legend>
          <label>URL<input name="Url"></label>
          <label>API Key<input name="ApiKey" type="password" autocomplete="off" placeholder="Leave blank to keep current key"></label>
          <label>Quality Profile ID<input name="QualityProfileId" inputmode="numeric"></label>
          <label>Series Type<input name="SeriesType"></label>
          <label class="full">Root Folder Path<input name="RootFolderPath"></label>
        </fieldset>
        <fieldset data-prefix="Prowlarr">
          <legend>Prowlarr</legend>
          <label>URL<input name="Url"></label>
          <label>API Key<input name="ApiKey" type="password" autocomplete="off" placeholder="Leave blank to keep current key"></label>
        </fieldset>
        <fieldset data-prefix="FlareSolverr">
          <legend>FlareSolverr</legend>
          <label class="full">URL<input name="Url"></label>
        </fieldset>
        <fieldset data-prefix="Qbittorrent">
          <legend>qBittorrent</legend>
          <label>URL<input name="Url"></label>
          <label>Process Name<input name="ProcessName"></label>
        </fieldset>
        <div class="actions">
          <button class="primary" type="submit">Save Configuration</button>
          <span class="muted" id="saveNote"></span>
        </div>
      </form>
    </section>
  </main>
  <div class="toast" id="toast"></div>
  <script>
    const checks = document.getElementById('checks');
    const links = document.getElementById('links');
    const overall = document.getElementById('overall');
    const checkedAt = document.getElementById('checkedAt');
    const toast = document.getElementById('toast');

    function showToast(message) {
      toast.textContent = message;
      toast.style.display = 'block';
      setTimeout(() => toast.style.display = 'none', 4500);
    }

    function setValue(selector, value) {
      const el = document.querySelector(selector);
      if (el && value !== undefined && value !== null) el.value = value;
    }

    function collectFieldset(prefix) {
      const result = {};
      document.querySelectorAll(`fieldset[data-prefix="${prefix}"] input`).forEach(input => {
        result[input.name] = input.inputMode === 'numeric' ? Number(input.value || 0) : input.value;
      });
      return result;
    }

    async function loadConfig() {
      const root = await fetch('/api/config').then(r => r.json());
      const cfg = root.Bot || {};
      setValue('input[name="GuildId"]', cfg.GuildId || 0);
      setValue('input[name="CompletionCheckSeconds"]', cfg.CompletionCheckSeconds || 300);
      for (const name of ['Radarr','Sonarr','Prowlarr','FlareSolverr','Qbittorrent']) {
        const section = cfg[name] || {};
        document.querySelectorAll(`fieldset[data-prefix="${name}"] input`).forEach(input => {
          if ((input.name === 'ApiKey' || input.name === 'DiscordToken') && section[input.name] === '********') return;
          if (section[input.name] !== undefined && section[input.name] !== null) input.value = section[input.name];
        });
      }
    }

    async function loadStatus() {
      const data = await fetch('/api/status').then(r => r.json());
      overall.textContent = data.overallStatus;
      overall.className = `status-pill ${data.overallStatus}`;
      checkedAt.textContent = `Checked ${new Date(data.checkedAt).toLocaleString()}`;
      checks.innerHTML = '';
      for (const item of data.checks) {
        const card = document.createElement('div');
        card.className = 'check';
        card.innerHTML = `
          <div class="check-head"><h3>${item.name}</h3><span class="dot ${item.status}"></span></div>
          <div class="message">${item.message || ''}</div>
          <div class="actions">${item.link ? `<a class="button" href="${item.link}" target="_blank" rel="noreferrer">Open</a>` : ''}<span class="muted">${item.version || ''}</span></div>
        `;
        checks.appendChild(card);
      }
      links.innerHTML = '';
      for (const [name, href] of Object.entries(data.links || {})) {
        if (!href) continue;
        const a = document.createElement('a');
        a.className = 'button';
        a.href = href;
        a.target = '_blank';
        a.rel = 'noreferrer';
        a.textContent = name;
        links.appendChild(a);
      }
    }

    document.getElementById('refresh').addEventListener('click', loadStatus);
    document.getElementById('completeOnboarding').addEventListener('click', async () => {
      const response = await fetch('/api/onboarding/complete', { method: 'POST' });
      if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        showToast(error.error || 'Onboarding is not ready.');
        return;
      }
      showToast('Onboarding saved.');
      loadStatus();
    });
    document.getElementById('configForm').addEventListener('submit', async event => {
      event.preventDefault();
      const payload = {
        DiscordToken: document.querySelector('input[name="DiscordToken"]').value,
        GuildId: document.querySelector('input[name="GuildId"]').value.replace(/\D/g, '') || '0',
        CompletionCheckSeconds: Number(document.querySelector('input[name="CompletionCheckSeconds"]').value || 300),
        Radarr: collectFieldset('Radarr'),
        Sonarr: collectFieldset('Sonarr'),
        Prowlarr: collectFieldset('Prowlarr'),
        FlareSolverr: collectFieldset('FlareSolverr'),
        Qbittorrent: collectFieldset('Qbittorrent')
      };
      const response = await fetch('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!response.ok) {
        showToast('Configuration save failed.');
        return;
      }
      document.getElementById('saveNote').textContent = 'Saved. Restart Starrboard to apply service settings.';
      showToast('Configuration saved.');
      loadStatus();
    });

    loadConfig().then(loadStatus).catch(err => showToast(err.message));
  </script>
</body>
</html>
""";
}

