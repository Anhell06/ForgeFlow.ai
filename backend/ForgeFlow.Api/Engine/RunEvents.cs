using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ForgeFlow.Engine;

/// <summary>In-process pub/sub of run progress for SSE subscribers. Key "*" receives every run's status changes.</summary>
public class RunEvents
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<JsonObject>>> _subs = new();

    public void Publish(string runId, JsonObject evt)
    {
        evt["runId"] = runId;
        evt["ts"] = DateTime.UtcNow;
        Send(runId, evt);
        if (evt["type"]?.GetValue<string>() is "run.status" or "node.status") Send("*", (JsonObject)evt.DeepClone());
    }

    void Send(string key, JsonObject evt)
    {
        if (!_subs.TryGetValue(key, out var subs)) return;
        foreach (var (_, ch) in subs) ch.Writer.TryWrite((JsonObject)evt.DeepClone());
    }

    public Subscription Subscribe(string key)
    {
        var ch = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subs.GetOrAdd(key, _ => new())[id] = ch;
        return new Subscription(ch.Reader, () =>
        {
            if (_subs.TryGetValue(key, out var subs)) subs.TryRemove(id, out _);
            ch.Writer.TryComplete();
        });
    }

    public sealed class Subscription(ChannelReader<JsonObject> reader, Action dispose) : IDisposable
    {
        public ChannelReader<JsonObject> Reader { get; } = reader;
        public void Dispose() => dispose();
    }
}
