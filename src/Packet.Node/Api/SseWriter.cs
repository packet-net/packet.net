using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Packet.Node.Api;

/// <summary>
/// The one Server-Sent-Events writer every streaming endpoint uses: the response envelope, the
/// heartbeat/read race, and a single write-error policy.
/// </summary>
/// <remarks>
/// <para>
/// The node has six SSE feeds (monitor <c>/events</c>, <c>/rigs/events</c>, port tuning, port
/// spectrum, the per-session console drawer, and the node command console). They were six
/// copies of the same loop with <b>three</b> different write-error policies: some swallowed a
/// client-gone <see cref="IOException"/>, some caught only
/// <see cref="OperationCanceledException"/>, and the spectrum feed caught nothing at all on the
/// write path, so a broken pipe surfaced there as an unhandled 500-level fault where it was a
/// normal disconnect everywhere else (review item C067, #694). One helper, one policy.
/// </para>
/// <para>
/// <b>The policy.</b> A write that fails with <see cref="OperationCanceledException"/> or
/// <see cref="IOException"/> means the client went away mid-write; that is a normal SSE
/// teardown, not a server fault, so it is swallowed and the loop ends when
/// <see cref="HttpContext.RequestAborted"/> fires (or the source channel completes). Anything
/// else still propagates: a genuine bug must not be hidden.
/// </para>
/// <para>
/// <b>The cadence</b> rides the injected <see cref="TimeProvider"/> (repo rule §2.7), so a
/// fake clock drives the heartbeat in tests.
/// </para>
/// </remarks>
internal static class SseWriter
{
    /// <summary>How often a <c>: ping</c> comment is emitted when the source is quiet, keeping
    /// the stream warm through buffering proxies.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Set the SSE response envelope: the content type, no caching, and no buffering anywhere
    /// (the nginx hint plus Kestrel's own body-buffering off), so an event reaches the browser
    /// the instant it is written rather than on a proxy flush.
    /// </summary>
    public static void Begin(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    /// <summary>Write an SSE comment line (e.g. the initial <c>: connected</c>, which flushes
    /// the headers so the client's <c>onopen</c> fires promptly).</summary>
    public static Task CommentAsync(HttpContext ctx, string text, CancellationToken ct)
        => WriteRawAsync(ctx, $": {text}\n\n", ct);

    /// <summary>Write one named SSE event. <paramref name="data"/> must be a single line
    /// (JSON is, and a text chunk must be JSON-encoded by the caller so embedded CR/LF can't
    /// break the framing).</summary>
    public static Task EventAsync(HttpContext ctx, string name, string data, CancellationToken ct)
        => WriteRawAsync(ctx, $"event: {name}\ndata: {data}\n\n", ct);

    /// <summary>
    /// Pump a channel to the client until the client goes away or the channel completes: race
    /// each read against the heartbeat tick, emit <c>: ping</c> when the heartbeat wins, and
    /// write one <paramref name="eventName"/> event per item, rendered by
    /// <paramref name="render"/>.
    /// </summary>
    public static async Task PumpAsync<T>(
        HttpContext ctx,
        ChannelReader<T> reader,
        TimeProvider clock,
        string eventName,
        Func<T, string> render,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(render);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Race an item becoming readable against the heartbeat tick, staying
                // responsive to both.
                var waitRead = reader.WaitToReadAsync(ct).AsTask();
                var heartbeat = Task.Delay(HeartbeatInterval, clock, ct);
                var done = await Task.WhenAny(waitRead, heartbeat).ConfigureAwait(false);

                if (done == heartbeat)
                {
                    await CommentAsync(ctx, "ping", ct).ConfigureAwait(false);
                    continue;
                }

                if (!await waitRead.ConfigureAwait(false))
                {
                    // The source completed (subscription disposed, session closed, peer gone).
                    // Nothing more will arrive; end the response.
                    break;
                }

                while (reader.TryRead(out var item))
                {
                    await EventAsync(ctx, eventName, render(item), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away (RequestAborted). Normal SSE teardown: the caller's
            // using-scoped subscription unsubscribes and completes its channel.
        }
    }

    // Write a UTF-8 SSE chunk and flush it immediately. See the type remarks for why a
    // cancellation or IOException here is swallowed rather than bubbling up as a 500.
    private static async Task WriteRawAsync(HttpContext ctx, string s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            await ctx.Response.WriteAsync(s, ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-write — expected.
        }
        catch (IOException)
        {
            // Broken pipe to a vanished client — expected.
        }
    }
}
