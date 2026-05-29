using System.IO;
using System.Text.Json;

namespace Navigation;

internal sealed class NavigationFormState
{
    public string? SelectedProjectName { get; set; }

    public bool UseStandardSearch { get; set; }

    public bool UseStandardBack { get; set; }

    public bool UseChatOcr { get; set; }

    public bool StopContactOnOcrReject { get; set; }

    public bool StopWorkflowOnOcrReject { get; set; }

    public bool AutoScrollWorkflowLog { get; set; } = true;

    public static NavigationFormState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new NavigationFormState();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NavigationFormState>(json) ?? new NavigationFormState();
        }
        catch
        {
            return new NavigationFormState();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }
}
