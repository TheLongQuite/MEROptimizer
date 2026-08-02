using Mirror;
using System.Collections.Generic;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public static class NetworkBatcher
{
    private class PlayerQueue
    {
        public readonly Queue<byte[]> Messages = new();
    }

    private static readonly Dictionary<NetworkConnectionToClient, PlayerQueue> Queues = new();
    private static int _cachedMaxBytes = -1;
    private static float _cachedNumberOfPrimitivePerSpawn = float.NaN;
    
    public static void Enqueue(NetworkConnectionToClient conn, byte[] message)
    {
        if (conn == null || message == null) 
            return;
        
        if (!Queues.TryGetValue(conn, out PlayerQueue queue))
        {
            queue = new();
            Queues[conn] = queue;
        }
        
        queue.Messages.Enqueue(message);
    }

    public static void EnqueueBatch(NetworkConnectionToClient conn, List<byte[]> messages)
    {
        if (conn == null || messages == null || messages.Count == 0) 
            return;
        
        if (!Queues.TryGetValue(conn, out PlayerQueue queue))
        {
            queue = new();
            Queues[conn] = queue;
        }
        
        foreach (byte[] t in messages)
            queue.Messages.Enqueue(t);
    }

    public static void Update()
    {
        if (Queues.Count == 0) return;

        List<NetworkConnectionToClient> toRemove = null;
        if (_cachedMaxBytes == -1 || 
            !Mathf.Approximately(_cachedNumberOfPrimitivePerSpawn, MerOptimizer.NumberOfPrimitivePerSpawn))
        {
            _cachedNumberOfPrimitivePerSpawn = MerOptimizer.NumberOfPrimitivePerSpawn;

            int computedMaxBytes = 8000;
            if (MerOptimizer.NumberOfPrimitivePerSpawn != 0)
            {
                float count = MerOptimizer.NumberOfPrimitivePerSpawn;
                if (count is > 0 and < 1)
                    count = 1;

                computedMaxBytes = (int)(count * 80);
            }

            _cachedMaxBytes = computedMaxBytes;
        }

        int maxBytes = _cachedMaxBytes;

        foreach (KeyValuePair<NetworkConnectionToClient, PlayerQueue> kvp in Queues)
        {
            NetworkConnectionToClient conn = kvp.Key;
            PlayerQueue queue = kvp.Value;

            if (conn == null)
            {
                toRemove ??= [];
                toRemove.Add(null);
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
                
                conn.Send(new(msg));
                bytesSentThisFrame += msg.Length;
            }
        }

        if (toRemove == null)
            return;

        {
            foreach (NetworkConnectionToClient conn in toRemove)
                Queues.Remove(conn);
        }
    }

    public static void ClearQueue(NetworkConnectionToClient conn)
    {
        if (conn != null && Queues.TryGetValue(conn, out PlayerQueue queue))
            queue.Messages.Clear();
    }
}