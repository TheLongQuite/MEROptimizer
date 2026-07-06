using System;
using Mirror;
using System.Collections.Generic;

namespace MEROptimizer.MEROptimizer.Application.Components;

public static class NetworkBatcher
{
    private class PlayerQueue
    {
        public Queue<byte[]> Messages = new Queue<byte[]>();
    }

    private static readonly Dictionary<NetworkConnectionToClient, PlayerQueue> _queues = new();

    public static void Enqueue(NetworkConnectionToClient conn, byte[] message)
    {
        if (conn == null || message == null) return;
        if (!_queues.TryGetValue(conn, out var queue))
        {
            queue = new PlayerQueue();
            _queues[conn] = queue;
        }
        queue.Messages.Enqueue(message);
    }

    public static void EnqueueBatch(NetworkConnectionToClient conn, List<byte[]> messages)
    {
        if (conn == null || messages == null || messages.Count == 0) return;
        if (!_queues.TryGetValue(conn, out var queue))
        {
            queue = new PlayerQueue();
            _queues[conn] = queue;
        }
        for (int i = 0; i < messages.Count; i++)
        {
            queue.Messages.Enqueue(messages[i]);
        }
    }

    public static void Update()
    {
        if (_queues.Count == 0) return;

        List<NetworkConnectionToClient> toRemove = null;

        int maxBytes = 8000;
        if (MerOptimizer.NumberOfPrimitivePerSpawn != 0)
        {
            float count = MerOptimizer.NumberOfPrimitivePerSpawn;
            if (count > 0 && count < 1) count = 1;
            maxBytes = (int)(count * 80);
        }

        foreach (var kvp in _queues)
        {
            var conn = kvp.Key;
            var queue = kvp.Value;

            if (conn == null)
            {
                toRemove ??= new List<NetworkConnectionToClient>();
                toRemove.Add(conn);
                continue;
            }

            if (queue.Messages.Count == 0) continue;

            int bytesSentThisFrame = 0;

            while (queue.Messages.Count > 0)
            {
                byte[] msg = queue.Messages.Peek();

                if (bytesSentThisFrame + msg.Length > maxBytes)
                    break;

                queue.Messages.Dequeue();
                
                conn.Send(new ArraySegment<byte>(msg), Channels.Reliable);
                bytesSentThisFrame += msg.Length;
            }
        }

        if (toRemove != null)
        {
            foreach (var conn in toRemove)
                _queues.Remove(conn);
        }
    }

    public static void ClearQueue(NetworkConnectionToClient conn)
    {
        if (conn != null && _queues.TryGetValue(conn, out var queue))
        {
            queue.Messages.Clear();
        }
    }
}