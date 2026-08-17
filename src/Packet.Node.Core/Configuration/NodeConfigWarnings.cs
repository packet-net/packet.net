namespace Packet.Node.Core.Configuration;

/// <summary>
/// Non-fatal config quirk detection: things that parse + validate but deserve the operator's
/// attention on the boot log. Consumed by the providers' <c>WarnOnConfigQuirks</c> at load/apply —
/// the same channel the NET/ROM routing back-compat resolver warns through. Deliberately NOT
/// validator rules: each is a legal configuration that may be intentional, so it must never block
/// an apply.
/// </summary>
public static class NodeConfigWarnings
{
    /// <summary>
    /// One warning per MQTT <c>{instance}</c> label shared by two or more ports (#586). The label is
    /// the port's <see cref="PortConfig.MqttInstance"/> when set, else its id — exactly how the frame
    /// emitter resolves the topic segment — so sharing one silently merges the ports' kissproxy topic
    /// streams under a single <c>{instance}</c>. That can be intentional (multi-port same-band feeding
    /// one collector key), hence a logged warning rather than a validation error.
    /// </summary>
    public static IReadOnlyList<string> DuplicateMqttInstances(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Ports
            .Select(p => (Port: p, Label: string.IsNullOrWhiteSpace(p.MqttInstance) ? p.Id : p.MqttInstance!.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .GroupBy(x => x.Label, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g =>
                $"ports {string.Join(", ", g.Select(x => $"'{x.Port.Id}'"))} share the MQTT instance label " +
                $"'{g.Key}' — their kissproxy topic streams will merge under one {{instance}} segment. " +
                "If that is not intended, give each port a distinct mqttInstance.")
            .ToArray();
    }

    /// <summary>
    /// Ports whose AX.25 window seed (k) exceeds 7. Legal, but only a mod-128 (SABME) link
    /// can use it: a mod-8 session clamps the live window to Modulus-1 = 7
    /// (Ax25SessionContext.EffectiveWindow), so on a port that only ever answers plain
    /// SABM the extra is inert. Surfaced so an operator who meant "wider window" on a
    /// mod-8 link learns why it never gets wider (packet-net/packet.net#696, C083).
    /// </summary>
    public static IReadOnlyList<string> WideWindowSeeds(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Ports
            .Where(p => p.Ax25?.WindowSize is > 7)
            .Select(p =>
                $"port '{p.Id}' sets ax25.windowSize {p.Ax25!.WindowSize} - a window wider than 7 " +
                "only takes effect on mod-128 (SABME) links; mod-8 (SABM) sessions on this port " +
                "clamp to 7 outstanding I-frames.")
            .ToArray();
    }
}
