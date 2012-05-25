using System;
using System.IO;
using System.Text;

namespace AntMicro.Migrant
{
	/// <summary>
	/// Provides the mechanism for reading primitive values from a stream.
	/// </summary>
	/// <remarks>
	/// Can be used as a replacement for the <see cref="System.IO.BinaryReader" /> . Provides
	/// more compact output and reads no more data from the stream than requested. Although
	/// the underlying format is not specified at this point, it is guaranteed to be consistent with
	/// <see cref="AntMicro.Migrant.PrimitiveWriter" />. Reader has to be disposed after used,
	/// otherwise stream position corruption can occur. Reader does not possess the stream
	/// and does not close it after dispose.
	/// </remarks>
    public sealed class PrimitiveReader : IDisposable
    {
		/// <summary>
		/// Initializes a new instance of the <see cref="AntMicro.Migrant.PrimitiveReader" /> class.
		/// </summary>
		/// <param name='stream'>
		/// The underlying stream which will be used to read data. Has to be readable.
		/// </param>
        public PrimitiveReader(Stream stream)
        {
            this.stream = stream;
            // buffer size is the size of the maximal padding
            buffer = new byte[Helpers.MaximalPadding];
        }

		/// <summary>
		/// Gets the current position.
		/// </summary>
		/// <value>
		/// The position, which is the number of bytes read after the object was
		/// constructed.
		/// </value>
        public long Position
        {
            get
            {
                return currentPosition - currentBufferSize + currentBufferPosition;
            }
        }

		/// <summary>
		/// Reads and returns <see cref="System.Double" />.
		/// </summary>
        public double ReadDouble()
        {
            return BitConverter.Int64BitsToDouble(ReadInt64());
        }

		/// <summary>
		/// Reads and returns <see cref="System.Single" />.
		/// </summary>
        public float ReadSingle()
        {
            return (float)BitConverter.Int64BitsToDouble(ReadInt64());
        }

		/// <summary>
		/// Reads and returns <see cref="System.DateTime" />.
		/// </summary>
        public DateTime ReadDateTime()
        {
            return Helpers.DateTimeEpoch.AddTicks(ReadInt64());
        }

		/// <summary>
		/// Reads and returns <see cref="System.TimeSpan" />.
		/// </summary>
        public TimeSpan ReadTimeSpan()
        {
            return TimeSpan.FromTicks(ReadInt64());
        }

		/// <summary>
		/// Reads and returns <see cref="System.Byte" />.
		/// </summary>
        public byte ReadByte()
        {
            CheckBuffer();
            return buffer[currentBufferPosition++];
        }

		/// <summary>
		/// Reads and returns <see cref="System.SByte" />.
		/// </summary>
        public sbyte ReadSByte()
        {
            return (sbyte)ReadByte();
        }

		/// <summary>
		/// Reads and returns <see cref="System.Int16" />.
		/// </summary>
        public short ReadInt16()
        {
            return (short)InnerReadInteger();
        }

		/// <summary>
		/// Reads and returns <see cref="System.UInt16" />.
		/// </summary>
        public ushort ReadUInt16()
        {
            return (ushort)InnerReadInteger();
        }

		/// <summary>
		/// Reads and returns <see cref="System.Int32" />.
		/// </summary>
        public int ReadInt32()
        {
            return (int)InnerReadInteger();
        }

		/// <summary>
		/// Reads and returns <see cref="System.UInt32" />.
		/// </summary>
        public uint ReadUInt32()
        {
            return (uint)InnerReadInteger();
        }

		/// <summary>
		/// Reads and returns <see cref="System.Int64" />.
		/// </summary>
		public long ReadInt64()
        {
            return (long)InnerReadInteger();
        }

		/// <summary>
		/// Reads and returns <see cref="System.UInt64" />.
		/// </summary>
        public ulong ReadUInt64()
        {
            return InnerReadInteger();
        }

		/// <summary>
		/// Reads and returns <see cref="System.Char" />.
		/// </summary>
        public char ReadChar()
        {
            return (char)ReadUInt16();
        }

		/// <summary>
		/// Reads and returns <see cref="System.Boolean" />.
		/// </summary>
        public bool ReadBool()
        {
            return ReadByte() == 1;
        }

		/// <summary>
		/// Reads and returns string.
		/// </summary>
        public string ReadString()
        {
            bool fake;
            var length = ReadInt32(); // length prefix
            var chunk = InnerChunkRead(length, out fake);
            return Encoding.UTF8.GetString(chunk.Array, chunk.Offset, chunk.Count);
        }

		/// <summary>
		/// Reads the given number of bytes.
		/// </summary>
		/// <returns>
		/// The array holding read bytes.
		/// </returns>
		/// <param name='count'>
		/// Number of bytes to read.
		/// </param>
        public byte[] ReadBytes(int count)
        {
            bool bufferCreated;
            var chunk = InnerChunkRead(count, out bufferCreated);
            if(bufferCreated)
            {
                return chunk.Array;
            }
            var result = new byte[count];
            Array.Copy(chunk.Array, chunk.Offset, result, 0, chunk.Count);
            return result;
        }

		/// <summary>
		/// Copies given number of bytes to a given stream.
		/// </summary>
		/// <param name='destination'>
		/// Writeable stream to which data will be copied.
		/// </param>
		/// <param name='howMuch'>
		/// The number of bytes which will be copied to the destination stream.
		/// </param>
        public void CopyTo(Stream destination, long howMuch)
        {
            // first we need to flush the inner buffer into a stream
            var dataLeft = currentBufferSize - currentBufferPosition;
            var toRead = (int)Math.Min(dataLeft, howMuch);
            destination.Write(buffer, currentBufferPosition, toRead);
            currentBufferPosition += toRead;
            howMuch -= toRead;
            if(howMuch <= 0)
            {
                return;
            }
            // we can reuse the regular buffer since it is invalidated at this point anyway
            int read;
            while((read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, howMuch))) > 0)
            {
                howMuch -= read;
                destination.Write(buffer, 0, read);
                currentPosition += read;
            }
        }

		/// <summary>
		/// After this call stream's position is updated to match the padding used by <see cref="AntMicro.Migrant.PrimitiveWriter"/>.
		/// It is needed to be called if one expects consecutive reads (of data written previously by consecutive writes).
		/// </summary>
		/// <remarks>
		/// Call <see cref="Dispose"/> when you are finished using the <see cref="AntMicro.Migrant.PrimitiveReader"/>. The
		/// <see cref="Dispose"/> method leaves the <see cref="AntMicro.Migrant.PrimitiveReader"/> in an unusable state. After
		/// calling <see cref="Dispose"/>, you must release all references to the
		/// <see cref="AntMicro.Migrant.PrimitiveReader"/> so the garbage collector can reclaim the memory that the
		/// <see cref="AntMicro.Migrant.PrimitiveReader"/> was occupying.
		/// </remarks>
        public void Dispose()
        {
            // we have to leave stream in aligned position
            var toRead = Helpers.GetCurrentPaddingValue(currentPosition);
            stream.ReadOrThrow(buffer, 0, toRead);
        }

        private ulong InnerReadInteger()
        {
            ulong next;
            var result = 0UL;
            var shift = 0;
            do
            {
                next = ReadByte();
                result |= (next & 0x7FU) << shift;
                shift += 7;
            } while((next & 128) > 0);
            return result;
        }

        private void CheckBuffer()
        {
            if(currentBufferSize <= currentBufferPosition)
            {
                ReloadBuffer();
            }
        }

        private void ReloadBuffer()
        {
            // how much can we read?
            var toRead = Helpers.GetNextBytesToRead(currentPosition);
            stream.ReadOrThrow(buffer, 0, toRead);
            currentPosition += toRead;
            currentBufferSize = toRead;
            currentBufferPosition = 0;
        }

        private ArraySegment<byte> InnerChunkRead(int byteNumber, out bool bufferCreated)
        {
            bufferCreated = false;
            var dataLeft = currentBufferSize - currentBufferPosition;
            if(byteNumber > dataLeft)
            {
                var data = new byte[byteNumber];
                bufferCreated = true;
                var toRead = byteNumber - dataLeft;
                Array.Copy(buffer, currentBufferPosition, data, 0, dataLeft);
                currentBufferPosition += dataLeft;
                stream.ReadOrThrow(data, dataLeft, toRead);
                currentPosition += toRead;
                return new ArraySegment<byte>(data, 0, byteNumber);
            }
            var result = new ArraySegment<byte>(buffer, currentBufferPosition, byteNumber);
            currentBufferPosition += byteNumber;
            return result;
        }

        private long currentPosition;
        private readonly byte[] buffer;
        private int currentBufferSize;
        private int currentBufferPosition;
        private readonly Stream stream;
    }
}

