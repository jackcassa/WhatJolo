namespace WhatJolo;

public sealed class PostgresConnectionSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "whatjolo";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
