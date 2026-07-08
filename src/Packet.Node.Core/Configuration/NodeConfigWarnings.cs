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
}
