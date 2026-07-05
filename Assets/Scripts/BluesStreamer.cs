using System;
using System.Net.WebSockets;
using System.Threading;
using UnityEngine;

public class BluesStreamer
{
    private readonly ClientWebSocket _socket;
    private CancellationToken _socketToken;
    private ThreadInstanceManager.ThreadInstance<TransformData, Memory<byte>> SerialisationThread;

    public bool SendTrueTransforms;

    public BluesStreamer() { 
        _socket = new ClientWebSocket();
        _socket.ConnectAsync(new System.Uri("google.com"), _socketToken); //URI is placeholder


        SerialisationThread = ThreadInstanceManager.CreateThreadInstance<TransformData, Memory<byte>>(
                SerializeTransformData, (x) => { }, "DataSerializer");
    }

    public Memory<byte> SerializeTransformData(TransformData inboundData) {
                
        // The inbound Data is read, and it is determined of which type of Serialization is done for this specific transform data

        return new Memory<byte>();
    }

    public void BinaryDataPacketAccumulator(Memory<byte> binaryData) { 
        //Accumulate the Binary Serialized transform events. into a Queue. Whenever the queue hits 2KB. We flush out 2KBs worth of binary data and sent it as a packet. We maintain a ByteQueue incase there is more than 2KB, we keep sending packets till there is less than 2KB
    }

}
