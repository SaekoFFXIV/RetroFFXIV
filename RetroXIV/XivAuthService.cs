using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RetroXIV;

// XIVAuth OAuth2 device-code flow for FFXIV character identity.
// The plugin requests a device code, the user authorizes in a browser,
// and we poll for the access token. The character's persistent_key
// becomes the stable player identity for netplay and streaming.
public sealed class XivAuthService : IDisposable
{
    private const string BaseUrl = "https://xivauth.net";
    // The OAuth client ID (not the app ID) of the RetroFFXIV client on xivauth.net.
    private const string ClientId = "rXPG4dUdHmUcDJP5U55YHLxIChFQ19hozifpolJdd_0";
    // character / character:all / character:manage are mutually exclusive on
    // XIVAuth; the plugin reads the character list, so it needs character:all.
    private const string Scope = "user character:all refresh";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Configuration config;
    private readonly Action<string> log;
    private readonly Action saveConfig;
    private CancellationTokenSource? pollCts;

    public XivAuthService(Configuration config, Action<string> log, Action saveConfig)
    {
        this.config = config;
        this.log = log;
        this.saveConfig = saveConfig;
    }

    // --- State ---

    public bool IsLoggedIn =>
        !string.IsNullOrEmpty(config.XivAuthAccessToken) &&
        !string.IsNullOrEmpty(config.PlayerPersistentKey);

    public bool IsPolling { get; private set; }
    public string DeviceCode { get; private set; } = string.Empty;
    public string UserCode { get; private set; } = string.Empty;
    public string VerificationUrl { get; private set; } = string.Empty;
    public string LoginUrl { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;

    public event Action? StateChanged;

    private void Notify() => StateChanged?.Invoke();

    // --- Device code flow ---

    public async Task StartLoginAsync()
    {
        Error = string.Empty;
        Status = "Requesting device code...";
        Notify();

        try
        {
            var body = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
            {
                new("client_id", ClientId),
                new("scope", Scope),
            });

            // XIVAuth's doorkeeper fork mounts the device-code endpoint at
            // /oauth/authorize_device, not the RFC's /device/code path.
            var response = await Http.PostAsync($"{BaseUrl}/oauth/authorize_device", body);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Error = $"Device code request failed ({(int)response.StatusCode}): {json}";
                Status = string.Empty;
                log(Error);
                Notify();
                return;
            }

            var device = JsonSerializer.Deserialize<DeviceCodeResponse>(json);
            if (device == null)
            {
                Error = "Failed to parse device code response.";
                Status = string.Empty;
                Notify();
                return;
            }

            DeviceCode = device.DeviceCode;
            UserCode = device.UserCode;
            VerificationUrl = device.VerificationUri;
            // The approval page pre-fills the code when given user_code, so open
            // verification_uri_complete (or build the equivalent) automatically.
            LoginUrl = !string.IsNullOrEmpty(device.VerificationUriComplete)
                ? device.VerificationUriComplete
                : $"{device.VerificationUri}?user_code={Uri.EscapeDataString(device.UserCode)}";
            Status = $"Approve code {UserCode} in your browser.";
            IsPolling = true;
            Notify();

            log($"XIVAuth: device code issued, user_code={UserCode}");

            try
            {
                Dalamud.Utility.Util.OpenLink(LoginUrl);
            }
            catch (Exception ex)
            {
                log($"XIVAuth: could not open browser automatically: {ex.Message}");
            }

            pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollForTokenAsync(device.Interval, pollCts.Token));
        }
        catch (Exception ex)
        {
            Error = $"Login failed: {ex.Message}";
            Status = string.Empty;
            log(Error);
            Notify();
        }
    }

    private async Task PollForTokenAsync(int interval, CancellationToken token)
    {
        var delay = Math.Max(interval, 3) * 1000;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(delay, token);

                var body = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
                {
                    new("grant_type", DeviceGrantType),
                    new("device_code", DeviceCode),
                    new("client_id", ClientId),
                });

                var response = await Http.PostAsync($"{BaseUrl}/oauth/token", body);
                var json = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
                    if (tokenResponse?.AccessToken != null)
                    {
                        await OnTokenReceived(tokenResponse);
                        return;
                    }
                }

                var error = JsonSerializer.Deserialize<ErrorResponse>(json);
                switch (error?.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        delay += 2000;
                        continue;
                    case "expired_token":
                        Error = "Device code expired. Try logging in again.";
                        Status = string.Empty;
                        IsPolling = false;
                        log("XIVAuth: device code expired");
                        Notify();
                        return;
                    case "access_denied":
                        Error = "Authorization was denied.";
                        Status = string.Empty;
                        IsPolling = false;
                        log("XIVAuth: authorization denied");
                        Notify();
                        return;
                    default:
                        Error = $"Token error: {error?.Error ?? json}";
                        Status = string.Empty;
                        IsPolling = false;
                        log(Error);
                        Notify();
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = $"Polling failed: {ex.Message}";
            Status = string.Empty;
            IsPolling = false;
            log(Error);
            Notify();
        }
    }

    private async Task OnTokenReceived(TokenResponse tokenResponse)
    {
        config.XivAuthAccessToken = tokenResponse.AccessToken ?? string.Empty;
        config.XivAuthRefreshToken = tokenResponse.RefreshToken ?? string.Empty;
        config.XivAuthTokenExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tokenResponse.ExpiresIn - 60;
        saveConfig();

        IsPolling = false;
        Status = "Fetching character...";
        Notify();

        await FetchCharacterAsync();
    }

    // --- Token refresh ---

    public async Task<bool> EnsureValidTokenAsync()
    {
        if (string.IsNullOrEmpty(config.XivAuthAccessToken))
            return false;

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < config.XivAuthTokenExpiry)
            return true;

        if (string.IsNullOrEmpty(config.XivAuthRefreshToken))
        {
            Logout();
            return false;
        }

        try
        {
            var body = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", config.XivAuthRefreshToken),
                new("client_id", ClientId),
            });

            var response = await Http.PostAsync($"{BaseUrl}/oauth/token", body);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                log($"XIVAuth: token refresh failed ({(int)response.StatusCode})");
                Logout();
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse?.AccessToken == null)
            {
                Logout();
                return false;
            }

            config.XivAuthAccessToken = tokenResponse.AccessToken ?? string.Empty;
            config.XivAuthRefreshToken = tokenResponse.RefreshToken ?? config.XivAuthRefreshToken;
            config.XivAuthTokenExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tokenResponse.ExpiresIn - 60;
            log("XIVAuth: token refreshed");
            return true;
        }
        catch (Exception ex)
        {
            log($"XIVAuth: token refresh error: {ex.Message}");
            return false;
        }
    }

    // --- Character fetch ---

    private async Task FetchCharacterAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v1/characters");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.XivAuthAccessToken);

            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Error = $"Failed to fetch characters ({(int)response.StatusCode}): {json}";
                Status = string.Empty;
                log(Error);
                Notify();
                return;
            }

            // The characters index renders a bare JSON array, not a wrapped object.
            var characters = JsonSerializer.Deserialize<List<CharacterModel>>(json);
            var character = characters is { Count: > 0 } ? characters[0] : null;

            if (character == null)
            {
                Error = "No verified characters found on this XIVAuth account. Verify a character on xivauth.net first.";
                Status = string.Empty;
                log(Error);
                Notify();
                return;
            }

            config.PlayerPersistentKey = character.PersistentKey;
            config.PlayerCharacterName = character.Name;
            config.PlayerLodestoneId = long.TryParse(character.LodestoneId, out var lodestoneId) ? lodestoneId : 0;
            config.PlayerWorld = character.HomeWorld;
            saveConfig();

            Status = string.Empty;
            Error = string.Empty;
            log($"XIVAuth: logged in as {character.Name} ({character.HomeWorld}), key={character.PersistentKey[..8]}...");
            Notify();
        }
        catch (Exception ex)
        {
            Error = $"Failed to fetch character: {ex.Message}";
            Status = string.Empty;
            log(Error);
            Notify();
        }
    }

    // --- Logout ---

    public void Logout()
    {
        pollCts?.Cancel();
        IsPolling = false;
        DeviceCode = string.Empty;
        UserCode = string.Empty;
        VerificationUrl = string.Empty;
        LoginUrl = string.Empty;
        Status = string.Empty;
        Error = string.Empty;

        config.XivAuthAccessToken = string.Empty;
        config.XivAuthRefreshToken = string.Empty;
        config.XivAuthTokenExpiry = 0;
        config.PlayerPersistentKey = string.Empty;
        config.PlayerCharacterName = string.Empty;
        config.PlayerLodestoneId = 0;
        config.PlayerWorld = string.Empty;
        saveConfig();

        log("XIVAuth: logged out");
        Notify();
    }

    // The player identity to use for netplay/streaming: the XIVAuth
    // persistent_key when logged in, falling back to the local UUID.
    public string GetPlayerUid() =>
        !string.IsNullOrEmpty(config.PlayerPersistentKey)
            ? config.PlayerPersistentKey
            : config.PlayerUid;

    public void Dispose()
    {
        pollCts?.Cancel();
        pollCts?.Dispose();
    }

    // --- JSON models ---

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
        [JsonPropertyName("verification_uri_complete")] public string VerificationUriComplete { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class CharacterModel
    {
        [JsonPropertyName("persistent_key")] public string PersistentKey { get; set; } = "";
        // The API renders lodestone_id as a string.
        [JsonPropertyName("lodestone_id")] public string LodestoneId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("home_world")] public string HomeWorld { get; set; } = "";
        [JsonPropertyName("data_center")] public string DataCenter { get; set; } = "";
        [JsonPropertyName("avatar_url")] public string AvatarUrl { get; set; } = "";
    }
}
