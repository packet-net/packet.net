namespace Packet.Node.Core.Mqtt;

/// <summary>
/// The narrow publish seam the <see cref="MqttFrameEmitter"/> writes through — one enqueue call
/// carrying exactly the four things a kissproxy publish needs: the topic, the payload bytes, the QoS,
/// and the retain flag. The production implementation (<see cref="ManagedMqttPublishSink"/>) wraps
/// MQTTnet's <c>ManagedMqttClient.EnqueueAsync</c>; a test double captures the tuples so the topic +
/// payload contract can be asserted byte-for-byte without standing up a broker.
/// </summary>
/// <remarks>
/// Intentionally free of MQTTnet types (QoS is a plain <see cref="int"/> 0..2) so the test project can
/// implement it without referencing the broker library. Disposal stops + releases the underlying
/// client; the emitter owns the sink's lifetime.
/// </remarks>
internal interface IMqttPublishSink : IAsyncDisposable
{
    /// <summary>Enqueue one message for publication. Non-blocking by contract — the managed client
    /// queues internally and drains as the broker connection allows, so a slow/dead broker never
    /// back-pressures the caller (the frame tap).</summary>
    ValueTask PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct);

    /// <summary>Messages currently queued awaiting the broker (the managed client's bounded pending
    /// queue), for the <c>pdn_mqtt_pending_messages</c> gauge. A test double may return 0.</summary>
    long PendingMessageCount { get; }
}
