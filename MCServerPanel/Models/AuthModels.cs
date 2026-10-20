namespace MCServerPanel.Models;

public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SetupRequest
{
    public string Token { get; set; } = "";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public sealed class SessionDto
{
    public bool Authenticated { get; set; }
    public bool SetupRequired { get; set; }
    public string? Username { get; set; }
    public bool TestMode { get; set; }
}
