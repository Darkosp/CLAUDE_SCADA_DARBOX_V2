using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ScadaDarbox.Gateway.RealTime;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>A hub invocation received from the server.</summary>
public sealed record HubMessage(string Target, JsonElement Arguments);

/// <summary>
/// A minimal SignalR client speaking the JSON hub protocol over a raw WebSocket, the way a
/// browser does: negotiate with the token in a header, then open the socket with the token
/// in the query string.
/// </summary>
/// <remarks>
/// Written against the protocol rather than taken from the .NET SignalR client package,
/// which would be a dependency outside ADR-0006's table for the sake of tests. The subset
/// needed is small, and speaking it directly means the query-string token is exercised
/// exactly as a browser sends it.
/// </remarks>
public sealed class HubTestClient : IAsyncDisposable
{
    private const char RecordSeparator = '';
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<HubMessage> _received = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextInvocationId;

    private HubTestClient()
    {
    }

    /// <summary>Completes when the server ends the connection.</summary>
    public Task Closed => _closed.Task;

    public static async Task<HubTestClient> ConnectAsync(Uri baseAddress, string token)
    {
        string connectionToken;

        using (var http = new HttpClient { BaseAddress = baseAddress })
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/hubs/tags/negotiate?negotiateVersion=1"))
        {
            negotiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(negotiate);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            connectionToken = body.GetProperty("connectionToken").GetString()!;
        }

        var client = new HubTestClient();
        var socketUri = new UriBuilder(baseAddress)
        {
            Scheme = "ws",
            Path = "/hubs/tags",
            Query = $"id={Uri.EscapeDataString(connectionToken)}&access_token={Uri.EscapeDataString(token)}",
        }.Uri;

        await client._socket.ConnectAsync(socketUri, CancellationToken.None);
        _ = client.ReceiveLoopAsync();

        await client.SendRecordAsync("""{"protocol":"json","version":1}""");
        await client._handshake.Task.WaitAsync(HandshakeTimeout);

        _ = client.KeepAliveAsync();
        return client;
    }

    public IReadOnlyList<HubMessage> Received(string target) =>
        _received.Where(message => message.Target == target).ToList();

    /// <summary>Every tag id carried by the tag-value pushes received so far.</summary>
    public IReadOnlyList<Guid> TagIdsReceived() =>
        Received(TagHub.TagValuesMethod)
            .SelectMany(message => message.Arguments[0].EnumerateArray())
            .Select(snapshot => snapshot.GetProperty("tagId").GetGuid())
            .ToList();

    public void Clear() => _received.Clear();

    public async Task WaitForTagAsync(Guid tagId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!TagIdsReceived().Contains(tagId))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No value for tag {tagId} arrived within {timeout}.");
            }

            await Task.Delay(50);
        }
    }

    public async Task WaitForAsync(string target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (Received(target).Count == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No '{target}' message arrived within {timeout}.");
            }

            await Task.Delay(50);
        }
    }

    public async Task<JsonElement> InvokeAsync(string method, TimeSpan timeout)
    {
        var invocationId = Interlocked.Increment(ref _nextInvocationId).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[invocationId] = completion;

        await SendRecordAsync(JsonSerializer.Serialize(new
        {
            type = 1,
            invocationId,
            target = method,
            arguments = Array.Empty<object>(),
        }));

        return await completion.Task.WaitAsync(timeout);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Already gone.
        }

        await _stop.CancelAsync();
        _socket.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var text = new StringBuilder();

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                text.Append(characters, 0, decoder.GetChars(buffer, 0, result.Count, characters, 0, flush: false));

                int separator;
                while ((separator = text.ToString().IndexOf(RecordSeparator)) >= 0)
                {
                    var record = text.ToString(0, separator);
                    text.Remove(0, separator + 1);

                    if (!Dispatch(record))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // The connection ended.
        }
        finally
        {
            _closed.TrySetResult();
            _handshake.TrySetException(new InvalidOperationException("The connection closed before the handshake completed."));

            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new InvalidOperationException("The connection closed before the invocation completed."));
            }
        }
    }

    /// <returns>False when the server has closed the connection.</returns>
    private bool Dispatch(string record)
    {
        using var document = JsonDocument.Parse(record);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var type))
        {
            if (root.TryGetProperty("error", out var refused))
            {
                _handshake.TrySetException(new InvalidOperationException(refused.GetString()));
            }
            else
            {
                _handshake.TrySetResult();
            }

            return true;
        }

        switch (type.GetInt32())
        {
            case 1:
                _received.Enqueue(new HubMessage(root.GetProperty("target").GetString()!, root.GetProperty("arguments").Clone()));
                break;

            case 3 when _pending.TryRemove(root.GetProperty("invocationId").GetString()!, out var completion):
                if (root.TryGetProperty("error", out var failure))
                {
                    completion.TrySetException(new InvalidOperationException(failure.GetString()));
                }
                else
                {
                    completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                }

                break;

            case 7:
                return false;
        }

        return true;
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                await SendRecordAsync("""{"type":6}""");
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException
                                              or InvalidOperationException or ObjectDisposedException)
        {
            // The connection ended.
        }
    }

    private async Task SendRecordAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + RecordSeparator);

        await _sendGate.WaitAsync();
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
