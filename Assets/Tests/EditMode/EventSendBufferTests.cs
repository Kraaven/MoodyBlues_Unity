using System;
using NUnit.Framework;

namespace MoodyBlues.Tests
{
    /// <summary>
    /// Drop-oldest used to take the bytes at the read cursor even while a send of exactly those
    /// bytes was on the socket; when the send completed they were released a second time, losing
    /// events that had never been sent and desyncing the event sizes from the buffer.
    /// </summary>
    [TestFixture]
    public sealed class EventSendBufferTests
    {
        private const int EventSize = 10;

        private static byte[] Event(byte id)
        {
            var bytes = new byte[EventSize];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = id;
            return bytes;
        }

        [Test]
        public void Overflow_WithNoSendInFlight_DropsOldestEvents()
        {
            var buffer = new EventSendBuffer(40);
            for (byte id = 1; id <= 4; id++) Assert.IsTrue(buffer.TryWrite(Event(id), 0));

            Assert.IsTrue(buffer.TryWrite(Event(5), 0));

            Assert.AreEqual(1, buffer.DroppedEvents);
            Assert.AreEqual(4, buffer.EventCount);
            Assert.IsTrue(buffer.TryPrepareChunk(1000, out ReadOnlyMemory<byte> chunk));
            Assert.AreEqual(3 * EventSize, chunk.Length, "the readable region wraps, so the newest event comes next");
            Assert.AreEqual(2, chunk.Span[0], "the oldest event was the one dropped");
            Assert.AreEqual(4, chunk.Span[3 * EventSize - 1]);
            buffer.Release(chunk.Length);
            Assert.IsTrue(buffer.TryPrepareChunk(1000, out chunk));
            Assert.AreEqual(5, chunk.Span[0]);
        }

        [Test]
        public void Overflow_WhileSendInFlight_NeverDropsTheInFlightBytes()
        {
            var buffer = new EventSendBuffer(40);
            for (byte id = 1; id <= 4; id++) Assert.IsTrue(buffer.TryWrite(Event(id), 0));

            Assert.IsTrue(buffer.TryPrepareChunk(2 * EventSize, out ReadOnlyMemory<byte> inFlight));
            Assert.AreEqual(2 * EventSize, inFlight.Length);
            buffer.BeginSend(inFlight.Length);

            Assert.IsFalse(buffer.TryWrite(Event(5), 0), "nothing may be dropped, so the new event is refused");
            Assert.AreEqual(1, buffer.DroppedEvents, "the refused event is counted");
            Assert.AreEqual(4, buffer.EventCount);
            Assert.AreEqual(1, inFlight.Span[0], "the bytes on the socket are untouched");

            buffer.CompleteSend();

            Assert.AreEqual(2, buffer.EventCount, "only the sent events were released");
            Assert.AreEqual(2 * EventSize, buffer.ReadableBytes);
            Assert.IsTrue(buffer.TryPrepareChunk(1000, out ReadOnlyMemory<byte> rest));
            Assert.AreEqual(2 * EventSize, rest.Length);
            Assert.AreEqual(3, rest.Span[0], "the unsent events survive the completed send");
            Assert.AreEqual(4, rest.Span[EventSize]);
        }

        [Test]
        public void AbandonedSend_KeepsItsBytesBufferedAndDroppable()
        {
            var buffer = new EventSendBuffer(40);
            for (byte id = 1; id <= 4; id++) Assert.IsTrue(buffer.TryWrite(Event(id), 0));
            buffer.TryPrepareChunk(EventSize, out ReadOnlyMemory<byte> chunk);
            buffer.BeginSend(chunk.Length);

            buffer.AbandonSend();

            Assert.AreEqual(4, buffer.EventCount);
            Assert.IsTrue(buffer.TryWrite(Event(5), 0));
            Assert.AreEqual(4, buffer.EventCount);
            Assert.AreEqual(1, buffer.DroppedEvents);
        }

        [Test]
        public void EventLargerThanTheBuffer_IsRefused()
        {
            var buffer = new EventSendBuffer(8);

            Assert.IsFalse(buffer.TryWrite(Event(1), 0));
            Assert.AreEqual(1, buffer.DroppedEvents);
        }
    }
}
