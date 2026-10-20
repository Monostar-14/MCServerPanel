namespace MCServerPanel.Models;

public sealed class NodeDto
{
    public string Kind { get; set; } = "scalar";
    public string Type { get; set; } = "string";
    public string? Key { get; set; }
    public int? Index { get; set; }
    public string? Comment { get; set; }
    public string? DocComment { get; set; }
    public int Line { get; set; }
    public bool Editable { get; set; } = true;
    public string Style { get; set; } = "plain";
    public object? Value { get; set; }
    public string Raw { get; set; } = "";
    public double? Step { get; set; }
    public bool Multiline { get; set; }
    public List<object> Path { get; set; } = new();
    public string PathKey { get; set; } = "";
    public List<NodeDto>? Children { get; set; }
}

public sealed class ConfigFileDto
{
    public string RelativePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string DirectoryName { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }
    public bool IsMain { get; set; }
}

public sealed class PluginGroupDto
{
    public string Name { get; set; } = "";
    public string? Jar { get; set; }
    public string? Version { get; set; }
    public List<ConfigFileDto> Files { get; set; } = new();
}

public sealed class CatalogDto
{
    public string? ServerPath { get; set; }
    public string? PluginsPath { get; set; }
    public List<PluginGroupDto> Plugins { get; set; } = new();
    public int FileCount { get; set; }
    public List<string> Hints { get; set; } = new();
}

public sealed class ConfigResponse
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public int LineCount { get; set; }
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }
    public string Text { get; set; } = "";
    public NodeDto? Root { get; set; }
    public string? ParseError { get; set; }
    public int? ErrorLine { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class SaveRequest
{
    public string File { get; set; } = "";
    public string? BaseHash { get; set; }
    public bool Force { get; set; }
    public string? ClientId { get; set; }
    public List<Yaml.YamlEditRequest> Edits { get; set; } = new();
}

public sealed class ClaimRequest
{
    public string File { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientName { get; set; }
    public bool Takeover { get; set; }
}

public sealed class ReleaseRequest
{
    public string ClientId { get; set; } = "";
}

public sealed class LockDto
{
    public string File { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public int AgeSeconds { get; set; }
    public bool Mine { get; set; }
}

public sealed class ClaimResultDto
{
    public bool Ok { get; set; }
    public LockDto? Holder { get; set; }
    public LockDto? Previous { get; set; }
}

public sealed class WatchDto
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }
    public bool Changed { get; set; }
    public bool LockBusy { get; set; }
    public LockDto? Holder { get; set; }
    public List<LockDto> Locks { get; set; } = new();
}

public sealed class SaveResponse
{
    public ConfigResponse Config { get; set; } = new();
    public List<string> Applied { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class SettingsDto
{
    public string? ServerPath { get; set; }
    public string? PluginsPath { get; set; }
    public string? Error { get; set; }
    public List<string> Hints { get; set; } = new();
    public int Port { get; set; } = 8989;
    public int ActualPort { get; set; }
    public bool AllowLan { get; set; }
    public List<string> Urls { get; set; } = new();
    public string SettingsFile { get; set; } = "";
    public bool RestartRequired { get; set; }
    public bool AuthConfigured { get; set; }
    public bool PluginsRootLocked { get; set; }
}

public sealed class SettingsRequest
{
    public string? ServerPath { get; set; }
    public int Port { get; set; } = 8989;
    public bool AllowLan { get; set; } = true;
}

public sealed class BrowseEntryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool HasPlugins { get; set; }
    public bool IsServerRoot { get; set; }
    public int ConfigCount { get; set; }
}

public sealed class BrowseDto
{
    public string? Path { get; set; }
    public string? Parent { get; set; }
    public List<BrowseEntryDto> Directories { get; set; } = new();
    public List<string> Drives { get; set; } = new();
    public string? Error { get; set; }
}
