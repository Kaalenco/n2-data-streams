namespace N2.DataStreams.A2Protocol;

/// <summary>
/// Optional outbound proxy for <see cref="AS2Send.CreateHttpClient"/>.
/// </summary>
public sealed class ProxySettings
{
    public string Name { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Domain { get; init; }

    // Never print the password.
    public override string ToString() => $"ProxySettings({Name})";
}
