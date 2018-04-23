﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using K4os.Compression.LZ4.Encoders;
using K4os.Hash.xxHash;

namespace K4os.Compression.LZ4.Streams
{
	public class LZ4InputStream: Stream
	{
		private readonly Stream _inner;
		private readonly byte[] _buffer16 = new byte[16];
		private int _index16;

		private int _decoded;
		private readonly bool _interactive = true;
		private readonly Func<ILZ4FrameInfo, ILZ4StreamDecoder> _decoderFactory;
		private ILZ4StreamDecoder _decoder;
		private ILZ4FrameInfo _frameInfo;
		private byte[] _buffer;

		public LZ4InputStream(Stream inner, Func<ILZ4FrameInfo, ILZ4StreamDecoder> decoderFactory)
		{
			_inner = inner;
			_decoderFactory = decoderFactory;
		}

		public override void Flush() =>
			_inner.Flush();

		public override Task FlushAsync(CancellationToken cancellationToken) =>
			_inner.FlushAsync(cancellationToken);

		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = 0;
			while (count > 0)
			{
				if (_decoder == null)
					ReadFrame();

				if (_decoded <= 0 && (_decoded = ReadBlock()) == 0)
					break;

				if (ReadDecoded(buffer, ref offset, ref count, ref read))
					break;
			}

			return read;
		}

		[SuppressMessage("ReSharper", "InconsistentNaming")]
		private void ReadFrame()
		{
			Read0();

			var magic = TryRead32();
			if (magic != 0x184D2204)
				throw MagicNumberExpected();

			Read0();

			var FLG_BD = Read16();

			var FLG = FLG_BD & 0xFF;

			var chaining = ((FLG >> 5) & 0x01) == 0;
			var bchecksum = ((FLG >> 4) & 0x01) != 0;
			var cchecksum = ((FLG >> 2) & 0x01) != 0;

			var BD = (FLG_BD >> 8) & 0xFF;

			var hasContentSize = (BD & (1 << 3)) != 0;
			var hasDictionary = (BD & (1 << 0)) != 0;
			var blockSizeCode = (BD >> 4) & 0x07;

			if (hasContentSize) 
				Read64(); // needs to be read (if present) but we don't care

			var dictionaryId = hasDictionary ? (uint?) Read32() : null;

			var actualHC = (byte) (XXH32.DigestOf(_buffer16, 0, _index16) >> 8);
			var expectedHC = Read8();

			if (actualHC != expectedHC)
				throw InvalidHeaderChecksum();

			var blockSize = MaxBlockSize(blockSizeCode);

			_frameInfo = new LZ4FrameInfo(cchecksum, chaining, bchecksum, dictionaryId, blockSize);
			_decoder = _decoderFactory(_frameInfo);
			_buffer = new byte[blockSize];
		}

		private static InvalidDataException InvalidHeaderChecksum() =>
			new InvalidDataException("Invalid LZ4 frame header checksum");

		private static InvalidDataException MagicNumberExpected() =>
			new InvalidDataException("LZ4 frame magic number expected");

		private void CloseFrame()
		{
			_decoder = null;
			_frameInfo = null;
			_buffer = null;
		}

		private static int MaxBlockSize(int blockSizeCode)
		{
			switch (blockSizeCode)
			{
				case 7: return Mem.M4;
				case 6: return Mem.M1;
				case 5: return Mem.K256;
				case 4: return Mem.K64;
				default: return Mem.K64;
			}
		}

		private unsafe int ReadBlock()
		{
			Read0();

			var blockLength = (int) Read32();
			if (blockLength == 0)
			{
				if (_frameInfo.ContentChecksum)
					Read32();
				CloseFrame();
				return 0;
			}

			var uncompressed = (blockLength & 0x80000000) != 0;
			blockLength &= 0x7FFFFFFF;
			_inner.Read(_buffer, 0, blockLength);
			if (_frameInfo.BlockChecksum)
				Read32();

			fixed (byte* bufferP = _buffer)
				return uncompressed
					? _decoder.Inject(bufferP, blockLength)
					: _decoder.Decode(bufferP, blockLength);
		}

		private bool ReadDecoded(byte[] buffer, ref int offset, ref int count, ref int read)
		{
			if (_decoded <= 0)
				return true;

			var length = Math.Min(count, _decoded);
			_decoder.Drain(buffer, offset, -_decoded, length);
			_decoded -= length;
			offset += length;
			count -= length;
			read += length;

			return _interactive;
		}

		private bool ReadN(int count, bool optional = false)
		{
			var index = 0;
			while (index < count)
			{
				var read = _inner.Read(_buffer16, _index16 + index, count - index);
				if (read == 0)
				{
					if (index == 0 && optional)
						return false;

					throw new IOException();
				}

				index += read;
			}

			_index16 += index;

			return true;
		}

		private void Read0()
		{
			_index16 = 0;
		}

		private ulong Read64()
		{
			ReadN(sizeof(ulong));
			return BitConverter.ToUInt64(_buffer16, _index16 - sizeof(ulong));
		}

		private uint? TryRead32()
		{
			if (!ReadN(sizeof(uint), true))
				return null;

			return BitConverter.ToUInt32(_buffer16, _index16 - sizeof(uint));
		}

		private uint Read32()
		{
			ReadN(sizeof(uint));
			return BitConverter.ToUInt32(_buffer16, _index16 - sizeof(uint));
		}

		private ushort Read16()
		{
			ReadN(sizeof(ushort));
			return BitConverter.ToUInt16(_buffer16, _index16 - sizeof(ushort));
		}

		private byte Read8()
		{
			ReadN(sizeof(byte));
			return _buffer16[_index16 - 1];
		}

		public override Task<int> ReadAsync(
			byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			Task.FromResult(Read(buffer, offset, count));

		public override int ReadByte() => 
			Read(_buffer16, _index16, 1) > 0 ? _buffer16[_index16] : -1;

		protected override void Dispose(bool disposing)
		{
			if (!disposing)
				return;

			_inner.Dispose();
		}

		public override bool CanRead => _inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => -1;

		public override long Position
		{
			get => -1;
			set => throw new InvalidOperationException();
		}

		public override bool CanTimeout => _inner.CanTimeout;

		public override int WriteTimeout
		{
			get => _inner.WriteTimeout;
			set => _inner.WriteTimeout = value;
		}

		public override int ReadTimeout
		{
			get => _inner.ReadTimeout;
			set => _inner.ReadTimeout = value;
		}

		public override long Seek(long offset, SeekOrigin origin) =>
			throw InvalidOperation("Seek");

		public override void SetLength(long value) =>
			throw InvalidOperation("SetLength");

		public override void Write(byte[] buffer, int offset, int count) =>
			throw InvalidOperation("Write");

		public override void WriteByte(byte value) =>
			throw InvalidOperation("WriteByte");

		public override Task WriteAsync(
			byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			throw InvalidOperation("WriteAsync");

		private Exception InvalidOperation(string operation) =>
			new InvalidOperationException($"Operation {operation} is not allowed for {GetType().Name}");
	}
}