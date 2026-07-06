using AdminToys;
using LabApi.Features.Wrappers;
using Mirror;
using System;
using System.Linq;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class ClientSidePrimitive
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }
    public PrimitiveType PrimitiveType { get; set; }
    public Color Color { get; set; }
    public PrimitiveFlags PrimitiveFlags { get; set; }
    public string SourceName { get; set; }

    public byte[] SerializedSpawnMessage { get; private set; }
    public byte[] SerializedDestroyMessage { get; private set; }
    public uint NetId { get; set; }

    public ClientSidePrimitive(Vector3 position, Quaternion rotation, Vector3 scale, 
        PrimitiveType primitiveType, Color color, PrimitiveFlags primitiveFlags, 
        string sourceName = null)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
        PrimitiveType = primitiveType;
        Color = color;
        PrimitiveFlags = primitiveFlags;
        SourceName = sourceName ?? string.Empty;
        NetId = NetworkIdentity.GetNextNetworkId();
        
        GenerateNetworkMessages();
    }

    private void GenerateNetworkMessages()
    {
        NetworkWriterPooled writer = NetworkWriterPool.Get();
        try
        {
            writer.Write<byte>(1);
            writer.Write<byte>(67);
            writer.Write(Position);
            writer.Write(Rotation);
            writer.Write(Scale);
            writer.Write<byte>(0);
            writer.Write(false);
            writer.Write((int)PrimitiveType);
            writer.Write(Color);
            writer.Write((byte)PrimitiveFlags);
            writer.Write<uint>(0);

            ArraySegment<byte> segment = writer.ToArraySegment();
            byte[] payloadCopy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array!, segment.Offset, payloadCopy, 0, segment.Count);

            SpawnMessage spawnMessage = new SpawnMessage
            {
                netId = NetId,
                isLocalPlayer = false,
                isOwner = false,
                sceneId = 0,
                assetId = MerOptimizer.PrimitiveAssetId,
                position = Position,
                rotation = Rotation,
                scale = Scale,
                payload = new ArraySegment<byte>(payloadCopy)
            };

            ObjectDestroyMessage destroyMessage = new ObjectDestroyMessage
            {
                netId = NetId,
            };

            ushort spawnId = NetworkMessageId<SpawnMessage>.Id;
            NetworkWriterPooled spawnWriter = NetworkWriterPool.Get();
            spawnWriter.WriteUShort(spawnId);
            spawnWriter.Write(spawnMessage);
            SerializedSpawnMessage = spawnWriter.ToArray();
            NetworkWriterPool.Return(spawnWriter);

            ushort destroyId = NetworkMessageId<ObjectDestroyMessage>.Id;
            NetworkWriterPooled destroyWriter = NetworkWriterPool.Get();
            destroyWriter.WriteUShort(destroyId);
            destroyWriter.Write(destroyMessage);
            SerializedDestroyMessage = destroyWriter.ToArray();
            NetworkWriterPool.Return(destroyWriter);
        }
        finally
        {
            NetworkWriterPool.Return(writer);
        }
    }

    public void DestroyForEveryone()
    {
        foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy))
            DestroyClientPrimitive(player);
    }

    public void DestroyClientPrimitive(Player target)
    {
        if (target.IsDestroyed || target.IsHost || target.IsNpc || target.IsDummy) 
            return;
        
        SendRaw(target.Connection, SerializedDestroyMessage);
    }

    public void SpawnForEveryone()
    {
        foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy))
            SpawnClientPrimitive(player);
    }

    public void SpawnClientPrimitive(Player target)
    {
        if (target.IsDestroyed || target.IsHost || target.IsNpc || target.IsDummy) 
            return;
        
        SendRaw(target.Connection, SerializedSpawnMessage);
    }

    private void SendRaw(NetworkConnection conn, byte[] data)
    {
        if (conn is not NetworkConnectionToClient clientConn)
            return;

        NetworkWriterPooled writer = NetworkWriterPool.Get();
        try
        {
            writer.WriteBytes(data, 0, data.Length);
            clientConn.Send(writer.ToArraySegment());
        }
        finally
        {
            NetworkWriterPool.Return(writer);
        }
    }
}