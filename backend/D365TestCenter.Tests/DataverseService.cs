using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace D365TestCenter.Core;

/// <summary>
/// Stellt eine Dataverse-Verbindung her über MSAL Device Code Flow
/// mit DPAPI-basiertem Token-Cache (persistiert zwischen Sitzungen).
/// </summary>
public sealed class DataverseService
{
    /// <summary>Standard Dynamics 365 First-Party App ID für Device Code Flow.</summary>
    private const string DefaultClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    private const string Authority = "https://login.microsoftonline.com/organizations";
    private const string TokenCacheFileName = "markant_integrationtests_cache.bin";

    private static readonly Dictionary<string, string> Environments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dev"] = "https://markant-dev.crm4.dynamics.com",
        ["test"] = "https://markant-test.crm4.dynamics.com"
    };

    /// <summary>
    /// Verbindet sich mit einer Dataverse-Umgebung über MSAL Device Code Flow.
    /// Der Benutzer muss beim ersten Aufruf einen Code im Browser eingeben.
    /// Nachfolgende Aufrufe verwenden den persistierten Token-Cache.
    /// </summary>
    /// <param name="environment">"dev" oder "test"</param>
    /// <returns>Verbundener ServiceClient</returns>
    public static ServiceClient Connect(string environment)
    {
        return ConnectAsync(environment).GetAwaiter().GetResult();
    }

    /// <summary>Asynchrone Variante von Connect.</summary>
    public static async Task<ServiceClient> ConnectAsync(string environment)
    {
        var url = ResolveEnvironmentUrl(environment);
        var scopes = new[] { $"{url.TrimEnd('/')}/.default" };

        var msalApp = PublicClientApplicationBuilder.Create(DefaultClientId)
            .WithAuthority(Authority)
            .WithDefaultRedirectUri()
            .Build();

        await RegisterTokenCacheAsync(msalApp);

        var accessToken = await AcquireTokenAsync(msalApp, scopes);

        var client = new ServiceClient(
            new Uri(url),
            _ => Task.FromResult(accessToken));

        if (!client.IsReady)
            throw new InvalidOperationException(
                $"Dataverse-Verbindung fehlgeschlagen: {client.LastError}");

        return client;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Token-Beschaffung
    // ═══════════════════════════════════════════════════════════════════

    private static async Task<string> AcquireTokenAsync(
        IPublicClientApplication msalApp, string[] scopes)
    {
        try
        {
            var accounts = await msalApp.GetAccountsAsync();
            var result = await msalApp.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                .ExecuteAsync();
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            var result = await msalApp.AcquireTokenWithDeviceCode(scopes, callback =>
            {
                Console.WriteLine();
                Console.WriteLine(callback.Message);
                return Task.CompletedTask;
            }).ExecuteAsync();

            return result.AccessToken;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Persistenter Token-Cache (DPAPI auf Windows)
    // ═══════════════════════════════════════════════════════════════════

    private static async Task RegisterTokenCacheAsync(IPublicClientApplication msalApp)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Markant", "IntegrationTests");
        Directory.CreateDirectory(cacheDir);

        var storageProperties = new StorageCreationPropertiesBuilder(
                TokenCacheFileName, cacheDir)
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.RegisterCache(msalApp.UserTokenCache);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  URL-Auflösung
    // ═══════════════════════════════════════════════════════════════════

    private static string ResolveEnvironmentUrl(string environment)
    {
        if (Environments.TryGetValue(environment, out var url))
            return url;

        if (Uri.TryCreate(environment, UriKind.Absolute, out _))
            return environment;

        throw new ArgumentException(
            $"Unbekannte Umgebung: '{environment}'. Unterstützt: {string.Join(", ", Environments.Keys)}");
    }
}
