using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WhatJolo;

public sealed record ProjectCatalogSnapshot(IReadOnlyList<string> ProjectNames, string? SelectedProjectName);

public static class SharedAppBootstrap
{
    public static async Task EnsureConfiguredPostgresReadyAsync(Action<string>? onProgress = null)
    {
        AppDebugLog.Info("DB/Autoconnect", "Autoconnect PostgreSQL avviato.");
        SharedDatabase.ActivateConfiguredPostgres();
        onProgress?.Invoke("Verifica connessione PostgreSQL configurata...");
        try
        {
            await Task.Run(() => SharedDatabase.EnsureDatabaseReady(message => onProgress?.Invoke(message)));
            AppDebugLog.Info("DB/Autoconnect", "Autoconnect PostgreSQL completato con successo.");
        }
        catch (Exception ex)
        {
            AppDebugLog.Error("DB/Autoconnect", "Autoconnect PostgreSQL fallito.", ex);
            throw;
        }
    }

    public static async Task<ProjectCatalogSnapshot> LoadProjectCatalogAsync(
        ProjectWorkspaceService workspaceService,
        string? preferredSelection = null,
        string? fallbackProjectName = null)
    {
        return await Task.Run(() => LoadProjectCatalog(workspaceService, preferredSelection, fallbackProjectName));
    }

    public static ProjectCatalogSnapshot LoadProjectCatalog(
        ProjectWorkspaceService workspaceService,
        string? preferredSelection = null,
        string? fallbackProjectName = null)
    {
        var projectNames = workspaceService.GetProjectNames()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? selectedProjectName = null;
        if (!string.IsNullOrWhiteSpace(preferredSelection))
        {
            selectedProjectName = projectNames.FirstOrDefault(name =>
                string.Equals(name, preferredSelection, StringComparison.OrdinalIgnoreCase));
        }

        selectedProjectName ??= projectNames.FirstOrDefault();

        if (selectedProjectName is null && !string.IsNullOrWhiteSpace(fallbackProjectName))
        {
            selectedProjectName = workspaceService.EnsureProject(fallbackProjectName);
            if (!projectNames.Contains(selectedProjectName, StringComparer.OrdinalIgnoreCase))
            {
                projectNames.Add(selectedProjectName);
            }
        }

        return new ProjectCatalogSnapshot(projectNames, selectedProjectName);
    }

    public static string GetDefaultDbInstanceHost()
    {
        return GetIpv4Addresses().FirstOrDefault() ?? Environment.MachineName;
    }

    public static string BuildMachineIpSummary()
    {
        var addresses = GetIpv4Addresses();
        return addresses.Count == 0
            ? "IPv4 non disponibile"
            : string.Join(", ", addresses);
    }

    public static string BuildRemoteAccessAddresses()
    {
        var lines = new List<string> { $"MachineName: {Environment.MachineName}" };
        var addresses = GetIpv4Addresses();
        lines.Add(addresses.Count == 0
            ? "IPv4: non disponibile"
            : "IPv4: " + string.Join(", ", addresses));
        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildDbInstanceStatus(PostgresConnectionSettings settings, string host, string port, string database)
    {
        if (SharedDatabase.IsPostgresConfigured())
        {
            return $"Connesso a PostgreSQL su {host}:{port}/{database}.";
        }

        if (string.IsNullOrWhiteSpace(settings.Host) && string.IsNullOrWhiteSpace(settings.Password))
        {
            return $"Istanza DB inizializzata con i dati locali di {Environment.MachineName}.";
        }

        return $"Configurazione pronta per {host}:{port}/{database}. Premi Connetti.";
    }

    private static IReadOnlyList<string> GetIpv4Addresses()
    {
        try
        {
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
