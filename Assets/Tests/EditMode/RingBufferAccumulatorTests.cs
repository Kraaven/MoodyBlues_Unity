using System;
using NUnit.Framework;

namespace MoodyBlues.Tests
{
    /// <summary>
    /// The ring buffer's subtle part is the seam. An event is never allowed to straddle the end of
    /// the backing array, so when one would, the leftover tail is stamped with a skip marker and
    /// the write cursor wraps. The reader has to step over that padding without ever handing it to
    /// a caller and without leaving the read cursor inside it -- get that wrong and the stream
    /// gains bytes that decode as garbage.
    ///
    /// The marker is 0, which is safe precisely because no real event type is 0 (they start at 1).
    /// </summary>
    [TestFixture]
    public sealed class RingBufferAccumulatorTests
    {
        [Test]
        public void NewBuffer_IsEmpty()
        {
            var buffer = new RingBufferAccumulator(64);

            Assert.AreEqual(64, buffer.Capacity);
            Assert.AreEqual(0, buffer.ReadableBytes);
            Assert.AreEqual(64, buffer.FreeBytes);
        }

        [Test]
        public void ReserveCommit_MakesBytesReadable()
        {
            var buffer = new RingBufferAccumulator(64);

            Span<byte> destination = buffer.Reserve(4);
            destination[0] = 1; destination[1] = 2; destination[2] = 3; destination[3] = 4;
            buffer.Commit(4);

            Assert.AreEqual(4, buffer.ReadableBytes);
            Assert.AreEqual(60, buffer.FreeBytes);

            Assert.IsTrue(buffer.TryTakeFlushChunk(1, 64, out ReadOnlyMemory<byte> chunk));
            Assert.AreEqual(new byte[] { 1, 2, 3, 4 }, chunk.ToArray());
        }

        [Test]
        public void TryTakeFlushChunk_RespectsMinimumFlushSize()
        {
            var buffer = new RingBufferAccumulator(64);
            Write(buffer, 4, 0xAA);

            Assert.IsFalse(buffer.TryTakeFlushChunk(8, 64, out _), "4 readable bytes should not satisfy a minimum of 8");
            Assert.IsTrue(buffer.TryTakeFlushChunk(4, 64, out _), "4 readable bytes should satisfy a minimum of 4");
        }

        [Test]
        public void TryTakeFlushChunk_CapsAtMaxChunkSize()
        {
            var buffer = new RingBufferAccumulator(64);
            Write(buffer, 16, 0xBB);

            Assert.IsTrue(buffer.TryTakeFlushChunk(1, 8, out ReadOnlyMemory<byte> chunk));
            Assert.AreEqual(8, chunk.Length);
        }

        [Test]
        public void ReleaseFlushChunk_FreesSpaceForReuse()
        {
            var buffer = new RingBufferAccumulator(16);

            Write(buffer, 8, 0x11);
            Assert.IsTrue(buffer.TryTakeFlushChunk(1, 16, out ReadOnlyMemory<byte> chunk));
            buffer.ReleaseFlushChunk(chunk.Length);

            Assert.AreEqual(0, buffer.ReadableBytes);
            Assert.AreEqual(16, buffer.FreeBytes);

            // And the freed region is usable again.
            Write(buffer, 8, 0x22);
            Assert.AreEqual(8, buffer.ReadableBytes);
        }

        [Test]
        public void WriteThatWouldStraddleTheSeam_WrapsAndThePaddingIsNeverReturned()
        {
            // 16-byte buffer: fill 12, release them (cursors now at 12), then write 6. Six bytes
            // do not fit in the 4 remaining before the end, so the tail is skipped and the write
            // wraps to the start.
            var buffer = new RingBufferAccumulator(16);

            Write(buffer, 12, 0x01);
            Assert.IsTrue(buffer.TryTakeFlushChunk(1, 16, out ReadOnlyMemory<byte> first));
            Assert.AreEqual(12, first.Length);
            buffer.ReleaseFlushChunk(first.Length);

            Write(buffer, 6, 0x02);

            // The reader must step over the 4 wasted bytes and hand back only the real payload.
            Assert.IsTrue(buffer.TryTakeFlushChunk(1, 16, out ReadOnlyMemory<byte> second));
            Assert.AreEqual(6, second.Length, "the skipped tail must not be included in the chunk");

            foreach (byte b in second.ToArray())
            {
                Assert.AreEqual(0x02, b, "padding bytes leaked into a flush chunk");
            }

            buffer.ReleaseFlushChunk(second.Length);
            Assert.AreEqual(0, buffer.ReadableBytes, "wasted tail plus payload should both be accounted for");
        }

        [Test]
        public void ExactFitToTheEnd_DoesNotWasteATail()
        {
            // 16 bytes written as 8 + 8 lands exactly on the end, so no skip marker is needed.
            var buffer = new RingBufferAccumulator(16);

            Write(buffer, 8, 0x0A);
            Write(buffer, 8, 0x0B);

            Assert.AreEqual(16, buffer.ReadableBytes);
            Assert.AreEqual(0, buffer.FreeBytes);

            Assert.IsTrue(buffer.TryTakeFlushChunk(1, 16, out ReadOnlyMemory<byte> chunk));
            Assert.AreEqual(16, chunk.Length, "a perfectly aligned fill should be fully readable in one chunk");
        }

        [Test]
        public void Reserve_RejectsSizesItCannotEverHold()
        {
            var buffer = new RingBufferAccumulator(16);

            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Reserve(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Reserve(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Reserve(17));
        }

        [Test]
        public void Reserve_ThrowsWhenGenuinelyFull()
        {
            // MoodyBluesStreamer.TryReserve exists to make sure this is never reached in the live
            // path -- it keeps MaxEventSize of headroom so a wasted tail can always be absorbed.
            // Pinned here so that guarantee stays load-bearing rather than incidental.
            var buffer = new RingBufferAccumulator(16);
            Write(buffer, 16, 0xFF);

            Assert.Throws<InvalidOperationException>(() => buffer.Reserve(1));
        }

        [Test]
        public void SkipMarkerValueCannotCollideWithARealEventType()
        {
            // The reader distinguishes padding from payload by the byte value alone, so this is a
            // real constraint on the protocol, not just an implementation detail.
            foreach (Serializer.EventType type in Enum.GetValues(typeof(Serializer.EventType)))
            {
                Assert.AreNotEqual(0, (byte)type, $"{type} collides with the ring buffer's skip marker");
            }
        }

        private static void Write(RingBufferAccumulator buffer, int size, byte fill)
        {
            Span<byte> destination = buffer.Reserve(size);
            for (int i = 0; i < size; i++) destination[i] = fill;
            buffer.Commit(size);
        }
    }
}
